using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class VarianceWatchdogTests
{
    private static SignatureVerdict CachedHuman() => new()
    {
        SignatureId = "sig-X",
        BotProbability = 0.04,
        Confidence = 0.9,
        RiskBand = RiskBand.Low,
        RequestCount = 50,
        LastSeenUtc = DateTime.UtcNow.AddSeconds(-30),
    };

    private static HttpContext CtxFrom(string ip, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        ctx.Request.Path = path;
        return ctx;
    }

    [Fact]
    public void NoChange_DoesNotTrip()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        watchdog.RecordObservation("sig-X", "10.0.0.5", "/blog/post");

        var ctx = CtxFrom("10.0.0.5", "/blog/post");
        var result = watchdog.Check(ctx, "sig-X", CachedHuman(), new VarianceWatchdogOptions());
        Assert.False(result.Tripped);
    }

    [Fact]
    public void IpRotation_WithinWindow_Trips()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        watchdog.RecordObservation("sig-Y", "10.0.0.5", "/blog/post");

        var ctx = CtxFrom("10.1.0.5", "/blog/post");
        var result = watchdog.Check(ctx, "sig-Y", CachedHuman(), new VarianceWatchdogOptions());
        Assert.True(result.Tripped);
        Assert.Contains("ip", result.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IpRotation_DisabledViaOptions_DoesNotTrip()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        watchdog.RecordObservation("sig-Z", "10.0.0.5", "/");
        var ctx = CtxFrom("10.1.0.5", "/");
        var opts = new VarianceWatchdogOptions { IpRotationWindowSeconds = 0 };
        var result = watchdog.Check(ctx, "sig-Z", CachedHuman(), opts);
        Assert.False(result.Tripped);
    }

    [Fact]
    public void RateSpike_WhenObservedRateExceedsMultiplier_Trips()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        for (var i = 0; i < 200; i++)
            watchdog.RecordObservation("sig-R", "10.0.0.5", "/api");

        var ctx = CtxFrom("10.0.0.5", "/api");
        var opts = new VarianceWatchdogOptions { RateSpikeMultiplier = 2.0 };
        var result = watchdog.Check(ctx, "sig-R", CachedHuman(), opts);
        Assert.True(result.Tripped);
    }

    [Fact]
    public void Disabled_NeverTrips()
    {
        var watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        watchdog.RecordObservation("sig-D", "10.0.0.5", "/");
        var ctx = CtxFrom("10.1.0.5", "/");
        var opts = new VarianceWatchdogOptions { Enabled = false };
        var result = watchdog.Check(ctx, "sig-D", CachedHuman(), opts);
        Assert.False(result.Tripped);
    }
}
