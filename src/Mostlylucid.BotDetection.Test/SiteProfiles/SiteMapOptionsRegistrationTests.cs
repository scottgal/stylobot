using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.SiteProfiles;
using Xunit;

namespace Mostlylucid.BotDetection.Test.SiteProfiles;

/// <summary>
///     Regression guard: <c>SiteMapOptions</c> (<c>BotDetection:Sites</c>) and the
///     <c>SiteProfiles</c> subsystem (<see cref="ISiteProfileCatalog"/>,
///     <see cref="ISiteProfileResolver"/>) were fully built, unit tested via direct
///     construction (<c>SiteProfileResolverTests</c>), and consumed
///     (<c>HoneypotPathTagger</c>'s optional profile-resolver parameter) but never
///     registered anywhere in <c>BotDetectionModule</c> -- the exact same silent-drop
///     class already fixed once for <c>DomainNormalizerOptions</c> in the same file.
///     Every real host therefore ran with an empty <c>SiteMapOptions</c> regardless of
///     configured <c>BotDetection:Sites</c>, and any consumer resolving the optional
///     <c>ISiteProfileResolver</c> always got null -- per-host honeypot profile
///     promotion never fired in production.
/// </summary>
public class SiteMapOptionsRegistrationTests
{
    private static ServiceProvider Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Sites:DefaultProfile"] = "generic",
                ["BotDetection:Sites:Domains:0:Host"] = "blog.example.com",
                ["BotDetection:Sites:Domains:0:Profile"] = "wordpress"
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
    public void SiteMapOptions_binds_configured_domains_through_the_full_DI_graph()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<SiteMapOptions>>().Value;

        Assert.Equal("generic", options.DefaultProfile);
        var rule = Assert.Single(options.Domains);
        Assert.Equal("blog.example.com", rule.Host);
        Assert.Equal("wordpress", rule.Profile);
    }

    [Fact]
    public void ISiteProfileResolver_resolves_through_the_full_DI_graph()
    {
        using var provider = Build();

        Assert.NotNull(provider.GetService<ISiteProfileResolver>());
        Assert.NotNull(provider.GetService<ISiteProfileCatalog>());
    }
}
