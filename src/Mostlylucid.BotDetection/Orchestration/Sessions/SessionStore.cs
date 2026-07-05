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

    /// <summary>
    ///     Phase-1 lifecycle sink for <see cref="SessionFinalizingSignal"/>.
    ///     The cleanup loop raises here when an aggregate meets eviction
    ///     criteria; the aggregate stays in the partition (marked
    ///     pending-eviction) until either the configured expected ack
    ///     count is received on <see cref="Finalizations"/> or the
    ///     deadline expires. This lets echo / vector atoms materialise a
    ///     durable projection off the full aggregate before it disappears.
    /// </summary>
    public TypedSignalSink<SessionFinalizingSignal> Lifecycle { get; }

    /// <summary>
    ///     Phase-2 ack sink for <see cref="SessionFinalizedAckSignal"/>.
    ///     Subscribers that complete finalization raise here; the store
    ///     collects acks per fingerprint and releases the pending-eviction
    ///     latch once the expected count is reached. Exposed publicly so
    ///     subscribers (SessionEchoAtom, MarkovVectorAtom) share the same
    ///     sink the store watches on.
    /// </summary>
    public TypedSignalSink<SessionFinalizedAckSignal> Finalizations { get; }

    // Pending-eviction latches. When Phase 1 fires, we insert (siteId, fpId)
    // → PendingEviction record and start counting acks. When count reaches
    // ExpectedAckCount or the deadline expires, we remove from the partition
    // and drop the latch. Concurrent-dict keyed by "site|fp" — same
    // partitioning shape as the aggregate store itself.
    private readonly ConcurrentDictionary<string, PendingEviction> _pending = new(StringComparer.Ordinal);

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

        // Lifecycle + Finalizations run on their own inner sinks so their
        // TTL/capacity does not collide with the Changes stream (which
        // sees per-upsert traffic; lifecycle only fires on eviction).
        // Capacity sized to "worst-case burst of evictions per pressure
        // pass" — bounded by MaxAggregatesPerSite; 4096 covers a full
        // partition tick even at the upper clamp.
        var lifecycleInner = new SignalSink(maxCapacity: 4096, maxAge: TimeSpan.FromMinutes(5));
        Lifecycle = new TypedSignalSink<SessionFinalizingSignal>(
            lifecycleInner, maxCapacity: 4096, maxAge: TimeSpan.FromMinutes(5));
        var ackInner = new SignalSink(maxCapacity: 4096, maxAge: TimeSpan.FromMinutes(5));
        Finalizations = new TypedSignalSink<SessionFinalizedAckSignal>(
            ackInner, maxCapacity: 4096, maxAge: TimeSpan.FromMinutes(5));

        // Wire ack collection: increment the pending latch when a matching
        // ack arrives; the cleanup loop's finalize wait polls it.
        Finalizations.TypedSignalRaised += evt =>
        {
            var payload = evt.Payload;
            if (_pending.TryGetValue(PendingKey(payload.SiteId, payload.FingerprintId), out var latch))
            {
                latch.RecordAck(payload.AckedBy);
            }
        };

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
                    await RunSiteCleanupAsync(siteId, partition, _cts.Token).ConfigureAwait(false);
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

    private async Task RunSiteCleanupAsync(string siteId, SitePartition partition, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var count = partition.Aggregates.Count;
        if (count == 0) return;

        // Adaptive TTL + adaptive finalize deadline: shrink as the partition
        // approaches capacity. Same pressure fraction drives both — under
        // load we age things out faster AND wait less for finalization acks.
        var pressure = Math.Clamp((double)count / _maxAggregatesPerSite, 0.0, 1.0);
        var effectiveTtl = TimeSpan.FromTicks((long)(
            _options.Ttl.Ticks * (1.0 - pressure) +
            _options.MinTtlUnderPressure.Ticks * pressure));
        var effectiveDeadline = TimeSpan.FromTicks((long)(
            _options.FinalizeDeadline.Ticks * (1.0 - pressure) +
            _options.MinFinalizeDeadlineUnderPressure.Ticks * pressure));

        // Pass 1 -- collect age-out candidates.
        var ageOutCandidates = new List<(string FpId, SessionAggregate Agg, SessionFinalizeReason Reason)>();
        foreach (var (fingerprintId, aggregate) in partition.Aggregates)
        {
            if (_pending.ContainsKey(PendingKey(siteId, fingerprintId))) continue; // already finalizing
            var age = now - aggregate.LastSample;
            var absoluteAge = now - aggregate.FirstSample;
            if (absoluteAge > _options.MaxLifetime)
                ageOutCandidates.Add((fingerprintId, aggregate, SessionFinalizeReason.MaxLifetime));
            else if (age > effectiveTtl)
                ageOutCandidates.Add((fingerprintId, aggregate, SessionFinalizeReason.Ttl));
        }

        // Pass 2 -- pressure overflow: if the surviving count (excluding
        // already-pending finalizations) is still over cap, mark lowest-
        // priority for eviction under Reason.Pressure. Sort ignores anything
        // already in ageOutCandidates and anything already pending.
        var survivorCount = partition.Aggregates.Count
            - ageOutCandidates.Count
            - _pending.Keys.Count(k => k.StartsWith(siteId + "|", StringComparison.Ordinal));
        if (survivorCount > _maxAggregatesPerSite)
        {
            var overflow = survivorCount - _maxAggregatesPerSite;
            var ageOutSet = ageOutCandidates.Select(c => c.FpId).ToHashSet(StringComparer.Ordinal);
            var pressureVictims = partition.Aggregates
                .Where(kvp =>
                    !ageOutSet.Contains(kvp.Key) &&
                    !_pending.ContainsKey(PendingKey(siteId, kvp.Key)))
                .OrderBy(kvp => kvp.Value.RetentionPriority)
                .Take(overflow)
                .Select(kvp => (kvp.Key, kvp.Value, SessionFinalizeReason.Pressure));
            ageOutCandidates.AddRange(pressureVictims);
        }

        if (ageOutCandidates.Count == 0) return;

        // Phase 1 -- raise finalizing signals + register pending latches.
        // Under EmitFinalizingSignal=false we skip straight to remove (test
        // hosts / trimmed builds without echo atoms).
        if (!_options.EmitFinalizingSignal || _options.ExpectedAckCount <= 0)
        {
            foreach (var (fpId, _, _) in ageOutCandidates)
                partition.Aggregates.TryRemove(fpId, out _);
            _logger.LogDebug(
                "SessionStore cleanup site={Site}: TTL={Ttl}, evicted={Evicted} (no-finalize path)",
                siteId, effectiveTtl, ageOutCandidates.Count);
            return;
        }

        var deadlineUtc = now.Add(effectiveDeadline);
        var latches = new List<(string PendingKeyStr, string FpId)>(ageOutCandidates.Count);
        foreach (var (fpId, aggregate, reason) in ageOutCandidates)
        {
            var key = PendingKey(siteId, fpId);
            var latch = new PendingEviction(_options.ExpectedAckCount);
            if (!_pending.TryAdd(key, latch)) continue; // concurrent cleanup pass raced; skip
            latches.Add((key, fpId));

            Lifecycle.Raise(
                signal: SessionFinalizingSignal.Key.Name,
                payload: new SessionFinalizingSignal
                {
                    FingerprintId = fpId,
                    SiteId = siteId,
                    Aggregate = aggregate,
                    DeadlineUtc = deadlineUtc,
                    Reason = reason,
                },
                key: fpId);
        }

        // Phase 2 -- wait for acks or deadline. Poll every 10ms so quick
        // acks complete fast without hammering the CPU. The whole cleanup
        // pass is bounded by effectiveDeadline; slow subscribers only
        // delay this pass, not the loop cadence.
        try
        {
            await WaitForAcksAsync(latches, effectiveDeadline, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — evict everything unconditionally, no more delays.
        }

        // Phase 3 -- remove from partition. Fires whether acks landed or the
        // deadline was hit; either way the aggregate leaves memory here.
        var actuallyEvicted = 0;
        foreach (var (key, fpId) in latches)
        {
            partition.Aggregates.TryRemove(fpId, out _);
            _pending.TryRemove(key, out _);
            actuallyEvicted++;
        }

        _logger.LogDebug(
            "SessionStore cleanup site={Site}: TTL={Ttl}, deadline={Deadline}, evicted={Evicted}, remaining={Remaining}",
            siteId, effectiveTtl, effectiveDeadline, actuallyEvicted, partition.Aggregates.Count);
    }

    private async Task WaitForAcksAsync(
        IReadOnlyList<(string PendingKey, string FpId)> latches,
        TimeSpan deadline,
        CancellationToken ct)
    {
        if (latches.Count == 0) return;
        var start = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var allComplete = true;
            foreach (var (key, _) in latches)
            {
                if (!_pending.TryGetValue(key, out var latch) || !latch.IsComplete)
                {
                    allComplete = false;
                    break;
                }
            }
            if (allComplete) return;
            if (DateTimeOffset.UtcNow - start >= deadline) return;
            await Task.Delay(_options.AckPollInterval, ct).ConfigureAwait(false);
        }
    }

    private static string PendingKey(string siteId, string fingerprintId)
        => $"{siteId}|{fingerprintId}";

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

    /// <summary>
    ///     Tracks acks received against an expected count for a single
    ///     pending-eviction fingerprint. Duplicate acks from the same
    ///     source atom don't inflate the count (dedup via <c>_seen</c>).
    /// </summary>
    private sealed class PendingEviction
    {
        private readonly int _expected;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private int _count;

        public PendingEviction(int expected)
        {
            _expected = Math.Max(1, expected);
        }

        public bool IsComplete => Volatile.Read(ref _count) >= _expected;

        public void RecordAck(string ackedBy)
        {
            lock (_gate)
            {
                if (!_seen.Add(ackedBy)) return; // duplicate ack from same atom
                _count++;
            }
        }
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