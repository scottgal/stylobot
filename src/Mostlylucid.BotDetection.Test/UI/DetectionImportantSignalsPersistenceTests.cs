using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     CONTRACT: a detection row's <see cref="DashboardDetectionEvent.ImportantSignals"/> must
///     survive the FOSS SQLite persistence round-trip.
///     <para>
///         Root cause this pins: the live broadcast path builds the dict
///         (<c>DetectionBroadcastMiddleware.BuildImportantSignals</c>) and hands the SAME object
///         to <c>IDashboardEventStore.AddDetectionAsync</c> — but the <c>detections</c> table had
///         no <c>important_signals</c> column, <c>AddDetectionAsync</c> never listed it in the
///         INSERT, and <c>GetDetectionsAsync</c> never projected it. So every read surface that
///         sources a detection row from the store saw <c>ImportantSignals == null</c>:
///         the signature-detail "Detection Signals" categories panel, the verified-bot trust
///         triple, the protocol chip, the warm-up UA-family derivation
///         (<c>SignatureAggregateCache.WarmFromDetections</c>, whose own comment records
///         "Warmup events have empty ImportantSignals (… doesn't survive the persistence
///         round-trip)"), and the UA-version history query. The PostgreSQL store has persisted
///         <c>important_signals JSONB</c> all along — FOSS silently dropped it.
///     </para>
///     <para>
///         These tests are written against the REAL <see cref="SqliteDashboardEventStore"/>
///         (no recording double), so they fail before the column/INSERT/read exist and pass after.
///     </para>
/// </summary>
public sealed class DetectionImportantSignalsPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public DetectionImportantSignalsPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-signals-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }

    private SqliteDashboardEventStore NewStore(out string dashboardDbPath)
    {
        dashboardDbPath = Path.Combine(_tempDir, "dashboard.db");
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db")
        });
        return new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, options);
    }

    private static DashboardDetectionEvent Detection(Dictionary<string, object>? signals) => new()
    {
        RequestId = "req-" + Guid.NewGuid().ToString("N")[..8],
        Timestamp = DateTime.UtcNow,
        IsBot = false,
        BotProbability = 0.12,
        Confidence = 0.9,
        RiskBand = "Low",
        Method = "GET",
        Path = "/pricing",
        StatusCode = 200,
        PrimarySignature = "sig-" + Guid.NewGuid().ToString("N")[..12],
        ImportantSignals = signals,
    };

    [Fact]
    public async Task Detections_table_has_important_signals_column()
    {
        var store = NewStore(out var dashboardDbPath);

        // GetDetectionsAsync triggers EnsureInitializedAsync internally.
        await store.GetDetectionsAsync();

        await using var conn = new SqliteConnection($"Data Source={dashboardDbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(detections);";
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        columns.Should().Contain("important_signals",
            "the FOSS detection row must carry the per-request signal dict the broadcast path built");
    }

    [Fact]
    public async Task AddDetectionAsync_persists_important_signals_and_read_back_preserves_types()
    {
        var store = NewStore(out _);

        var signals = new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["ua.family_version"] = "124",
            ["tls.version"] = "TLSv1.3",
            ["headless.indicator"] = false,
            ["verifiedbot.checked"] = true,
            ["intent.threat_score"] = 0.42,
        };

        await store.AddDetectionAsync(Detection(signals));

        var readBack = await store.GetDetectionsAsync();
        readBack.Should().ContainSingle();

        var roundTripped = readBack[0].ImportantSignals;
        roundTripped.Should().NotBeNull(
            "a persisted detection row must read back with its signals, not null");
        roundTripped!.Should().ContainKeys(signals.Keys);

        // Type fidelity matters: downstream readers do `is bool b && b`
        // (verifiedbot.spoofed/confirmed) and `is string s`, so a JSON round-trip that
        // hands back JsonElement boxes silently breaks the verified-bot trust triple.
        roundTripped["ua.family"].Should().Be("Chrome");
        roundTripped["headless.indicator"].Should().Be(false);
        roundTripped["verifiedbot.checked"].Should().Be(true);
        Convert.ToDouble(roundTripped["intent.threat_score"]).Should().BeApproximately(0.42, 1e-9);
    }

    [Fact]
    public async Task Detection_with_no_signals_reads_back_null_not_empty_dict()
    {
        // Rows with genuinely nothing to say must not grow an empty JSON blob: the
        // read surfaces treat null and empty identically, and the column stays cheap.
        var store = NewStore(out _);
        await store.AddDetectionAsync(Detection(new Dictionary<string, object>()));

        var readBack = await store.GetDetectionsAsync();
        readBack.Should().ContainSingle();
        readBack[0].ImportantSignals.Should().BeNull();
    }

    [Fact]
    public async Task Blocked_PII_keys_never_reach_the_row_even_via_the_store()
    {
        // Defence in depth: the middleware filters at build time, but the store is the
        // durability boundary -- a hand-built event (or a future caller) must not be able
        // to persist PII into the dashboard DB through this path.
        var store = NewStore(out _);
        var detection = Detection(new Dictionary<string, object>
        {
            ["ua.family"] = "curl",
            ["ua.raw"] = "curl/8.4.0",
            ["pii.email"] = "someone@example.com",
            ["pii.country"] = "GB",
        });

        await store.AddDetectionAsync(detection);

        var readBack = await store.GetDetectionsAsync();
        readBack[0].ImportantSignals.Should().NotBeNull();
        readBack[0].ImportantSignals!.Keys.Should().Contain("ua.family");
        readBack[0].ImportantSignals!.Keys.Should().NotContain(new[] { "ua.raw", "pii.email", "pii.country" });
    }
}
