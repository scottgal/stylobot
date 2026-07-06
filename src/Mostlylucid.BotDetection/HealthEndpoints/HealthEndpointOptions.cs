namespace Mostlylucid.BotDetection.HealthEndpoints;

/// <summary>
///     Configuration for health-endpoint path recognition. Binds to
///     <c>BotDetection:HealthEndpoints</c> in the host configuration.
/// </summary>
/// <remarks>
///     Paths are matched case-insensitively at segment boundaries: a configured
///     path of <c>/health</c> matches both <c>/health</c> and <c>/health/liveness</c>
///     but NOT <c>/healthcheck</c>. This lets health paths with sub-resources
///     (e.g. <c>/health/ready</c>, <c>/health/live</c>) inherit the same
///     "is health" verdict without needing explicit enumeration.
/// </remarks>
public sealed class HealthEndpointOptions
{
    public const string SectionName = "BotDetection:HealthEndpoints";

    /// <summary>
    ///     Path prefixes that identify health / readiness / liveness probes.
    ///     Each entry is matched case-insensitively at segment boundaries by
    ///     <see cref="HealthEndpointCatalog"/>. Defaults to the ten standard
    ///     Kubernetes / cloud-provider probe paths.
    /// </summary>
    public List<string> Paths { get; set; } = new(DefaultPaths);

    /// <summary>
    ///     The ten well-known health-probe paths used by Kubernetes, Azure,
    ///     AWS, GCP, and similar platforms.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPaths =
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
        "/admin/alive",
    ];

    /// <summary>Returns a new instance pre-populated with <see cref="DefaultPaths"/>.</summary>
    public static HealthEndpointOptions Default => new();
}
