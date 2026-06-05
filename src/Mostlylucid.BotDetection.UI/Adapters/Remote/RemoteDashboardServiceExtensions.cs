using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Registration helper for remote-mode dashboard hosts (stylobot-ui). Wires the typed
///     <see cref="GatewayApiClient"/> with the configured base URL + <c>X-SB-Api-Key</c>
///     header, then registers every <c>Remote*</c> store impl ahead of the dashboard's
///     local SQLite fallbacks. Because every dashboard store registration in
///     <c>AddStyloBotDashboard</c> uses <c>TryAddSingleton</c>, these wins-by-being-first
///     registrations take over without the consumer touching middleware.
///
///     <para>
///     Call this <strong>before</strong> <c>AddStyloBotDashboard</c>. In remote mode the
///     host should also <em>not</em> wire detection middleware (no <c>UseBotDetection</c> /
///     no <c>DetectionBroadcastMiddleware</c>) - the viewer never produces detections.
///     </para>
/// </summary>
public static class RemoteDashboardServiceExtensions
{
    public static IServiceCollection AddStyloBotDashboardRemote(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new DashboardSourceOptions();
        configuration.GetSection("StyloBot:Source").Bind(options);
        return services.AddStyloBotDashboardRemote(options);
    }

    public static IServiceCollection AddStyloBotDashboardRemote(
        this IServiceCollection services,
        DashboardSourceOptions options)
    {
        if (options.Pull.Type != DashboardSourceType.Rest)
            throw new InvalidOperationException(
                $"AddStyloBotDashboardRemote requires Pull.Type = Rest, got '{options.Pull.Type}'.");
        if (string.IsNullOrWhiteSpace(options.Pull.Url))
            throw new InvalidOperationException("StyloBot:Source:Pull:Url is required in rest mode.");

        services.AddSingleton(options);

        services.AddHttpClient<GatewayApiClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(options.Pull.Url!.TrimEnd('/') + "/");
            if (!string.IsNullOrEmpty(options.Pull.ApiKey))
                // Matches Mostlylucid.BotDetection.Api.Auth.ApiKeyAuthenticationHandler.HeaderName
                // (kept as a literal here because UI cannot reference Api - that would cycle).
                http.DefaultRequestHeaders.Add("X-SB-Api-Key", options.Pull.ApiKey);
            http.Timeout = TimeSpan.FromSeconds(options.Pull.TimeoutSeconds);
        });

        // Remote store registrations - go in BEFORE AddStyloBotDashboard so the TryAdd
        // fallbacks (SqliteDashboardEventStore, SqliteSessionStore, etc.) skip themselves.
        services.AddSingleton<IDashboardEventStore, RemoteDashboardEventStore>();
        services.AddSingleton<ISessionStore, RemoteSessionStore>();
        services.AddSingleton<ISignatureLabelStore, RemoteSignatureLabelStore>();
        services.AddSingleton<IFingerprintApprovalStore, RemoteFingerprintApprovalStore>();
        services.AddSingleton<IPinnedEndpointStore, RemotePinnedEndpointStore>();
        services.AddSingleton<IShapeSearchStore, RemoteShapeSearchStore>();
        services.AddSingleton<IFingerprintReader, RemoteFingerprintReader>();
        services.AddSingleton<BotDetection.Identity.BrowserModes.IFingerprintBrowserModeStore,
            RemoteFingerprintBrowserModeStore>();
        services.AddSingleton<IEntityReader, RemoteEntityReader>();
        services.AddSingleton<IConfigEditorService, RemoteConfigEditorService>();
        services.AddSingleton<IBotClusterReader, RemoteBotClusterReader>();
        // Route names: read-only over the gateway catalog. No local SQLite store, no live
        // writes -- naming is config-driven on the gateway. This wins over the dashboard's
        // SqliteRouteNameStore (TryAdd) so the viewer never creates dashboard.db.
        services.AddSingleton<Services.Routes.IRouteNameStore, RemoteRouteNameStore>();

        return services;
    }
}
