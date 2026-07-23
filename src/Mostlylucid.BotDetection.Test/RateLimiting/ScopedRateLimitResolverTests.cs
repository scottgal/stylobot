using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimiting;
using Xunit;

namespace Mostlylucid.BotDetection.Test.RateLimiting;

public class ScopedRateLimitResolverTests
{
    private static ScopedRateLimitResolver Resolver(RateLimitOptions options)
    {
        var monitor = new TestOptionsMonitor<RateLimitOptions>(options);
        return new ScopedRateLimitResolver(monitor);
    }

    [Fact]
    public void Disabled_options_return_empty_set()
    {
        var resolver = Resolver(new RateLimitOptions
        {
            Enabled = false,
            Limits = { ["scraper-class"] = new() { OverLimitAction = "block" } },
        });
        var rules = resolver.ResolveRules(host: null, path: "/", method: "GET");
        Assert.Empty(rules);
    }

    [Fact]
    public void Global_rules_apply_when_no_endpoint_matches()
    {
        var options = new RateLimitOptions
        {
            Limits = { ["scraper-class"] = new() { OverLimitAction = "block" } },
        };
        var resolver = Resolver(options);
        var rules = resolver.ResolveRules(null, "/some/path", "GET");
        Assert.Single(rules);
        Assert.Equal("block", rules[0].OverLimitAction);
    }

    [Fact]
    public void Endpoint_rule_overrides_global_by_name()
    {
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
        var rules = resolver.ResolveRules(null, "/api/users", "GET");
        // Endpoint's override replaces the global same-named rule.
        Assert.Single(rules);
        Assert.Equal("block", rules[0].OverLimitAction);
    }

    [Fact]
    public void Inherit_false_truncates_parent_rules()
    {
        var options = new RateLimitOptions
        {
            Limits = { ["scraper-class"] = new() { OverLimitAction = "throttle-status" } },
            Endpoints =
            {
                ["/api/users"] = new()
                {
                    Inherit = false,
                    Limits = { ["ai-class"] = new() { OverLimitAction = "block" } },
                },
            },
        };
        var resolver = Resolver(options);
        var rules = resolver.ResolveRules(null, "/api/users", "GET");
        // Global's scraper-class was dropped; only the endpoint's ai-class remains.
        Assert.Single(rules);
        Assert.Equal("block", rules[0].OverLimitAction);
    }

    [Fact]
    public void Method_scope_overrides_endpoint()
    {
        var options = new RateLimitOptions
        {
            Endpoints =
            {
                ["/api/search"] = new()
                {
                    Limits = { ["ai-class"] = new() { OverLimitAction = "throttle-status" } },
                    Methods =
                    {
                        ["GET"] = new()
                        {
                            Limits = { ["ai-class"] = new() { OverLimitAction = "block" } },
                        },
                    },
                },
            },
        };
        var resolver = Resolver(options);

        var getRules = resolver.ResolveRules(null, "/api/search", "GET");
        var postRules = resolver.ResolveRules(null, "/api/search", "POST");

        Assert.Single(getRules);
        Assert.Equal("block", getRules[0].OverLimitAction);   // method override

        Assert.Single(postRules);
        Assert.Equal("throttle-status", postRules[0].OverLimitAction);  // endpoint default
    }

    [Fact]
    public void Prefix_glob_matches_path()
    {
        var options = new RateLimitOptions
        {
            Endpoints =
            {
                ["/api/*"] = new()
                {
                    Limits = { ["scraper-class"] = new() { OverLimitAction = "block" } },
                },
            },
        };
        var resolver = Resolver(options);
        Assert.Single(resolver.ResolveRules(null, "/api/users/42", "GET"));
        Assert.Empty(resolver.ResolveRules(null, "/static/foo.png", "GET"));
    }

    [Fact]
    public void Subject_spec_effective_merges_value_and_values()
    {
        var spec1 = new RateLimitSubjectSpec { Type = RateLimitSubjectKind.Country, Value = "GB" };
        Assert.Equal(new[] { "GB" }, spec1.Effective());

        var spec2 = new RateLimitSubjectSpec { Type = RateLimitSubjectKind.Country, Values = { "RU", "BY" } };
        Assert.Equal(new[] { "RU", "BY" }, spec2.Effective());

        var spec3 = new RateLimitSubjectSpec
        {
            Type = RateLimitSubjectKind.Country,
            Value = "GB",
            Values = { "RU", "BY" },
        };
        Assert.Equal(new[] { "GB", "RU", "BY" }, spec3.Effective());
    }

    private sealed class TestOptionsMonitor<T> : IOptions<T> where T : class
    {
        public TestOptionsMonitor(T value) { Value = value; }
        public T Value { get; }
    }
}
