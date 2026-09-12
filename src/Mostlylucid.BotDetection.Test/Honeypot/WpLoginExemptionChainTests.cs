using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Honeypot;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Test.Honeypot;

/// <summary>
///     Guards the site-profile exemption chain that
///     <c>BdfReplayTests.BrowserOnWpLoginScenario_WithWordPressProfile_IsNotFlaggedAsScraper</c>
///     depends on, one link at a time.
///     <para>
///     Written while diagnosing the CI-red BDF test (2026-09-12): a real browser hitting
///     /wp-login.php on a wordpress-profiled host scored Scraper at bot_probability 0.52. All
///     three links below PASSED, which located the break DOWNSTREAM of them -- CveProbeAtom was
///     a third reader of the honeypot catalog and the only one not honouring the exemption.
///     They are kept because walking the chain is what made the real cause visible instead of
///     guessing at it, and because a future break in any single link should be attributable here
///     rather than only observable as a red BDF replay.
///     </para>
/// </summary>
public class WpLoginExemptionChainTests
{
    private static (ConfigHoneypotExemptStore Store, HttpContext Ctx) Build(string host)
    {
        var catalog = new SiteProfileCatalog(NullLogger<SiteProfileCatalog>.Instance);
        var map = new SiteMapOptions
        {
            DefaultProfile = "generic",
            Domains = new List<SiteMapRule> { new() { Host = host, Profile = "wordpress" } }
        };
        var normalizer = new DomainNormalizer(
            Options.Create(new DomainNormalizerOptions()), PublicSuffixList.LoadEmbedded());
        var resolver = new SiteProfileResolver(
            catalog, Options.Create(map), normalizer, NullLogger<SiteProfileResolver>.Instance);

        var store = new ConfigHoneypotExemptStore(
            Options.Create(new HoneypotDetectionOptions()), resolver);

        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        return (store, ctx);
    }

    [Fact]
    public void LINK_1_the_catalog_loads_the_wordpress_profile()
    {
        var catalog = new SiteProfileCatalog(NullLogger<SiteProfileCatalog>.Instance);
        Assert.True(catalog.Profiles.ContainsKey("wordpress"),
            $"embedded profiles loaded: {string.Join(", ", catalog.Profiles.Keys)}");
        Assert.NotEmpty(catalog.Get("wordpress")!.Honeypot!.FrameworkPaths!);
    }

    [Fact]
    public void LINK_2_the_resolver_resolves_wp_demo_local_to_wordpress()
    {
        var catalog = new SiteProfileCatalog(NullLogger<SiteProfileCatalog>.Instance);
        var map = new SiteMapOptions
        {
            DefaultProfile = "generic",
            Domains = new List<SiteMapRule> { new() { Host = "wp.demo.local", Profile = "wordpress" } }
        };
        var normalizer = new DomainNormalizer(
            Options.Create(new DomainNormalizerOptions()), PublicSuffixList.LoadEmbedded());
        var resolver = new SiteProfileResolver(
            catalog, Options.Create(map), normalizer, NullLogger<SiteProfileResolver>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("wp.demo.local");

        var profile = resolver.Resolve(ctx);
        Assert.Equal("wordpress", profile?.Id);
    }

    [Fact]
    public void LINK_3_IsExempt_returns_true_for_wp_login_on_the_wordpress_host()
    {
        var (store, ctx) = Build("wp.demo.local");
        var normalized = HoneypotPathDefinitions.NormalizePath("/wp-login.php");

        Assert.True(store.IsExempt(normalized, ctx),
            $"IsExempt('{normalized}') on Host=wp.demo.local returned FALSE — the framework_paths " +
            "exemption that HaxxorAtom's path_probes depends on is not applying");
    }
}
