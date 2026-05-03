using Microsoft.AspNetCore.Builder;
using Mostlylucid.GeoDetection.Middleware;

namespace Mostlylucid.GeoDetection.Extensions;

/// <summary>
///     Extension methods for geo-routing middleware configuration
/// </summary>
public static class GeoRoutingExtensions
{
    /// <summary>
    ///     Use geo-routing middleware
    /// </summary>
    public static IApplicationBuilder UseGeoRouting(this IApplicationBuilder app)
    {
        return app.UseMiddleware<GeoRoutingMiddleware>();
    }
}