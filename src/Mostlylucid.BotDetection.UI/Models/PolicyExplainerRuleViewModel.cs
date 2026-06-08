namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Per-rule trace row in the explainer panel. Mirrors the visible Effective
///     tab row but adds the per-term breakdown plus the winner / skipped
///     markers so the operator can read "this rule was the winner because
///     these terms matched; the others were not evaluated because an earlier
///     rule won".
/// </summary>
/// <param name="RuleId">Stable rule identifier. Becomes the row's <c>data-rule-id</c>.</param>
/// <param name="SourcePill">Uppercase pill text -- <c>ENDPOINT</c> / <c>SUBDOMAIN</c> / <c>DOMAIN</c> / <c>GLOBAL</c>.</param>
/// <param name="ActionVerdict">Pre-rendered human-readable verdict (mirrors the Effective tab row).</param>
/// <param name="ActionColorClass">Canonical dashboard verdict colour class.</param>
/// <param name="IsWinner"><c>true</c> when this rule decided the replayed cycle. Drives the "WOULD APPLY" badge.</param>
/// <param name="WasEvaluated"><c>false</c> means the rule was never run because an earlier rule already won. <see cref="Terms"/> will be empty and <see cref="SkipReason"/> will be populated.</param>
/// <param name="PredicateText">Round-tripped predicate text used as the row headline.</param>
/// <param name="Terms">Per-term outcomes in evaluation order. Empty when <see cref="WasEvaluated"/> is <c>false</c>.</param>
/// <param name="TotalEvalMicros">Sum of every term's lookup + compare cost in microseconds.</param>
/// <param name="SkipReason">Human-readable explanation when <see cref="WasEvaluated"/> is <c>false</c>; <c>null</c> otherwise.</param>
public sealed record PolicyExplainerRuleViewModel(
    Guid RuleId,
    string SourcePill,
    string ActionVerdict,
    string ActionColorClass,
    bool IsWinner,
    bool WasEvaluated,
    string PredicateText,
    IReadOnlyList<PolicyExplainerTermViewModel> Terms,
    long TotalEvalMicros,
    string? SkipReason);
