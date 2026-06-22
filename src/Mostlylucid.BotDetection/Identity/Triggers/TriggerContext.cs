using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Identity.Triggers;

/// <summary>
///     The state an <see cref="AdaptiveTriggerEvaluator"/> evaluates an
///     <see cref="AdaptiveTriggerPolicy"/> against. Built fresh per heartbeat
///     by the subscriber so we never carry stale signal snapshots.
/// </summary>
/// <param name="Now">Wall-clock instant of this evaluation. Caller passes the tick's timestamp so eval is testable with a virtual clock.</param>
/// <param name="LastSuccessfulRunUtc">When the gated work last completed successfully. Null when it has never run; treated as "infinitely overdue" so first call fires (subject to other gates).</param>
/// <param name="CurrentBand">Current load band reported by <see cref="ILoadBandSource"/>. Pass <see cref="LoadBand.Low"/> when no sensor is wired.</param>
/// <param name="Signals">Snapshot of named pressure signals, supplied by the subscriber's <see cref="IAdaptiveTriggerSignalSource"/>.</param>
public readonly record struct TriggerContext(
    DateTimeOffset Now,
    DateTimeOffset? LastSuccessfulRunUtc,
    LoadBand CurrentBand,
    IReadOnlyDictionary<string, double> Signals);

/// <summary>
///     Outcome of evaluating an <see cref="AdaptiveTriggerPolicy"/> against a
///     <see cref="TriggerContext"/>. <see cref="Reason"/> is a human-readable
///     explanation surfaced verbatim on the <c>/admin/learning/health</c>
///     observability endpoint so operators can answer "why isn't this firing"
///     without re-reading the spec.
/// </summary>
public readonly record struct TriggerDecision(bool ShouldRun, string Reason)
{
    /// <summary>Convenience factory: trigger should NOT run. Reason is required.</summary>
    public static TriggerDecision NoBecause(string reason) => new(false, reason);

    /// <summary>Convenience factory: trigger should run. Reason is required.</summary>
    public static TriggerDecision YesBecause(string reason) => new(true, reason);
}
