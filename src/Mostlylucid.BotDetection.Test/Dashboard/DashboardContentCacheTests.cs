using Microsoft.Extensions.Options;
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
    public async Task GetAsync_composes_once_then_serves_cached()
    {
        var composes = 0;
        await using var cache = NewCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, currentTick: 5);

        var r1 = await cache.GetAsync(Traffic, Window(), 5, default);
        var r2 = await cache.GetAsync(Traffic, Window(), 5, default);

        Assert.Equal(1, composes);
        Assert.Same(r1, r2);
    }

    [Fact]
    public async Task GetAsync_recomputes_for_a_new_tick()
    {
        var composes = 0;
        await using var cache = NewCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, currentTick: 6);

        await cache.GetAsync(Traffic, Window(), 5, default);
        await cache.GetAsync(Traffic, Window(), 6, default);

        Assert.Equal(2, composes); // distinct ticks -> distinct keys
    }

    [Fact]
    public async Task GetAsync_collapses_sub_bucket_windows_to_one_entry()
    {
        var composes = 0;
        await using var cache = NewCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, currentTick: 5);

        // same 60-minute bucket, 50 seconds apart -> same envelope -> one compute
        await cache.GetAsync(Traffic, Window(start: new DateTime(2026, 7, 8, 12, 0, 5, DateTimeKind.Utc)), 5, default);
        await cache.GetAsync(Traffic, Window(start: new DateTime(2026, 7, 8, 12, 0, 55, DateTimeKind.Utc)), 5, default);

        Assert.Equal(1, composes);
    }

    [Fact]
    public async Task Different_audience_is_a_distinct_entry()
    {
        var composes = 0;
        await using var cache = NewCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, currentTick: 5);

        await cache.GetAsync(Traffic, Window(audience: "all"), 5, default);
        await cache.GetAsync(Traffic, Window(audience: "bots"), 5, default);

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

        await cache.GetCurrentAsync(Traffic, Window(), default); // composes at tick 5, warm=5
        Assert.Equal(1, composes);

        tick = 6; // tick advanced; nothing warmed tick 6 yet
        await cache.GetCurrentAsync(Traffic, Window(), default); // resolves to warm tick 5 -> HIT
        Assert.Equal(1, composes); // the fix: was 2 (a cold miss per tick) before the re-key
    }

    [Fact]
    public async Task GetAsync_returns_empty_without_composing_on_a_cold_miss_when_ComputeOnColdMiss_is_false()
    {
        // Cleanup while touching this subsystem for the compose-batch-overload incident:
        // ComputeOnColdMiss was declared + documented but never actually read anywhere,
        // so setting it to false had zero effect -- every request-path cold miss always
        // computed synchronously regardless. Wiring it up gives operators a real "never
        // let a request thread compute compose-batch" safety valve for a future incident.
        var composes = 0;
        await using var cache = new DashboardContentCache(
            (_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => 5L, Options.Create(new DashboardMaterializerOptions { ComputeOnColdMiss = false }));

        var result = await cache.GetAsync(Traffic, Window(), 5, default);

        Assert.Equal(0, composes);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task GetAsync_still_serves_an_already_warmed_entry_when_ComputeOnColdMiss_is_false()
    {
        // The gate is only for genuine cold misses -- an entry the materializer already
        // warmed must still be served, not blanked out.
        var composes = 0;
        long tick = 5;
        await using var cache = new DashboardContentCache(
            (_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions { ComputeOnColdMiss = false }));

        var warmed = await cache.WarmAsync(Traffic, Window(), 5, default); // materializer warms it first
        var result = await cache.GetAsync(Traffic, Window(), 5, default); // request-path read hits the warm entry

        Assert.Equal(1, composes); // only the warm computed, not the read
        Assert.Same(warmed, result);
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
