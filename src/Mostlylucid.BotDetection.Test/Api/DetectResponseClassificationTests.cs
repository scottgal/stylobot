using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Api;

public sealed class DetectResponseClassificationTests
{
    [Theory]
    [InlineData(0.8, 0.9, false)]
    [InlineData(0.8, 0.6, true)]
    [InlineData(0.9, 0.9, true)]
    public void Api_verdict_uses_the_configured_bot_floor(double probability, double floor, bool expected)
    {
        var response = DetectResponse.FromEvidence(new AggregatedEvidence
        {
            BotProbability = probability, Confidence = 0.9, RiskBand = RiskBand.Medium
        }, floor);
        Assert.Equal(expected, response.Verdict.IsBot);
    }
}
