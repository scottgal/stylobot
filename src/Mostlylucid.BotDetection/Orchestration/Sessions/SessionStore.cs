using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Shared per-domain session store. Holds one
///     <see cref="SessionAggregate"/> entry per fingerprint currently
///     active in a session window, partitioned internally by
///     <c>SiteProfile.Id</c> so tenant isolation is preserved. Shaped
///     eviction (see <see cref="SessionAggregate.RetentionPriority"/>)
///     ranks the "still learning" identities above the "already classified"
///     ones under pressure. Adaptive TTL shortens the sliding window as
///     headroom drops.
/// </summary>
/// <remarks>
///     <para>
///         <b>Not a cache</b> in the memoize sense -- there is no factory,
///         no "compute on miss" (see <c>feedback_no_caches_freshness_over_locality</c>).
///         It is a bounded, priority-shaped registry of live per-fingerprint
///         aggregates whose retention is behaviourally scored. Writes are
///         upserts. Reads are direct lookups or filtered senses on
///         <see cref="Changes"/>.
///     </para>
///     <para>
///         <b>Change notifications</b> -- every upsert also raises the new
///         aggregate on <see cref="Changes"/>, a typed signal sink. The
///         session atom subscribes there (TypedSignalRaised) to detect
///         aggregate mutations that shift the fingerprint; downstream
///         persistence emits when the shift crosses the persisted
///         fingerprint's cached-band threshold.
///     </para>
///     <para>
///         <b>Concurrency</b> -- per-site partitions are
///         <see cref="ConcurrentDictionary{TKey,TValue}"/>; upsert races
///         resolve via <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey, TValue, Func{TKey, TValue, TValue})"/>.
///         Aggregate merge is delegated to the caller so different
///         escalators can define their own merge policies (default merge
///         provided as a static helper).
///     </para>
/// </remarks>
public sealed class SessionStore : IDisposable
{
    private readonly SessionStoreOptions _options;
    private readonly ILogger<SessionStore> _logger;
    private readonly ConcurrentDictionary<string, SitePartition> _sites = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _cleanupLoop;
    private readonly int _maxAggregatesPerSite;
    private int _disposed;

    /// <summary>
    ///     Change-stream sink. Every upsert raises the freshly-merged
    ///     aggregate. Session atom hooks <c>TypedSignalRaised</c> to react
    ///     to aggregate mutations. Kept separate from the indexed store so
    ///     observers can filter cheaply while lookups stay O(1).
    /// </summary>
    public TypedSignalSink<SessionAggregate> Changes { get; }

    public SessionStore(
        IOptions<SessionStoreOptions> options,
        ILogger<SessionStore> logger,
        StyloFlow.Orchestration.IInitSignalBus? initSignalBus = null)
    {
        _options = options.Value;
        _logger = logger;
        _maxAggregatesPerSite = _options.ResolveMaxAggregatesPerSite();

        var innerSink = new SignalSink(
            maxCapacity: Math.Min(_maxAggregatesPerSite, 4096),
            maxAge: _options.Ttl);
        Changes = new TypedSignalSink<SessionAggregate>(
            innerSink,
            maxCapacity: Math.Min(_maxAggregatesPerSite, 4096),
            maxAge: _options.Ttl);

        // First-upsert fires the init signal so SessionAtom +
        // SessionPersistenceAtom lazy-boot via AddOnInitSignal<T>. Guarded
        // by Interlocked.Exchange so only the first raise fires; the bus
        // itself is idempotent so a repeat call is harmless. Optional bus
        // so direct-construction tests (which do not go through DI) keep
        // working without wiring a bus.
        if (initSignalBus is not null)
        {
            var initFired = 0;
            Changes.TypedSignalRaised += _ =>
            {
                if (System.Threading.Interlocked.Exchange(ref initFired, 1) == 0)
                    initSignalBus.Raise(SessionStoreOptions.InitSignal);
            };
        }

        _logger.LogInformation(
            "SessionStore initialised: max {Max} aggregates/site, TTL {Ttl}, cleanup {Cleanup}, min-under-pressure {Floor}",
            _maxAggregatesPerSite, _options.Ttl, _options.CleanupInterval, _options.MinTtlUnderPressure);

        _cleanupLoop = Task.Run(RunCleanupLoopAsync);
    }

    /// <summary>
    ///     Upsert a <see cref="SessionSample"/> into the aggregate for its
    ///     <see cref="SessionSample.FingerprintId"/> on
    ///     <see cref="SessionSample.SiteId"/>. Returns the resulting
    ///     aggregate. Raises the aggregate on <see cref="Changes"/> so the
    ///     session atom sees the mutation.
    /// </summary>
    public SessionAggregate Upsert(SessionSample sample)
    {
        var partition = _sites.GetOrAdd(sample.SiteId, _ => new SitePartition());
        var updated = partition.Aggregates.AddOrUpdate(
            sample.FingerprintId,
            _ => SessionAggregateMerge.FromFirstSample(sample),
            (_, existing) => SessionAggregateMerge.Merge(existing, sample));

        Changes.Raise(
            signal: SessionSignalKeys.AggregateUpdated.Name,
            payload: updated,
            key: sample.FingerprintId);

        return updated;
    }

    /// <summary>
    ///     Look up the current aggregate for a fingerprint on a site. Null
    ///     when no aggregate exists (fingerprint has not seen a sample in
    ///     this session window, or was evicted). Read-only; does not touch
    ///     the sliding TTL.
    /// </summary>
    public SessionAggregate? TryGet(string siteId, string fingerprintId)
    {
        if (!_sites.TryGetValue(siteId, out var partition)) return null;
        return partition.Aggregates.TryGetValue(fingerprintId, out var entry) ? entry : null;
    }

    /// <summary>
    ///     Snapshot of every aggregate currently held for a site. Used by
    ///     the session atom on tick-driven aggregation sweeps + by
    ///     dashboards for per-site session views.
    /// </summary>
    public IReadOnlyList<SessionAggregate> SnapshotSite(string siteId)
    {
        if (!_sites.TryGetValue(siteId, out var partition)) return Array.Empty<SessionAggregate>();
        return partition.Aggregates.Values.ToArray();
    }

    private async Task RunCleanupLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_options.CleanupInterval, _cts.Token).ConfigureAwait(false);
                foreach (var (siteId, partition) in _sites)
                    RunSiteCleanup(siteId, partition);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionStore cleanup loop crashed");
        }
    }

    private void RunSiteCleanup(string siteId, SitePartition partition)
    {
        var now = DateTimeOffset.UtcNow;
        var count = partition.Aggregates.Count;
        if (count == 0) return;

        // Adaptive TTL: shrink as the partition approaches capacity.
        var pressure = Math.Clamp((double)count / _maxAggregatesPerSite, 0.0, 1.0);
        var effectiveTtl = TimeSpan.FromTicks((long)(
            _options.Ttl.Ticks * (1.0 - pressure) +
            _options.MinTtlUnderPressure.Ticks * pressure));

        // Pass 1 -- age-out.
        foreach (var (fingerprintId, aggregate) in partition.Aggregates)
        {
            var age = now - aggregate.LastSample;
            var absoluteAge = now - aggregate.FirstSample;
            if (age > effectiveTtl || absoluteAge > _options.MaxLifetime)
            {
                partition.Aggregates.TryRemove(fingerprintId, out _);
            }
        }

        // Pass 2 -- if still over cap, drop lowest-priority first.
        if (partition.Aggregates.Count <= _maxAggregatesPerSite) return;
        var overflow = partition.Aggregates.Count - _maxAggregatesPerSite;
        var toEvict = partition.Aggregates
            .OrderBy(kvp => kvp.Value.RetentionPriority)
            .Take(overflow)
            .Select(kvp => kvp.Key)
            .ToArray();
        foreach (var key in toEvict) partition.Aggregates.TryRemove(key, out _);

        _logger.LogDebug(
            "SessionStore cleanup site={Site}: TTL={Ttl}, kept={Kept}, evicted={Evicted}",
            siteId, effectiveTtl, partition.Aggregates.Count, count - partition.Aggregates.Count);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); }
        catch { /* already disposed */ }
        try { _cleanupLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* cleanup crashed or timed out */ }
        _cts.Dispose();
    }

    private sealed class SitePartition
    {
        public ConcurrentDictionary<string, SessionAggregate> Aggregates { get; } =
            new(StringComparer.Ordinal);
    }
}

/// <summary>
///     Named signals raised on <see cref="SessionStore.Changes"/>. Session
///     atom senses these to react to aggregate mutations.
/// </summary>
public static class SessionSignalKeys
{
    public static readonly SignalKey<SessionAggregate> AggregateUpdated =
        new("session.aggregate.updated");
}