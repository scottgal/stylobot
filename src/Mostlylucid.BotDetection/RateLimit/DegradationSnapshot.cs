namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Point-in-time snapshot of <see cref="DegradationAtom"/>'s rolling EWMA
///     arms, captured by <see cref="DegradationHistorySampler"/> on every
///     Tick10s. One record per sample; the dashboard's site-health widget
///     reads a contiguous slice of these to render time-series chartlets.
///     <para>
///         The atom itself is process-local truth-at-instant; the history ring
///         lives in <see cref="DegradationHistoryAtom"/> on the gateway. The
///         dashboard host is a thin REST client per
///         <c>project_gateway_data_locality</c> and never caches.
///     </para>
///     <para>
///         <see cref="LatencyP50Ms"/> and <see cref="LatencyP95Ms"/> both come
///         from the atom's single <see cref="DegradationAtom.LatencyEmaMs"/>
///         arm today (the atom does not yet hold percentile estimators). The
///         schema reserves the two slots so a future arm-out can populate
///         distinct p50 / p95 without a snapshot-shape break.
///     </para>
/// </summary>
/// <param name="TimestampUtc">When the sample was taken (UTC, ScheduleCoordinator tick).</param>
/// <param name="Latency5xxRate">EWMA of 5xx response fraction.</param>
/// <param name="Latency4xxRate">EWMA of 4xx response fraction (excludes 429).</param>
/// <param name="Latency429Rate">EWMA of 429 response fraction.</param>
/// <param name="LatencyP50Ms">EWMA-derived p50 latency estimate (ms).</param>
/// <param name="LatencyP95Ms">EWMA-derived p95 latency estimate (ms).</param>
/// <param name="NotFoundRate">EWMA of 404 response fraction.</param>
public sealed record DegradationSnapshot(
    DateTime TimestampUtc,
    double Latency5xxRate,
    double Latency4xxRate,
    double Latency429Rate,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double NotFoundRate);