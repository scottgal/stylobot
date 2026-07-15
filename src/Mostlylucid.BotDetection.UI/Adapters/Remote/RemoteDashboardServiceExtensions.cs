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
            // Identify this dashboard's pull traffic so the gateway's detection
            // pipeline classifies it as a known-internal client. Without this,
            // .NET sends no User-Agent at all and the request shape (no UA +
            // repeating internal HTTP/2 + api-key) matches the wget archetype.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(StyloBotInternalUserAgent.Value);
            http.Timeout = TimeSpan.FromSeconds(options.Pull.TimeoutSeconds);
        });

        // Remote store registrations - go in BEFORE AddStyloBotDashboard so the TryAdd
        // fallbacks (SqliteDashboardEventStore, SqliteDetectionArchive, etc.) skip themselves.
        services.AddSingleton<IDashboardEventStore, RemoteDashboardEventStore>();
        services.AddSingleton<IDetectionArchive, RemoteDetectionArchive>();
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
        // Config-baseline effective view: the thin client can't compose it (no local config, no
        // IActionPolicyRegistry), so read the gateway's composed rows over REST. Registered before
        // AddStyloBotDashboard, whose local composer is a TryAdd -- so remote wins, and the policies
        // page shows the GATEWAY's config baseline instead of skipping it.
        services.AddSingleton<Services.IConfigBaselineProvider, RemoteConfigBaselineProvider>();
        // Compliance: the gateway OWNS the pack selection + guardians; the site reads
        // status over REST and loads the (static, embedded) pack catalogue locally. No
        // local compliance store, no Postgres connection on the site
        // (feedback_upstream_owns_no_stylobot_state). Wins over any InMemory default.
        services.AddSingleton<BotDetection.Compliance.ICompliancePackProvider, RemoteCompliancePackProvider>();
        // Same instance also satisfies IComplianceStatusReader so the compliance tab
        // shows the gateway's live guardian count instead of "Not configured".
        services.AddSingleton<BotDetection.Compliance.IComplianceStatusReader>(sp =>
            (RemoteCompliancePackProvider)sp.GetRequiredService<BotDetection.Compliance.ICompliancePackProvider>());
        // Route names: read-only over the gateway catalog. No local SQLite store, no live
        // writes -- naming is config-driven on the gateway. This wins over the dashboard's
        // SqliteRouteNameStore (TryAdd) so the viewer never creates dashboard.db.
        services.AddSingleton<Services.Routes.IRouteNameStore, RemoteRouteNameStore>();
        // Site-health history is part of IDashboardEventStore now (the new
        // GetDegradationHistoryAsync method on RemoteDashboardEventStore
        // proxies the existing /api/v1/site-health/history endpoint). No
        // separate ISiteHealthQuery registration needed -- the dashboard
        // view component reads IDashboardEventStore directly.

        return services;
    }
}
