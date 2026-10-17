using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Services;
namespace Mostlylucid.BotDetection.Dashboard.Data;

/// <summary>Backend persistence, query caches and invalidation hub, without dashboard rendering.</summary>
public static class DashboardDataServiceExtensions
{
    public static IServiceCollection AddDashboardData(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<Queries.TrafficQueryService>();
        // Shared options. This host maps the SignalR hub, so these options ARE the hub's
        // authorization inputs -- they are not "no UI needed" bookkeeping.
        //
        // This used to be a bare `new StyloBotDashboardOptions { Enabled = true }`. On the
        // gateway that meant RequireAuthentication=false, Auth.Mode=None,
        // AuthorizationFilter=null, RequireAuthorizationPolicy=null,
        // AllowUnauthenticatedAccess=false and IsDevelopment()=false under Staging -- so
        // every auth branch fell through to DENY, deterministically, no matter what the
        // website resolved for the page. The hub aborted every connection with "dashboard
        // auth failed" and live updates were dead (2026-08-09).
        //
        // Resolved through DashboardAccessResolver, the SAME function the website host uses,
        // rather than by copying its config block here: two hosts reading the same env var
        // independently is the two-sources-of-truth trap that caused this. Registered as a
        // FACTORY so IConfiguration / IHostEnvironment are resolved from DI at build time.
        // Still TryAdd, so a host that configures its own options explicitly still wins.
        services.TryAddSingleton(sp =>
        {
            var options = new StyloBotDashboardOptions { Enabled = true };
            return DashboardAccessResolver.ApplyAccessPosture(
                options,
                sp.GetService<IConfiguration>(),
                sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.EnvironmentName
                    .Equals("Development", StringComparison.OrdinalIgnoreCase) ?? false);
        });

        // SignalR for broadcasting to connected dashboard clients
        services.AddSignalR();
        services.AddAotSafeDashboardHubContext(); // AOT-safe typed-hub wrapper (no-op on JIT)

        // Event store: SQLite by default (persists across restarts).
        // PostgreSQL package overrides via RemoveAll + AddSingleton when configured.
        services.TryAddSingleton<IDashboardEventStore, SqliteDashboardEventStore>();

        // Operator-supplied signature labels for detector weighting / ground truth
        // (in-memory by default; production wires a SQLite or PostgreSQL implementation).
        services.TryAddSingleton<ISignatureLabelStore, SqliteSignatureLabelStore>();

        // Visitor "flag as wrong" feedback store. Owned by the enforcement component
        // (this host runs detection); the upstream site POSTs to /api/v1/feedback
        // rather than writing it directly. PostgreSQL pack overrides via RemoveAll.
        services.TryAddSingleton<IDetectionFeedbackStore, SqliteDetectionFeedbackStore>();

        // Aggregate cache - populated by beacon, read by API endpoints
        services.TryAddSingleton<DashboardAggregateCache>();

        // Write-through signature cache - single source of truth for top bots
        services.TryAddSingleton<SignatureAggregateCache>();

        // Stateless UA aggregator - used by broadcaster beacon + view components with params
        services.TryAddSingleton<DashboardUserAgentAggregator>();

        // Warm the SignatureAggregateCache from DB on startup so Top Bots / Live Activity /
        // visitor list aren't empty after restart. Cache is in-memory read-through;
        // persistence is the source of truth in IDashboardEventStore.
        //
        // Gateway-only per project_gateway_data_locality. On remote-mode hosts
        // (marketing site, stylobot-ui) the warmup would issue DB queries against
        // a store that doesn't belong to this process AND freeze the cache at a
        // stale startup snapshot — DetectionBroadcastMiddleware runs only on the
        // gateway, so nothing refreshes the cache afterwards. That produces the
        // classic "home YOU card shows 100% / signature detail shows 7% for the
        // same sig" divergence: home reads cache (stale), detail reads event
        // store (current). Skip the warmup when AddStyloBotDashboardRemote has
        // already registered DashboardSourceOptions; the cache stays registered
        // (satisfies DI for middleware / view-components) but stays empty, so
        // all read paths cleanly fall through to IDashboardEventStore — which
        // on remote-mode hosts is RemoteDashboardEventStore (REST → gateway).
        // One source of truth.
        if (!services.Any(s => s.ServiceType == typeof(Mostlylucid.BotDetection.UI.Adapters.Remote.DashboardSourceOptions)))
        {
            services.AddHostedService<SignatureAggregateCacheWarmupService>();
        }

        // LLM result callback (needed if LLM classification is enabled)
        services.TryAddSingleton<ILlmResultCallback, LlmResultSignalRCallback>();

        // Cluster description callback for live cluster updates
        services.TryAddSingleton<IClusterDescriptionCallback, ClusterDescriptionSignalRCallback>();

        return services;
    }
    /// <summary>Persist detections and expose invalidation without referencing the Razor UI.</summary>
    public static IApplicationBuilder UseDashboardData(this IApplicationBuilder app)
    {
        app.UseMiddleware<DetectionBroadcastMiddleware>();
        var options = app.ApplicationServices.GetService<StyloBotDashboardOptions>();
        if (app is IEndpointRouteBuilder endpoints)
            endpoints.MapHub<StyloBotDashboardHub>(options?.HubPath ?? "/stylobot/hub");
        return app;
    }
}
