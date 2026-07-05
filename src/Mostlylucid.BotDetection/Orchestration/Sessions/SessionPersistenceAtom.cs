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
///         Fire-and-forget from the <c>TypedSignalRaised</c> callback so
///         the session atom's evaluate loop stays hot. Errors are logged
///         and swallowed -- persistence failure never propagates back into
///         detection.
///     </para>
///     <para>
///         Optional dependency on <see cref="IFingerprintStore"/>: hosts
///         without a store (sidecar-only, tests) still get the escalator +
///         session atom running; only the durable write is skipped.
///     </para>
/// </remarks>
public sealed class SessionPersistenceAtom : IDisposable
{
    private readonly SessionAtom _sessionAtom;
    private readonly IFingerprintStore? _fingerprintStore;
    private readonly ILogger<SessionPersistenceAtom>? _logger;
    private readonly Action<SignalEvent<SessionPersistenceSignal>> _onShift;
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
        }
    }

    private void OnShiftDetected(SignalEvent<SessionPersistenceSignal> evt)
    {
        if (_disposed != 0) return;
        if (_fingerprintStore is null) return;
        _ = Task.Run(() => WriteAsync(evt.Payload, CancellationToken.None));
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
    }
}