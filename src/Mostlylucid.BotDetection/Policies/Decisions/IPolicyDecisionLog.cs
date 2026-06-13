namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     Append-only log of per-rule policy decisions. The evaluator writes one
///     row per rule it touched for each request; the dashboard reads windowed
///     aggregates and per-fingerprint replays.
/// </summary>
/// <remarks>
///     Implementations are expected to be write-behind (bounded channel +
///     periodic drainer) so the request path never blocks on durable
///     storage. <see cref="FlushAsync"/> is provided for tests and for
///     deterministic shutdown.
/// </remarks>
public interface IPolicyDecisionLog
{
    /// <summary>Append a decision row. Implementations may be write-behind.</summary>
    ValueTask AppendAsync(PolicyDecision decision, CancellationToken ct = default);

    /// <summary>Flush any buffered decisions to durable storage immediately.</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    ///     Read an aggregate over a sliding window ending now. <paramref name="ruleId"/>
    ///     scopes the aggregation to a single rule.
    /// </summary>
    Task<PolicyDecisionAggregate> AggregateAsync(
        Guid ruleId,
        TimeSpan window,
        CancellationToken ct = default);

    /// <summary>
    ///     Read raw decisions for a specific request fingerprint, ordered by
    ///     observation time ascending. Powers the explainer's
    ///     "replay this request" panel.
    /// </summary>
    Task<IReadOnlyList<PolicyDecision>> GetByFingerprintAsync(
        string fingerprint,
        int max = 100,
        CancellationToken ct = default);

    /// <summary>
    ///     Stream every decision row observed within the supplied
    ///     <paramref name="window"/> ending now, ordered by
    ///     <see cref="PolicyDecision.ObservedAt"/> ascending. Powers C8's
    ///     backtest runner: the runner projects a candidate predicate over
    ///     each row's <see cref="PolicyDecision.SignalsSnapshot"/> without
    ///     pulling the entire window into memory.
    /// </summary>
    /// <param name="window">Sliding window ending now.</param>
    /// <param name="maxRows">Hard cap on rows yielded. Defaults to 100k.</param>
    /// <param name="ct">Cancellation token forwarded to the underlying reader.</param>
    IAsyncEnumerable<PolicyDecision> StreamWindowAsync(
        TimeSpan window,
        int maxRows = 100_000,
        CancellationToken ct = default);

    /// <summary>
    ///     Distinct fingerprint ids observed by the log, most-recently-seen first,
    ///     capped at <paramref name="limit"/>. The <c>limit</c> is clamped to at
    ///     least 1 so callers can safely thread an unvalidated config value
    ///     through. Returns empty when the log has no rows.
    /// </summary>
    Task<IReadOnlyList<string>> GetRecentFingerprintsAsync(
        int limit,
        CancellationToken ct = default);
}
