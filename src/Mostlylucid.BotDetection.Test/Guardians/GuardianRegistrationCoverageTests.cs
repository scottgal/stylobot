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
                ["BotDetection:Identity:Enabled"] = identityEnabled ? "true" : "false"
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
}
