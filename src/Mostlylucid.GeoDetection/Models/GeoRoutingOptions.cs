using Microsoft.AspNetCore.Http;

namespace Mostlylucid.GeoDetection.Models;

/// <summary>
///     Result of geo-blocking check
/// </summary>
public class GeoBlockResult
{
    /// <summary>
    ///     Whether the request should be blocked
    /// </summary>
    public bool IsBlocked { get; set; }

    /// <summary>
    ///     Reason for blocking
    /// </summary>
    public string? BlockReason { get; set; }

    /// <summary>
    ///     Geographic location of the request
    /// </summary>
    public GeoLocation? Location { get; set; }

    /// <summary>
    ///     IP address that was checked
    /// </summary>
    public string? IpAddress { get; set; }
}

/// <summary>
///     Options for geo-based routing
/// </summary>
public class GeoRoutingOptions
{
    /// <summary>
    ///     Enable country detection and routing
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Enable test mode (allows X-Test-Country header to override detection)
    ///     Only enable in development/testing environments!
    /// </summary>
    public bool EnableTestMode { get; set; } = false;

    /// <summary>
    ///     Countries allowed to access the site (if set, all others are blocked)
    ///     Example: new[] { "US", "GB", "CA" } - Only these countries can access
    /// </summary>
    public string[]? AllowedCountries { get; set; }

    /// <summary>
    ///     Countries blocked from accessing the site
    ///     Example: new[] { "CN", "RU", "KP" } - These countries are blocked
    /// </summary>
    public string[]? BlockedCountries { get; set; }

    /// <summary>
    ///     IPs that bypass all geo restrictions
    ///     Example: new[] { "1.2.3.4", "5.6.7.0/24" }
    /// </summary>
    public string[]? WhitelistedIps { get; set; }

    /// <summary>
    ///     Default country route if detection fails
    /// </summary>
    public string DefaultRoute { get; set; } = "/";

    /// <summary>
    ///     Country-specific route mappings
    ///     Example: { "US": "/en-us", "GB": "/en-gb", "FR": "/fr" }
    /// </summary>
    public Dictionary<string, string> CountryRoutes { get; set; } = new();

    /// <summary>
    ///     Status code to return when blocked (default: 451 Unavailable For Legal Reasons)
    /// </summary>
    public int BlockedStatusCode { get; set; } = 451;

    /// <summary>
    ///     Custom blocked page path (optional)
    /// </summary>
    public string? BlockedPagePath { get; set; }

    /// <summary>
    ///     Block VPN/proxy traffic
    /// </summary>
    public bool BlockVpns { get; set; } = false;

    /// <summary>
    ///     Block hosting/datacenter traffic (often bots)
    /// </summary>
    public bool BlockHosting { get; set; } = false;

    /// <summary>
    ///     Add X-Country header to responses
    /// </summary>
    public bool AddCountryHeader { get; set; } = true;

    /// <summary>
    ///     Store country in HttpContext.Items for downstream use
    ///     Key: "GeoLocation"
    /// </summary>
    public bool StoreInContext { get; set; } = true;

    /// <summary>
    ///     Enable country-based routing (automatic redirects)
    /// </summary>
    public bool EnableAutoRouting { get; set; } = false;

    /// <summary>
    ///     Custom action when a request is blocked
    /// </summary>
    public Func<HttpContext, GeoBlockResult, Task>? OnBlocked { get; set; }

    /// <summary>
    ///     Custom action when a request is routed
    /// </summary>
    public Func<HttpContext, GeoLocation, Task>? OnRouted { get; set; }
}