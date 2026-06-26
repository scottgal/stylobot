using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Middleware;

public class BotDetectionMiddlewareFailureTests
{
    private sealed class FixedBandSource : ILoadBandSource
    {
        public FixedBandSource(LoadBand band) => CurrentBand = band;
        public LoadBand CurrentBand { get; }
    }

    [Fact]
    public void LoadShed_PolicyOptionsRespected_ForCriticalBand()
    {
        // Cover the integration of LoadShedDecision with policy options at unit level.
        // The actual middleware gate runs only with full DI; this verifies the decision
        // service the middleware will use.
        var sensor = new FixedBandSource(LoadBand.Critical);
        var decision = new LoadShedDecision(sensor);

        var alwaysShedPolicy = new DetectionPolicy
        {
            Name = "shed-all",
            LoadShed = new LoadShedOptions { BotShedAtCritical = 1.0 }
        };
        // Bot class + BotShedAtCritical=1.0 at Critical band: always shed.
        Assert.True(decision.ShouldShed(VisitorClass.Bot, alwaysShedPolicy.LoadShed, requestSeed: 42));

        var neverShedPolicy = new DetectionPolicy { Name = "default" };
        // Human class + HumanShedAtCritical=0.0 (default): never shed.
        Assert.False(decision.ShouldShed(VisitorClass.Human, neverShedPolicy.LoadShed, requestSeed: 42));
    }

    [Fact]
    public void FailOpen_AllowsRequest_AndDoesNotSet503()
    {
        var policy = new DetectionPolicy { Name = "test", OnFailure = FailureMode.FailOpen };
        var ctx = new DefaultHttpContext();

        var fh = BotDetectionMiddleware.HandleDetectionFailureFor(policy);
        var result = fh.Apply(ctx, new InvalidOperationException("boom"));

        Assert.True(result.ContinuePipeline, "FailOpen must continue the pipeline");
        Assert.NotEqual(503, ctx.Response.StatusCode);
    }

    [Fact]
    public void FailClosed_Returns503_AndShortCircuits()
    {
        var policy = new DetectionPolicy { Name = "test", OnFailure = FailureMode.FailClosed };
        var ctx = new DefaultHttpContext();

        var fh = BotDetectionMiddleware.HandleDetectionFailureFor(policy);
        var result = fh.Apply(ctx, new InvalidOperationException("boom"));

        Assert.False(result.ContinuePipeline, "FailClosed must NOT continue the pipeline");
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.True(ctx.Response.Headers.ContainsKey("X-StyloBot-Failed"));
    }

    [Fact]
    public void LogOnly_AllowsRequest_AndWritesDiagnosticHeader()
    {
        var policy = new DetectionPolicy { Name = "test", OnFailure = FailureMode.LogOnly };
        var ctx = new DefaultHttpContext();

        var fh = BotDetectionMiddleware.HandleDetectionFailureFor(policy);
        var result = fh.Apply(ctx, new InvalidOperationException("boom"));

        Assert.True(result.ContinuePipeline, "LogOnly must continue the pipeline");
        Assert.True(ctx.Response.Headers.ContainsKey("X-StyloBot-Failed"));
    }
}
