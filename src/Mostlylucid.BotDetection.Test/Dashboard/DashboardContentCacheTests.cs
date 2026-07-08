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
}
