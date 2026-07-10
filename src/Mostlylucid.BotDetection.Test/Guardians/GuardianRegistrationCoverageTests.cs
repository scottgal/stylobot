using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Guardians;

namespace Mostlylucid.BotDetection.Test.Guardians;

/// <summary>
///     Registration coverage for the discrete Data guardians (Part B / Task 11).
///     The five vector guardians register unconditionally; the two identity
///     guardians register only under <c>Identity:Enabled</c> (they operate on
///     <c>fingerprints.db</c>, which is dormant otherwise). This test pins the
///     roster so a dropped or mis-gated <c>AddSingleton&lt;IGuardian&gt;</c> fails loud.
/// </summary>
public sealed class GuardianRegistrationCoverageTests
{
    private static readonly string[] VectorGuardians =
    {
        "BucketRetention",
        "SessionCompaction",
        "HnswCompaction",
        "CentroidRetention",
        "SignatureCap"
    };

    private static readonly string[] IdentityGuardians =
    {
        "FingerprintObservationRetention",
        "FingerprintEviction"
    };

    private static ServiceProvider BuildProvider(bool identityEnabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Identity:Enabled"] = identityEnabled ? "true" : "false",
                // AddBotDetection fails fast on a null DatabasePath (it used to fall back
                // to an unbounded in-memory DB). This registration-coverage test never
                // persists, so it opts into in-memory explicitly with an empty path,
                // exactly as AddBotDetectionInMemory does. Empty passes; only null throws.
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
    public async Task With_identity_enabled_all_seven_guardians_resolve_as_data_category()
    {
        await using var provider = BuildProvider(identityEnabled: true);

        var guardians = provider.GetServices<IGuardian>().ToList();
        var names = guardians.Select(g => g.Name).ToList();

        foreach (var expected in VectorGuardians.Concat(IdentityGuardians))
            Assert.Contains(expected, names);

        Assert.All(guardians, g => Assert.Equal(GuardianCategory.Data, g.Category));
    }

    [Fact]
    public async Task With_identity_disabled_only_the_five_vector_guardians_resolve()
    {
        await using var provider = BuildProvider(identityEnabled: false);

        var names = provider.GetServices<IGuardian>().Select(g => g.Name).ToList();

        foreach (var expected in VectorGuardians)
            Assert.Contains(expected, names);

        foreach (var absent in IdentityGuardians)
            Assert.DoesNotContain(absent, names);
    }

    [Fact]
    public async Task GuardianService_walker_is_registered_and_collects_the_guardians()
    {
        // Regression: the guardians were registered as IGuardian, but GuardianService
        // (the walker that runs them off the ScheduleCoordinator's Tick1m) was NOT
        // registered, so the whole tier silently never ran. The eager-resolve in
        // BotDetectionHostedSingletonsBootstrap uses GetService (not GetRequiredService),
        // which returned null when the walker was unregistered, with no error. This pins
        // that the walker resolves and sees every registered guardian.
        await using var provider = BuildProvider(identityEnabled: true);

        var walker = provider.GetService<GuardianService>();
        Assert.NotNull(walker);
        Assert.Equal(provider.GetServices<IGuardian>().Count(), walker!.Guardians.Count);
    }
}
