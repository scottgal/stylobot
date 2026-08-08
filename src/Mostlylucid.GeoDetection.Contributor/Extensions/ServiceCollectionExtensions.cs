using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Data.Sources;
using Mostlylucid.BotDetection.Setup;
using Mostlylucid.GeoDetection.Contributor.Setup;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Contributor.Extensions;

/// <summary>
///     Extension methods for the GeoDetection wire-up. The former
///     <c>IContributingDetector</c>-based geo enrichment moved into the
///     atom orchestrator's hydrator step; these extensions retain the
///     options binding + setup-resource registration so hosts that call
///     <c>AddGeoDetectionContributor</c> or <c>ConfigureGeoDetection</c>
///     compile unchanged.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGeoDetectionContributor(
        this IServiceCollection services,
        Action<GeoContributorOptions>? configureOptions = null)
    {
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<GeoContributorOptions>()
                .BindConfiguration("BotDetection:Geo");
        }

        services.AddGeoFetchSourceRegistration();
        return services;
    }

    public static IServiceCollection AddGeoDetectionContributor(
        this IServiceCollection services,
        GeoContributorOptions options)
    {
        services.Configure<GeoContributorOptions>(o =>
        {
            o.EnableBotVerification = options.EnableBotVerification;
            o.EnableInconsistencyDetection = options.EnableInconsistencyDetection;
            o.SuspiciousCountries = options.SuspiciousCountries;
            o.TrustedCountries = options.TrustedCountries;
            o.FlagHostingIps = options.FlagHostingIps;
            o.FlagVpnIps = options.FlagVpnIps;
            o.VerifiedBotConfidenceBoost = options.VerifiedBotConfidenceBoost;
            o.BotOriginMismatchPenalty = options.BotOriginMismatchPenalty;
            o.SignalWeight = options.SignalWeight;
            o.Priority = options.Priority;
        });

        services.AddGeoFetchSourceRegistration();
        return services;
    }

    private static IServiceCollection AddGeoFetchSourceRegistration(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetupResource, GeoIpSetupResource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFetchSourceContributor, GeoDetectionFetchSourceContributor>());
        return services;
    }
}