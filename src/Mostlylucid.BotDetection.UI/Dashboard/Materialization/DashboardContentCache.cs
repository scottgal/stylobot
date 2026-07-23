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
    private readonly IOptionsMonitor<DashboardMaterializerOptions> _optionsMonitor;

    // Live-reads through the monitor (not a snapshot captured once at construction) so an
    // operator's config change -- e.g. ComputeOnColdMiss as a request-thread stabiliser --
    // takes effect on /admin/reload without a process restart. The cache atom's OWN
    // construction params (sliding/absolute expiration, max size) are still baked in once
    // below; only the per-call reads (ComputeOnColdMiss, LiveEnvelopeMaxAgeTicks,
    // RetentionRecentTicks) benefit from live reload.
    private DashboardMaterializerOptions _options => _optionsMonitor.CurrentValue;

    // Live-envelope registry: the (manifest, window) pairs read recently enough that
    // the materializer should keep warming them. Keyed by envelope so re-reads of the
    // same view refresh the last-seen tick. Age-pruned in LiveEnvelopes().
    private readonly ConcurrentDictionary<DashboardContentEnvelope, LiveEntry> _live = new();

    private sealed record LiveEntry(DashboardPageManifest Manifest, DashboardPageWindow Window, long LastSeenTick);

    // Envelope -> the most recent tick that was warmed (by the materializer or a read).
    // Reads resolve to THIS tick, not strictly the current tick — so a read never cold-
    // misses just because the materializer hasn't composed this exact tick yet; it falls
    // back to the previous warmed tick, which is still in the atom. The tick is the version;
    // the envelope is the key (operator's model). This is what keeps reads reliably warm.
    private readonly ConcurrentDictionary<DashboardContentEnvelope, long> _latestWarmTick = new();

    private void RecordWarm(DashboardContentEnvelope env, long tick) =>
        _latestWarmTick.AddOrUpdate(env, tick, (_, old) => Math.Max(old, tick));

    public DashboardContentCache(
        Func<DashboardPageManifest, DashboardPageWindow, CancellationToken, Task<DashboardPageResult>> compose,
        Func<long> currentTick,
        IOptionsMonitor<DashboardMaterializerOptions> options,
        SignalSink? signals = null)
    {
        ArgumentNullException.ThrowIfNull(compose);
        ArgumentNullException.ThrowIfNull(currentTick);
        ArgumentNullException.ThrowIfNull(options);
        _currentTick = currentTick;
        _optionsMonitor = options;

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
        // Mark this view live so the materializer keeps it warm at future ticks, and record
        // that this tick is warm so GetCurrentAsync can resolve to it.
        _live[key.Envelope] = new LiveEntry(manifest, window, _currentTick());
        RecordWarm(key.Envelope, tick);

        // ComputeOnColdMiss=false is the request-path safety valve: an operator can stop
        // request threads from ever computing a (potentially expensive, e.g. compose-batch)
        // read themselves, leaving that work to the tick materializer only. Only gates a
        // genuine miss -- an entry the materializer already warmed is still served normally.
        if (!_options.ComputeOnColdMiss)
            return Task.FromResult(_atom.TryGet(key, out var existing) ? existing! : EmptyResult);

        return _atom.GetOrComputeAsync(key, ct);
    }

    private static readonly DashboardPageResult EmptyResult =
        new(new Models.DashboardDatasetBundle(null, null, null, null, null));

    public Task<DashboardPageResult> GetCurrentAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, CancellationToken ct)
    {
        // Resolve to the LATEST warmed tick for this envelope (current or a recent previous),
        // NOT strictly the current tick — so the read hits the materializer's most recent warm
        // instead of cold-missing a not-yet-composed current tick. Only a brand-new envelope
        // (never warmed) composes once, at the current tick.
        var env = DashboardContentEnvelope.From(manifest, window);
        var readTick = _latestWarmTick.TryGetValue(env, out var warmed) ? warmed : _currentTick();
        return GetAsync(manifest, window, readTick, ct);
    }

    public Task<DashboardPageResult> WarmAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct)
    {
        // Materializer warm: compose (env, tick) and record it as the latest warm — but do
        // NOT touch liveness (the materializer's own warm must not keep an envelope alive).
        var key = new DashboardContentKey(manifest, window, tick);
        RecordWarm(key.Envelope, tick);
        return _atom.GetOrComputeAsync(key, ct);
    }

    public bool TryGet(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, out DashboardPageResult? result)
        => _atom.TryGet(new DashboardContentKey(manifest, window, tick), out result);

    public IReadOnlyCollection<(DashboardPageManifest Manifest, DashboardPageWindow Window, int AccessCount, DateTimeOffset LastAccess)> LiveEnvelopes()
    {
        var current = _currentTick();
        var maxAge = _options.LiveEnvelopeMaxAgeTicks;
        var live = new List<(DashboardPageManifest, DashboardPageWindow, int, DateTimeOffset)>();
        foreach (var kvp in _live)
        {
            if (current - kvp.Value.LastSeenTick > maxAge)
            {
                _live.TryRemove(kvp.Key, out _); // aged out — stop warming it
                continue;
            }

            // Hotness is computed AT READ TIME from the atom's own tracking for this envelope's
            // latest known warm tick -- never a value stored alongside _live itself.
            var latestTick = _latestWarmTick.TryGetValue(kvp.Key, out var t) ? t : kvp.Value.LastSeenTick;
            var statsKey = new DashboardContentKey(kvp.Value.Manifest, kvp.Value.Window, latestTick);
            var (accessCount, lastAccess) = _atom.TryGetEntryStats(statsKey, out var stats)
                ? (stats.AccessCount, stats.LastAccess)
                : (0, DateTimeOffset.MinValue);

            live.Add((kvp.Value.Manifest, kvp.Value.Window, accessCount, lastAccess));
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
