using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Reputation;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Middleware;

public sealed class WebhookOutcomeRecorderMiddlewareTests
{
    [Fact]
    public async Task Records_upstream_status_after_next_not_before()
    {
        var rep = new Mock<IWebhookEndpointReputation>();
        int? seenAtRecord = null;
        rep.Setup(r => r.RecordOutcome(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
           .Callback<string, string, int>((_, _, s) => seenAtRecord = s);
        var ctx = new DefaultHttpContext(); ctx.Request.Method = "POST"; ctx.Request.Path = "/hooks/x";
        ctx.Items["sb.webhook.endpoint"] = "/hooks/x";
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("1.1.1.1");
        RequestDelegate next = c => { c.Response.StatusCode = 200; return Task.CompletedTask; }; // upstream sets 200
        await new WebhookOutcomeRecorderMiddleware(next, rep.Object).InvokeAsync(ctx);
        seenAtRecord.Should().Be(200);   // proves it read the status AFTER _next set it
        rep.Verify(r => r.RecordOutcome("/hooks/x", "1.1.1.1", 200), Times.Once);
    }

    [Fact]
    public async Task Non_webhook_request_is_not_recorded()
    {
        var rep = new Mock<IWebhookEndpointReputation>();
        var ctx = new DefaultHttpContext(); ctx.Request.Method = "GET"; ctx.Request.Path = "/";
        await new WebhookOutcomeRecorderMiddleware(_ => Task.CompletedTask, rep.Object).InvokeAsync(ctx);
        rep.Verify(r => r.RecordOutcome(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }
}
