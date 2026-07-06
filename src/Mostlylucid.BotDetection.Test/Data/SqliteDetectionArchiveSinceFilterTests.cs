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

    private static PersistedSession MakeSession(
        string signature,
        DateTime endedAt,
        bool isBot = false) => new()
    {
        Signature = signature,
        StartedAt = endedAt.AddMinutes(-5),
        EndedAt = endedAt,
        RequestCount = 3,
        Vector = new byte[516],
        Maturity = 0.5f,
        DominantState = "PageView",
        IsBot = isBot,
        AvgBotProbability = isBot ? 0.9 : 0.1,
        AvgConfidence = 0.7,
        RiskBand = isBot ? "High" : "Low",
    };

    // ── Baseline: no since filter ────────────────────────────────────────────

    [Fact]
    public async Task Returns_all_sessions_when_since_is_null()
    {
        var now = DateTime.UtcNow;
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("sig-a", now.AddDays(-10)));
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("sig-b", now.AddDays(-1)));
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("sig-c", now));

        var results = await _store.GetRecentSessionsAsync(limit: 50, since: null);

        results.Should().HaveCount(3);
    }

    // ── Since filter: basic exclusion ────────────────────────────────────────

    [Fact]
    public async Task Excludes_sessions_that_ended_before_since_cutoff()
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-7);

        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("old", now.AddDays(-8)));    // before cutoff
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("recent", now.AddDays(-1))); // after cutoff

        var results = await _store.GetRecentSessionsAsync(limit: 50, since: cutoff);

        results.Should().ContainSingle();
        results[0].Signature.Should().Be("recent");
    }

    [Fact]
    public async Task Returns_empty_when_all_sessions_are_older_than_cutoff()
    {
        var now = DateTime.UtcNow;
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("old-a", now.AddDays(-10)));
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("old-b", now.AddDays(-15)));

        var results = await _store.GetRecentSessionsAsync(since: now.AddDays(-7));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Includes_session_at_exact_since_boundary()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("boundary", cutoff));

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

        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("bot-old",    now.AddDays(-10), isBot: true));
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("bot-recent", now.AddDays(-1),  isBot: true));
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("human-recent", now,            isBot: false));

        var results = await _store.GetRecentSessionsAsync(isBot: true, since: cutoff);

        results.Should().ContainSingle();
        results[0].Signature.Should().Be("bot-recent");
    }

    [Fact]
    public async Task IsBot_false_filter_with_since_excludes_old_human_sessions()
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-3);

        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("human-old",    now.AddDays(-5), isBot: false));
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("human-recent", now.AddDays(-1), isBot: false));
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("bot-recent",   now,             isBot: true));

        var results = await _store.GetRecentSessionsAsync(isBot: false, since: cutoff);

        results.Should().ContainSingle();
        results[0].Signature.Should().Be("human-recent");
    }

    // ── Ordering preserved ───────────────────────────────────────────────────

    [Fact]
    public async Task Results_are_ordered_by_ended_at_descending()
    {
        var now = DateTime.UtcNow;
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("oldest", now.AddHours(-3)));
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("newest", now));
        await _store.AddSessionAsync(RequestScope.Unknown, MakeSession("middle", now.AddHours(-1)));

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
            await _store.AddSessionAsync(RequestScope.Unknown, MakeSession($"sig-{i}", now.AddMinutes(-i)));

        var results = await _store.GetRecentSessionsAsync(limit: 3, since: now.AddDays(-1));

        results.Should().HaveCount(3);
    }

    // ── Multi-domain: scope is persisted + hydrated ──────────────────────────

    [Fact]
    public async Task AddSessionAsync_persists_scope_and_read_path_hydrates_it()
    {
        var scope = new RequestScope("acme.com", "www.acme.com");

        await _store.AddSessionAsync(scope, MakeSession("sig-acme", DateTime.UtcNow));

        var results = await _store.GetRecentSessionsAsync();

        results.Should().ContainSingle();
        results[0].Domain.Should().Be("acme.com");
        results[0].Host.Should().Be("www.acme.com");
    }
}