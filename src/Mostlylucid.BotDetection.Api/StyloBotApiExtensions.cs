using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Api.Middleware;
using Mostlylucid.BotDetection.Llm.Tunnel;

namespace Mostlylucid.BotDetection.Api;

public static class StyloBotApiExtensions
{
    public static IServiceCollection AddStyloBotApi(
        this IServiceCollection services,
        Action<StyloBotApiOptions>? configure = null)
    {
        var options = new StyloBotApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);

        // ApiKeyAuthenticationHandler ctor takes IApiKeyStore. Even if no /api
        // route requires the scheme, .NET auth activates the handler at
        // pipeline build time and needs the dep to resolve. Register the
        // FOSS default in-memory store; operators / commercial packs replace
        // via TryAdd-loses with SQLite / Postgres variants at their own site.
        services.TryAddSingleton<Mostlylucid.BotDetection.Services.IApiKeyStore,
            Mostlylucid.BotDetection.Services.InMemoryApiKeyStore>();

        services.AddAuthorizationBuilder()
            .AddPolicy(ApiKeyAuthenticationHandler.SchemeName, policy =>
                policy.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                      .RequireAuthenticatedUser());

        if (options.EnableOpenApi)
        {
            services.AddOpenApi();
        }

        // Insert the source-generated context AHEAD of the framework default so that
        // Native AOT publishes don't need DefaultJsonTypeInfoResolver (which requires
        // dynamic code). Reflection-based resolution still runs at index >0 for any
        // type not in the context, which keeps the dev-time (non-AOT) build working
        // identically.
        services.ConfigureHttpJsonOptions(opts =>
            opts.SerializerOptions.TypeInfoResolverChain.Insert(0, StyloBotJsonContext.Default));

        services.TryAddSingleton<ILlmNodeRegistry, InMemoryLlmNodeRegistry>();

        // BDF endpoint dependencies: the per-signature export + debug-gated harvest
        // surface both live under /api/v1/bdf, so their services belong here next to
        // MapBdfEndpoints, not in AddStyloBotDashboard (which the gateway never calls).
        // TryAdd is idempotent if AddStyloBotDashboard also registers (dashboard hosts).
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.BdfExportService>();
        services.AddOptions<Mostlylucid.BotDetection.UI.Configuration.BdfHarvestOptions>()
            .BindConfiguration("BotDetection:Debug:BdfHarvest");
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.BdfHarvestService>();

        // Config-baseline read surface (GET /api/v1/policies/config-baseline). The gateway never
        // calls AddStyloBotDashboard, so the composer isn't registered by that path -- but it's a
        // pure-compute service (IOptionsMonitor<BotDetectionOptions> + IActionPolicyRegistry +
        // IEffectivePolicyConfigOverlay, all present once AddBotDetection ran), so wire it here so a
        // remote / thin-client dashboard can read the GATEWAY's composed baseline. Guarded on the
        // real registry (never a null/empty stand-in). TryAdd is idempotent with a dashboard host.
        if (services.Any(sd => sd.ServiceType == typeof(Mostlylucid.BotDetection.Actions.IActionPolicyRegistry)))
        {
            services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.IEffectivePolicyConfigOverlay,
                Mostlylucid.BotDetection.UI.Services.PassthroughEffectivePolicyConfigOverlay>();
            services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.EffectivePolicyComposer>();
            services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.IConfigBaselineProvider>(
                sp => sp.GetRequiredService<Mostlylucid.BotDetection.UI.Services.EffectivePolicyComposer>());
        }

        return services;
    }

    public static IEndpointRouteBuilder MapStyloBotApi(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<StyloBotApiOptions>();

        endpoints.MapDetectEndpoints();
        endpoints.MapReadEndpoints();
        endpoints.MapMeEndpoints();
        endpoints.MapLlmNodeControllerEndpoints();
        endpoints.MapMetricsEndpoints();
        endpoints.MapMetricsSnapshotEndpoints();
        endpoints.MapRoutesEndpoints();
        endpoints.MapClusterEndpoints();
        endpoints.MapLabelEndpoints();
        endpoints.MapApprovalEndpoints();
        endpoints.MapEndpointPinEndpoints();
        endpoints.MapSessionEndpoints();
        endpoints.MapUserAgentEndpoints();
        endpoints.MapInvestigationEndpoints();
        endpoints.MapBdfEndpoints();
        endpoints.MapFeedbackEndpoints();
        endpoints.MapComplianceEndpoints();
        endpoints.MapConfigEndpoints();
        endpoints.MapIdentityEndpoints();
        endpoints.MapEntityEndpoints();
        endpoints.MapSiteHealthHistoryEndpoint();

        if (options.EnableOpenApi)
        {
            endpoints.MapOpenApi("/api/v1/openapi.json");
        }

        return endpoints;
    }

    public static IApplicationBuilder UseStyloBotResponseHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ResponseHeaderInjectionMiddleware>();
    }
}
