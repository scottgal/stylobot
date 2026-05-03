using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Api.Middleware;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Tests.Middleware;

public class ResponseHeaderTests
{
    [Fact]
    public void InjectHeaders_WritesAllExpectedHeaders()
    {
        var context = new DefaultHttpContext();
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.92, Confidence = 0.87, RiskBand = RiskBand.High,
            PrimaryBotType = Mostlylucid.BotDetection.Models.BotType.Scraper,
            PrimaryBotName = "GPTBot", ThreatScore = 0.15, ThreatBand = ThreatBand.Low,
            PolicyName = "default", TotalProcessingTimeMs = 4,
            ContributingDetectors = new HashSet<string>(),
            Signals = new Dictionary<string, object>()
        };
        context.Items["BotDetection.AggregatedEvidence"] = evidence;

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("true", context.Response.Headers["X-StyloBot-IsBot"].ToString());
        Assert.Equal("0.92", context.Response.Headers["X-StyloBot-Probability"].ToString());
        Assert.Equal("0.87", context.Response.Headers["X-StyloBot-Confidence"].ToString());
        Assert.Equal("Scraper", context.Response.Headers["X-StyloBot-BotType"].ToString());
        Assert.Equal("GPTBot", context.Response.Headers["X-StyloBot-BotName"].ToString());
        Assert.Equal("High", context.Response.Headers["X-StyloBot-RiskBand"].ToString());
        Assert.Equal("Block", context.Response.Headers["X-StyloBot-Action"].ToString());
        Assert.Equal("0.15", context.Response.Headers["X-StyloBot-ThreatScore"].ToString());
        Assert.Equal("Low", context.Response.Headers["X-StyloBot-ThreatBand"].ToString());
        Assert.Equal("default", context.Response.Headers["X-StyloBot-Policy"].ToString());
    }

    [Fact]
    public void InjectHeaders_NoEvidence_NoHeaders()
    {
        var context = new DefaultHttpContext();
        ResponseHeaderInjection.InjectHeaders(context);
        Assert.False(context.Response.Headers.ContainsKey("X-StyloBot-IsBot"));
    }

    [Fact]
    public void InjectHeaders_HumanVerdict_IsBotFalse()
    {
        var context = new DefaultHttpContext();
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.12, Confidence = 0.95, RiskBand = RiskBand.VeryLow,
            ThreatScore = 0, ThreatBand = ThreatBand.None, TotalProcessingTimeMs = 2,
            ContributingDetectors = new HashSet<string>(),
            Signals = new Dictionary<string, object>()
        };
        context.Items["BotDetection.AggregatedEvidence"] = evidence;

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("false", context.Response.Headers["X-StyloBot-IsBot"].ToString());
        Assert.Equal("Allow", context.Response.Headers["X-StyloBot-Action"].ToString());
    }
}
