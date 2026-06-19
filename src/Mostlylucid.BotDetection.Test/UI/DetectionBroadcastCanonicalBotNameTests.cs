using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins the canonical-casing normaliser at the dashboard broadcast
///     boundary. Both <c>BuildDetectionFromEvidence</c> (locally-orchestrated)
///     and <c>BuildDetectionFromUpstream</c> (upstream-trusted gateway header)
///     must fold whatever the per-request contributor emitted into the
///     BotPatternLoader catalog's canonical casing before the event leaves
///     the middleware -- so every downstream reader (cache, views, all
///     surfaces) sees ONE canonical string per identity.
/// </summary>
public class DetectionBroadcastCanonicalBotNameTests
{
    [Theory]
    [InlineData("googlebot",  "Googlebot")]
    [InlineData("GOOGLEBOT",  "Googlebot")]
    [InlineData("Googlebot",  "Googlebot")]
    public void BuildDetectionFromEvidence_canonicalises_BotName(string emitted, string expected)
    {
        var middleware = NewMiddleware();
        var ctx = NewHttpContext();

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.95,
            Confidence = 0.9,
            RiskBand = RiskBand.High,
            PrimaryBotName = emitted,
        };

        var detection = middleware.BuildDetectionFromEvidence(ctx, evidence);
        detection.BotName.Should().Be(expected);
    }

    [Fact]
    public void BuildDetectionFromEvidence_passes_unknown_botname_through()
    {
        // Custom matcher labels and fediverse suffixes pass through unchanged --
        // the normaliser is opt-in, only rewrites names it knows.
        var middleware = NewMiddleware();
        var ctx = NewHttpContext();

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.95,
            Confidence = 0.9,
            RiskBand = RiskBand.High,
            PrimaryBotName = "Customer FX scraper",
        };

        var detection = middleware.BuildDetectionFromEvidence(ctx, evidence);
        detection.BotName.Should().Be("Customer FX scraper");
    }

    [Theory]
    [InlineData("googlebot",  "Googlebot")]
    [InlineData("GOOGLEBOT",  "Googlebot")]
    public void BuildDetectionFromUpstream_canonicalises_BotName(string emitted, string expected)
    {
        var middleware = NewMiddleware();
        var ctx = NewHttpContext();

        var result = new BotDetectionResult
        {
            IsBot = true,
            ConfidenceScore = 0.95,
            BotName = emitted,
        };

        var detection = middleware.BuildDetectionFromUpstream(ctx, result);
        detection.BotName.Should().Be(expected);
    }

    // --- helpers (mirrors DetectionBroadcastMiddlewareCaptureTests) ---

    private static DetectionBroadcastMiddleware NewMiddleware()
    {
        RequestDelegate next = _ => System.Threading.Tasks.Task.CompletedTask;
        return new DetectionBroadcastMiddleware(next, NullLogger<DetectionBroadcastMiddleware>.Instance);
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/test";
        ctx.Response.StatusCode = 200;
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();
        return ctx;
    }
}
