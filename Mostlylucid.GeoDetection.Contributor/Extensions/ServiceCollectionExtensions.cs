using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.GeoDetection.Contributor.Extensions;

/// <summary>
///     Extension methods for registering the GeoDetection contributor with DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds the geo-detection contributor to the bot detection pipeline.
    ///     Requires that AddGeoLocationServices() and AddBotDetection() have been called.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureOptions">Optional options configuration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddGeoDetectionContributor(
        this IServiceCollection services,
        Action<GeoContributorOptions>? configureOptions = null)
    {
        // Configure options
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<GeoContributorOptions>()
                .BindConfiguration("BotDetection:Geo");
        }

        // Register the server-side geo contributor
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IContributingDetector, GeoContributor>());

        // Register the client-side geo contributor (if enabled)
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IContributingDetector, GeoClientContributor>());

        return services;
    }

    /// <summary>
    ///     Adds the geo-detection contributor with specific options.
    /// </summary>
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

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IContributingDetector, GeoContributor>());

        return services;
    }
}
