namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Immutable point-in-time result of one active probe tick against a
///     named upstream.
/// </summary>
/// <param name="Status">
///     One of <c>"healthy"</c>, <c>"unhealthy"</c>, or <c>"unknown"</c>.
///     <c>"unknown"</c> is used during warm-up before the first probe
///     completes. <paramref name="FailureReason"/> is non-null when
///     <paramref name="Status"/> is not <c>"healthy"</c>.
/// </param>
/// <param name="LatencyMs">Round-trip latency of the probe in milliseconds.</param>
/// <param name="CheckedAtUtc">Wall-clock time the probe result was recorded.</param>
/// <param name="FailureReason">
///     Human-readable failure detail when <paramref name="Status"/> is not
///     <c>"healthy"</c>; null otherwise.
/// </param>
public sealed record ActiveProbeSnapshot(
    string Status,
    int LatencyMs,
    DateTimeOffset CheckedAtUtc,
    string? FailureReason);
