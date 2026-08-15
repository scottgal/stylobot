using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Ladder-on-sessions (operator ruling 2026-08-15 — supersedes the window-folds-only
///     reading): the sessions table is a first-class row unit WITHIN
///     <see cref="TemporalStoreOptions.SessionRowHorizon"/> — AddSessionAsync writes the
///     summary-grain row and the Sessions view reads the live rows. Past the horizon,
///     <see cref="SqliteDashboardEventStore.DeResolveSessionsAsync"/> verifies the
///     signature's aggregate coverage (the fold's fused hour rows), lands a guarded
///     one-time backfill ONLY where coverage is absent (never a double count), and
///     deletes the row — the table flat by construction.
///     <para>
///     The per-session ANALYTIC BAGGAGE (vector, transitions, timing entropy, narrative)
///     is not written and not served — the row carries the operator's grain only.
///     </para>
/// </summary>
public sealed class SessionDeResolutionTests : IDisposable
{
    private readonly string _tempDir;

    public SessionDeResolutionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-session-deres-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        SqliteConnection.ClearAllPools();
    }

    private (SqliteDetectionArchive Archive, SqliteDashboardEventStore Store) BuildStores()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db")
        });
        var dashboardOptions = Options.Create(new StyloBotDashboardOptions
        {
            TemporalStore = new TemporalStoreOptions { CompressionEnabled = true, SessionRowHorizon = TimeSpan.FromHours(24) }
        });
        var archive = new SqliteDetectionArchive(NullLogger<SqliteDetectionArchive>.Instance, options);
        var store = new SqliteDashboardEventStore(
            NullLogger<SqliteDashboardEventStore>.Instance, options, dashboardOptions);
        return (archive, store);
    }

    private static PersistedSession MakeSession(string signature, DateTime endedAt) => new()
    {
        Signature = signature,
        StartedAt = endedAt.AddMinutes(-5),
        EndedAt = endedAt,
        RequestCount = 3,
        // The analytic baggage fields are carried by the payload but NOT written —
        // the storage boundary keeps the operator's grain only.
        Vector = new byte[516],
        Maturity = 0.5f,
        DominantState = "PageView",
        IsBot = false,
        AvgBotProbability = 0.1,
        AvgConfidence = 0.7,
        RiskBand = "Low",
        TransitionCountsJson = "{\"PageView->ApiCall\": 2}",
        PathsJson = "[\"/\",\"/blog\"]",
        AvgProcessingTimeMs = 12.5,
        TimingEntropy = 0.3f,
        Narrative = "baggage must not land",
    };

    [Fact]
    public async Task AddSessionAsync_writes_the_summary_grain_row_without_baggage()
    {
        var (archive, _) = BuildStores();
        await archive.InitializeAsync();

        var now = DateTime.UtcNow;
        await archive.AddSessionAsync(RequestScope.Unknown, MakeSession("sig-summary", now), CancellationToken.None);

        var rows = await archive.GetRecentSessionsAsync(limit: 10);
        var row = Assert.Single(rows);
        Assert.Equal("sig-summary", row.Signature);
        Assert.Equal(3, row.RequestCount);
        Assert.Equal(12.5, row.AvgProcessingTimeMs);
        // The operator's grain survives; the analytic baggage does not.
        Assert.Empty(row.Vector); // NULL blob reads as an empty array
        Assert.Null(row.TransitionCountsJson);
        Assert.Equal(0f, row.TimingEntropy);
        Assert.Null(row.Narrative);
    }

    [Fact]
    public async Task DeResolveSessionsAsync_keeps_live_rows_and_de_resolves_aged_ones()
    {
        var (archive, store) = BuildStores();
        await archive.InitializeAsync();

        var now = DateTime.UtcNow;
        // A live row (within the 24h horizon) and an aged row (past it).
        await archive.AddSessionAsync(RequestScope.Unknown, MakeSession("sig-live", now), CancellationToken.None);
        await archive.AddSessionAsync(RequestScope.Unknown, MakeSession("sig-aged", now.AddHours(-48)), CancellationToken.None);

        await store.DeResolveSessionsAsync(now, CancellationToken.None);

        // The live row stays (the Sessions view reads live rows within the horizon)…
        var rows = await archive.GetRecentSessionsAsync(limit: 10);
        rows.Should().ContainSingle(r => r.Signature == "sig-live");
        // …the aged row is de-resolved (deleted).
        rows.Should().NotContain(r => r.Signature == "sig-aged");

        // The aged row's data never reached the aggregates (no fused rows exist for the
        // signature) — the guarded one-time backfill landed a sparse aggregate row
        // anchored at the session's start hour.
        await using var conn = new SqliteConnection(StoreConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM detections WHERE signature = @sig AND (fused = 1 OR method = 'SESSION')";
        cmd.Parameters.AddWithValue("@sig", "sig-aged");
        var backfilled = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        backfilled.Should().Be(1, "the guarded backfill folds the aged row's summary into the aggregates once");
    }

    [Fact]
    public async Task DeResolveSessionsAsync_never_double_counts_when_coverage_exists()
    {
        var (archive, store) = BuildStores();
        await archive.InitializeAsync();

        var now = DateTime.UtcNow;
        await archive.AddSessionAsync(RequestScope.Unknown, MakeSession("sig-covered", now.AddHours(-48)), CancellationToken.None);

        // First pass: coverage absent → backfill lands + row deleted.
        await store.DeResolveSessionsAsync(now, CancellationToken.None);
        // Second pass: the backfill's own row IS the coverage → nothing more lands.
        await store.DeResolveSessionsAsync(now, CancellationToken.None);

        await using var conn = new SqliteConnection(StoreConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM detections WHERE signature = @sig AND (fused = 1 OR method = 'SESSION')";
        cmd.Parameters.AddWithValue("@sig", "sig-covered");
        var rows = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        rows.Should().Be(1, "coverage exists after the first backfill — a second pass must never double count");

        var sessions = await archive.GetRecentSessionsAsync(limit: 10);
        sessions.Should().NotContain(s => s.Signature == "sig-covered");
    }

    private string StoreConnectionString
        => $"Data Source={Path.Combine(_tempDir, "dashboard.db")};Cache=Shared;Pooling=true";
}
