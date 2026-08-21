using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Real-machine timing measurements for <see cref="EphemeralPatternReputationCache"/>'s
///     two background work bodies (DecayWork, PersistWork) -- gathered to choose data-driven
///     <c>maxBodyDuration</c> values for the mostlylucid.ephemeral 3.x adoption (the operator's
///     explicit instruction: "get the number from data rather than from [a guessed] sentence").
///     Not a strict pass/fail perf gate (wall-clock on a shared CI runner is not a reliable
///     SLO) -- the generous upper-bound assertions are a basic regression guard; the actual
///     measured numbers are what feed the maxBodyDuration decision, printed via ITestOutputHelper
///     so a real run's output is the evidence, not a comment guessing at it.
/// </summary>
public sealed class EphemeralPatternReputationCacheTimingTests : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-repcache-timing-{Guid.NewGuid():N}");
    private SqliteLearnedPatternStore? _store;

    public EphemeralPatternReputationCacheTimingTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_tempDir);
    }

    private (EphemeralPatternReputationCache Cache, SqliteLearnedPatternStore Store) NewCache(int maxPatterns)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db")
        });
        _store = new SqliteLearnedPatternStore(NullLogger<SqliteLearnedPatternStore>.Instance, options);
        var updater = new PatternReputationUpdater(NullLogger<PatternReputationUpdater>.Instance, options);
        var cache = new EphemeralPatternReputationCache(
            NullLogger<EphemeralPatternReputationCache>.Instance, updater, _store, maxPatterns: maxPatterns);
        return (cache, _store);
    }

    [Fact]
    public async Task DecaySweepAsync_timing_at_default_maxPatterns_scale()
    {
        const int maxPatterns = 10_000; // EphemeralPatternReputationCache's default cap
        var (cache, _) = NewCache(maxPatterns);

        for (var i = 0; i < maxPatterns; i++)
            cache.GetOrCreate($"pattern-{i}", "UserAgent", $"ua-pattern-{i}");

        var sw = Stopwatch.StartNew();
        await cache.DecaySweepAsync();
        sw.Stop();

        _output.WriteLine($"DecaySweepAsync over {maxPatterns} patterns: {sw.Elapsed.TotalMilliseconds:F1}ms");

        // Generous regression guard, not the SLO itself -- DecayWork is a pure in-memory
        // O(n) scan (ApplyTimeDecay per entry, no I/O), so it should be low-tens-of-ms even
        // at the full cap; 5s would mean something got structurally slower, not just noisy.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"DecaySweepAsync took {sw.Elapsed.TotalMilliseconds:F1}ms for {maxPatterns} patterns -- expected low-tens-of-ms for a pure in-memory scan.");

        await cache.DisposeAsync();
    }

    [Fact]
    public async Task GarbageCollectAsync_timing_at_default_maxPatterns_scale()
    {
        const int maxPatterns = 10_000;
        var (cache, _) = NewCache(maxPatterns);

        for (var i = 0; i < maxPatterns; i++)
            cache.GetOrCreate($"pattern-{i}", "UserAgent", $"ua-pattern-{i}");

        var sw = Stopwatch.StartNew();
        await cache.GarbageCollectAsync();
        sw.Stop();

        _output.WriteLine($"GarbageCollectAsync over {maxPatterns} patterns: {sw.Elapsed.TotalMilliseconds:F1}ms");

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"GarbageCollectAsync took {sw.Elapsed.TotalMilliseconds:F1}ms for {maxPatterns} patterns.");

        await cache.DisposeAsync();
    }

    [Fact]
    public async Task PersistAsync_timing_for_a_realistic_dirty_batch()
    {
        // PersistWork is the I/O-bound body: sequential SqliteLearnedPatternStore.UpsertAsync
        // calls (single-writer, by design, to avoid SQLite lock contention) against a REAL
        // temp SQLite file -- not a fake/no-op store -- so this measures genuine disk I/O on
        // this machine, not an idealized in-memory number.
        const int dirtyCount = 500; // a plausible single decay-sweep's worth of state changes
        var (cache, _) = NewCache(maxPatterns: 10_000);

        for (var i = 0; i < dirtyCount; i++)
        {
            var rep = new PatternReputation
            {
                PatternId = $"pattern-{i}",
                PatternType = "UserAgent",
                Pattern = $"ua-pattern-{i}",
                BotScore = 0.7,
                Support = 3,
                State = ReputationState.Suspect,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
                StateChangedAt = DateTimeOffset.UtcNow
            };
            cache.Update(rep); // marks IsDirty = true
        }

        var sw = Stopwatch.StartNew();
        await cache.PersistAsync();
        // PersistAsync only ENQUEUES the batch onto the single-writer coordinator's channel;
        // the actual sequential SQLite writes happen on its background thread. Poll the REAL
        // store (not an in-memory flag) until every dirty pattern round-trips, so the
        // measurement covers genuine disk I/O completion, not just the enqueue.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var persisted = 0;
        while (DateTime.UtcNow < deadline)
        {
            persisted = (await _store!.GetByConfidenceAsync(0.0)).Count;
            if (persisted >= dirtyCount) break;
            await Task.Delay(25);
        }
        sw.Stop();

        _output.WriteLine($"PersistAsync (enqueue + real SQLite drain) for {dirtyCount} dirty patterns: {sw.Elapsed.TotalMilliseconds:F1}ms ({persisted}/{dirtyCount} rows confirmed persisted)");
        Assert.Equal(dirtyCount, persisted);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"PersistAsync path took {sw.Elapsed.TotalMilliseconds:F1}ms for {dirtyCount} patterns.");

        await cache.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
