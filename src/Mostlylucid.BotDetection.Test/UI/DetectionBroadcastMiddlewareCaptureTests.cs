using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public class DetectionBroadcastMiddlewareCaptureTests
{
    [Fact]
    public void BuildDetectionFromEvidence_sets_ResponseBytes_from_ContentLength()
    {
        var middleware = NewMiddleware();
        var ctx = NewHttpContext();
        ctx.Response.ContentLength = 8192;

        var detection = middleware.BuildDetectionFromEvidence(ctx, NewMinimalEvidence());

        detection.ResponseBytes.Should().Be(8192);
    }

    [Fact]
    public void BuildDetectionFromEvidence_ResponseBytes_null_when_no_ContentLength()
    {
        var middleware = NewMiddleware();
        var ctx = NewHttpContext();

        var detection = middleware.BuildDetectionFromEvidence(ctx, NewMinimalEvidence());

        detection.ResponseBytes.Should().BeNull();
    }

    [Fact]
    public void BuildDetectionFromUpstream_sets_ResponseBytes_from_ContentLength()
    {
        var middleware = NewMiddleware();
        var ctx = NewHttpContext();
        ctx.Response.ContentLength = 256;

        var detection = middleware.BuildDetectionFromUpstream(ctx, NewMinimalUpstreamResult());

        detection.ResponseBytes.Should().Be(256);
    }

    [Fact]
    public void BuildDetectionFromUpstream_ResponseBytes_null_when_no_ContentLength()
    {
        var middleware = NewMiddleware();
        var ctx = NewHttpContext();

        var detection = middleware.BuildDetectionFromUpstream(ctx, NewMinimalUpstreamResult());

        detection.ResponseBytes.Should().BeNull();
    }

    // --- helpers ---

    private static DetectionBroadcastMiddleware NewMiddleware()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        return new DetectionBroadcastMiddleware(next, NullLogger<DetectionBroadcastMiddleware>.Instance);
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/test";
        ctx.Response.StatusCode = 200;
        // Builders call context.RequestServices.GetService<IOptions<StyloBotDashboardOptions>>()
        // (guarded with ?. so null return is fine). An empty container satisfies this.
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();
        return ctx;
    }

    private static AggregatedEvidence NewMinimalEvidence() =>
        new()
        {
            BotProbability = 0,
            Confidence = 0.5,
            RiskBand = RiskBand.Low,
            // Ledger = null gives empty Contributions via Array.Empty fallback
        };

    private static BotDetectionResult NewMinimalUpstreamResult() =>
        new()
        {
            IsBot = false,
            ConfidenceScore = 0.1,
        };
}
