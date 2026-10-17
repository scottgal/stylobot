using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Extensions;

public static class ApiTrafficEndpointExtensions
{
    /// <summary>Map after registering the dashboard middleware, which supplies the shared authorization gate.</summary>
    public static IEndpointRouteBuilder MapStyloBotDashboardPages(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<StyloBotDashboardOptions>();
        if (!options.Enabled || !options.UseApiTraffic) return endpoints;
        endpoints.MapControllerRoute("StyloBotApiTraffic", options.BasePath.Trim('/') + "/traffic",
            new { controller = "ApiTraffic", action = "Index" })
            .Add(builder => { if (builder is RouteEndpointBuilder route) route.Order = -100; });
        return endpoints;
    }
}
