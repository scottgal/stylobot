using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     What kind of slow-path work the coordinator is queuing — used for diagnostics
///     and for the priority computation (operator-triggered always preempts).
/// </summary>
public enum IdentitySlowPathKind
{
    Pass2Match,
    CorrectionWrite,
    ObservationRecord,
    DriftVerify,
    OperatorReverify,
    OperatorAiOpinion
}

/// <summary>
///     Outcome of a coordinator-managed slow-path call. <see cref="Outcome.Shed"/> means
///     the coordinator did not run the work — the caller should fall back to the
///     fast-path default verdict.
/// </summary>
public enum SlowPathDispatchOutcome
{
    Executed,
    Coalesced,
    SheddedQueueFull,
    SheddedBreakerOpen,
    SheddedSamePerFpCap
}

public sealed record IdentityCoordinatorDiagnostics(
    int QueueDepth,
    int InFlightCount,
    long TotalEnqueued,
    long TotalExecuted,
    long TotalCoalesced,
    long TotalShedQueueFull,
    long TotalShedBreakerOpen,
    long TotalShedPerFpCap,
    bool BreakerOpen,
    DateTime? BreakerOpenedAt);

/// <summary>
///     Per-fingerprint slow-path coordinator. Four layered defences in one service:
///
///     <para>1. Keyed serialisation per fingerprint id — at most one slow-path operation
///     in flight per fp; bursts that match an in-flight call coalesce into waiters.</para>
///
///     <para>2. Priority scheduling — global priority queue ordered by risk score with
///     aging boost. High-risk fingerprints (already-suspicious, ambiguity-probing,
///     drift-flagged) jump the queue; settled-benign work waits.</para>
///
///     <para>3. Admission control — per-fp queued cap plus global queue depth cap with
///     drop-oldest backpressure. Under sustained burst from one fp the freshest few
///     requests set the verdict for all.</para>
///
///     <para>4. Circuit breaker — when the global queue stays full beyond the
///     trip-hold threshold, the coordinator stops admitting new non-operator work and
///     callers fall back to the fast-path default. Auto-resets when pressure drops.</para>
///
///     The fast path (cache hits, L1 confirm wins) does not touch this coordinator —
///     it serves verdicts in microseconds and never blocks. The coordinator gates only
///     the slow path so the fast path always returns, even under adversarial burst.
/// </summary>
public sealed class IdentityProcessingCoordinator : BackgroundService
{
    private readonly ILogger<IdentityProcessingCoordinator> _logger;
    private readonly IdentityCoordinatorOptions _options;

    private readonly object _queueLock = new();
    // PriorityQueue ordered by negated effective score so smaller TPriority comes first.
    private readonly PriorityQueue<QueueItem, double> _queue = new();
    // Per-fp pending tally for the per-fp admission cap.
    private readonly Dictionary<string, int> _queuedByFp = new(StringComparer.OrdinalIgnoreCase);
    // Per-fp in-flight tracker for coalesce decisions; value carries the start time.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, InflightSlot> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _newItemSignal = new(0);

    private long _totalEnqueued;
    private long _totalExecuted;
    private long _totalCoalesced;
    private long _totalShedQueueFull;
    private long _totalShedBreakerOpen;
    private long _totalShedPerFpCap;

    private DateTime? _breakerOpenedAt;
    private DateTime? _underTripThresholdSince;
    private DateTime? _underResetThresholdSince;

    public IdentityProcessingCoordinator(
        ILogger<IdentityProcessingCoordinator> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value.Identity.Coordinator;
    }

    public bool BreakerOpen => _breakerOpenedAt is not null;

    /// <summary>
    ///     Test-only: clear in-memory coalesce state without affecting persisted
    ///     fingerprints. The BDF replay rig calls this between scenarios alongside
    ///     <c>SqliteFingerprintStore.TruncateAllAsync</c> so a previous scenario's
    ///     unresolved Pass 2 entries can't shed a fresh allocation that happens to
    ///     reuse a fingerprint id. Production code never calls this.
    /// </summary>
    public void ResetInflight() => _inflight.Clear();

    public IdentityCoordinatorDiagnostics GetDiagnostics() => new(
        QueueDepth: SnapshotQueueDepth(),
        InFlightCount: _inflight.Count,
        TotalEnqueued: Interlocked.Read(ref _totalEnqueued),
        TotalExecuted: Interlocked.Read(ref _totalExecuted),
        TotalCoalesced: Interlocked.Read(ref _totalCoalesced),
        TotalShedQueueFull: Interlocked.Read(ref _totalShedQueueFull),
        TotalShedBreakerOpen: Interlocked.Read(ref _totalShedBreakerOpen),
        TotalShedPerFpCap: Interlocked.Read(ref _totalShedPerFpCap),
        BreakerOpen: BreakerOpen,
        BreakerOpenedAt: _breakerOpenedAt);

    /// <summary>
    ///     Submit slow-path work for a fingerprint. Returns the operation's result when
    ///     executed, or <c>default(T)</c> when shed/coalesced — caller treats that as
    ///     "use the fast-path default." <paramref name="kind"/> drives priority biases
    ///     (operator-triggered work bypasses the breaker). <paramref name="riskScore"/>
    ///     is the fingerprint's current bot probability (or equivalent), used as the
    ///     base priority before aging.
    /// </summary>
    public Task<(T? Result, SlowPathDispatchOutcome Outcome)> RunAsync<T>(
        string fingerprintId,
        IdentitySlowPathKind kind,
        double riskScore,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        var isOperatorTriggered = kind is IdentitySlowPathKind.OperatorReverify or IdentitySlowPathKind.OperatorAiOpinion;

        // Layer 4: circuit breaker. Operator-triggered work always runs.
        if (BreakerOpen && !isOperatorTriggered)
        {
            Interlocked.Increment(ref _totalShedBreakerOpen);
            return Task.FromResult<(T?, SlowPathDispatchOutcome)>((default, SlowPathDispatchOutcome.SheddedBreakerOpen));
        }

        // Layer 1: coalesce on young in-flight for the same fingerprint.
        if (_inflight.TryGetValue(fingerprintId, out var slot))
        {
            var ageMs = (DateTime.UtcNow - slot.StartedAt).TotalMilliseconds;
            if (ageMs < _options.CoalesceWindowMs)
            {
                Interlocked.Increment(ref _totalCoalesced);
                return Task.FromResult<(T?, SlowPathDispatchOutcome)>((default, SlowPathDispatchOutcome.Coalesced));
            }
            // Older in-flight: don't queue another, fall back to fast-path default.
            Interlocked.Increment(ref _totalShedPerFpCap);
            return Task.FromResult<(T?, SlowPathDispatchOutcome)>((default, SlowPathDispatchOutcome.SheddedSamePerFpCap));
        }

        var tcs = new TaskCompletionSource<(T? Result, SlowPathDispatchOutcome Outcome)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new QueueItem
        {
            FingerprintId = fingerprintId,
            Kind = kind,
            RiskScore = riskScore,
            EnqueuedAt = DateTime.UtcNow,
            IsOperatorTriggered = isOperatorTriggered,
            Run = async runCt =>
            {
                try
                {
                    var result = await operation(runCt);
                    Interlocked.Increment(ref _totalExecuted);
                    tcs.TrySetResult((result, SlowPathDispatchOutcome.Executed));
                }
                catch (OperationCanceledException) when (runCt.IsCancellationRequested)
                {
                    tcs.TrySetResult((default, SlowPathDispatchOutcome.Executed));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            // Drop-oldest path resolves the TCS without running the operation. Caller
            // sees a shed outcome and falls back to the fast-path default exactly the
            // same way as a breaker-shed or per-fp-cap-shed.
            ShedAs = outcome => tcs.TrySetResult((default, outcome)),
            Ct = ct
        };

        lock (_queueLock)
        {
            // Layer 3a: per-fp queued cap.
            var queuedForThisFp = _queuedByFp.GetValueOrDefault(fingerprintId, 0);
            if (queuedForThisFp >= _options.MaxQueuedPerFingerprint)
            {
                Interlocked.Increment(ref _totalShedPerFpCap);
                return Task.FromResult<(T?, SlowPathDispatchOutcome)>((default, SlowPathDispatchOutcome.SheddedSamePerFpCap));
            }

            // Layer 3b: global queue depth cap with drop-oldest.
            if (_queue.Count >= _options.MaxQueueDepth)
            {
                var dropped = DropOldestLocked();
                if (dropped is not null)
                {
                    dropped.Item.ShedAs(SlowPathDispatchOutcome.SheddedQueueFull);
                    Interlocked.Increment(ref _totalShedQueueFull);
                }
            }

            _queue.Enqueue(item, ComputeNegatedPriority(item, DateTime.UtcNow));
            _queuedByFp[fingerprintId] = queuedForThisFp + 1;
            Interlocked.Increment(ref _totalEnqueued);
        }

        _newItemSignal.Release();
        return tcs.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = Math.Max(1, _options.WorkerCount);
        _logger.LogInformation(
            "IdentityProcessingCoordinator started (workers={N}, max queue={MaxQ}, max per-fp queued={MaxFp}, breaker trip @ {Trip:P0})",
            workerCount, _options.MaxQueueDepth, _options.MaxQueuedPerFingerprint, _options.BreakerTripThreshold);

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(stoppingToken), stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _newItemSignal.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }

            QueueItem? item = TryDequeueDispatchable();
            if (item is null) continue;

            try
            {
                // _totalExecuted is incremented inside the operation closure (before the
                // TCS resolves) so callers reading diagnostics after their await see the
                // counter reflect their work.
                await item.Run(item.Ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Slow-path item failed for fingerprint {Id}", item.FingerprintId);
            }
            finally
            {
                _inflight.TryRemove(item.FingerprintId, out _);
                // Wake one waiter so any item requeued behind this fp gets a fresh dispatch attempt.
                try { _newItemSignal.Release(); } catch (SemaphoreFullException) { /* normal at low load */ }
            }

            UpdateBreakerStateLocked();
        }
    }

    /// <summary>
    ///     Pops the highest-priority item. If its fingerprint is already in-flight on
    ///     another worker, requeues it with a small priority penalty (lets newer
    ///     non-conflicting work go first) and returns null — caller spins back to the
    ///     signal wait. The aging boost catches up over time so requeued items aren't
    ///     starved indefinitely. Reserves the inflight slot atomically with the dequeue.
    /// </summary>
    private QueueItem? TryDequeueDispatchable()
    {
        lock (_queueLock)
        {
            if (_queue.Count == 0) return null;
            if (!_queue.TryDequeue(out var item, out _) || item is null) return null;

            // Decrement per-fp queued count immediately — the item is no longer queued
            // even if we requeue it (we'll re-add the count below).
            var queued = _queuedByFp.GetValueOrDefault(item.FingerprintId, 0) - 1;
            if (queued <= 0) _queuedByFp.Remove(item.FingerprintId);
            else _queuedByFp[item.FingerprintId] = queued;

            // Per-fp serialisation: another worker is already running an op for this fp.
            // Requeue with a small penalty so newer non-conflicting work goes first; the
            // in-flight worker will release the signal on completion, giving us another
            // chance.
            if (_inflight.ContainsKey(item.FingerprintId))
            {
                var newPriority = ComputeNegatedPriority(item, DateTime.UtcNow) + 0.001;
                _queue.Enqueue(item, newPriority);
                _queuedByFp[item.FingerprintId] = _queuedByFp.GetValueOrDefault(item.FingerprintId, 0) + 1;
                return null;
            }

            _inflight[item.FingerprintId] = new InflightSlot { StartedAt = DateTime.UtcNow };
            return item;
        }
    }

    private double ComputeNegatedPriority(QueueItem item, DateTime now)
    {
        // PriorityQueue dequeues the smallest TPriority first, so we negate for a max-heap.
        // Operator-triggered items get a strong head-of-line bias.
        var ageSeconds = (now - item.EnqueuedAt).TotalSeconds;
        var operatorBias = item.IsOperatorTriggered ? 100.0 : 0.0;
        var ageBoost = ageSeconds * _options.AgingBoostPerSecond;
        var effective = item.RiskScore + ageBoost + operatorBias;
        return -effective;
    }

    /// <summary>
    ///     Drops the lowest-priority queued item to make room for a new arrival.
    ///     PriorityQueue doesn't support arbitrary removal; we rebuild by drain-and-restore
    ///     of every-but-one. Acceptable at the cap-trigger boundary because shedding is
    ///     intentionally rare; the cap exists to bound worst-case memory, not as a
    ///     steady-state operation.
    /// </summary>
    private DroppedItem? DropOldestLocked()
    {
        if (_queue.Count == 0) return null;
        var keep = new List<(QueueItem item, double prio)>(_queue.Count);
        QueueItem? worst = null;
        double worstPrio = double.NegativeInfinity;
        var now = DateTime.UtcNow;
        while (_queue.Count > 0)
        {
            _queue.TryDequeue(out var entry, out var prio);
            if (entry is null) continue;
            // Recompute current priority including age (so the lowest-EFFECTIVE-priority is dropped).
            var current = ComputeNegatedPriority(entry, now);
            if (current > worstPrio)
            {
                if (worst is not null) keep.Add((worst, worstPrio));
                worst = entry;
                worstPrio = current;
            }
            else
            {
                keep.Add((entry, current));
            }
        }
        foreach (var (it, p) in keep) _queue.Enqueue(it, p);
        if (worst is null) return null;
        var c = _queuedByFp.GetValueOrDefault(worst.FingerprintId, 0) - 1;
        if (c <= 0) _queuedByFp.Remove(worst.FingerprintId);
        else _queuedByFp[worst.FingerprintId] = c;
        return new DroppedItem(worst);
    }

    private int SnapshotQueueDepth()
    {
        lock (_queueLock) return _queue.Count;
    }

    /// <summary>
    ///     Layer 4 — breaker state machine. Trips when queue depth has been above the
    ///     trip threshold for the trip-hold duration; resets when below the reset
    ///     threshold for the reset-hold duration. Both transitions require sustained
    ///     conditions to avoid flapping under bursty load.
    /// </summary>
    private void UpdateBreakerStateLocked()
    {
        var depth = SnapshotQueueDepth();
        var max = Math.Max(1, _options.MaxQueueDepth);
        var fraction = (double)depth / max;
        var now = DateTime.UtcNow;

        if (!BreakerOpen)
        {
            if (fraction >= _options.BreakerTripThreshold)
            {
                _underTripThresholdSince ??= now;
                if ((now - _underTripThresholdSince.Value).TotalSeconds >= _options.BreakerTripHoldSeconds)
                {
                    _breakerOpenedAt = now;
                    _underTripThresholdSince = null;
                    _underResetThresholdSince = null;
                    _logger.LogWarning(
                        "Identity slow-path breaker OPEN (queue depth {Depth}/{Max} = {Pct:P0})",
                        depth, _options.MaxQueueDepth, fraction);
                }
            }
            else _underTripThresholdSince = null;
        }
        else
        {
            if (fraction <= _options.BreakerResetThreshold)
            {
                _underResetThresholdSince ??= now;
                if ((now - _underResetThresholdSince.Value).TotalSeconds >= _options.BreakerResetHoldSeconds)
                {
                    _breakerOpenedAt = null;
                    _underResetThresholdSince = null;
                    _logger.LogInformation(
                        "Identity slow-path breaker RESET (queue depth {Depth}/{Max} = {Pct:P0})",
                        depth, _options.MaxQueueDepth, fraction);
                }
            }
            else _underResetThresholdSince = null;
        }
    }

    private sealed class QueueItem
    {
        public required string FingerprintId { get; init; }
        public required IdentitySlowPathKind Kind { get; init; }
        public required double RiskScore { get; init; }
        public required DateTime EnqueuedAt { get; init; }
        public required bool IsOperatorTriggered { get; init; }
        public required Func<CancellationToken, Task> Run { get; init; }
        public required Action<SlowPathDispatchOutcome> ShedAs { get; init; }
        public required CancellationToken Ct { get; init; }
    }

    private sealed class InflightSlot
    {
        public required DateTime StartedAt { get; init; }
    }

    private sealed record DroppedItem(QueueItem Item);
}
