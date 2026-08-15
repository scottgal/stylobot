using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Tests for the <c>since</c> parameter on
///     <see cref="IDetectionArchive.GetRecentSessionsAsync"/>.
///     Sessions older than the <c>since</c> cutoff must be excluded so
///     that the dashboard never displays sessions whose detection events
///     have aged out of the seven-day retention window (which would
///     produce a 404 on the signature detail page).
/// </summary>
public sealed class SqliteDetectionArchiveSinceFilterTests : IAsyncLifetime
{
    private SqliteDetectionArchive _store = null!;
    private string _dbDir = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"since-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var dbFilePath = Path.Combine(_dbDir, "botdetection.db");
        var opts = Options.Create(new BotDetectionOptions { DatabasePath = dbFilePath });
        _store = new SqliteDetectionArchive(NullLogger<SqliteDetectionArchive>.Instance, opts);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    ///     Seed a legacy sessions row directly (Phase B of the write-path grain
    ///     redesign: <c>AddSessionAsync</c> now FOLDS the session summary into the
    ///     window aggregates instead of writing a sessions row — the dashboard reads
    ///     re-pointed at the folds. The archive's sessions read surface remains for
    ///     legacy rows, so these tests seed the table directly to pin its semantics).
    /// </summary>
    private async Task SeedSessionAsync(
        string signature, DateTime endedAt, bool isBot = false, RequestScope? scope = null)
    {
        var now = endedAt;
        var started = now.AddMinutes(-5);
        await using var conn = new SqliteConnection(StoreConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (
                domain, host, signature, started_at, ended_at, request_count, vector, maturity,
                dominant_state, is_bot, avg_bot_probability, avg_confidence, risk_band,
                action, bot_name, bot_type, country_code, top_reasons_json,
                transition_counts_json, paths_json, avg_processing_time_ms,
                error_count, timing_entropy, narrative,
                header_hashes_json, user_agent_raw,
                frequency_fingerprint, drift_vector
            ) VALUES (
                @domain, @host, @sig, @started, @ended, 3, @vector, 0.5,
                'PageView', @isBot, @prob, 0.7, @risk,
                NULL, NULL, NULL, NULL, NULL,
                NULL, NULL, 0,
                0, 0, NULL,
                NULL, NULL,
                NULL, NULL
            )
            """;
        cmd.Parameters.AddWithValue("@domain", (object?)(scope?.Domain) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@host", (object?)(scope?.Host) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sig", signature);
        cmd.Parameters.AddWithValue("@started", started.ToString("O"));
        cmd.Parameters.AddWithValue("@ended", now.ToString("O"));
        cmd.Parameters.AddWithValue("@vector", new byte[516]);
        cmd.Parameters.AddWithValue("@isBot", isBot ? 1 : 0);
        cmd.Parameters.AddWithValue("@prob", isBot ? 0.9 : 0.1);
        cmd.Parameters.AddWithValue("@risk", isBot ? "High" : "Low");
        await cmd.ExecuteNonQueryAsync();
    }

    private string StoreConnectionString
        => _store.PersistenceConnectionString!;

    // ── Baseline: no since filter ────────────────────────────────────────────

    [Fact]
    public async Task Returns_all_sessions_when_since_is_null()
    {
        var now = DateTime.UtcNow;
        await SeedSessionAsync("sig-a", now.AddDays(-10));
        await SeedSessionAsync("sig-b", now.AddDays(-1));
        await SeedSessionAsync("sig-c", now);

        var results = await _store.GetRecentSessionsAsync(limit: 50, since: null);

        results.Should().HaveCount(3);
    }

    // ── Since filter: basic exclusion ────────────────────────────────────────

    [Fact]
    public async Task Excludes_sessions_that_ended_before_since_cutoff()
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-7);

        await SeedSessionAsync("old", now.AddDays(-8));    // before cutoff
        await SeedSessionAsync("recent", now.AddDays(-1)); // after cutoff

        var results = await _store.GetRecentSessionsAsync(limit: 50, since: cutoff);

        results.Should().ContainSingle();
        results[0].Signature.Should().Be("recent");
    }

    [Fact]
    public async Task Returns_empty_when_all_sessions_are_older_than_cutoff()
    {
        var now = DateTime.UtcNow;
        await SeedSessionAsync("old-a", now.AddDays(-10));
        await SeedSessionAsync("old-b", now.AddDays(-15));

        var results = await _store.GetRecentSessionsAsync(since: now.AddDays(-7));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Includes_session_at_exact_since_boundary()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        await SeedSessionAsync("boundary", cutoff);

        var results = await _store.GetRecentSessionsAsync(since: cutoff);

        // ended_at >= @since, so the exact boundary session is included
        results.Should().ContainSingle();
        results[0].Signature.Should().Be("boundary");
    }

    // ── Since + isBot combination ────────────────────────────────────────────

    [Fact]
    public async Task Combines_since_with_isBot_filter()
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-7);

        await SeedSessionAsync("bot-old",    now.AddDays(-10), isBot: true);
        await SeedSessionAsync("bot-recent", now.AddDays(-1),  isBot: true);
        await SeedSessionAsync("human-recent", now,            isBot: false);

        var results = await _store.GetRecentSessionsAsync(isBot: true, since: cutoff);

        results.Should().ContainSingle();
        results[0].Signature.Should().Be("bot-recent");
    }

    [Fact]
    public async Task IsBot_false_filter_with_since_excludes_old_human_sessions()
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-3);

        await SeedSessionAsync("human-old",    now.AddDays(-5), isBot: false);
        await SeedSessionAsync("human-recent", now.AddDays(-1), isBot: false);
        await SeedSessionAsync("bot-recent",   now,             isBot: true);

        var results = await _store.GetRecentSessionsAsync(isBot: false, since: cutoff);

        results.Should().ContainSingle();
        results[0].Signature.Should().Be("human-recent");
    }

    // ── Ordering preserved ───────────────────────────────────────────────────

    [Fact]
    public async Task Results_are_ordered_by_ended_at_descending()
    {
        var now = DateTime.UtcNow;
        await SeedSessionAsync("oldest", now.AddHours(-3));
        await SeedSessionAsync("newest", now);
        await SeedSessionAsync("middle", now.AddHours(-1));

        var results = await _store.GetRecentSessionsAsync(since: now.AddDays(-1));

        results.Select(s => s.Signature)
               .Should().ContainInOrder("newest", "middle", "oldest");
    }

    // ── Limit still applies ──────────────────────────────────────────────────

    [Fact]
    public async Task Limit_is_respected_after_applying_since_filter()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            await SeedSessionAsync($"sig-{i}", now.AddMinutes(-i));

        var results = await _store.GetRecentSessionsAsync(limit: 3, since: now.AddDays(-1));

        results.Should().HaveCount(3);
    }

    // ── Multi-domain: scope is persisted + hydrated ──────────────────────────

    [Fact]
    public async Task LegacyRow_scope_is_persisted_and_read_path_hydrates_it()
    {
        var scope = new RequestScope("acme.com", "www.acme.com");

        await SeedSessionAsync("sig-acme", DateTime.UtcNow, scope: scope);

        var results = await _store.GetRecentSessionsAsync();

        results.Should().ContainSingle();
        results[0].Domain.Should().Be("acme.com");
        results[0].Host.Should().Be("www.acme.com");
    }
}