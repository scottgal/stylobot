namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Per-action-kind edit slices. Exactly one is non-null on a
///     <see cref="PolicyEditRowViewModel"/>; which one depends on
///     <see cref="PolicyEditRowViewModel.ActionKind"/>. Zero-field actions
///     (allow / observe / block) carry no slice -- their kind string is enough.
/// </summary>
public sealed record TagActionEdit(string Name);

/// <summary>
///     Edit slice for a <c>challenge</c> action. <see cref="Kind"/> is the
///     challenge implementation (e.g. <c>"turnstile"</c>, <c>"captcha"</c>,
///     <c>"jschallenge"</c>); <see cref="SiteKey"/> is the optional public
///     site-key the challenge mounts under.
/// </summary>
public sealed record ChallengeActionEdit(string Kind, string? SiteKey = null);

/// <summary>
///     Edit slice for a <c>ratelimit</c> action. Carries every field the
///     traffic-shaping editor exposes, including the ones not yet
///     persisted at the discriminated-union level (<see cref="Burst"/>,
///     <see cref="MitigationTimeoutSeconds"/>, <see cref="OverLimitAction"/>);
///     those flow through the commercial JSONB sidecar landing later in the
///     plan.
/// </summary>
/// <param name="Rate">Steady-state requests allowed per <paramref name="Unit"/>.</param>
/// <param name="Unit">Bucket unit: <c>"second"</c>, <c>"minute"</c>, or <c>"hour"</c>.</param>
/// <param name="Key">Key the bucket is partitioned by: <c>"fingerprint"</c>, <c>"ip"</c>, <c>"identity"</c>, <c>"session"</c>, <c>"path-template"</c>, or <c>"custom-signal"</c>.</param>
/// <param name="Burst">Optional burst capacity above the steady-state rate; <c>null</c> = unset.</param>
/// <param name="MitigationTimeoutSeconds">Optional cool-down before the bucket releases; <c>null</c> = unset.</param>
/// <param name="OverLimitAction">Name of another action policy invoked when the bucket is exhausted.</param>
public sealed record RateLimitActionEdit(
    int Rate,
    string Unit,
    string Key,
    int? Burst,
    int? MitigationTimeoutSeconds,
    string OverLimitAction);

/// <summary>
///     Edit slice for a <c>throttle</c> action. Maps directly onto
///     <see cref="Mostlylucid.BotDetection.Policies.Rules.PolicyAction.Throttle"/>.
/// </summary>
/// <param name="RequestsPerSecond">Steady-state cap; must be &gt; 0 at submit time.</param>
/// <param name="Reason">Operator-facing label surfaced in decision-log entries.</param>
public sealed record ThrottleActionEdit(int RequestsPerSecond, string? Reason);