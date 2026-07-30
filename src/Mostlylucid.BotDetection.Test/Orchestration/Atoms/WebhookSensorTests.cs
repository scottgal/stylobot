using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Definitions.Webhooks;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Reputation;
using Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms;

/// <summary>
///     Unit tests for <see cref="WebhookSensor"/>. The load-bearing test is
///     <see cref="Post_forged_provider_header_fresh_ip_is_not_recognized"/> — a real
///     provider signature-header NAME from a fresh IP with no reputation record must NOT
///     be recognized. The signature header alone is spoofable; trust requires an
///     IP-based corroborator (dominant source IP, verified delivery record, or a
///     provider's published IP range). That negative test is the proof this sensor
///     recognizes webhook deliveries WITHOUT a bypass.
/// </summary>
public sealed class WebhookSensorTests
{
    private const string Session = "s1";

    private static WebhookSensor New(HttpContext ctx, IWebhookEndpointReputation? rep = null)
        => new(NullLogger<WebhookSensor>.Instance, new StubDetectorConfigProvider(),
               new StaticHttpContextAccessor(ctx), WebhookCatalog.Default, rep);

    private static DefaultHttpContext Post(string path, (string, string)[] headers, string ct = "application/json")
    {
        var c = new DefaultHttpContext();
        c.Request.Method = "POST";
        c.Request.Path = path;
        c.Request.ContentType = ct;
        foreach (var (k, v) in headers) c.Request.Headers[k] = v;
        return c;
    }

    [Fact] // named provider + an IP-based corroborator (verified record) => recognized
    public async Task Post_named_provider_with_verified_record_is_recognized_lowthreat()
    {
        var rep = new Mock<IWebhookEndpointReputation>();
        rep.Setup(r => r.HasVerifiedRecord(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var ctx = Post("/hooks/stripe", new[] { ("Stripe-Signature", "t=1,v1=abc") });
        var r = await New(ctx, rep.Object).DetectAsync(new SignalSink(1000, TimeSpan.FromMinutes(1)), Session);
        r.Should().ContainSingle();
        r[0].ConfidenceDelta.Should().BeLessThan(0);
        r[0].BotType.Should().Be(BotType.GoodBot.ToString());
    }

    [Fact] // LOAD-BEARING spoof guard — a FORGED provider header from a fresh IP is NOT trusted
    public async Task Post_forged_provider_header_fresh_ip_is_not_recognized()
    {
        // real provider header NAME but no reputation (fresh IP, no verified record, no dominance)
        var ctx = Post("/hooks/stripe", new[] { ("Stripe-Signature", "forged") });
        var sink = new SignalSink(1000, TimeSpan.FromMinutes(1));
        var r = await New(ctx, rep: null).DetectAsync(sink, Session); // null store => no IP corroboration
        r.Should().BeEmpty("a signature header alone is spoofable; trust needs an IP-based corroborator");
        sink.Detect(SignalKeys.WebhookShape).Should().BeTrue("it IS webhook-shaped (observed for learning), just not recognized");
    }

    [Fact]
    public async Task Get_request_is_ignored()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/hooks/stripe";
        (await New(ctx).DetectAsync(new SignalSink(1000, TimeSpan.FromMinutes(1)), Session)).Should().BeEmpty();
    }

    [Fact]
    public async Task Shape_only_from_dominant_ip_is_recognized()
    {
        var rep = new Mock<IWebhookEndpointReputation>();
        rep.Setup(r => r.IsDominantIp(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var ctx = Post("/hooks/x", new[]{("X-Webhook-Signature","z")}); // generic sig header, NOT a named provider
        var r = await New(ctx, rep.Object).DetectAsync(new SignalSink(1000, TimeSpan.FromMinutes(1)), Session);
        r.Should().ContainSingle(); r[0].ConfidenceDelta.Should().BeLessThan(0);
    }

    [Fact]
    public async Task Every_webhook_shaped_request_records_a_request_for_learning()
    {
        var rep = new Mock<IWebhookEndpointReputation>();
        var ctx = Post("/hooks/x", new[]{("X-Webhook-Signature","z")});
        await New(ctx, rep.Object).DetectAsync(new SignalSink(1000, TimeSpan.FromMinutes(1)), Session);
        rep.Verify(r => r.RecordRequest("/hooks/x", It.IsAny<string>()), Times.Once);
    }
}
