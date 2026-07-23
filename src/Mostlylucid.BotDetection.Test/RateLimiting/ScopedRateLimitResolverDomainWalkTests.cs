using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.RateLimiting;
using Xunit;

namespace Mostlylucid.BotDetection.Test.RateLimiting;

/// <summary>
///     Covers the new FOSS-symmetric domain + subdomain walk added to
///     <see cref="ScopedRateLimitResolver"/>. Detection semantics no longer
///     stop at the endpoint layer -- the resolver walks global → domain →
///     subdomain → endpoint → method whenever <c>Domains</c> is populated,
///     and degrades to the legacy global → endpoint → method walk when it
///     isn't (backward compat).
/// </summary>
public class ScopedRateLimitResolverDomainWalkTests
{
    private static ScopedRateLimitResolver Resolver(
        RateLimitOptions options,
        DomainNormalizer? normalizer = null)
    {
        var monitor = new TestOptionsMonitor<RateLimitOptions>(options);
        return new ScopedRateLimitResolver(monitor, normalizer);
    }

    private static DomainNormalizer NewNormalizer()
    {
        var opts = Options.Create(new DomainNormalizerOptions());
        var psl = PublicSuffixList.LoadEmbedded();
        return new DomainNormalizer(opts, psl);
    }

    [Fact]
    public void Global_only_config_walks_global_then_endpoint_then_method_unchanged()
    {
        // Domains dict is empty (the state of every existing FOSS deploy),
        // so the walk must degrade to the pre-change behaviour.
        var options = new RateLimitOptions
        {
            Limits = { ["scraper-class"] = new() { OverLimitAction = "throttle-status" } },
            Endpoints =
            {
                ["/api/users"] = new()
                {
                    Inherit = true,
                    Limits = { ["scraper-class"] = new() { OverLimitAction = "block" } },
                },
            },
        };
        var resolver = Resolver(options);
        var rules = resolver.ResolveRules(host: "example.com", path: "/api/users", method: "GET");
        Assert.Single(rules);
        Assert.Equal("block", rules[0].OverLimitAction);
    }

    [Fact]
    public void Domain_scope_rule_merges_with_global_when_host_matches()
    {
        var options = new RateLimitOptions
        {
            Limits = { ["scraper-class"] = new() { OverLimitAction = "throttle-status" } },
            Domains =
            {
                ["example.com"] = new()
                {
                    Inherit = true,
                    Limits = { ["scraper-class"] = new() { OverLimitAction = "block" } },
                },
            },
        };
        var resolver = Resolver(options);
        var rules = resolver.ResolveRules(host: "example.com", path: "/", method: "GET");
        // Domain overrides the global rule of the same name.
        Assert.Single(rules);
        Assert.Equal("block", rules[0].OverLimitAction);
    }

    [Fact]
    public void Subdomain_scope_overrides_domain_and_global()
    {
        var options = new RateLimitOptions
        {
            Limits = { ["scraper-class"] = new() { OverLimitAction = "throttle-status" } },
            Domains =
            {
                ["example.com"] = new()
                {
                    Inherit = true,
                    Limits = { ["scraper-class"] = new() { OverLimitAction = "warn" } },
                    Subdomains =
                    {
                        ["api.example.com"] = new()
                        {
                            Inherit = true,
                            Limits = { ["scraper-class"] = new() { OverLimitAction = "block" } },
                        },
                    },
                },
            },
        };
        // Domain scope keyed at eTLD+1 -- normalizer resolves api.example.com -> example.com.
        var resolver = Resolver(options, NewNormalizer());
        var rules = resolver.ResolveRules(host: "api.example.com", path: "/", method: "GET");
        // Subdomain overrides both the domain and the global rule of the same name.
        Assert.Single(rules);
        Assert.Equal("block", rules[0].OverLimitAction);
    }

    [Fact]
    public void Wildcard_domain_matches_but_exact_key_wins()
    {
        var options = new RateLimitOptions
        {
            Domains =
            {
                ["*.example.com"] = new()
                {
                    Inherit = true,
                    Limits = { ["scraper-class"] = new() { OverLimitAction = "warn" } },
                },
                ["admin.example.com"] = new()
                {
                    Inherit = true,
                    Limits = { ["scraper-class"] = new() { OverLimitAction = "block" } },
                },
            },
        };
        var resolver = Resolver(options);

        // Wildcard catches api.example.com.
        var apiRules = resolver.ResolveRules(host: "api.example.com", path: "/", method: "GET");
        Assert.Single(apiRules);
        Assert.Equal("warn", apiRules[0].OverLimitAction);

        // Exact key beats the wildcard for admin.example.com.
        var adminRules = resolver.ResolveRules(host: "admin.example.com", path: "/", method: "GET");
        Assert.Single(adminRules);
        Assert.Equal("block", adminRules[0].OverLimitAction);
    }

    [Fact]
    public void DomainNormalizer_registrable_lookup_used_as_fallback()
    {
        // Config keys on the eTLD+1 form; request comes in on a bare subdomain
        // (no wildcard in config). The normalizer's eTLD+1 fallback should
        // still resolve the domain scope.
        var options = new RateLimitOptions
        {
            Domains =
            {
                ["example.com"] = new()
                {
                    Inherit = true,
                    Limits = { ["scraper-class"] = new() { OverLimitAction = "block" } },
                },
            },
        };
        var resolver = Resolver(options, NewNormalizer());
        var rules = resolver.ResolveRules(host: "api.example.com", path: "/", method: "GET");
        Assert.Single(rules);
        Assert.Equal("block", rules[0].OverLimitAction);
    }

    [Fact]
    public void Endpoint_nested_under_subdomain_wins_over_root_endpoints()
    {
        var options = new RateLimitOptions
        {
            Endpoints =
            {
                ["/api/*"] = new()
                {
                    Limits = { ["scraper-class"] = new() { OverLimitAction = "throttle-status" } },
                },
            },
            Domains =
            {
                ["example.com"] = new()
                {
                    Inherit = true,
                    Subdomains =
                    {
                        ["api.example.com"] = new()
                        {
                            Inherit = true,
                            Endpoints =
                            {
                                ["/api/*"] = new()
                                {
                                    Limits = { ["scraper-class"] = new() { OverLimitAction = "block" } },
                                },
                            },
                        },
                    },
                },
            },
        };
        // Domain scope keyed at eTLD+1 -- normalizer resolves api.example.com -> example.com.
        var resolver = Resolver(options, NewNormalizer());
        var rules = resolver.ResolveRules(host: "api.example.com", path: "/api/users", method: "GET");
        // Endpoint resolution starts from the innermost active scope
        // (subdomain), so the subdomain's /api/* wins.
        Assert.Single(rules);
        Assert.Equal("block", rules[0].OverLimitAction);
    }

    private sealed class TestOptionsMonitor<T> : IOptions<T> where T : class
    {
        public TestOptionsMonitor(T value) { Value = value; }
        public T Value { get; }
    }
}