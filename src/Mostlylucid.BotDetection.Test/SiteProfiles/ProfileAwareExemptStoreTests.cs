using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Honeypot;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Test.SiteProfiles;

public class ProfileAwareExemptStoreTests
{
    private static (ConfigHoneypotExemptStore store, SiteProfileResolver resolver) Build(
        List<string>? globalExempts = null,
        params (string host, string profile)[] rules)
    {
        var catalog = new SiteProfileCatalog(NullLogger<SiteProfileCatalog>.Instance);
        var map = new SiteMapOptions
        {
            DefaultProfile = "generic",
            Domains = rules.Select(r => new SiteMapRule { Host = r.host, Profile = r.profile }).ToList()
        };
        var normalizer = new DomainNormalizer(
            Options.Create(new DomainNormalizerOptions()),
            PublicSuffixList.LoadEmbedded());
        var resolver = new SiteProfileResolver(
            catalog, Options.Create(map), normalizer, NullLogger<SiteProfileResolver>.Instance);

        var hpOptions = new HoneypotDetectionOptions { ExemptPaths = globalExempts ?? new() };
        var monitor = new TestOptionsMonitor<HoneypotDetectionOptions>(hpOptions);
        var store = new ConfigHoneypotExemptStore(monitor, resolver);
        return (store, resolver);
    }

    private static HttpContext CtxFor(string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        return ctx;
    }

    [Fact]
    public void WordPressHost_ExemptsWpLogin()
    {
        var (store, _) = Build(rules: ("blog.example.com", "wordpress"));
        Assert.True(store.IsExempt("/wp-login.php", CtxFor("blog.example.com")));
    }

    [Fact]
    public void WordPressHost_ExemptsWpAdminSubpath()
    {
        var (store, _) = Build(rules: ("blog.example.com", "wordpress"));
        // /wp-admin* glob should match /wp-admin/users.php
        Assert.True(store.IsExempt("/wp-admin/users.php", CtxFor("blog.example.com")));
    }

    [Fact]
    public void NonWordPressHost_DoesNotExemptWpLogin()
    {
        // Same path on a host with no rule -> default profile (generic) -> no exemption.
        var (store, _) = Build(rules: ("blog.example.com", "wordpress"));
        Assert.False(store.IsExempt("/wp-login.php", CtxFor("api.example.com")));
    }

    [Fact]
    public void OperatorGlobalExempt_StillWorks_NullContext()
    {
        var (store, _) = Build(globalExempts: ["/custom-path"]);
        Assert.True(store.IsExempt("/custom-path"));
    }

    [Fact]
    public void GenericProfile_NoExtraExempts()
    {
        var (store, _) = Build(rules: ("api.example.com", "generic"));
        Assert.False(store.IsExempt("/wp-login.php", CtxFor("api.example.com")));
    }

    [Fact]
    public void NoContext_StillEvaluatesGlobalExempts()
    {
        var (store, _) = Build(globalExempts: ["/wp-login.php"]);
        Assert.True(store.IsExempt("/wp-login.php"));
    }

    [Fact]
    public void AspnetHost_DoesNotExemptWpLogin()
    {
        // ASPNet profile has no wp-* exempts; wp-login should NOT be skipped
        // (and Tier 2 elevation fires as for any non-WP site).
        var (store, _) = Build(rules: ("api.example.com", "aspnet"));
        Assert.False(store.IsExempt("/wp-login.php", CtxFor("api.example.com")));
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }
}
