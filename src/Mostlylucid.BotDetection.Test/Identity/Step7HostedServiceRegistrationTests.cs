using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Regression guards for hosted <see cref="BackgroundService"/> registrations the Step-7
///     contributor delete (1a8d2745) dropped while keeping the classes. Both are the same
///     invisible failure class: the singleton/type still resolves, but with no
///     <c>AddHostedService</c> the <c>ExecuteAsync</c> loop never runs, so the feature is
///     silently dead in production.
///     <list type="bullet">
///         <item><description>
///             <see cref="IdentityProcessingCoordinator"/> (factory form): its worker loops drain
///             the slow-path queue. Dead loops -> FingerprintMatchAtom Pass-2 sheds to L1-only and
///             absorption folds never drain.
///         </description></item>
///         <item><description>
///             <see cref="SignatureCoordinatorWarmupService"/> (type form): replays persisted
///             requests into the SignatureCoordinator at startup. Dead -> clustering cold-starts
///             from live traffic only after every restart.
///         </description></item>
///     </list>
/// </summary>
public sealed class Step7HostedServiceRegistrationTests
{
    private static IServiceCollection BuildServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Identity:Enabled"] = "true",
                // AddBotDetection fails fast on a null DatabasePath; opt into in-memory with
                // an empty path exactly as AddBotDetectionInMemory does. Empty passes.
                ["BotDetection:DatabasePath"] = string.Empty
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection();
        return services;
    }

    [Fact]
    public async Task Coordinator_is_hosted_as_the_same_instance_the_atoms_inject()
    {
        var services = BuildServices();
        await using var provider = services.BuildServiceProvider();

        var injected = provider.GetRequiredService<IdentityProcessingCoordinator>();

        // Walk the IHostedService registrations and activate each. Some (e.g. the schedule
        // coordinator watchdog) need host infra the bare test container lacks; skip those --
        // they are not the coordinator. The coordinator's registration is the factory form
        // AddHostedService(sp => sp.GetRequiredService<IdentityProcessingCoordinator>()), so
        // its instance is reference-equal to the injected singleton. Without the hosted
        // registration (the Step-7 regression) nothing here matches and the queue never pumps.
        var hostedIsSameInstance = false;
        foreach (var sd in services.Where(d => d.ServiceType == typeof(IHostedService)))
        {
            object? instance = null;
            try
            {
                instance = sd.ImplementationInstance
                    ?? sd.ImplementationFactory?.Invoke(provider)
                    ?? (sd.ImplementationType is not null
                        ? ActivatorUtilities.CreateInstance(provider, sd.ImplementationType)
                        : null);
            }
            catch
            {
                // Not resolvable in the bare container -> not the coordinator.
            }

            if (ReferenceEquals(instance, injected))
            {
                hostedIsSameInstance = true;
                break;
            }
        }

        Assert.True(hostedIsSameInstance,
            "IdentityProcessingCoordinator must be registered as a hosted service resolving the " +
            "injectable singleton, so its worker loops drain the queue the atoms enqueue to.");
    }

    [Fact]
    public void SignatureCoordinatorWarmupService_is_registered_as_a_hosted_service()
    {
        var services = BuildServices();

        // Type-form registration: assert the descriptor exists without activating any hosted
        // service (activation needs host infra the bare container lacks). Without it, clustering
        // cold-starts from live traffic only after every restart (the Step-7 regression).
        var registered = services.Any(sd =>
            sd.ServiceType == typeof(IHostedService)
            && sd.ImplementationType == typeof(SignatureCoordinatorWarmupService));

        Assert.True(registered,
            "SignatureCoordinatorWarmupService must be registered as a hosted service so it " +
            "replays the persisted request corpus into the SignatureCoordinator on startup.");
    }

    [Fact]
    public async Task WellKnownBotIndex_is_registered_as_a_resolvable_singleton()
    {
        // The atom refactor dropped the WellKnownBotIndex registration. UserAgentAtom /
        // AiScraperAtom take it as an OPTIONAL ctor param (WellKnownBotIndex? = null), so an
        // unregistered index resolves to null instead of throwing -- the catalog branch
        // (if (_wellKnownBots is { Count: > 0 })) is silently skipped and all ~635 arcjet bots
        // (PetalBot, ...) fall through to "appears normal" human. Pin it back.
        var services = BuildServices();
        await using var provider = services.BuildServiceProvider();

        var index = provider.GetService<Mostlylucid.BotDetection.Definitions.WellKnownBots.WellKnownBotIndex>();
        Assert.NotNull(index);

        // Singleton: the atoms, the refresh service, and the middleware fallback must share ONE
        // index so the refresh service's seed/refresh is visible to the consumers.
        var again = provider.GetService<Mostlylucid.BotDetection.Definitions.WellKnownBots.WellKnownBotIndex>();
        Assert.Same(index, again);
    }

    [Fact]
    public async Task WellKnownBotRefreshService_resolves_and_seeds_the_catalog_baseline()
    {
        // The refresh service is IDisposable + coordinator-tick (NOT IHostedService), eager-
        // resolved by BotDetectionHostedSingletonsBootstrap -- whose GetService call was left
        // orphaned when the registration dropped. Registering the singleton re-arms it. Its ctor
        // seeds the embedded arcjet baseline into the shared index synchronously, so this asserts
        // the BEHAVIOURAL end of the drift: after resolution the catalog is actually populated.
        // Count == 0 is what made PetalBot (and ~635 others) read as a verified real browser.
        var services = BuildServices();
        await using var provider = services.BuildServiceProvider();

        var refresh = provider.GetService<Mostlylucid.BotDetection.Definitions.WellKnownBots.WellKnownBotRefreshService>();
        Assert.NotNull(refresh);

        var index = provider.GetRequiredService<Mostlylucid.BotDetection.Definitions.WellKnownBots.WellKnownBotIndex>();
        Assert.True(index.Count > 0,
            "resolving the refresh service must seed the embedded baseline into the shared index; " +
            "Count == 0 means the catalog is dead and catalog-only bots misclassify as human.");
    }
}
