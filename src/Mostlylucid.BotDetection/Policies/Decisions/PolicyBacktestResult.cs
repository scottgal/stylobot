namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     Output of <see cref="PolicyBacktestRunner"/>. Buckets the rows in a
///     window into four counts: total scanned, "would have matched the
///     candidate predicate", "would have been enforced (this rule wins)",
///     and "matched but a higher-priority rule won". Carries percentile
///     latency proxies and a sample of matching fingerprints so the panel
///     can offer a "show matching requests" affordance.
/// </summary>
/// <param name="TotalRequests">Total decision rows scanned in the window.</param>
/// <param name="WouldMatch">Rows where the candidate predicate evaluated true.</param>
/// <param name="WouldEnforce">Rows that matched AND would have won (no higher-priority Allow won).</param>
/// <param name="OverriddenByHigherPriority">Rows that matched but were overridden by a higher-priority rule.</param>
/// <param name="EstimatedAddedP50Micros">Median predicate-eval latency proxy, microseconds.</param>
/// <param name="EstimatedAddedP99Micros">99th-percentile predicate-eval latency proxy, microseconds.</param>
/// <param name="SampleMatchingFingerprints">Up to 25 fingerprints from matched rows.</param>
/// <param name="Window">Window the projection covered.</param>
/// <param name="ComputedAt">Wall-clock time the result was assembled.</param>
/// <param name="Caveat">
///     Optional caveat string when the result is inconclusive --
///     typically because some rows pre-date the C8 schema patch and
///     therefore don't carry a signals snapshot to project the candidate
///     predicate against. <c>null</c> when the result is fully reliable.
/// </param>
public sealed record PolicyBacktestResult(
    int TotalRequests,
    int WouldMatch,
    int WouldEnforce,
    int OverriddenByHigherPriority,
    long EstimatedAddedP50Micros,
    long EstimatedAddedP99Micros,
    IReadOnlyList<string> SampleMatchingFingerprints,
    TimeSpan Window,
    DateTimeOffset ComputedAt,
    string? Caveat = null);
