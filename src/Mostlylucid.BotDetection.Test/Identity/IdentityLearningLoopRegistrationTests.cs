using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Regression guard for the dead identity learning loop. The Step-7 contributor
///     delete (1a8d2745, ServiceCollectionExtensions 1778 -> 156 lines) dropped the DI
///     registrations for the tick-driven learning services while
///     BotDetectionHostedSingletonsBootstrap kept eager-resolving them via GetService,
///     which returns null silently. So absorption / drift / calibration / entity
///     resolution / convergence / markov / mode-absorption / rollup never ran -- and
///     absorption not running is the root cause of the identity observation leak
///     (observations pile up unabsorbed because nothing folds them into centroids).
///
///     This pins that each learning service RESOLVES through the full DI graph. If a
///     transitive dependency is also dropped, resolving throws here (naming the missing
///     type) instead of the bootstrap silently no-op'ing the service at boot.
/// </summary>
public class IdentityLearningLoopRegistrationTests
{
    private static ServiceProvider Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Identity:Enabled"] = "true",
                ["BotDetection:DatabasePath"] = string.Empty
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task All_tick_driven_identity_learning_services_resolve_through_the_full_di_graph()
    {
        await using var provider = Build();

        Assert.NotNull(provider.GetService<Mostlylucid.BotDetection.Identity.FingerprintAbsorptionService>());
        Assert.NotNull(provider.GetService<Mostlylucid.BotDetection.Identity.BrowserModes.FingerprintModeAbsorptionService>());
        Assert.NotNull(provider.GetService<Mostlylucid.BotDetection.Identity.BrowserModes.FingerprintRollupRecomputeService>());
        Assert.NotNull(provider.GetService<Mostlylucid.BotDetection.Identity.FingerprintDriftService>());
        Assert.NotNull(provider.GetService<Mostlylucid.BotDetection.Identity.IdentityWeightCalibrationService>());
        Assert.NotNull(provider.GetService<Mostlylucid.BotDetection.Services.DeploymentNormCalibrationService>());
        Assert.NotNull(provider.GetService<Mostlylucid.BotDetection.Services.EntityResolutionService>());
        Assert.NotNull(provider.GetService<Mostlylucid.BotDetection.Markov.PopulationMarkovService>());
        Assert.NotNull(provider.GetService<Mostlylucid.BotDetection.Services.SignatureConvergenceService>());
    }
}
