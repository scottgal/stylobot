using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Behaviour pins for <see cref="ThrottleActionPolicy"/>. Specifically guards
///     the "fast 429 + Retry-After" contract for the visible-throttle presets
///     (tools, status) so a future regression cannot reintroduce the wall-clock
///     hold that caused upstream proxies (Caddy default dial_timeout 3s) to
///     return 502 instead of the 429 the policy promised.
/// </summary>
public class ThrottleActionPolicyTests
{
    [Fact]
    public async Task Tools_HighRisk_Returns429Fast_NotWallClockDelay()
    {
        // The Tools preset has MaxDelayMs = 15000 and ScaleByRisk = true.
        // At risk = 1.0 the calculated delay is the full 15s. That value
        // belongs in the Retry-After header (advisory back-off for the
        // client), NOT in the response wall-clock time. Holding the
        // response for 15s blows past every realistic upstream proxy
        // dial_timeout (Caddy default 3s) and causes the well-documented
        // throttle-vs-502 collision behind reverse proxies / SSH tunnels.
        var policy = new ThrottleActionPolicy("throttle-tools", ThrottleActionOptions.Tools);
        var ctx = NewContext();
        var evidence = EvidenceAt(probability: 1.0, RiskBand.VeryHigh);

        var sw = Stopwatch.StartNew();
        var result = await policy.ExecuteAsync(ctx, evidence);
        sw.Stop();

        Assert.False(result.Continue, "tools policy returns 429; the request must not continue upstream");
        Assert.Equal(429, ctx.Response.StatusCode);
        Assert.True(ctx.Response.Headers.ContainsKey("Retry-After"),
            "Retry-After must be advertised so well-behaved tools can back off");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"throttle-tools must respond fast (< 2s); took {sw.ElapsedMilliseconds}ms. " +
            "Edge proxies have ~3s dial_timeout; holding the response for the full " +
            "MaxDelayMs (15s) produces upstream 502s instead of the intended 429.");
    }

    [Fact]
    public async Task Tools_RetryAfterHeader_ReflectsScaledDelay()
    {
        // Even though the wall-clock response is fast, the Retry-After
        // header still has to advertise the calculated back-off so the
        // visible-throttling contract (well-behaved tools auto-slow) keeps
        // working. The header is the throttle now; the delay value drives
        // it, not Task.Delay.
        var policy = new ThrottleActionPolicy("throttle-tools", ThrottleActionOptions.Tools);
        var ctx = NewContext();

        await policy.ExecuteAsync(ctx, EvidenceAt(probability: 1.0, RiskBand.VeryHigh));

        var retryAfter = ctx.Response.Headers["Retry-After"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(retryAfter), "Retry-After must be set");
        var seconds = int.Parse(retryAfter, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(seconds >= 1,
            $"Retry-After should reflect the scaled back-off (>= 1s); got {seconds}");
    }

    [Fact]
    public async Task Status_Returns429Fast()
    {
        // Status preset already has MaxDelayMs = 200 and an explicit
        // RetryAfterSeconds = 60. Pin that the fast-path contract holds.
        var policy = new ThrottleActionPolicy("throttle-status", ThrottleActionOptions.Status);
        var ctx = NewContext();
        var evidence = EvidenceAt(probability: 1.0, RiskBand.VeryHigh);

        var sw = Stopwatch.StartNew();
        var result = await policy.ExecuteAsync(ctx, evidence);
        sw.Stop();

        Assert.False(result.Continue);
        Assert.Equal(429, ctx.Response.StatusCode);
        Assert.Equal("60", ctx.Response.Headers["Retry-After"].ToString());
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"throttle-status must be fast; took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Stealth_KeepsWallClockDelayBeforeContinue()
    {
        // The Stealth path is the ONLY one that should actually hold the
        // request wall-clock (silent throttling of forwarded bot traffic
        // depends on the delay being real). ReturnStatus = false here,
        // so the wall-clock budget is preserved.
        var options = new ThrottleActionOptions
        {
            BaseDelayMs = 200,
            MinDelayMs = 150,
            MaxDelayMs = 500,
            JitterPercent = 0,
            ScaleByRisk = false,
            ReturnStatus = false,
        };
        var policy = new ThrottleActionPolicy("throttle-stealth", options);
        var ctx = NewContext();
        var evidence = EvidenceAt(probability: 0.8, RiskBand.High);

        var sw = Stopwatch.StartNew();
        var result = await policy.ExecuteAsync(ctx, evidence);
        sw.Stop();

        Assert.True(result.Continue,
            "stealth allows the request to continue upstream after the delay");
        Assert.True(sw.ElapsedMilliseconds >= 150,
            $"stealth must apply the wall-clock delay (>= 150ms); took {sw.ElapsedMilliseconds}ms");
    }

    private static HttpContext NewContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/";
        return ctx;
    }

    // ── Internal-class exemption (2026-08-16, the compose-path 429 incident) ──

    [Fact]
    public async Task Internal_classified_request_is_never_throttled()
    {
        // The product's OWN traffic (peer-trusted / the hub-beacon plumbing paths)
        // must never hit the tools-throttle — even a DIRECT dispatch (the enforcement-
        // layer invariant; the resolver + rate-limit exemptions prevent the dispatch,
        // this refuses the throttle itself). No 429, no throttle headers.
        var policy = new ThrottleActionPolicy("throttle-tools", ThrottleActionOptions.Tools,
            NullLogger<ThrottleActionPolicy>.Instance);
        var ctx = NewContext();

        var result = await policy.ExecuteAsync(ctx,
            EvidenceAt(0.95, RiskBand.High) with { PrimaryBotType = BotType.Internal });

        Assert.True(result.Continue);
        Assert.Equal(string.Empty, ctx.Response.Headers["X-Throttle-Policy"].ToString());
        Assert.Equal(string.Empty, ctx.Response.Headers["Retry-After"].ToString());
    }

    [Fact]
    public async Task Non_internal_tool_verdict_is_still_throttled()
    {
        // Control: the exemption is specific to Internal — a genuine Tool-classified
        // visitor still gets the tools-throttle.
        var policy = new ThrottleActionPolicy("throttle-tools", ThrottleActionOptions.Tools,
            NullLogger<ThrottleActionPolicy>.Instance);
        var ctx = NewContext();

        var result = await policy.ExecuteAsync(ctx,
            EvidenceAt(0.95, RiskBand.High) with { PrimaryBotType = BotType.Tool });

        Assert.False(result.Continue);
    }

    private static AggregatedEvidence EvidenceAt(double probability, RiskBand band) => new()
    {
        BotProbability = probability,
        Confidence = 1.0,
        RiskBand = band,
        Signals = new Dictionary<string, object>(),
    };
}
