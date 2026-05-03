using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.ApiHolodeck.Actions;
using Mostlylucid.BotDetection.ApiHolodeck.Contributors;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.ApiHolodeck.Extensions;

/// <summary>
///     Extension methods for adding Holodeck services to the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Add API Holodeck honeypot services to the service collection.
    ///     This includes the HolodeckActionPolicy, ShapeBuilder, HoneypotLinkContributor, and HoneypotReporter.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    ///     <code>
    ///     // Basic registration
    ///     builder.Services.AddBotDetection();
    ///     builder.Services.AddApiHolodeck();
    /// 
    ///     // With configuration
    ///     builder.Services.AddApiHolodeck(options =>
    ///     {
    ///         options.MockApiBaseUrl = "http://localhost:5116/api/mock";
    ///         options.Mode = HolodeckMode.Chaos;
    ///         options.ReportToProjectHoneypot = true;
    ///     });
    ///     </code>
    /// </example>
    public static IServiceCollection AddApiHolodeck(
        this IServiceCollection services,
        Action<HolodeckOptions>? configure = null)
    {
        // Configure options
        services.AddOptions<HolodeckOptions>()
            .BindConfiguration(HolodeckOptions.SectionName)
            .Configure(options => configure?.Invoke(options));

        // Configure shape builder options
        services.AddOptions<ShapeBuilderOptions>()
            .BindConfiguration("BotDetection:Holodeck:ShapeBuilder");

        // Register HTTP client for MockLLMApi
        services.AddHttpClient("Holodeck", client =>
        {
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("User-Agent", "Mostlylucid.BotDetection.ApiHolodeck/1.0");
        });

        // Register HTTP client for ShapeBuilder LLM calls
        services.AddHttpClient("ShapeBuilder",
            client => { client.DefaultRequestHeaders.Add("Accept", "application/json"); });

        // Register the ShapeBuilder for intelligent API type detection
        services.AddSingleton<IShapeBuilder, ShapeBuilder>();

        // Register the Holodeck action policy
        services.AddSingleton<HolodeckActionPolicy>();
        services.AddSingleton<IActionPolicy>(sp => sp.GetRequiredService<HolodeckActionPolicy>());

        // Register the honeypot link contributor
        services.AddSingleton<HoneypotLinkContributor>();
        services.AddSingleton<IContributingDetector>(sp => sp.GetRequiredService<HoneypotLinkContributor>());

        // Register the honeypot reporter background service
        services.AddHostedService<HoneypotReporter>();

        // Holodeck coordinator for per-fingerprint engagement slots
        services.AddSingleton<HolodeckCoordinator>();

        // Beacon tracking
        services.AddSingleton<BeaconCanaryGenerator>(sp =>
        {
            var botOptions = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            var holoOptions = sp.GetRequiredService<IOptions<HolodeckOptions>>().Value;
            var secret = botOptions.SignatureHashKey ?? "stylobot-default-beacon-key";
            return new BeaconCanaryGenerator(secret, holoOptions.BeaconCanaryLength);
        });
        services.AddSingleton<ICanaryGenerator>(sp => sp.GetRequiredService<BeaconCanaryGenerator>());

        services.AddSingleton<BeaconStore>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var dbPath = Path.Combine(env.ContentRootPath, "beacons.db");
            return new BeaconStore($"Data Source={dbPath};Cache=Shared");
        });
        services.AddSingleton<IBeaconStore>(sp => sp.GetRequiredService<BeaconStore>());

        // Beacon contributor (detects canary replay from rotated fingerprints)
        services.AddSingleton<BeaconContributor>();
        services.AddSingleton<IContributingDetector>(sp => sp.GetRequiredService<BeaconContributor>());

        return services;
    }

    /// <summary>
    ///     Add API Holodeck services with explicit configuration binding.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">Configuration section to bind from</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddApiHolodeck(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<HolodeckOptions>()
            .Bind(configuration);

        // Configure shape builder options from nested section
        var shapeBuilderSection = configuration.GetSection("ShapeBuilder");
        if (shapeBuilderSection.Exists())
            services.AddOptions<ShapeBuilderOptions>().Bind(shapeBuilderSection);
        else
            services.AddOptions<ShapeBuilderOptions>();

        // Register HTTP clients
        services.AddHttpClient("Holodeck");
        services.AddHttpClient("ShapeBuilder");

        // Register the ShapeBuilder
        services.AddSingleton<IShapeBuilder, ShapeBuilder>();

        // Register services
        services.AddSingleton<HolodeckActionPolicy>();
        services.AddSingleton<IActionPolicy>(sp => sp.GetRequiredService<HolodeckActionPolicy>());
        services.AddSingleton<HoneypotLinkContributor>();
        services.AddSingleton<IContributingDetector>(sp => sp.GetRequiredService<HoneypotLinkContributor>());
        services.AddHostedService<HoneypotReporter>();

        // Holodeck coordinator for per-fingerprint engagement slots
        services.AddSingleton<HolodeckCoordinator>();

        // Beacon tracking
        services.AddSingleton<BeaconCanaryGenerator>(sp =>
        {
            var botOptions = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            var holoOptions = sp.GetRequiredService<IOptions<HolodeckOptions>>().Value;
            var secret = botOptions.SignatureHashKey ?? "stylobot-default-beacon-key";
            return new BeaconCanaryGenerator(secret, holoOptions.BeaconCanaryLength);
        });
        services.AddSingleton<ICanaryGenerator>(sp => sp.GetRequiredService<BeaconCanaryGenerator>());

        services.AddSingleton<BeaconStore>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var dbPath = Path.Combine(env.ContentRootPath, "beacons.db");
            return new BeaconStore($"Data Source={dbPath};Cache=Shared");
        });
        services.AddSingleton<IBeaconStore>(sp => sp.GetRequiredService<BeaconStore>());

        // Beacon contributor (detects canary replay from rotated fingerprints)
        services.AddSingleton<BeaconContributor>();
        services.AddSingleton<IContributingDetector>(sp => sp.GetRequiredService<BeaconContributor>());

        return services;
    }
}