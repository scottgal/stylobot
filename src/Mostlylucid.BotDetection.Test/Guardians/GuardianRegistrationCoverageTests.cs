using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task Double_module_wire_registers_each_guardian_once_and_does_not_crash_the_walker()
    {
        // Regression (deploy- 2026-07-10, exit 139 crash-loop on the enterprise gateway):
        // a host that wires the module twice -- e.g. AddBotDetection() +
        // AddBotDetectionModule(), and the former just chains to the latter -- registered
        // every guardian twice via AddSingleton, and GuardianService..ctor's
        // ToDictionary(g => g.Name) threw on the duplicate Name, hard-crashing at boot.
        // TryAddEnumerable now dedupes the registration so a double-wire is safe.
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
        services.AddBotDetection(); // the double wire that crashed staging

        await using var provider = services.BuildServiceProvider();

        var names = provider.GetServices<IGuardian>().Select(g => g.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count()); // each guardian registered exactly once
        var walker = provider.GetService<GuardianService>();
        Assert.NotNull(walker); // resolves without the ToDictionary duplicate-key crash
        Assert.Equal(names.Distinct().Count(), walker!.Guardians.Count);
    }

    [Fact]
    public void GuardianService_ctor_dedupes_two_different_classes_that_share_a_Name()
    {
        // TryAddEnumerable dedupes by (service, impl) type, so two DIFFERENT guardian
        // classes with the same Name (e.g. a pack colliding with a FOSS guardian) both
        // still register. The ctor's DistinctBy(g => g.Name) guards that path so a Name
        // collision can never crash the walker at construction.
        var dupes = new IGuardian[] { new NamedFakeGuardian("Dup"), new NamedFakeGuardian("Dup") };

        var ex = Record.Exception(() =>
            new GuardianService(dupes, NullLogger<GuardianService>.Instance));

        Assert.Null(ex);
    }

    private sealed class NamedFakeGuardian(string name) : IGuardian
    {
        public string Name => name;
        public GuardianCategory Category => GuardianCategory.Data;
        public TimeSpan Interval => TimeSpan.FromMinutes(30);
        public bool Enabled => true;
        public Task<GuardianReport> GuardAsync(CancellationToken ct = default) =>
            Task.FromResult(new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = "noop"
            });
    }
}
