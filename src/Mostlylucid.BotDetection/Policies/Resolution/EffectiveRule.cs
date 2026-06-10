using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Resolution;

/// <summary>
///     One rule + the source scope it was attached to. Used by the UI to
///     render the "inherited from" badge and by the Stack tab to group by
///     source. <see cref="IsInherited"/> is <c>true</c> when the rule lives
///     on a broader scope than the one the resolver was queried for.
///
///     <para>
///         <see cref="IsArmed"/> is <c>true</c> when the resolver merged the
///         rule in because its <see cref="Triggers.ArmedRuleAtom"/> is
///         currently armed (Phase G runtime). The <see cref="Rule"/> still
///         exposes its authored action; if
///         <see cref="Rules.RuleTriggerOptions.ActionWhileArmed"/> is
///         non-null on the rule's <see cref="Rules.PolicyRule.Trigger"/>, the
///         enforcement pipeline substitutes that action for the regular one
///         while armed. The trailing default keeps every existing positional
///         call site compiling.
///     </para>
/// </summary>
/// <param name="Rule">The underlying <see cref="PolicyRule"/> as stored.</param>
/// <param name="SourceScope">The scope the rule is authored against -- mirror of <see cref="PolicyRule.Scope"/>.</param>
/// <param name="IsInherited"><c>true</c> when <see cref="SourceScope"/> is broader than the queried scope.</param>
/// <param name="IsArmed"><c>true</c> when the rule was merged in from the armed-rule registry (Phase G).</param>
public sealed record EffectiveRule(
    PolicyRule Rule,
    PolicyScope SourceScope,
    bool IsInherited,
    bool IsArmed = false);
