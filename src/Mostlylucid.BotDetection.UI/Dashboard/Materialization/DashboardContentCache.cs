using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.SlidingCache;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     Content cache over the canonical <see cref="SlidingCacheAtom{TKey,TValue}"/>.
///     The atom's factory IS the cache-first-read + cold-miss compose in one:
///     <c>GetOrComputeAsync</c> serves the materialized bundle or composes it once.
///     The tick materializer calls <see cref="GetAsync"/> to warm hot pages ahead of
///     reads; the request path calls the same to read. Importance-scored retention
///     keeps the current tick, ages out old snapshots, evicts cold envelopes.
/// </summary>
public sealed class DashboardContentCache : IDashboardContentCache, IAsyncDisposable
{
    private readonly SlidingCacheAtom<DashboardContentKey, DashboardPageResult> _atom;
    private readonly Func<long> _currentTick;
    private readonly DashboardMaterializerOptions _options;

    public DashboardContentCache(
        Func<DashboardPageManifest, DashboardPageWindow, CancellationToken, Task<DashboardPageResult>> compose,
        Func<long> currentTick,
        IOptions<DashboardMaterializerOptions> options,
        SignalSink? signals = null)
    {
        ArgumentNullException.ThrowIfNull(compose);
        ArgumentNullException.ThrowIfNull(currentTick);
        _currentTick = currentTick;
        _options = options.Value;

        var sink = signals ?? new SignalSink(
            Math.Max(2, _options.ContentCacheMaxEntries * 2),
            _options.ContentSlidingExpiration);

        _atom = new SlidingCacheAtom<DashboardContentKey, DashboardPageResult>(
            factory: (key, ct) => compose(key.Manifest, key.Window, ct),
            slidingExpiration: _options.ContentSlidingExpiration,
            absoluteExpiration: _options.ContentAbsoluteExpiration,
            maxSize: _options.ContentCacheMaxEntries,
            signals: sink,
            retentionScorer: ScoreRetention);
    }

    public Task<DashboardPageResult> GetAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct)
        => _atom.GetOrComputeAsync(new DashboardContentKey(manifest, window, tick), ct);

    public bool TryGet(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, out DashboardPageResult? result)
        => _atom.TryGet(new DashboardContentKey(manifest, window, tick), out result);

    /// <summary>
    ///     Importance retention (higher = keep longer): the current tick's bundle is
    ///     what clients are reading (max); recent ticks stay for previous-tick compare;
    ///     older snapshots score near zero and are evicted first under size pressure.
    /// </summary>
    private double ScoreRetention(DashboardContentKey key, DashboardPageResult _)
    {
        var age = _currentTick() - key.Tick;
        if (age <= 0) return 1.0;
        if (age <= _options.RetentionRecentTicks) return 0.5;
        return 0.05;
    }

    public ValueTask DisposeAsync() => _atom.DisposeAsync();
}
