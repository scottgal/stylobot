using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Tests.Endpoints;

public class DetectEndpointsTests
{
    [Fact]
    public void DetectResponse_FromEvidence_MapsVerdictCorrectly()
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.92,
            Confidence = 0.87,
            RiskBand = RiskBand.High,
            PrimaryBotType = Mostlylucid.BotDetection.Models.BotType.Scraper,
            PrimaryBotName = "GPTBot",
            ThreatScore = 0.15,
            ThreatBand = ThreatBand.Low,
            TotalProcessingTimeMs = 4.2,
            ContributingDetectors = new HashSet<string> { "UserAgent", "Header", "Ip" },
            PolicyName = "default",
            AiRan = false,
            Signals = new Dictionary<string, object> { ["ua.isBot"] = true, ["ip.isDatacenter"] = true }
        };

        var response = DetectResponse.FromEvidence(evidence);

        Assert.True(response.Verdict.IsBot);
        Assert.Equal(0.92, response.Verdict.BotProbability);
        Assert.Equal(0.87, response.Verdict.Confidence);
        Assert.Equal("Scraper", response.Verdict.BotType);
        Assert.Equal("GPTBot", response.Verdict.BotName);
        Assert.Equal("High", response.Verdict.RiskBand);
        Assert.Equal("Block", response.Verdict.RecommendedAction);
        Assert.Equal(0.15, response.Verdict.ThreatScore);
        Assert.Equal("Low", response.Verdict.ThreatBand);
        Assert.Equal(3, response.Meta.DetectorsRun);
        Assert.Equal("default", response.Meta.PolicyName);
        Assert.False(response.Meta.AiRan);
    }

    [Fact]
    public void DetectResponse_FromEvidence_HumanVerdict()
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.12, Confidence = 0.95, RiskBand = RiskBand.VeryLow,
            ThreatScore = 0.0, ThreatBand = ThreatBand.None, TotalProcessingTimeMs = 2.1,
            ContributingDetectors = new HashSet<string> { "UserAgent" },
            Signals = new Dictionary<string, object>()
        };

        var response = DetectResponse.FromEvidence(evidence);

        Assert.False(response.Verdict.IsBot);
        Assert.Equal("Allow", response.Verdict.RecommendedAction);
        Assert.Null(response.Verdict.BotType);
    }

    [Theory]
    [InlineData(RiskBand.High, "Block")]
    [InlineData(RiskBand.VeryHigh, "Block")]
    [InlineData(RiskBand.Medium, "Challenge")]
    [InlineData(RiskBand.Elevated, "Throttle")]
    [InlineData(RiskBand.Low, "Allow")]
    [InlineData(RiskBand.VeryLow, "Allow")]
    [InlineData(RiskBand.Unknown, "Allow")]
    public void DetectResponse_FromEvidence_MapsRiskBandToAction(RiskBand riskBand, string expectedAction)
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.5, Confidence = 0.5, RiskBand = riskBand,
            ThreatScore = 0, ThreatBand = ThreatBand.None, TotalProcessingTimeMs = 1,
            ContributingDetectors = new HashSet<string>(), Signals = new Dictionary<string, object>()
        };

        var response = DetectResponse.FromEvidence(evidence);
        Assert.Equal(expectedAction, response.Verdict.RecommendedAction);
    }
}
