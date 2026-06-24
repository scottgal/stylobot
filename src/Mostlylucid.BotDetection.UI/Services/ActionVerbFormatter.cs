using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Render a <see cref="PolicyAction"/> as a webmaster-readable sentence
///     fragment for Band 2 (Body) of <c>_RuleCard.cshtml</c>. The seven action
///     kinds map to plain verbs (allow / observe / block / tag as ... /
///     challenge with ... / rate-limit to ... / throttle to ...). Observe-mode
///     prepends <c>"would "</c>; presence of a trigger (sustain/recover
///     hysteresis) appends <c>" &#183; hysteresis"</c>. The full
///     sustain/recover/armed payload lives in the expand surface (B8), not the
///     headline -- this fragment is the single-line THEN clause.
///     Spec: docs/superpowers/specs/2026-06-24-policy-stack-3band-card-design.md
/// </summary>
public static class ActionVerbFormatter
{
    /// <summary>
    ///     Convert <paramref name="action"/> to its THEN-clause sentence
    ///     fragment.
    /// </summary>
    /// <param name="action">The rule's authored action.</param>
    /// <param name="observeMode">
    ///     When <c>true</c>, prepend <c>"would "</c> so the operator reads the
    ///     row as a counterfactual ("would block" rather than "block"). Sourced
    ///     from <see cref="PolicyRule.Mode"/> == <see cref="PolicyMode.Observe"/>.
    /// </param>
    /// <param name="hasTrigger">
    ///     When <c>true</c>, append <c>" &#183; hysteresis"</c> as a marker
    ///     that this rule arms / disarms via a <see cref="RuleTriggerOptions"/>
    ///     sustain-recover machine. Sourced from <c>PolicyRule.Trigger is not
    ///     null</c>.
    /// </param>
    /// <returns>Webmaster-readable THEN sentence fragment.</returns>
    public static string ToSentence(PolicyAction action, bool observeMode, bool hasTrigger)
    {
        var verb = action switch
        {
            PolicyAction.Allow             => "allow",
            PolicyAction.Observe           => "observe (no action)",
            PolicyAction.Block             => "block",
            PolicyAction.Tag t             => $"tag as {t.Name}",
            PolicyAction.Challenge c       => $"challenge with {c.Kind}",
            PolicyAction.RateLimit r       => $"rate-limit to {r.RequestsPerMinute}/min",
            PolicyAction.Throttle th       =>
                $"throttle to {th.RequestsPerSecond} rps"
                + (string.IsNullOrEmpty(th.Reason) ? string.Empty : $" (reason: {th.Reason})"),
            _                              => action.ToString().ToLowerInvariant()
        };

        var prefix = observeMode ? "would " : string.Empty;
        var suffix = hasTrigger ? " · hysteresis" : string.Empty;
        return $"{prefix}{verb}{suffix}";
    }
}
