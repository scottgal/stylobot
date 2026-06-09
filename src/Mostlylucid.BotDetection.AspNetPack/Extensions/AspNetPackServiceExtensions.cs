using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.AspNetPack.Inventory;
using Mostlylucid.BotDetection.AspNetPack.Services;
using Mostlylucid.BotDetection.AspNetPack.Ui;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.AspNetPack.Extensions;

/// <summary>
///     DI registration helpers for the FOSS read-only AspNetPack.
/// </summary>
public static class AspNetPackServiceExtensions
{
    /// <summary>
    ///     Registers the read-only AspNetPack dashboard contribution + the
    ///     in-process endpoint inventory. Idempotent via <c>TryAdd</c> --
    ///     safe to call from any host that loads the FOSS dashboard.
    /// </summary>
    public static IServiceCollection AddAspNetPackReadOnly(this IServiceCollection services)
    {
        services.TryAddSingleton<IAspNetEndpointInventory, AspNetEndpointInventory>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDashboardPack, AspNetPackReadOnlyDashboardPack>());

        // Pack Metrics B2 -- /dashboard/aspnet-hub page builder + JSON
        // summary endpoint backing. Pure read; goes through IMeterStream
        // (always required) + IAspNetEndpointInventory (optional --
        // viewer hosts pull data via REST per feedback_remote_mode_optional_di).
        services.TryAddSingleton<IAspNetPackHubBuilder, AspNetPackHubPageBuilder>();
        return services;
    }
}