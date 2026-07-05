using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Signal-native reactor over <see cref="SessionStore.Changes"/>.
///     Watches every aggregate mutation, compares against the persisted
///     fingerprint's cached band, emits a persistence signal when the
///     aggregate shifts the fingerprint. Otherwise logs and moves on.
/// </summary>
/// <remarks>
///     <para>
///         "Shifts the fingerprint" means one of:
///         <list type="bullet">
///             <item>Aggregate mean bot probability has crossed the
///             persisted <see cref="Fingerprint.CachedBotProbability"/>
///             band boundary (delta > <see cref="SessionAtomOptions.ProbabilityShiftDelta"/>).</item>
///             <item>Aggregate max bot probability has crossed the
///             high-confidence threshold and the persisted state has not
///             yet caught up.</item>
///             <item>The dominant client type inferred from the session
///             disagrees with the persisted <see cref="Fingerprint.InferredClientType"/>.</item>
///             <item>Any honeypot hit landed in the session window
///             (always shift-worthy).</item>
///         </list>
///         Persistence signal is raised on
///         <see cref="Persistence"/> as a typed
///         <see cref="SessionPersistenceSignal"/>. Downstream persistence
///         atoms subscribe to that.
///     </para>
///     <para>
///         The atom holds no state of its own -- it is a stateless
///         reactor. All state lives in the store's aggregate.
///     </para>
/// </remarks>
public sealed class SessionAtom : IDisposable
{
    private readonly SessionStore _store;
    private readonly IFingerprintReader? _fingerprintReader;
    private readonly ILogger<SessionAtom>? _logger;
    private readonly SessionAtomOptions _options;
    private readonly Action<SignalEvent<SessionAggregate>> _onAggregate;
    private int _disposed;

    /// <summary>
    ///     Persistence output sink. Raises a
    ///     <see cref="SessionPersistenceSignal"/> when the atom decides an
    ///     aggregate has shifted the fingerprint. Downstream persistence
    ///     atoms hook <c>TypedSignalRaised</c> on this to drive the write.
    /// </summary>
    public TypedSignalSink<SessionPersistenceSignal> Persistence { get; }

    public SessionAtom(
        SessionStore store,
        IOptions<SessionAtomOptions> options,
        IFingerprintReader? fingerprintReader = null,
        ILogger<SessionAtom>? logger = null)
    {
        _store = store;
        _options = options.Value;
        _fingerprintReader = fingerprintReader;
        _logger = logger;

        var inner = new SignalSink(maxCapacity: 4096, maxAge: TimeSpan.FromMinutes(5));
        Persistence = new TypedSignalSink<SessionPersistenceSignal>(inner);

        _onAggregate = OnAggregateUpdated;
        _store.Changes.TypedSignalRaised += _onAggregate;

        // Catch-up: the raise that fired the init signal (and thus caused
        // DI to construct us) predates our subscription, so TypedSignalRaised's
        // event-snapshot semantics wouldn't deliver it. Sense the Changes
        // sink for anything already in-flight and evaluate. Runs once per
        // singleton lifetime; empty on tests that construct directly against
        // an empty store.
        foreach (var evt in _store.Changes.Sense())
        {
            _ = Task.Run(() => EvaluateAsync(evt.Payload, CancellationToken.None));
        }
    }

    private void OnAggregateUpdated(SignalEvent<SessionAggregate> evt)
    {
        if (_disposed != 0) return;

        // Fire-and-forget: keep the store's upsert thread hot.
        _ = Task.Run(() => EvaluateAsync(evt.Payload, CancellationToken.None));
    }

    /// <summary>
    ///     Public for direct-drive tests that push aggregates through
    ///     without wiring the sink subscription.
    /// </summary>
    public async Task EvaluateAsync(SessionAggregate aggregate, CancellationToken ct)
    {
        try
        {
            var shift = await DetectShiftAsync(aggregate, ct).ConfigureAwait(false);
            if (shift is null)
            {
                _logger?.LogTrace(
                    "SessionAtom no-shift: fp={FingerprintId} mean={Mean:F2} samples={Count}",
                    aggregate.FingerprintId, aggregate.MeanBotProbability, aggregate.SampleCount);
                return;
            }

            _logger?.LogInformation(
                "SessionAtom shift detected: fp={FingerprintId} reason={Reason} delta={Delta:F2}",
                aggregate.FingerprintId, shift.Reason, shift.ProbabilityDelta);

            Persistence.Raise(
                signal: SessionPersistenceSignal.SignalName,
                payload: shift,
                key: aggregate.FingerprintId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "SessionAtom evaluation failed for fp={FingerprintId}",
                aggregate.FingerprintId);
        }
    }

    private async Task<SessionPersistenceSignal?> DetectShiftAsync(SessionAggregate aggregate, CancellationToken ct)
    {
        // Rule 1: any honeypot hit is always shift-worthy.
        if (aggregate.HoneypotHits > 0 && aggregate.SampleCount == aggregate.HoneypotHits)
        {
            // First hit or continuing honeypot session -- always persist.
            return new SessionPersistenceSignal
            {
                FingerprintId = aggregate.FingerprintId,
                SiteId = aggregate.SiteId,
                Reason = SessionShiftReason.Honeypot,
                ProbabilityDelta = aggregate.MeanBotProbability,
                Aggregate = aggregate,
            };
        }

        // Rules 2-4 need the persisted fingerprint to compare against.
        // When no reader is registered (test host, sidecar-only build),
        // we cannot detect shift so we log-only.
        if (_fingerprintReader is null) return null;

        Fingerprint? persisted = null;
        try
        {
            persisted = await _fingerprintReader.GetFingerprintAsync(aggregate.FingerprintId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex,
                "SessionAtom could not read persisted fingerprint fp={FingerprintId}",
                aggregate.FingerprintId);
            return null;
        }

        // Rule 2: fingerprint not yet persisted -- new session, always shift-worthy.
        if (persisted is null && aggregate.SampleCount >= _options.MinSamplesToPersist)
        {
            return new SessionPersistenceSignal
            {
                FingerprintId = aggregate.FingerprintId,
                SiteId = aggregate.SiteId,
                Reason = SessionShiftReason.NewFingerprint,
                ProbabilityDelta = aggregate.MeanBotProbability,
                Aggregate = aggregate,
            };
        }

        if (persisted is null) return null;

        // Rule 3: probability crossed the persisted band by more than delta.
        var delta = Math.Abs(aggregate.MeanBotProbability - persisted.CachedBotProbability);
        if (delta > _options.ProbabilityShiftDelta && aggregate.SampleCount >= _options.MinSamplesToPersist)
        {
            return new SessionPersistenceSignal
            {
                FingerprintId = aggregate.FingerprintId,
                SiteId = aggregate.SiteId,
                Reason = SessionShiftReason.ProbabilityShift,
                ProbabilityDelta = delta,
                Aggregate = aggregate,
            };
        }

        // Rule 4: dominant client type disagrees with persisted inferred type.
        if (!string.IsNullOrEmpty(aggregate.DominantClientType) &&
            !string.Equals(aggregate.DominantClientType, persisted.InferredClientType, StringComparison.OrdinalIgnoreCase) &&
            aggregate.SampleCount >= _options.MinSamplesToPersist)
        {
            return new SessionPersistenceSignal
            {
                FingerprintId = aggregate.FingerprintId,
                SiteId = aggregate.SiteId,
                Reason = SessionShiftReason.ClientTypeShift,
                ProbabilityDelta = delta,
                Aggregate = aggregate,
            };
        }

        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _store.Changes.TypedSignalRaised -= _onAggregate; }
        catch { /* store already torn down */ }
    }
}

/// <summary>
///     Signal payload raised on <see cref="SessionAtom.Persistence"/> when
///     the atom decides an aggregate has shifted the fingerprint.
///     Downstream persistence atoms consume this to drive the durable
///     write.
/// </summary>
public sealed record SessionPersistenceSignal
{
    public const string SignalName = "session.persistence.shift";

    public required string FingerprintId { get; init; }
    public required string SiteId { get; init; }
    public required SessionShiftReason Reason { get; init; }
    public required double ProbabilityDelta { get; init; }
    public required SessionAggregate Aggregate { get; init; }
}

/// <summary>Why a persistence signal was raised.</summary>
public enum SessionShiftReason
{
    /// <summary>Aggregate mean probability crossed the persisted band by more than delta.</summary>
    ProbabilityShift,

    /// <summary>Dominant client type disagrees with the persisted inferred type.</summary>
    ClientTypeShift,

    /// <summary>Any honeypot hit landed in the session window.</summary>
    Honeypot,

    /// <summary>Fingerprint not yet persisted -- first meaningful session.</summary>
    NewFingerprint,
}