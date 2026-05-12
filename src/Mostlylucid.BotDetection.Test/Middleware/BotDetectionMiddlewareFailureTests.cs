using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Policies;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Middleware;

public class BotDetectionMiddlewareFailureTests
{
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
