using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Resolution;

/// <summary>
///     One rule + the source scope it was attached to. Used by the UI to
///     render the "inherited from" badge and by the Stack tab to group by
///     source. <see cref="IsInherited"/> is <c>true</c> when the rule lives
///     on a broader scope than the one the resolver was queried for.
/// </summary>
/// <param name="Rule">The underlying <see cref="PolicyRule"/> as stored.</param>
/// <param name="SourceScope">The scope the rule is authored against -- mirror of <see cref="PolicyRule.Scope"/>.</param>
/// <param name="IsInherited"><c>true</c> when <see cref="SourceScope"/> is broader than the queried scope.</param>
public sealed record EffectiveRule(
    PolicyRule Rule,
    PolicyScope SourceScope,
    bool IsInherited);
