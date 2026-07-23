using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Test.Helpers;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Task 2 of the out-of-request materialization plan: the content cache wraps
///     the canonical <c>SlidingCacheAtom</c>. The key carries the compute inputs
///     (manifest+window) so the atom's factory can compose on a cold miss, but
///     cache identity is the (envelope, tick) — so the request-path read and the
///     tick materializer that share the same (manifest, window, tick) hit ONE
///     entry, and the compose runs at most once per (envelope, tick).
/// </summary>
public sealed class DashboardContentCacheTests
{
    private static readonly DashboardPageManifest Traffic =
        new("dashboard.traffic", new[] { "summary", "top-bots" });

    private static DashboardPageWindow Window(DateTime? start = null, string audience = "all")
        => new(start, null, audience, null, null, 500, 60);

    private static DashboardPageResult Result()
        => new(new DashboardDatasetBundle(null, null, null, null, null));

    private static DashboardContentCache NewCache(
        Func<DashboardPageManifest, DashboardPageWindow, CancellationToken, Task<DashboardPageResult>> compose,
        long currentTick = 5)
        => new(compose, () => currentTick, Options.Create(new DashboardMaterializerOptions()));

    [Fact]
    public async Task WarmAsync_composes_once_then_GetAsync_serves_cached_without_composing_again()
    {
        // Structural fix (measure-gate incident, §8): the request path (GetAsync/GetCurrentAsync)
        // NEVER composes -- only WarmAsync (the tick materializer, off the request thread) does.
        var composes = 0;
        await using var cache = NewCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, currentTick: 5);

        var warmed = await cache.WarmAsync(Traffic, Window(), 5, default);
        var read = await cache.GetAsync(Traffic, Window(), 5, default);

        Assert.Equal(1, composes);
        Assert.Same(warmed, read);
    }

    [Fact]
    public async Task WarmAsync_recomputes_for_a_new_tick()
    {
        var composes = 0;
        await using var cache = NewCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, currentTick: 6);

        await cache.WarmAsync(Traffic, Window(), 5, default);
        await cache.WarmAsync(Traffic, Window(), 6, default);

        Assert.Equal(2, composes); // distinct ticks -> distinct keys
    }

    [Fact]
    public async Task WarmAsync_collapses_sub_bucket_windows_to_one_entry()
    {
        var composes = 0;
        await using var cache = NewCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, currentTick: 5);

        // same 60-minute bucket, 50 seconds apart -> same envelope -> one compute
        await cache.WarmAsync(Traffic, Window(start: new DateTime(2026, 7, 8, 12, 0, 5, DateTimeKind.Utc)), 5, default);
        await cache.WarmAsync(Traffic, Window(start: new DateTime(2026, 7, 8, 12, 0, 55, DateTimeKind.Utc)), 5, default);

        Assert.Equal(1, composes);
    }

    [Fact]
    public async Task Different_audience_is_a_distinct_entry()
    {
        var composes = 0;
        await using var cache = NewCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, currentTick: 5);

        await cache.WarmAsync(Traffic, Window(audience: "all"), 5, default);
        await cache.WarmAsync(Traffic, Window(audience: "bots"), 5, default);

        Assert.Equal(2, composes);
    }

    [Fact]
    public async Task User_read_records_a_live_envelope_but_a_warm_does_not()
    {
        long tick = 1;
        await using var cache = new DashboardContentCache((_, _, _) => Task.FromResult(Result()), () => tick,
            Options.Create(new DashboardMaterializerOptions()));

        await cache.WarmAsync(Traffic, Window(), 1, default);
        Assert.Empty(cache.LiveEnvelopes()); // a warm must not keep an envelope alive

        await cache.GetAsync(Traffic, Window(), 1, default);
        Assert.Single(cache.LiveEnvelopes()); // a genuine user read does
    }

    [Fact]
    public async Task LiveEnvelopes_reports_AccessCount_sourced_from_the_atoms_own_tracking()
    {
        // §7 Tier 2 (materializer priority ranking, single-source-corrected): AccessCount/
        // LastAccess must be read straight off SlidingCacheAtom's existing per-key tracking
        // via TryGetEntryStats, not a second counter -- so repeated reads of the SAME
        // (envelope, tick) key should show up here with no extra bookkeeping in the cache itself.
        long tick = 1;
        await using var cache = new DashboardContentCache((_, _, _) => Task.FromResult(Result()), () => tick,
            Options.Create(new DashboardMaterializerOptions()));

        await cache.WarmAsync(Traffic, Window(), 1, default); // materializer creates the atom entry (AccessCount 1)
        await cache.GetAsync(Traffic, Window(), 1, default);  // request-path hit (AccessCount 2)
        await cache.GetAsync(Traffic, Window(), 1, default);  // request-path hit (AccessCount 3)

        var live = Assert.Single(cache.LiveEnvelopes());
        Assert.Equal(3, live.AccessCount);
    }

    [Fact]
    public async Task LiveEnvelopes_ages_out_views_beyond_max_age()
    {
        long tick = 1;
        await using var cache = new DashboardContentCache((_, _, _) => Task.FromResult(Result()), () => tick,
            Options.Create(new DashboardMaterializerOptions { LiveEnvelopeMaxAgeTicks = 3 }));

        await cache.GetAsync(Traffic, Window(), 1, default);

        tick = 1 + 3;                          // exactly at the age boundary -> still live
        Assert.Single(cache.LiveEnvelopes());

        tick = 1 + 4;                          // beyond max age -> pruned
        Assert.Empty(cache.LiveEnvelopes());
    }

    [Fact]
    public async Task GetCurrentAsync_hits_the_last_warm_across_a_tick_advance()
    {
        var composes = 0;
        long tick = 5;
        await using var cache = new DashboardContentCache(
            (_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));

        await cache.WarmAsync(Traffic, Window(), 5, default); // materializer warms tick 5
        Assert.Equal(1, composes);

        tick = 6; // tick advanced; nothing warmed tick 6 yet
        await cache.GetCurrentAsync(Traffic, Window(), default); // resolves to warm tick 5 -> HIT, no compose
        Assert.Equal(1, composes);
    }

    [Fact]
    public async Task GetAsync_returns_a_warming_placeholder_without_composing_on_a_genuine_cold_miss()
    {
        // §8 Fix 1 (measure-gate incident): structural, not a config valve -- the request path
        // NEVER computes on a cold miss, unconditionally. No option can turn this back on.
        var composes = 0;
        await using var cache = new DashboardContentCache(
            (_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => 5L, Options.Create(new DashboardMaterializerOptions()));

        var result = await cache.GetAsync(Traffic, Window(), 5, default);

        Assert.Equal(0, composes);
        Assert.True(result.IsWarming);
    }

    [Fact]
    public async Task GetAsync_serves_an_already_warmed_entry_without_composing_again()
    {
        var composes = 0;
        long tick = 5;
        await using var cache = new DashboardContentCache(
            (_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));

        var warmed = await cache.WarmAsync(Traffic, Window(), 5, default); // materializer warms it first
        var result = await cache.GetAsync(Traffic, Window(), 5, default); // request-path read hits the warm entry

        Assert.Equal(1, composes); // only the warm computed, not the read
        Assert.Same(warmed, result);
        Assert.False(result.IsWarming);
    }

    [Fact]
    public async Task GetCurrentAsync_serves_a_stale_but_present_snapshot_instead_of_a_placeholder()
    {
        // The heart of §8 Fix 1: a request landing while the materializer hasn't yet caught up
        // to the CURRENT tick must still get the last REAL snapshot (stale, but genuinely useful),
        // not a blank/placeholder -- GetCurrentAsync already resolves to the latest warmed tick.
        var composes = 0;
        long tick = 5;
        await using var cache = new DashboardContentCache(
            (_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));

        await cache.WarmAsync(Traffic, Window(), 5, default); // last real snapshot is at tick 5

        tick = 50; // materializer is many ticks behind (contention, slow compose, whatever)
        var result = await cache.GetCurrentAsync(Traffic, Window(), default);

        Assert.Equal(1, composes); // GetCurrentAsync never triggers a second compose
        Assert.False(result.IsWarming); // stale-but-real data, not a placeholder
    }

    [Fact]
    public async Task Concurrent_cold_reads_never_compose_even_under_load()
    {
        // Directly matches the §8 Fix 1 requirement: assert cold-miss never calls the sync
        // compose, even under concurrent request pressure against a never-warmed envelope.
        var composes = 0;
        await using var cache = new DashboardContentCache(
            (_, _, _) => { Interlocked.Increment(ref composes); return Task.FromResult(Result()); },
            () => 5L, Options.Create(new DashboardMaterializerOptions()));

        var reads = Enumerable.Range(0, 50)
            .Select(_ => cache.GetCurrentAsync(Traffic, Window(), default));
        var results = await Task.WhenAll(reads);

        Assert.Equal(0, Volatile.Read(ref composes));
        Assert.All(results, r => Assert.True(r.IsWarming));
    }

    [Fact]
    public async Task GetCurrentAsync_hits_a_materializer_warm_at_a_later_current_tick()
    {
        var composes = 0;
        long tick = 5;
        await using var cache = new DashboardContentCache(
            (_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));

        await cache.WarmAsync(Traffic, Window(), 5, default); // materializer warms env at tick 5
        Assert.Equal(1, composes);

        tick = 7; // current tick is now ahead of the warm
        await cache.GetCurrentAsync(Traffic, Window(), default); // resolves to warm tick 5 -> HIT
        Assert.Equal(1, composes);
    }
}
