using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
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
    ///     Adds StyloBot UI services (tag helpers, view components, detection data extraction)
    ///     WITHOUT the full dashboard route or SignalR hub.
    ///     <para>
    ///     Use this when you want to embed individual StyloBot widgets in your own pages
    ///     using tag helpers like <c>&lt;sb-badge /&gt;</c>, <c>&lt;sb-gate&gt;</c>, etc.
    ///     </para>
    ///     <para>
    ///     For the full standalone dashboard at a configurable route, use
    ///     <see cref="AddStyloBotDashboard(IServiceCollection, Action{StyloBotDashboardOptions}?)"/> instead.
    ///     </para>
    /// </summary>
    /// <example>
    ///     Lightweight setup (tag helpers only):
    ///     <code>
    ///     builder.Services.AddBotDetection();
    ///     builder.Services.AddStyloBotUI();
    ///     // Now use &lt;sb-badge /&gt;, &lt;sb-gate&gt;, &lt;sb-human&gt; etc. in your Razor views
    ///     </code>
    /// </example>
    public static IServiceCollection AddStyloBotUI(this IServiceCollection services)
    {
        services.AddHttpContextAccessor(); // Needed by sb-* gating TagHelpers

        // Detection data extraction for ViewComponents and TagHelpers
        services.TryAddSingleton<DetectionDataExtractor>();

        return services;
    }

    /// <summary>
    ///     Adds Stylobot Dashboard services to the service collection.
    ///     This is the "batteries included" option: full dashboard route, SignalR hub,
    ///     all widget partials, real-time updates, API endpoints.
    ///     <para>
    ///     Internally calls <see cref="AddStyloBotUI"/> so all tag helpers are available too.
    ///     </para>
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

        // Register lightweight UI services (tag helpers, view components)
        services.AddStyloBotUI();

        services.AddSignalR();

        // Ensure MVC services are available for Razor view rendering (idempotent)
        services.AddControllersWithViews();

        // Razor view renderer for middleware-hosted dashboard
        services.AddSingleton<RazorViewRenderer>();

        // Event store for in-memory history
        services.AddSingleton<IDashboardEventStore, InMemoryDashboardEventStore>();

        // Aggregate cache — populated by beacon, read by API endpoints
        services.AddSingleton<DashboardAggregateCache>();

        // Write-through signature cache — single source of truth for top bots
        services.AddSingleton<SignatureAggregateCache>();

        // Background beacon — computes all dashboard aggregates periodically
        services.AddHostedService<DashboardSummaryBroadcaster>();

        // Server-side visitor cache for HTMX rendering
        services.AddSingleton<VisitorListCache>();

        // BDF export service for generating BDF v2 documents from detection data
        services.AddSingleton<BdfExportService>();

        // Warm visitor cache from DB on startup so "Top Bots" isn't empty after restarts
        services.AddHostedService<VisitorCacheWarmupService>();

        // LLM result callback for background classification coordinator
        services.TryAddSingleton<ILlmResultCallback, LlmResultSignalRCallback>();

        // Cluster description callback for background LLM cluster naming
        services.TryAddSingleton<IClusterDescriptionCallback, ClusterDescriptionSignalRCallback>();

        // Register dashboard data API paths with the bot detection policy system.
        // Detection runs on ALL paths including dashboard API — no exclusions.
        // BotDetectionMiddleware resolves the detection policy for these paths
        // and applies the configured action policy automatically.
        services.PostConfigure<BotDetectionOptions>(opts =>
        {
            var policyName = options.DataApiDetectionPolicy;
            var basePath = options.BasePath.TrimEnd('/');

            if (!opts.Policies.TryGetValue(policyName, out var policyConfig) || policyConfig == null)
            {
                policyConfig = new DetectionPolicyConfig();
                opts.Policies[policyName] = policyConfig;
            }

            if (string.IsNullOrWhiteSpace(policyConfig.ActionPolicyName))
                policyConfig.ActionPolicyName = options.DataApiActionPolicyName;
            policyConfig.ActionPolicyOverridable = true;

            opts.PathPolicies[$"{basePath}/api/**"] = policyName;
        });

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

        // Detection runs on ALL paths including dashboard API — no exclusions.
        // FastPathReputation fix (UA patterns don't trigger early exit) prevents feedback loops.

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

    // ==========================================
    // Lightweight persistence (for gateways/proxies)
    // ==========================================

    /// <summary>
    ///     Adds detection persistence services WITHOUT the full dashboard UI.
    ///     Use this in gateways/proxies that run detection and should save results
    ///     to the shared database, but don't serve the dashboard page.
    ///     <para>
    ///     Registers: event store, SignalR hub, broadcast middleware, visitor cache.
    ///     Does NOT register: dashboard UI, simulator, ViewComponent data extraction.
    ///     </para>
    /// </summary>
    /// <example>
    ///     Gateway setup:
    ///     <code>
    ///     builder.Services.AddBotDetection();
    ///     builder.Services.AddBotDetectionPersistence();
    ///     // ...
    ///     app.UseBotDetection();
    ///     app.UseBotDetectionPersistence(); // saves detections to shared DB
    ///     </code>
    /// </example>
    public static IServiceCollection AddBotDetectionPersistence(this IServiceCollection services)
    {
        // Shared options (Enabled=true but no UI path needed)
        services.TryAddSingleton(new StyloBotDashboardOptions { Enabled = true });

        // SignalR for broadcasting to connected dashboard clients
        services.AddSignalR();

        // Event store (in-memory by default, replaced by PostgreSQL when configured)
        services.TryAddSingleton<IDashboardEventStore, InMemoryDashboardEventStore>();

        // Aggregate cache — populated by beacon, read by API endpoints
        services.TryAddSingleton<DashboardAggregateCache>();

        // Write-through signature cache — single source of truth for top bots
        services.TryAddSingleton<SignatureAggregateCache>();

        // Server-side visitor cache (needed by broadcast middleware)
        services.TryAddSingleton<VisitorListCache>();

        // Warm visitor cache from DB on startup so "Top Bots" isn't empty after restarts
        services.AddHostedService<VisitorCacheWarmupService>();

        // LLM result callback (needed if LLM classification is enabled)
        services.TryAddSingleton<ILlmResultCallback, LlmResultSignalRCallback>();

        // Cluster description callback for live cluster updates
        services.TryAddSingleton<IClusterDescriptionCallback, ClusterDescriptionSignalRCallback>();

        return services;
    }

    /// <summary>
    ///     Adds the detection broadcast middleware that persists detection results
    ///     to the event store and broadcasts via SignalR.
    ///     Use after <see cref="Mostlylucid.BotDetection.Middleware.BotDetectionMiddlewareExtensions.UseBotDetection"/>.
    ///     <para>
    ///     This is the lightweight counterpart to <see cref="UseStyloBotDashboard"/> -
    ///     it saves detection data but doesn't serve the dashboard UI.
    ///     </para>
    /// </summary>
    public static IApplicationBuilder UseBotDetectionPersistence(this IApplicationBuilder app)
    {
        // Broadcast middleware: persists detections to event store + broadcasts to SignalR
        app.UseMiddleware<DetectionBroadcastMiddleware>();

        // Map SignalR hub so dashboard clients (on other hosts) can connect
        var options = app.ApplicationServices.GetService<StyloBotDashboardOptions>();
        var hubPath = options?.HubPath ?? "/stylobot/hub";
        app.UseEndpoints(endpoints => { endpoints.MapHub<StyloBotDashboardHub>(hubPath); });

        return app;
    }
}
