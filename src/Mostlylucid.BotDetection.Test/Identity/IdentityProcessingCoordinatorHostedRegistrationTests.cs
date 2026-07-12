using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Regression guard for the <see cref="IdentityProcessingCoordinator"/> DI wiring.
///     The Step-7 contributor delete (1a8d2745) dropped the coordinator's
///     <c>AddHostedService</c> registration while keeping the injectable singleton, so its
///     worker loops never started in production: <c>RunAsync</c> enqueued, the queue filled to
///     <c>MaxQueueDepth</c>, then every FingerprintMatchAtom Pass-2 sheds -- identity confirm
///     silently degraded to L1-only, and any work routed through the coordinator (the
///     absorption folds) would never drain. This asserts the coordinator is hosted as the SAME
///     instance the atoms inject, so its <c>ExecuteAsync</c> pumps the very queue they enqueue to.
/// </summary>
public sealed class IdentityProcessingCoordinatorHostedRegistrationTests
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
}
