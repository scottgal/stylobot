using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Fifth nested bug in the Internal-RiskBand fix sequence (2026-06-20):
///     <see cref="DetectionBroadcastMiddleware.BuildDetectionFromUpstream"/> was
///     hard-coding RiskBand from <c>botProbability</c> via a switch expression,
///     completely ignoring the gateway's <c>X-Bot-Detection-RiskBand</c> header.
///     For Internal traffic at 100% probability that produced "VeryHigh" even
///     when the gateway's composer had clamped to Low.
///
///     The fix prefers the upstream header over probability-bucketing. These
///     tests pin both branches so a future refactor that re-inverts the
///     precedence fails before staging shows it.
/// </summary>
public class BuildDetectionFromUpstreamRiskBandTests
{
    private static DetectionBroadcastMiddleware NewMiddleware() =>
        new(_ => System.Threading.Tasks.Task.CompletedTask,
            NullLogger<DetectionBroadcastMiddleware>.Instance);

    private static HttpContext NewContext(double probability, string? upstreamRiskBand)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Response.StatusCode = 200;
        if (upstreamRiskBand is not null)
            ctx.Request.Headers[StyloBotEdgeHeaderNames.RiskBand] = upstreamRiskBand;
        ctx.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        return ctx;
    }

    [Fact]
    public void Upstream_RiskBand_header_overrides_probability_bucket()
    {
        var ctx = NewContext(probability: 1.0, upstreamRiskBand: "Low");
        var result = new BotDetectionResult
        {
            IsBot = true,
            ConfidenceScore = 1.0,
            BotType = BotType.Internal,
            BotName = "StyloBot Internal",
        };

        var detection = NewMiddleware().BuildDetectionFromUpstream(ctx, result);

        Assert.Equal("Low", detection.RiskBand);
    }

    [Fact]
    public void Missing_RiskBand_header_falls_back_to_probability_bucket()
    {
        var ctx = NewContext(probability: 1.0, upstreamRiskBand: null);
        var result = new BotDetectionResult
        {
            IsBot = true,
            ConfidenceScore = 1.0,
            BotType = BotType.Scraper,
            BotName = "GenericScraper",
        };

        var detection = NewMiddleware().BuildDetectionFromUpstream(ctx, result);

        // 100% probability without a header → VeryHigh via fallback bucket.
        Assert.Equal("VeryHigh", detection.RiskBand);
    }

    [Theory]
    [InlineData("VeryLow")]
    [InlineData("Low")]
    [InlineData("Medium")]
    [InlineData("High")]
    [InlineData("VeryHigh")]
    public void Header_value_passes_through_unchanged(string headerBand)
    {
        var ctx = NewContext(probability: 0.5, upstreamRiskBand: headerBand);
        var result = new BotDetectionResult
        {
            IsBot = true,
            ConfidenceScore = 0.5,
            BotType = BotType.Unknown,
            BotName = "x",
        };

        var detection = NewMiddleware().BuildDetectionFromUpstream(ctx, result);

        Assert.Equal(headerBand, detection.RiskBand);
    }
}
