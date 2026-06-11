using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Read-side probe the dashboard uses to ask whether a materialized
///     rule has diverged from its template's last materialization. The
///     answer drives the "diverged from template" banner on
///     <c>_RuleRow.cshtml</c> (T4 Part B): the banner only renders for
///     rules whose <see cref="PolicyRule.Origin"/> is non-null AND whose
///     id is in the probe's diverged-set.
///
///     <para>
///         FOSS supplies <see cref="YamlRuleDivergenceProbe"/>, which reads
///         <c>YamlPolicyRuleStore.DivergedRuleIds</c> -- an in-memory set
///         that's empty until the first reload after process start.
///         Commercial overrides this DI registration with a Postgres-backed
///         probe that consults the <c>policy_rules.override_diverged</c>
///         column (durable across restarts).
///     </para>
///
///     <para>
///         The probe is intentionally minimal: one method, one input, one
///         output. The dashboard does not care WHY a rule diverged; it
///         only needs to know whether to render the banner. Operator
///         remediation flows live elsewhere -- template re-apply, detach,
///         delete -- and they touch the rule store directly, not this
///         probe.
///     </para>
/// </summary>
public interface IRuleDivergenceProbe
{
    /// <summary>
    ///     Returns <c>true</c> when the rule with this id is currently
    ///     diverged from its template's last materialization. Returns
    ///     <c>false</c> for unknown ids, for hand-authored rules with no
    ///     origin, and on every probe before any template materialization
    ///     has happened in this process.
    /// </summary>
    bool IsDiverged(Guid ruleId);
}
