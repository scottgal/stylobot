using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Behaviour pins for <see cref="RateLimitActionPolicy"/>. Phase 2 of
///     the policy-grammar work.
/// </summary>
public class RateLimitActionPolicyTests
{
    [Fact]
    public async Task BucketHasCapacity_Allows_AndAddsHeaders()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 10, OverLimitAction = "throttle-status",
        });
        var ctx = NewContext();

        var result = await policy.ExecuteAsync(ctx, EvidenceWithSig("sig:a"));

        Assert.True(result.Continue);
        Assert.Equal("rate-limit-test", ctx.Response.Headers["X-RateLimit-Policy"].ToString());
        Assert.Equal("60", ctx.Response.Headers["X-RateLimit-Limit"].ToString());
    }

    [Fact]
    public async Task BucketEmpty_RoutesToOverLimitAction()
    {
        var fakeOverLimit = new RecordingFallback("over-limit-policy");
        var registry = BuildRegistry(extra: fakeOverLimit);
        var store = new InMemoryTokenBucketStore();
        var policy = new RateLimitActionPolicy(
            "rate-limit-test",
            new RateLimitActionOptions { RequestsPerMinute = 60, BurstSize = 2, OverLimitAction = "over-limit-policy" },
            store, registry);
        var ctx = NewContext();
        var evidence = EvidenceWithSig("sig:a");

        // Burn the two-token burst.
        await policy.ExecuteAsync(ctx, evidence);
        await policy.ExecuteAsync(ctx, evidence);

        // Third request must hit the fallback.
        var result = await policy.ExecuteAsync(ctx, evidence);

        Assert.Equal(1, fakeOverLimit.HitCount);
        Assert.False(result.Continue);
    }

    [Fact]
    public async Task OverLimitMissingFromRegistry_FallsBackToBare429()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1, OverLimitAction = "does-not-exist",
        });
        var ctx = NewContext();
        var evidence = EvidenceWithSig("sig:a");

        await policy.ExecuteAsync(ctx, evidence);                 // consume the single token
        var denied = await policy.ExecuteAsync(ctx, evidence);    // bucket empty

        Assert.False(denied.Continue);
        Assert.Equal(429, ctx.Response.StatusCode);
        Assert.Equal("60", ctx.Response.Headers["Retry-After"].ToString());
    }

    [Fact]
    public async Task NoSignatureSignal_AllowsCleanly_NoGlobalBucket()
    {
        // If no signature is on the request, we'd otherwise share a single
        // bucket across every anonymous visitor -- "" as a key. That's worse
        // than not rate-limiting. The policy must allow cleanly instead.
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 10, KeyBy = RateLimitKey.Signature,
        });
        var ctx = NewContext();

        var result = await policy.ExecuteAsync(ctx, EvidenceWithoutSig());

        Assert.True(result.Continue);
        Assert.Contains("no-key", result.Description ?? "");
    }

    [Fact]
    public async Task IpKey_UsesRemoteIp_NotSignature()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1, KeyBy = RateLimitKey.Ip,
            OverLimitAction = "does-not-exist",
        });
        var ctx = NewContext("203.0.113.7");

        await policy.ExecuteAsync(ctx, EvidenceWithoutSig());       // consume token (IP key works without sig)
        var denied = await policy.ExecuteAsync(ctx, EvidenceWithoutSig());

        Assert.False(denied.Continue);
        Assert.Equal(429, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AdaptiveScaling_HalvesEffectiveAllowance_When05xMultiplier()
    {
        // Phase 4: 0.5 multiplier should halve the effective RequestsPerMinute.
        // With 60 RPM and burst 10, a 0.5 multiplier means burst 10 + 1 token/2sec.
        // After consuming the burst, immediate requests should be denied.
        var registry = BuildRegistry();
        var store = new InMemoryTokenBucketStore();
        var adaptive = new StubAdaptiveScaling(0.5, "degraded");
        var policy = new RateLimitActionPolicy(
            "rate-limit-test",
            new RateLimitActionOptions { RequestsPerMinute = 60, BurstSize = 10, OverLimitAction = "does-not-exist" },
            store, registry, adaptive);
        var ctx = NewContext();

        // Burn the burst, then one more.
        for (var i = 0; i < 10; i++) await policy.ExecuteAsync(ctx, EvidenceWithSig("sig:a"));
        var denied = await policy.ExecuteAsync(ctx, EvidenceWithSig("sig:a"));
        Assert.False(denied.Continue);

        // X-RateLimit-Limit header should reflect EFFECTIVE (30), not configured (60).
        Assert.Equal("30", ctx.Response.Headers["X-RateLimit-Limit"].ToString());
        Assert.Equal("degraded", ctx.Response.Headers["X-RateLimit-Tier"].ToString());
        Assert.Equal("0.50", ctx.Response.Headers["X-RateLimit-Multiplier"].ToString());
    }

    [Fact]
    public async Task AdaptiveScaling_AtNominal_NoExtraHeaders_LimitIsConfigured()
    {
        var registry = BuildRegistry();
        var store = new InMemoryTokenBucketStore();
        var adaptive = new StubAdaptiveScaling(1.0, null);
        var policy = new RateLimitActionPolicy(
            "rate-limit-test",
            new RateLimitActionOptions { RequestsPerMinute = 60, BurstSize = 10 },
            store, registry, adaptive);
        var ctx = NewContext();

        await policy.ExecuteAsync(ctx, EvidenceWithSig("sig:a"));

        Assert.Equal("60", ctx.Response.Headers["X-RateLimit-Limit"].ToString());
        // Tier/multiplier headers ONLY appear when scaling is active (multiplier < ~1.0).
        Assert.False(ctx.Response.Headers.ContainsKey("X-RateLimit-Tier"));
        Assert.False(ctx.Response.Headers.ContainsKey("X-RateLimit-Multiplier"));
    }

    [Fact]
    public void Policy_ReportsRateLimitIntent_NotThrottle()
    {
        var (policy, _) = Build(new RateLimitActionOptions());
        Assert.Equal(PolicyIntent.RateLimit, policy.Intent);
        // The ActionType reports Throttle (closest existing fit) for
        // backward-compat with the action-pipeline code. The intent layer
        // is what new code should branch on.
        Assert.Equal(ActionType.Throttle, policy.ActionType);
    }

    // ===== helpers =====

    private static (RateLimitActionPolicy policy, ActionPolicyRegistry registry) Build(RateLimitActionOptions opts)
    {
        var registry = BuildRegistry();
        var store = new InMemoryTokenBucketStore();
        var policy = new RateLimitActionPolicy("rate-limit-test", opts, store, registry);
        return (policy, registry);
    }

    private static ActionPolicyRegistry BuildRegistry(IActionPolicy? extra = null)
    {
        // Real registry so OverLimitAction lookup is exercised end-to-end
        // (throttle-status / block-soft are built-ins; extra plugs in the
        // recording fallback for routing tests).
        var policies = extra is null ? Array.Empty<IActionPolicy>() : new[] { extra };
        return new ActionPolicyRegistry(
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>(),
            policies);
    }

    private static HttpContext NewContext(string? remoteIp = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/test";
        if (remoteIp is not null)
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);
        return ctx;
    }

    private static AggregatedEvidence EvidenceWithSig(string sig) => new()
    {
        BotProbability = 0.5,
        Confidence = 0.8,
        RiskBand = RiskBand.Medium,
        Signals = new Dictionary<string, object> { [SignalKeys.PrimarySignature] = sig },
    };

    private static AggregatedEvidence EvidenceWithoutSig() => new()
    {
        BotProbability = 0.5,
        Confidence = 0.8,
        RiskBand = RiskBand.Medium,
        Signals = new Dictionary<string, object>(),
    };

    private sealed class StubAdaptiveScaling : Mostlylucid.BotDetection.RateLimit.IAdaptiveScalingTracker
    {
        public StubAdaptiveScaling(double multiplier, string? tier)
        {
            CurrentMultiplier = multiplier;
            CurrentTier = tier;
            TierEnteredAtUtc = tier is null ? null : DateTime.UtcNow;
        }
        public double CurrentMultiplier { get; }
        public string? CurrentTier { get; }
        public DateTime? TierEnteredAtUtc { get; }
    }

    // ── Internal-class exemption (2026-08-16, the compose-path 429 incident) ──

    [Fact]
    public async Task Internal_classified_request_is_never_rate_limited()
    {
        // The site's own data path (the compose key) classifies Internal by peer
        // trust; its prewarm burst must never 429 the dashboard — the exemption must
        // hold even when the bucket is empty.
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 1, BurstSize = 1, OverLimitAction = "throttle-status",
        });
        var ctx = NewContext();

        // Exhaust the bucket with ordinary visitor evidence.
        await policy.ExecuteAsync(ctx, EvidenceWithSig("sig:a"));
        var denied = await policy.ExecuteAsync(ctx, EvidenceWithSig("sig:a"));
        Assert.False(denied.Continue);

        // Internal-classified (operator-owned) evidence passes regardless of the
        // empty bucket, and does not stamp the rate-limit headers (fresh context —
        // the earlier calls stamped theirs).
        var internalCtx = NewContext();
        var internalResult = await policy.ExecuteAsync(internalCtx,
            EvidenceWithSig("sig:a") with { PrimaryBotType = BotType.Internal });

        Assert.True(internalResult.Continue);
        Assert.Equal(string.Empty, internalCtx.Response.Headers["X-RateLimit-Policy"].ToString());
    }

    [Fact]
    public async Task Non_internal_classified_request_is_still_rate_limited()
    {
        // Control: the exemption is specific to Internal — a genuine visitor bot
        // hitting the same key still gets 429'd after the burst.
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 1, BurstSize = 1, OverLimitAction = "throttle-status",
        });
        var ctx = NewContext();

        await policy.ExecuteAsync(ctx, EvidenceWithSig("sig:a") with { PrimaryBotType = BotType.Scraper });
        var denied = await policy.ExecuteAsync(ctx, EvidenceWithSig("sig:a") with { PrimaryBotType = BotType.Scraper });

        Assert.False(denied.Continue);
    }

    /// <summary>
    ///     Stub policy that records each invocation so the OverLimitAction
    ///     routing test can assert hand-off without a real throttle.
    /// </summary>
    private sealed class RecordingFallback : IActionPolicy
    {
        public RecordingFallback(string name) => Name = name;
        public string Name { get; }
        public ActionType ActionType => ActionType.Throttle;
        public PolicyIntent Intent => PolicyIntent.Throttle;
        public int HitCount { get; private set; }
        public Task<ActionResult> ExecuteAsync(HttpContext ctx, AggregatedEvidence ev, CancellationToken ct = default)
        {
            HitCount++;
            return Task.FromResult(ActionResult.Blocked(429, "stub fallback"));
        }
    }
}
