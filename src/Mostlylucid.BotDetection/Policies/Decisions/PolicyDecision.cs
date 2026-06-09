using Mostlylucid.BotDetection.Policies.Rules;

// "PolicyAction" is ambiguous inside the Mostlylucid.BotDetection.Policies tree
// (legacy enum at the parent namespace shadows the new record at .Rules), so
// the Decisions surface uses RuleAction as the alias for the record hierarchy.
// See SqlitePolicyDecisionLog for the full explanation.
using RuleAction = Mostlylucid.BotDetection.Policies.Rules.PolicyAction;

namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     One row in the policy decision log: a per-rule observation of a single
///     policy evaluation. Every rule that the evaluator touched for a given
///     request emits one of these, regardless of whether the rule's predicate
///     matched or whether the rule was the winner.
/// </summary>
/// <remarks>
///     <para>
///         The shape is deliberately a flat record so the writer side can
///         serialise straight to SQLite columns without any DTO mapping layer
///         in the hot path.
///     </para>
///     <para>
///         <see cref="RuleId"/> identifies the rule this row is about.
///         <see cref="WinnerRuleId"/> is the rule that actually decided the
///         request. When they match, this rule is the winner. When they
///         differ, this rule was overridden by a higher-priority sibling --
///         the "matched but lost" case the explainer surfaces.
///     </para>
/// </remarks>
/// <param name="RuleId">The rule this row is observing.</param>
/// <param name="WinnerRuleId">The rule whose action was applied for the request.</param>
/// <param name="Matched"><c>true</c> if <paramref name="RuleId"/>'s predicate evaluated true on this request.</param>
/// <param name="RequestFingerprint">Fingerprint id of the request being evaluated; null if no fingerprint was attached.</param>
/// <param name="Action">The action this rule would have applied had it been the winner. Serialised via its record Kind name.</param>
/// <param name="Mode">The lifecycle mode of the rule at decision time.</param>
/// <param name="EvalLatencyTicks">Stopwatch-tick cost of evaluating this rule's predicate.</param>
/// <param name="ObservedAt">Wall-clock time the decision was recorded.</param>
/// <param name="SignalsSnapshot">
///     Optional serialised JSON snapshot of the signals dictionary the
///     evaluator used for this request. Powers C8's backtest projection:
///     given a candidate predicate, the runner needs the input the original
///     decision was made against. Rows written before the C8 schema patch
///     have <c>null</c> here and surface an inconclusive caveat from the
///     backtest result.
/// </param>
public sealed record PolicyDecision(
    Guid RuleId,
    Guid WinnerRuleId,
    bool Matched,
    string? RequestFingerprint,
    RuleAction Action,
    PolicyMode Mode,
    long EvalLatencyTicks,
    DateTimeOffset ObservedAt,
    string? SignalsSnapshot = null);
