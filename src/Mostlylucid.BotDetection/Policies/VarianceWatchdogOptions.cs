namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Per-policy sensitivities for <see cref="Services.VarianceWatchdog"/>. Each check
///     can be independently disabled. Defaults are tuned for general-purpose sites;
///     high-security endpoints should set lower thresholds (more watchdog trips, more
///     pipeline runs).
/// </summary>
public sealed record VarianceWatchdogOptions
{
    /// <summary>Trip when the same primary signature appears from a new /24 within this many seconds. 0 to disable.</summary>
    public int IpRotationWindowSeconds { get; init; } = 300;

    /// <summary>
    ///     Trip when the requested path's family (first non-empty segment, lowercased) is not
    ///     among the bounded set of recently-observed families for this fingerprint. Activates
    ///     once at least three distinct families have been seen so warm-up traffic doesn't trip
    ///     itself. Default true; set false to disable path-divergence checks.
    /// </summary>
    public bool CheckPathCentroid { get; init; } = true;

    /// <summary>Trip when this fingerprint's recent request rate exceeds rolling mean by this multiplier. Default 10x. 0 to disable.</summary>
    public double RateSpikeMultiplier { get; init; } = 10.0;

    /// <summary>Master switch. Default true. Disable for tests or to debug Skip behaviour.</summary>
    public bool Enabled { get; init; } = true;
}
