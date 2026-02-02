using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Extensions;

/// <summary>
///     Extension methods for registering Stylobot Dashboard services.
/// </summary>
public static class StyloBotDashboardServiceExtensions
{
    /// <summary>
    ///     Adds Stylobot Dashboard services to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddStyloBotDashboard(
        this IServiceCollection services,
        Action<StyloBotDashboardOptions>? configure = null)
    {
        var options = new StyloBotDashboardOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSignalR();

        // Detection data extraction for ViewComponents
        services.AddSingleton<DetectionDataExtractor>();

        // Event store for in-memory history
        services.AddSingleton<IDashboardEventStore, InMemoryDashboardEventStore>();

        // Background service for summary updates
        services.AddHostedService<DashboardSummaryBroadcaster>();

        // Simulator if enabled
        if (options.EnableSimulator) services.AddHostedService<DashboardSimulatorService>();

        return services;
    }

    /// <summary>
    ///     Maps Stylobot Dashboard endpoints (UI and SignalR hub).
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseStyloBotDashboard(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<StyloBotDashboardOptions>();

        if (!options.Enabled) return app;

        // Broadcast REAL detections to SignalR - must be BEFORE UseEndpoints
        // This runs for ALL requests to capture detection results
        app.UseMiddleware<DetectionBroadcastMiddleware>();

        // Use dashboard middleware for routing dashboard UI requests
        app.UseMiddleware<StyloBotDashboardMiddleware>();

        // Map SignalR hub - this must be inside UseEndpoints
        app.UseEndpoints(endpoints => { endpoints.MapHub<StyloBotDashboardHub>(options.HubPath); });

        return app;
    }

    /// <summary>
    ///     Quick setup: Adds services and middleware with authorization filter.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="authFilter">Authorization filter (return true to allow, false to deny)</param>
    /// <param name="configure">Additional configuration options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddStyloBotDashboard(
        this IServiceCollection services,
        Func<HttpContext, Task<bool>> authFilter,
        Action<StyloBotDashboardOptions>? configure = null)
    {
        return services.AddStyloBotDashboard(options =>
        {
            options.AuthorizationFilter = authFilter;
            configure?.Invoke(options);
        });
    }
}