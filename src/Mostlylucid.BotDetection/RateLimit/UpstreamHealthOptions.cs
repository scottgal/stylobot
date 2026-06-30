namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Configuration for <see cref="UpstreamHealthGate"/>. Binds to
///     <c>BotDetection:UpstreamHealth</c> in the host configuration.
/// </summary>
/// <remarks>
///     The gate is "either / or": upstream is unhealthy when the 5xx EMA
///     exceeds <see cref="Unhealthy5xxThreshold"/> OR the 4xx EMA exceeds
///     <see cref="Unhealthy4xxThreshold"/>, AND we have seen at least
///     <see cref="MinSampleCount"/> responses. The sample-count floor
///     prevents cold-start false positives (one 500 in the first three
///     requests must not flip the gate).
/// </remarks>
public sealed class UpstreamHealthOptions
{
    /// <summary>
    ///     5xx EMA fraction that flips the gate to unhealthy. Default 0.25:
    ///     when a quarter of recent responses are 5xx, status-derived bot
    ///     detectors stand down because we can no longer tell scanner-shape
    ///     from origin-down-shape.
    /// </summary>
    public double Unhealthy5xxThreshold { get; set; } = 0.25;

    /// <summary>
    ///     4xx EMA fraction (excluding 429) that flips the gate to unhealthy.
    ///     Default 0.5: half the responses being 4xx is the "broken routing /
    ///     ingress misconfigured / 404-everything" outage shape, which would
    ///     otherwise be misread as a single client scanning every path.
    /// </summary>
    public double Unhealthy4xxThreshold { get; set; } = 0.5;

    /// <summary>
    ///     Minimum responses recorded before the gate is allowed to flip
    ///     to unhealthy. Default 10: a cold-starting gateway with a single
    ///     500 in its first request must not trip 100% of the EMA and
    ///     hand attackers a window where 404-scan signals are suppressed.
    /// </summary>
    public int MinSampleCount { get; set; } = 10;
}