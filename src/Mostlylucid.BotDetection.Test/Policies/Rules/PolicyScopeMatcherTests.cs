using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

/// <summary>
///     Per-slot matching semantics for <see cref="PolicyScopeMatcher.MatchesRequest"/>.
///     Each test exercises one slot (or the empty / fully-composite case)
///     against a signals dictionary shaped like the per-request bag the
///     resolver hands to <c>EffectiveWithContextAsync</c>.
/// </summary>
public class PolicyScopeMatcherTests
{
    [Fact]
    public void Empty_scope_matches_every_request()
    {
        var scope = PolicyScope.Wildcard();
        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>()));
        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["request.method"] = "POST",
            ["geo.country"] = "RU",
            ["identity.named_bot"] = "googlebot"
        }));
    }

    [Fact]
    public void Host_domain_only_matches_request_under_that_domain()
    {
        var scope = PolicyScope.Domain("acme.com");

        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["request.domain"] = "acme.com"
        }));

        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["request.domain"] = "other.com"
        }));

        // Missing domain signal: a non-null Host slot fails the match.
        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Method_only_scope_matches_case_insensitively()
    {
        var scope = new PolicyScope(Method: "POST");

        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["request.method"] = "POST"
        }));
        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["request.method"] = "post"
        }));
        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["request.method"] = "GET"
        }));
    }

    [Fact]
    public void Geo_only_scope_matches_case_insensitively()
    {
        var scope = new PolicyScope(Geo: "RU");

        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["geo.country"] = "RU"
        }));
        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["geo.country"] = "ru"
        }));
        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["geo.country"] = "US"
        }));
    }

    [Fact]
    public void Identity_named_bot_scope_matches_canonical_signal()
    {
        var scope = new PolicyScope(Identity: new IdentityScope.NamedBot("googlebot"));

        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["identity.named_bot"] = "googlebot"
        }));
        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["identity.named_bot"] = "bingbot"
        }));
    }

    [Fact]
    public void Identity_bot_type_scope_matches_category_signal()
    {
        var scope = new PolicyScope(Identity: new IdentityScope.BotType("scraper"));

        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["identity.bot_type"] = "scraper"
        }));
        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["identity.bot_type"] = "headless"
        }));
    }

    [Fact]
    public void Identity_human_browser_scope_matches_family_signal()
    {
        var scope = new PolicyScope(Identity: new IdentityScope.HumanBrowser("chrome"));

        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["identity.human_browser"] = "chrome"
        }));
        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["identity.human_browser"] = "firefox"
        }));
    }

    [Fact]
    public void Identity_fingerprint_scope_matches_fingerprint_id()
    {
        var scope = new PolicyScope(Identity: new IdentityScope.Fingerprint("fp-001"));

        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["identity.fingerprint_id"] = "fp-001"
        }));
        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["identity.fingerprint_id"] = "fp-002"
        }));
    }

    [Fact]
    public void Composite_scope_requires_every_slot_match()
    {
        var scope = new PolicyScope(
            Host: new HostScope.Domain("acme.com"),
            Method: "POST",
            Geo: "RU",
            Identity: new IdentityScope.BotType("scraper"));

        // All four slots match.
        Assert.True(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["request.domain"] = "acme.com",
            ["request.method"] = "POST",
            ["geo.country"] = "RU",
            ["identity.bot_type"] = "scraper"
        }));

        // Geo differs.
        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["request.domain"] = "acme.com",
            ["request.method"] = "POST",
            ["geo.country"] = "US",
            ["identity.bot_type"] = "scraper"
        }));

        // Identity differs.
        Assert.False(PolicyScopeMatcher.MatchesRequest(scope, new Dictionary<string, object?>
        {
            ["request.domain"] = "acme.com",
            ["request.method"] = "POST",
            ["geo.country"] = "RU",
            ["identity.bot_type"] = "headless"
        }));
    }
}
