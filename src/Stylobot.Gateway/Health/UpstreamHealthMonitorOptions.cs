namespace Stylobot.Gateway.Health;

/// <summary>
/// Options for the active upstream health monitor.
/// Bound from <c>BotDetection:UpstreamHealth</c> in appsettings.json.
/// </summary>
public class UpstreamHealthMonitorOptions
{
    public const string SectionName = "BotDetection:UpstreamHealth";

    /// <summary>
    /// Default candidate paths for health endpoint discovery.
    /// </summary>
    private static readonly string[] DefaultCandidatePaths =
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
    /// Whether active upstream health probing is enabled.
    /// Default: false (opt-in).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Ordered candidate paths probed on each upstream to discover a health endpoint.
    /// Operator-overridable; first path that returns 2xx wins.
    /// When overridden via configuration, replaces the default list.
    /// </summary>
    public string[] CandidatePaths { get; set; } = [];

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

    /// <summary>
    /// Apply default candidate paths if none were configured.
    /// Called by PostConfigure during options binding.
    /// </summary>
    public void ApplyDefaults()
    {
        if (CandidatePaths.Length == 0)
        {
            CandidatePaths = DefaultCandidatePaths;
        }
    }
}
