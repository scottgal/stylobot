using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Enforcement;

public sealed class VerifiedCrawlerFastPathTests
{
    [Theory]
    [InlineData("GET", "/")]
    [InlineData("HEAD", "/about")]
    public async Task Verified_public_marketing_request_bypasses_rate_limit(string method, string path)
    {
        var gate = Gate("marketing.example.test");
        var context = Context(method, path, "marketing.example.test");

        var (outcome, evidence) = await gate.EvaluateAsync(context, VerifiedEvidence(), Registry());

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
        Assert.Equal("verified-crawler-fast-path", evidence.TriggeredActionPolicyName);
        Assert.True(context.Items.ContainsKey("BotDetection.VerifiedCrawlerFastPath"));
    }

    [Theory]
    [InlineData("POST", "/")]
    [InlineData("GET", "/admin")]
    [InlineData("GET", "/api/data")]
    [InlineData("GET", "/dashboard")]
    [InlineData("GET", "/account")]
    public async Task Verified_sensitive_or_non_safe_request_remains_on_normal_rate_policy(string method, string path)
    {
        var (outcome, evidence) = await Gate("marketing.example.test")
            .EvaluateAsync(Context(method, path, "marketing.example.test"), VerifiedEvidence(), Registry());

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
        Assert.Equal("rate-limit-search", evidence.TriggeredActionPolicyName);
    }

    [Fact]
    public async Task Unverified_search_claim_remains_on_normal_rate_policy()
    {
        var (outcome, evidence) = await Gate("marketing.example.test")
            .EvaluateAsync(Context("GET", "/", "marketing.example.test"), UnverifiedSearchEvidence(), Registry());

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
        Assert.Equal("rate-limit-search", evidence.TriggeredActionPolicyName);
    }

    [Fact]
    public async Task Empty_host_configuration_is_a_no_op()
    {
        var (outcome, evidence) = await Gate()
            .EvaluateAsync(Context("GET", "/", "marketing.example.test"), VerifiedEvidence(), Registry());

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
        Assert.Equal("rate-limit-search", evidence.TriggeredActionPolicyName);
    }

    private static PostDetectionActionGate Gate(params string[] hosts) => new(
        Options.Create(new BotDetectionOptions
        {
            VerifiedCrawlerFastPath = new VerifiedCrawlerFastPathOptions { MarketingHosts = hosts.ToList() }
        }),
        NullLogger<PostDetectionActionGate>.Instance);

    private static IActionPolicyRegistry Registry() => new ActionPolicyRegistry(
        Options.Create(new BotDetectionOptions()), Array.Empty<IActionPolicyFactory>());

    private static DefaultHttpContext Context(string method, string path, string trustedSniHost)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        // This test models the trusted scope already stamped from gateway-validated SNI,
        // rather than deriving it from the request Host header.
        context.Items[HttpContextItemKeys.RequestScope] = new RequestScope("example.test", trustedSniHost);
        return context;
    }

    private static AggregatedEvidence VerifiedEvidence() => new()
    {
        BotProbability = 1,
        Confidence = 1,
        RiskBand = RiskBand.Low,
        PrimaryBotType = BotType.SearchEngine,
        Signals = new Dictionary<string, object> { [SignalKeys.FriendlyIpVerified] = true }
    };

    private static AggregatedEvidence UnverifiedSearchEvidence() => new()
    {
        BotProbability = 1,
        Confidence = 1,
        RiskBand = RiskBand.Low,
        PrimaryBotType = BotType.SearchEngine,
        Signals = new Dictionary<string, object>()
    };
}
