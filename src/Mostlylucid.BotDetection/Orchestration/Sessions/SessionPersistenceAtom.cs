using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Subscribes to <see cref="SessionAtom.Persistence"/> and writes the
///     shifted aggregate through <see cref="IFingerprintStore.RecordVerdictAsync"/>.
///     Completes the loop: escalator → store → atom → persistence → durable
///     fingerprint state.
/// </summary>
/// <remarks>
///     <para>
///         Uses <see cref="IFingerprintStore.RecordVerdictAsync"/> because
///         that method EWMA-blends the write against the existing cached
///         score (dict-authoritative, restart-survivable, first write is
///         direct assignment). This matches the session model: the atom
///         has already decided the shift is worth persisting; we do not
///         want a second layer of gating here, but we do want smoothing
///         against noisy transitions.
///     </para>
///     <para>
///         Signal callback pushes into a bounded channel; a single-reader
///         drainer serialises writes to <see cref="IFingerprintStore"/> so
///         downstream sees shifts in raise order. Earlier versions used
///         <c>Task.Run</c> per signal, which raced under load: three
///         sequential shifts p=0.5/0.6/0.7 could land 0.5/0.7/0.6 as the
///         later verdicts overwrote the earlier ones. That masked drift
///         and made cached verdicts non-deterministic.
///     </para>
///     <para>
///         Channel is <see cref="BoundedChannelFullMode.DropOldest"/> — same
///         shape as <c>ThreatIntelEnrichmentQueue</c> and
///         <c>PolicyDecisionLogQueue</c>. Under back-pressure the oldest
///         queued shift is dropped, not the newest; the most recent verdict
///         is the one operators care about.
///     </para>
///     <para>
///         Optional dependency on <see cref="IFingerprintStore"/>: hosts
///         without a store (sidecar-only, tests) still get the escalator +
///         session atom running; only the durable write is skipped and the
///         drainer never starts.
///     </para>
///     <para>
///         Test contract: assertions that inspect the fingerprint store
///         should <see cref="FlushAsync"/> first (or Dispose) so the
///         drainer has processed every enqueued shift. The prior
///         Task.Run-per-signal design made this untestable; a fixed
///         "yield 10 times" heuristic passed most of the time and failed
///         under thread-pool cold-start.
///     </para>
/// </remarks>
public sealed class SessionPersistenceAtom : IDisposable
{
    // Bounded so a mis-tuned store or slow disk cannot grow this dictionary
    // without bound. 512 slots cover ~10k active fingerprints at their
    // per-second shift budget with room to spare.
    private const int WriteQueueCapacity = 512;

    private readonly SessionAtom _sessionAtom;
    private readonly IFingerprintStore? _fingerprintStore;
    private readonly ILogger<SessionPersistenceAtom>? _logger;
    private readonly Action<SignalEvent<SessionPersistenceSignal>> _onShift;
    private readonly Channel<SessionPersistenceSignal>? _writeQueue;
    private readonly CancellationTokenSource? _drainerCts;
    private readonly Task? _drainerTask;
    // Pending = enqueued minus completed. Flush waits on this hitting zero
    // rather than on Reader.Count (which drops to 0 the moment the drainer
    // pulls the last item — before RecordVerdictAsync has run).
    private int _pending;
    private int _disposed;

    public SessionPersistenceAtom(
        SessionAtom sessionAtom,
        IFingerprintStore? fingerprintStore = null,
        ILogger<SessionPersistenceAtom>? logger = null)
    {
        _sessionAtom = sessionAtom;
        _fingerprintStore = fingerprintStore;
        _logger = logger;

        _onShift = OnShiftDetected;
        _sessionAtom.Persistence.TypedSignalRaised += _onShift;

        if (_fingerprintStore is null)
        {
            _logger?.LogInformation(
                "SessionPersistenceAtom subscribed to shift signals but no IFingerprintStore is registered -- writes will be skipped");
            return;
        }

        _writeQueue = Channel.CreateBounded<SessionPersistenceSignal>(
            new BoundedChannelOptions(WriteQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        _drainerCts = new CancellationTokenSource();
        _drainerTask = Task.Run(() => DrainAsync(_drainerCts.Token));
    }

    private void OnShiftDetected(SignalEvent<SessionPersistenceSignal> evt)
    {
        if (_disposed != 0) return;
        if (_writeQueue is null) return;
        // TryWrite is non-blocking: DropOldest guarantees success (the
        // channel evicts the oldest queued shift when full and accepts
        // this one). Never false in the DropOldest configuration.
        Interlocked.Increment(ref _pending);
        if (!_writeQueue.Writer.TryWrite(evt.Payload))
        {
            // Belt-and-braces: if a future channel mode change makes TryWrite
            // reject, keep the counter honest so Flush doesn't hang.
            Interlocked.Decrement(ref _pending);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        if (_writeQueue is null || _fingerprintStore is null) return;
        try
        {
            await foreach (var shift in _writeQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await WriteAsync(shift, ct).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Dispose. Drain whatever's still in the channel synchronously so
            // shifts observed just before shutdown are not silently dropped.
            while (_writeQueue.Reader.TryRead(out var shift))
            {
                try { await WriteAsync(shift, CancellationToken.None).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }
    }

    /// <summary>
    ///     Test helper: block until every currently-queued shift has been
    ///     written. Not for production use — production reads from the
    ///     fingerprint store, which is eventually-consistent by design.
    /// </summary>
    internal async Task FlushAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_writeQueue is null) return;
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        // Volatile read on _pending — drainer decrements AFTER the store
        // write completes, so this converges only when every enqueued shift
        // has actually landed. Reader.Count would fire early (the drainer
        // pulls the item and decrements-count before running RecordVerdictAsync).
        while (Interlocked.CompareExchange(ref _pending, 0, 0) > 0)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException(
                    $"SessionPersistenceAtom.FlushAsync did not drain within {timeout}");
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(SessionPersistenceSignal shift, CancellationToken ct)
    {
        if (_fingerprintStore is null) return;

        try
        {
            var riskBand = DeriveRiskBand(shift.Aggregate.MeanBotProbability).ToString();
            await _fingerprintStore
                .RecordVerdictAsync(
                    shift.FingerprintId,
                    shift.Aggregate.MeanBotProbability,
                    riskBand,
                    ct)
                .ConfigureAwait(false);

            _logger?.LogDebug(
                "SessionPersistenceAtom wrote verdict: fp={FingerprintId} site={SiteId} reason={Reason} p={Prob:F2}",
                shift.FingerprintId, shift.SiteId, shift.Reason, shift.Aggregate.MeanBotProbability);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "SessionPersistenceAtom write failed: fp={FingerprintId} reason={Reason}",
                shift.FingerprintId, shift.Reason);
        }
    }

    /// <summary>
    ///     Derives the risk band consistently with the detection
    ///     orchestrator's mapping so cached verdicts written by this atom
    ///     do not diverge from what the request path would compute for the
    ///     same probability. Kept private + duplicated on purpose -- this
    ///     write is off the hot path and a shared helper import would be
    ///     coupling for coupling's sake.
    /// </summary>
    private static RiskBand DeriveRiskBand(double probability) => probability switch
    {
        >= 0.95 => RiskBand.VeryHigh,
        >= 0.80 => RiskBand.High,
        >= 0.60 => RiskBand.Medium,
        >= 0.40 => RiskBand.Elevated,
        >= 0.20 => RiskBand.Low,
        _ => RiskBand.VeryLow,
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _sessionAtom.Persistence.TypedSignalRaised -= _onShift; }
        catch { /* atom already torn down */ }

        // Signal the drainer to stop, close the writer so no further
        // enqueues succeed, then wait briefly for the drainer to finish
        // any in-flight write. The DrainAsync catch block also flushes
        // remaining channel items synchronously so a burst of shifts
        // arriving in the same tick as Dispose still lands.
        _writeQueue?.Writer.TryComplete();
        _drainerCts?.Cancel();
        try { _drainerTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* drainer cancellation is expected */ }
        _drainerCts?.Dispose();
    }
}