using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Middleware;

/// <summary>
///     Middleware that performs geo-based routing and blocking
/// </summary>
public class GeoRoutingMiddleware
{
    public const string GeoLocationKey = "GeoLocation";
    public const string GeoBlockResultKey = "GeoBlockResult";
    private readonly ILogger<GeoRoutingMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly GeoRoutingOptions _options;

    public GeoRoutingMiddleware(
        RequestDelegate next,
        ILogger<GeoRoutingMiddleware> logger,
        IOptions<GeoRoutingOptions> options)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context, IGeoLocationService geoService)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        // Test mode: Allow overriding country via header (off by default)
        // Usage: ml-geo-test-mode: disable  (bypasses all geo-routing)
        //        ml-geo-test-mode: US       (simulates US traffic)
        var testMode = context.Request.Headers["ml-geo-test-mode"].FirstOrDefault();
        if (!string.IsNullOrEmpty(testMode) && _options.EnableTestMode)
        {
            // If "disable", bypass all geo-routing
            if (testMode.Equals("disable", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Test mode: Geo-routing disabled for this request");
                await _next(context);
                return;
            }

            // Otherwise, simulate traffic from specified country
            var testLocation = new GeoLocation
            {
                CountryCode = testMode.ToUpperInvariant(),
                CountryName = testMode,
                ContinentCode = "TEST"
            };

            _logger.LogInformation("Test mode: Simulating request from {Country}", testMode);

            if (_options.StoreInContext) context.Items[GeoLocationKey] = testLocation;

            if (_options.AddCountryHeader)
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.TryAdd("X-Country", testLocation.CountryCode);
                    context.Response.Headers.TryAdd("X-Test-Mode", "true");
                    return Task.CompletedTask;
                });

            var testBlockResult = EvaluateBlockingRules(testLocation, "test-ip");
            if (testBlockResult.IsBlocked)
            {
                context.Items[GeoBlockResultKey] = testBlockResult;

                if (_options.OnBlocked != null)
                {
                    await _options.OnBlocked(context, testBlockResult);
                    return;
                }

                context.Response.StatusCode = _options.BlockedStatusCode;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Access Restricted",
                    message = testBlockResult.BlockReason,
                    country = testLocation.CountryCode,
                    testMode = true
                });
                return;
            }

            await _next(context);
            return;
        }

        var ipAddress = GetClientIpAddress(context);
        if (string.IsNullOrEmpty(ipAddress))
        {
            await _next(context);
            return;
        }

        // Check if IP is whitelisted
        if (_options.WhitelistedIps?.Any() == true)
            if (IsWhitelisted(ipAddress, _options.WhitelistedIps))
            {
                _logger.LogDebug("IP {IP} is whitelisted, bypassing geo restrictions", ipAddress);
                await _next(context);
                return;
            }

        // Get geographic location
        var location = await geoService.GetLocationAsync(ipAddress, context.RequestAborted);

        if (location == null)
        {
            _logger.LogWarning("Could not determine location for IP {IP}", ipAddress);
            await _next(context);
            return;
        }

        // If local geo returned "Private Network" but a trusted upstream gateway forwarded
        // the real country code, prefer that (handles gateway → website proxy scenario)
        if (IsPrivateNetworkResult(location))
        {
            var gatewayCountry = context.Request.Headers["X-Bot-Detection-Country"].ToString();
            if (!string.IsNullOrEmpty(gatewayCountry) && gatewayCountry != "LOCAL")
            {
                location = new Models.GeoLocation
                {
                    CountryCode = gatewayCountry,
                    CountryName = gatewayCountry, // Name not available from header, code is sufficient
                    IsVpn = location.IsVpn,
                    IsHosting = location.IsHosting
                };
                _logger.LogDebug("Using gateway-forwarded country {Country} instead of Private Network", gatewayCountry);
            }
        }

        // Store in context for downstream use
        if (_options.StoreInContext) context.Items[GeoLocationKey] = location;

        // Add country header
        if (_options.AddCountryHeader)
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.TryAdd("X-Country", location.CountryCode);
                if (!string.IsNullOrEmpty(location.RegionCode))
                    context.Response.Headers.TryAdd("X-Region", location.RegionCode);
                return Task.CompletedTask;
            });

        // Check blocking rules
        var blockResult = EvaluateBlockingRules(location, ipAddress);

        if (blockResult.IsBlocked)
        {
            context.Items[GeoBlockResultKey] = blockResult;

            _logger.LogInformation(
                "Blocking request from {Country} ({IP}): {Reason}",
                location.CountryCode, ipAddress, blockResult.BlockReason);

            // Custom blocked handler
            if (_options.OnBlocked != null)
            {
                await _options.OnBlocked(context, blockResult);
                return;
            }

            // Redirect to blocked page or return status code
            if (!string.IsNullOrEmpty(_options.BlockedPagePath))
            {
                context.Response.Redirect(_options.BlockedPagePath);
                return;
            }

            context.Response.StatusCode = _options.BlockedStatusCode;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Access Restricted",
                message = blockResult.BlockReason,
                country = location.CountryCode,
                statusCode = _options.BlockedStatusCode
            });
            return;
        }

        // Country-based routing
        if (_options.EnableAutoRouting && _options.CountryRoutes.Any())
            if (_options.CountryRoutes.TryGetValue(location.CountryCode, out var route))
                // Don't redirect if already on the correct route
                if (!context.Request.Path.StartsWithSegments(route))
                {
                    _logger.LogInformation(
                        "Routing {Country} traffic to {Route}",
                        location.CountryCode, route);

                    if (_options.OnRouted != null) await _options.OnRouted(context, location);

                    context.Response.Redirect(route + context.Request.Path + context.Request.QueryString);
                    return;
                }

        await _next(context);
    }

    private GeoBlockResult EvaluateBlockingRules(GeoLocation location, string ipAddress)
    {
        var result = new GeoBlockResult
        {
            Location = location,
            IpAddress = ipAddress,
            IsBlocked = false
        };

        // Check VPN blocking
        if (_options.BlockVpns && location.IsVpn)
        {
            result.IsBlocked = true;
            result.BlockReason = "VPN/Proxy traffic is not allowed";
            return result;
        }

        // Check hosting/datacenter blocking
        if (_options.BlockHosting && location.IsHosting)
        {
            result.IsBlocked = true;
            result.BlockReason = "Datacenter/hosting traffic is not allowed";
            return result;
        }

        // Check allowed countries (whitelist mode)
        if (_options.AllowedCountries?.Any() == true)
            if (!_options.AllowedCountries.Contains(location.CountryCode, StringComparer.OrdinalIgnoreCase))
            {
                result.IsBlocked = true;
                result.BlockReason = $"Site is only available in: {string.Join(", ", _options.AllowedCountries)}";
                return result;
            }

        // Check blocked countries (blacklist mode)
        if (_options.BlockedCountries?.Any() == true)
            if (_options.BlockedCountries.Contains(location.CountryCode, StringComparer.OrdinalIgnoreCase))
            {
                result.IsBlocked = true;
                result.BlockReason = $"Access from {location.CountryName} ({location.CountryCode}) is not allowed";
                return result;
            }

        return result;
    }

    private string? GetClientIpAddress(HttpContext context)
    {
        // Check for forwarded IP first (behind proxy/load balancer)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ips.Length > 0) return ips[0].Trim();
        }

        // Check X-Real-IP header
        var realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(realIp)) return realIp;

        // Fall back to connection remote IP
        return context.Connection.RemoteIpAddress?.ToString();
    }

    private static bool IsPrivateNetworkResult(Models.GeoLocation location)
    {
        if (string.IsNullOrEmpty(location.CountryCode) || location.CountryCode == "XX")
            return true;
        if (location.CountryName?.Contains("Private", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        if (location.CountryName?.Contains("Reserved", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return false;
    }

    private bool IsWhitelisted(string ipAddress, string[] whitelist)
    {
        foreach (var whitelistedIp in whitelist)
        {
            // Exact match
            if (ipAddress.Equals(whitelistedIp, StringComparison.OrdinalIgnoreCase)) return true;

            // CIDR notation check (simplified)
            if (whitelistedIp.Contains('/'))
            {
                var prefix = whitelistedIp.Split('/')[0];
                if (ipAddress.StartsWith(prefix.Substring(0, prefix.LastIndexOf('.')))) return true;
            }
        }

        return false;
    }
}

/// <summary>
///     Extension methods for geo-routing middleware
/// </summary>
public static class GeoRoutingMiddlewareExtensions
{
    /// <summary>
    ///     Add geo-routing middleware to the pipeline
    /// </summary>
    public static IApplicationBuilder UseGeoRouting(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<GeoRoutingMiddleware>();
    }

    /// <summary>
    ///     Configure site to be available only in specific countries
    /// </summary>
    public static IApplicationBuilder RestrictToCountries(
        this IApplicationBuilder builder,
        params string[] countryCodes)
    {
        return builder.UseMiddleware<GeoRoutingMiddleware>();
    }
}