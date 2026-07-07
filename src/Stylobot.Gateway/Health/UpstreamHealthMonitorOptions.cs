namespace Stylobot.Gateway.Health;

/// <summary>
/// Options for the active upstream health monitor.
/// Bound from <c>BotDetection:UpstreamHealth</c> in appsettings.json.
/// </summary>
public class UpstreamHealthMonitorOptions
{
    public const string SectionName = "BotDetection:UpstreamHealth";

    /// <summary>
    /// Whether active upstream health probing is enabled.
    /// Default: false (opt-in).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Ordered candidate paths probed on each upstream to discover a health endpoint.
    /// Operator-overridable; first path that returns 2xx wins.
    /// </summary>
    public List<string> CandidatePaths { get; set; } =
    [
        "/health",
        "/healthz",
        "/livez",
        "/readyz",
        "/ready",
        "/live",
        "/ping",
        "/status",
        "/alive",
    ];

    /// <summary>
    /// How often (in seconds) each discovered upstream health endpoint is probed.
    /// Default: 60.
    /// </summary>
    public int ProbeIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// HTTP request timeout (in milliseconds) for each probe attempt.
    /// Default: 2000.
    /// </summary>
    public int ProbeTimeoutMs { get; set; } = 2000;
}
