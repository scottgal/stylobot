namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Lifecycle mode of a <see cref="PolicyRule"/>. Drives whether the
///     rule actually fires actions, only shadow-logs what it would have
///     done, or is parked out of evaluation entirely.
/// </summary>
public enum PolicyMode
{
    /// <summary>Rule is being authored. Skipped by the evaluator entirely.</summary>
    Draft,

    /// <summary>Rule is matched and logged but its action is not applied. Shadow mode.</summary>
    Observe,

    /// <summary>Rule is matched, logged, and its action is applied. Production posture.</summary>
    Live,

    /// <summary>Rule is parked; the evaluator ignores it just like <see cref="Draft"/> but the rule is preserved on disk.</summary>
    Disabled
}
