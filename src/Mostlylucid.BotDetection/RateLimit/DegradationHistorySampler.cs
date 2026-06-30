using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Subscribes to <see cref="TickCadence.Tick10s"/> on the
///     <see cref="IScheduleCoordinator"/> and snapshots
///     <see cref="DegradationAtom"/>'s current EWMA arms into the bounded
///     <see cref="DegradationHistoryAtom"/> ring.
///     <para>
///         Per <c>feedback_no_background_services</c>: NOT a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>. The
///         coordinator owns the only cadence loop in the project; this
///         service just subscribes a handler.
///     </para>
///     <para>
///         <c>Tick10s</c> is the same cadence as
///         <see cref="DegradationAtom"/>'s internal 5-second decay timer
///         doubled — so the sample arm cannot read a wholly-unstirred decay
///         value yet still keeps the ring small enough (360 entries =
///         ~1 hour at 10 s). Atom reads are EMA fetches off two
///         <c>ConcurrentDictionary</c>s; no race with
///         <see cref="DegradationAtom.RecordResponse"/> writers because the
///         AddOrUpdate write commutes with a snapshot Get.
///     </para>
/// </summary>
public sealed class DegradationHistorySampler : IDisposable
{
    private readonly DegradationAtom _atom;
    private readonly DegradationHistoryAtom _history;
    private readonly ILogger<DegradationHistorySampler> _logger;
    private readonly IDisposable? _subscription;
    private int _disposed;

    public DegradationHistorySampler(
        DegradationAtom atom,
        DegradationHistoryAtom history,
        ILogger<DegradationHistorySampler> logger,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(atom);
        ArgumentNullException.ThrowIfNull(history);
        _atom = atom;
        _history = history;
        _logger = logger;

        // Optional so existing direct-construction tests keep working --
        // tests drive OnTickAsync directly.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "DegradationHistorySampler",
                CostHint.Low,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     The schedule-coordinator tick handler. Public so tests can drive
    ///     a single beat deterministically without spinning up the
    ///     coordinator.
    /// </summary>
    public Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return Task.CompletedTask;

        try
        {
            // Reuse the same LatencyEmaMs slot for both p50 + p95 until the
            // atom grows distinct percentile estimators; the snapshot shape
            // already has two fields so the future change is additive.
            var latency = _atom.GetSignalValue(DegradationAtom.LatencyEmaMs);
            var snap = new DegradationSnapshot(
                TimestampUtc: now.UtcDateTime,
                Latency5xxRate: _atom.GetSignalValue(DegradationAtom.Error5xxRate),
                Latency4xxRate: _atom.GetSignalValue(DegradationAtom.Error4xxRate),
                Latency429Rate: _atom.GetSignalValue(DegradationAtom.Rate429),
                LatencyP50Ms: latency,
                LatencyP95Ms: latency,
                NotFoundRate: _atom.GetSignalValue(DegradationAtom.NotFoundRate));
            _history.Append(snap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DegradationHistorySampler: tick failed");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}