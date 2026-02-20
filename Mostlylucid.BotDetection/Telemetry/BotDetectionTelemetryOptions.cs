namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Configuration for the BotDetection OpenTelemetry instrumentation adapter.
///     Controls which telemetry signals are emitted when detection completes.
/// </summary>
public sealed class BotDetectionTelemetryOptions
{
    /// <summary>
    ///     Enable Prometheus-compatible counters and histograms.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    ///     Enable span attributes from the signal allowlist.
    /// </summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>
    ///     Emit per-contribution span events showing the scoring journey
    ///     (each detector's delta, weight, cumulative score).
    /// </summary>
    public bool EnableScoreJourney { get; set; } = true;

    /// <summary>
    ///     Override the default signal allowlist (keys promoted to span attributes).
    ///     When null, uses <see cref="SignalAllowlist.Default" />.
    /// </summary>
    public HashSet<string>? SignalAllowlist { get; set; }
}
