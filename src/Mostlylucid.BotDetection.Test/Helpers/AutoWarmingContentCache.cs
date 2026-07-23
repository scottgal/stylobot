using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;

namespace Mostlylucid.BotDetection.Test.Helpers;

/// <summary>
///     Test-only decorator that simulates "the tick materializer already warmed this
///     envelope" ahead of a <see cref="GetCurrentAsync"/> read, for controller-level tests
///     that assert on COMPOSITION/BATCHING correctness (does the controller issue one
///     ComposeBatchAsync, not N per-widget calls) rather than cold-miss semantics.
///     <see cref="Mostlylucid.BotDetection.Test.Dashboard.DashboardContentCacheTests"/> owns
///     the cold-miss/placeholder contract itself. Production code never does this --
///     <c>DashboardContentCache.GetCurrentAsync</c> never computes on the request thread
///     (structural, §8 of the compose-batch-overload review) -- this decorator exists only
///     so pre-existing controller tests don't need to reconstruct the controller's internal
///     window-building logic just to pre-warm the exact right key.
/// </summary>
public sealed class AutoWarmingContentCache : IDashboardContentCache
{
    private readonly DashboardContentCache _inner;
    private readonly Func<long> _currentTick;

    public AutoWarmingContentCache(DashboardContentCache inner, Func<long> currentTick)
    {
        _inner = inner;
        _currentTick = currentTick;
    }

    public Task<DashboardPageResult> GetAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct)
        => _inner.GetAsync(manifest, window, tick, ct);

    public async Task<DashboardPageResult> GetCurrentAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, CancellationToken ct)
    {
        await _inner.WarmAsync(manifest, window, _currentTick(), ct).ConfigureAwait(false);
        return await _inner.GetCurrentAsync(manifest, window, ct).ConfigureAwait(false);
    }

    public Task<DashboardPageResult> WarmAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct)
        => _inner.WarmAsync(manifest, window, tick, ct);

    public bool TryGet(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, out DashboardPageResult? result)
        => _inner.TryGet(manifest, window, tick, out result);

    public IReadOnlyCollection<(DashboardPageManifest Manifest, DashboardPageWindow Window, int AccessCount, DateTimeOffset LastAccess)> LiveEnvelopes()
        => _inner.LiveEnvelopes();
}
