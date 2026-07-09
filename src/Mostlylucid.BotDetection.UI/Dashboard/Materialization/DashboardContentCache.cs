using System.Collections.Concurrent;
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

    // Live-envelope registry: the (manifest, window) pairs read recently enough that
    // the materializer should keep warming them. Keyed by envelope so re-reads of the
    // same view refresh the last-seen tick. Age-pruned in LiveEnvelopes().
    private readonly ConcurrentDictionary<DashboardContentEnvelope, LiveEntry> _live = new();

    private sealed record LiveEntry(DashboardPageManifest Manifest, DashboardPageWindow Window, long LastSeenTick);

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
    {
        var key = new DashboardContentKey(manifest, window, tick);
        // Mark this view live so the materializer keeps it warm at future ticks.
        _live[key.Envelope] = new LiveEntry(manifest, window, _currentTick());
        return _atom.GetOrComputeAsync(key, ct);
    }

    public Task<DashboardPageResult> GetCurrentAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, CancellationToken ct)
        => GetAsync(manifest, window, _currentTick(), ct);

    public Task<DashboardPageResult> WarmAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct)
        // No liveness record: the materializer's own warm must not keep an envelope alive.
        => _atom.GetOrComputeAsync(new DashboardContentKey(manifest, window, tick), ct);

    public bool TryGet(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, out DashboardPageResult? result)
        => _atom.TryGet(new DashboardContentKey(manifest, window, tick), out result);

    public IReadOnlyCollection<(DashboardPageManifest Manifest, DashboardPageWindow Window)> LiveEnvelopes()
    {
        var current = _currentTick();
        var maxAge = _options.LiveEnvelopeMaxAgeTicks;
        var live = new List<(DashboardPageManifest, DashboardPageWindow)>();
        foreach (var kvp in _live)
        {
            if (current - kvp.Value.LastSeenTick > maxAge)
            {
                _live.TryRemove(kvp.Key, out _); // aged out — stop warming it
                continue;
            }
            live.Add((kvp.Value.Manifest, kvp.Value.Window));
        }
        return live;
    }

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
