namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Row for the <c>sb-policy-state</c> card on the Policies tab: one registered action policy
///     (or one configured-but-unregistered policy) with its effective runtime state.
/// </summary>
/// <param name="Name">Registered policy name (e.g. <c>content-cache-search</c>).</param>
/// <param name="Intent">Coarse intent, or <c>—</c> when the policy is not registered.</param>
/// <param name="IsEnabled">
///     A policy is only considered enabled when its action implementation is registered — the
///     spec's "configured policy is not enabled unless registered" rule. <c>false</c> for a
///     configured name that resolves to no implementation.
/// </param>
/// <param name="EnabledReason">Why the row is not enabled (null when enabled).</param>
/// <param name="Params">Effective runtime params the policy contributed (representation, bounds, counters...).</param>
public sealed record SbPolicyStateRowViewModel(
    string Name,
    string Intent,
    bool IsEnabled,
    string? EnabledReason,
    IReadOnlyDictionary<string, object> Params);
