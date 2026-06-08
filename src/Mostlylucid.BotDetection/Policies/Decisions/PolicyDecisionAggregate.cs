namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     Windowed aggregate over the policy decision log scoped to a single
///     rule. Powers the compact-row signals on the SbPolicyStack control:
///     trigger count, matched-vs-total (overridden = matched but the winner
///     was someone else), per-action win distribution, and p50/p99 evaluation
///     latency.
/// </summary>
/// <param name="RuleId">The rule the aggregate is scoped to.</param>
/// <param name="Window">The sliding window ending at <see cref="ComputedAt"/>.</param>
/// <param name="Matched">Number of rows in the window where this rule's predicate matched.</param>
/// <param name="TotalEvaluations">Total rows for this rule in the window (matched plus skipped).</param>
/// <param name="WinDistribution">
///     Lowercase action-kind name (e.g. <c>"block"</c>, <c>"challenge"</c>,
///     <c>"allow"</c>) to the number of times this rule was the winner with
///     that action in the window.
/// </param>
/// <param name="P50LatencyMicros">Median predicate-eval latency in microseconds.</param>
/// <param name="P99LatencyMicros">99th-percentile predicate-eval latency in microseconds.</param>
/// <param name="ComputedAt">Wall-clock time the aggregate was assembled.</param>
public sealed record PolicyDecisionAggregate(
    Guid RuleId,
    TimeSpan Window,
    int Matched,
    int TotalEvaluations,
    IReadOnlyDictionary<string, int> WinDistribution,
    long P50LatencyMicros,
    long P99LatencyMicros,
    DateTimeOffset ComputedAt);
