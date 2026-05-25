using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.EndpointPolicies;
using Xunit;

namespace Mostlylucid.BotDetection.Test.EndpointPolicies;

public class DetectionPolicyMatcherTests
{
    [Theory]
    [InlineData(">= 0.7", 0.7, true)]
    [InlineData(">= 0.7", 0.69, false)]
    [InlineData("> 0.7", 0.7, false)]
    [InlineData("> 0.7", 0.71, true)]
    [InlineData("<= 0.5", 0.5, true)]
    [InlineData("<= 0.5", 0.51, false)]
    [InlineData("< 0.5", 0.5, false)]
    [InlineData("== 1.0", 1.0, true)]
    [InlineData("== 1.0", 0.99, false)]
    [InlineData("!= 0", 0.1, true)]
    [InlineData("!= 0", 0.0, false)]
    public void Numeric_comparisons_evaluate_correctly(string expression, double actual, bool expected)
    {
        Assert.Equal(expected, DetectionPolicyMatcher.MatchNumeric(expression, actual));
    }

    [Theory]
    [InlineData("garbage", false)]
    [InlineData(">=", false)]              // missing rhs
    [InlineData("0.5", false)]             // missing op
    [InlineData(">= notanumber", false)]
    public void Malformed_numeric_expressions_return_false(string expression, bool expected)
    {
        Assert.Equal(expected, DetectionPolicyMatcher.MatchNumeric(expression, 0.5));
    }

    private static HttpContext Ctx(string method = "GET", string path = "/", string host = "example.com")
    {
        var c = new DefaultHttpContext();
        c.Request.Method = method;
        c.Request.Path = path;
        c.Request.Host = new HostString(host);
        return c;
    }

    [Fact]
    public void All_predicates_must_match_for_a_rule_to_fire()
    {
        var rule = new DetectionPolicyRule
        {
            Name = "block-confident-scrapers",
            BotProbability = ">= 0.7",
            Confidence = ">= 0.5",
            Type = "Scraper",
            Action = "block",
        };
        // Matching detection.
        Assert.NotNull(DetectionPolicyMatcher.FindMatch(
            new[] { rule },
            Ctx(),
            new DetectionInputs(BotProbability: 0.9, Confidence: 0.8, BotType: "Scraper", ThreatBand: null)));
        // Probability too low.
        Assert.Null(DetectionPolicyMatcher.FindMatch(
            new[] { rule },
            Ctx(),
            new DetectionInputs(0.5, 0.8, "Scraper", null)));
        // Wrong type.
        Assert.Null(DetectionPolicyMatcher.FindMatch(
            new[] { rule },
            Ctx(),
            new DetectionInputs(0.9, 0.8, "Browser", null)));
    }

    [Fact]
    public void Type_list_is_OR_within_a_single_rule()
    {
        var rule = new DetectionPolicyRule
        {
            Types = new() { "Scraper", "Probe" },
            Action = "block",
        };
        var rules = new[] { rule };
        Assert.NotNull(DetectionPolicyMatcher.FindMatch(rules, Ctx(), new(0, 0, "Scraper", null)));
        Assert.NotNull(DetectionPolicyMatcher.FindMatch(rules, Ctx(), new(0, 0, "Probe", null)));
        Assert.Null(DetectionPolicyMatcher.FindMatch(rules, Ctx(), new(0, 0, "Browser", null)));
    }

    [Fact]
    public void First_match_wins_in_declaration_order()
    {
        var rules = new[]
        {
            new DetectionPolicyRule { Type = "Scraper", Action = "block",     Name = "first" },
            new DetectionPolicyRule { Type = "Scraper", Action = "challenge", Name = "second" },
        };
        var match = DetectionPolicyMatcher.FindMatch(rules, Ctx(), new(0, 0, "Scraper", null));
        Assert.Equal("first", match?.Name);
    }

    [Fact]
    public void Empty_action_rule_is_skipped()
    {
        var rules = new[]
        {
            new DetectionPolicyRule { Type = "Scraper", Action = "",          Name = "broken" },
            new DetectionPolicyRule { Type = "Scraper", Action = "block",     Name = "good" },
        };
        var match = DetectionPolicyMatcher.FindMatch(rules, Ctx(), new(0, 0, "Scraper", null));
        Assert.Equal("good", match?.Name);
    }

    [Fact]
    public void Path_glob_and_method_combine_with_detection_matchers()
    {
        var rule = new DetectionPolicyRule
        {
            Method = "POST",
            Path = "/api/*",
            BotProbability = ">= 0.5",
            Action = "block",
        };
        var rules = new[] { rule };

        Assert.NotNull(DetectionPolicyMatcher.FindMatch(
            rules, Ctx("POST", "/api/users"), new(0.6, 0, null, null)));
        Assert.Null(DetectionPolicyMatcher.FindMatch(
            rules, Ctx("POST", "/static/foo.png"), new(0.6, 0, null, null)));
        Assert.Null(DetectionPolicyMatcher.FindMatch(
            rules, Ctx("GET", "/api/users"), new(0.6, 0, null, null)));
    }

    [Fact]
    public void Rule_with_no_matchers_matches_everything()
    {
        var rule = new DetectionPolicyRule { Action = "logonly" };
        var match = DetectionPolicyMatcher.FindMatch(
            new[] { rule }, Ctx(), new(0, 0, null, null));
        Assert.NotNull(match);
    }

    [Fact]
    public void Null_actual_against_populated_categorical_list_fails()
    {
        var rule = new DetectionPolicyRule { Type = "Scraper", Action = "block" };
        Assert.Null(DetectionPolicyMatcher.FindMatch(
            new[] { rule }, Ctx(), new(0, 0, BotType: null, ThreatBand: null)));
    }
}
