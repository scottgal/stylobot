using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Cross-policy wiring tests for the Phase 1+2 action-surface work. The
///     per-policy unit tests pin each policy in isolation; this file pins
///     the BOUNDARIES between them -- the places where one policy talks
///     to another (rate-limit -> sticky-deny via OverLimitAction), the
///     registry lookup, and the duplicated key-resolution logic in
///     <see cref="StickyDenyActionPolicy.ResolveKey"/>.
/// </summary>
public class ActionSurfaceWiringTests
{
    // === Registry built-ins ================================================

    [Fact]
    public void Registry_SilentDrop_IsRegisteredAsBuiltIn()
    {
        // Operators reference 'silent-drop' from config; the contract is
        // that the registry resolves it by name out of the box.
        var registry = NewRegistry();

        var policy = registry.GetPolicy("silent-drop");

        Assert.NotNull(policy);
        Assert.IsType<SilentDropActionPolicy>(policy);
        Assert.Equal(ActionType.Block, policy.ActionType);
    }

    [Fact]
    public void Registry_StickyDeny_IsRegisteredAsBuiltIn()
    {
        var registry = NewRegistry();

        var policy = registry.GetPolicy("sticky-deny");

        Assert.NotNull(policy);
        Assert.IsType<StickyDenyActionPolicy>(policy);
        Assert.Equal(PolicyIntent.Block, policy.Intent);
    }

    [Fact]
    public void Registry_PreviouslyShippedBuiltIns_StillRegistered()
    {
        // Defensive: the new constructor parameter for IStickyDenyTracker
        // mustn't have broken the existing built-in registrations.
        var registry = NewRegistry();

        foreach (var name in new[]
        {
            "block", "block-hard", "block-soft",
            "throttle", "throttle-stealth", "throttle-tools", "throttle-status",
            "challenge", "logonly",
            "rate-limit-search", "rate-limit-ai", "rate-limit-social", "rate-limit-monitoring",
        })
        {
            Assert.NotNull(registry.GetPolicy(name));
        }
    }

    // === RateLimit -> StickyDeny composition (the real use case) ===========

    [Fact]
    public async Task RateLimitOverflow_RoutesTo_StickyDeny_OverLimitAction()
    {
        // The headline composition: configure a rate-limit policy with
        // OverLimitAction = "sticky-deny". When the bucket overflows, the
        // overflow request must hit sticky-deny (which delegates to its
        // own under-threshold inner action -- recording stub here).
        var underThreshold = new RecordingPolicy("recording-fallback");
        var stickyOpts = new StickyDenyActionOptions
        {
            ViolationThreshold = 100, // high, so the first overflow stays under
            UnderThresholdAction = underThreshold.Name,
            KeyBy = RateLimitKey.Ip,
        };
        var tracker = new InMemoryStickyDenyTracker(stickyOpts);
        var sticky = new StickyDenyActionPolicy("sticky-deny-test", stickyOpts, tracker, NewRegistry(underThreshold));

        var registry = NewRegistry(underThreshold, sticky);
        var rateLimit = new RateLimitActionPolicy("rl",
            new RateLimitActionOptions
            {
                RequestsPerMinute = 60,
                BurstSize = 1,
                OverLimitAction = "sticky-deny-test",
                KeyBy = RateLimitKey.Ip,
            },
            new InMemoryTokenBucketStore(),
            registry);

        var ctx = NewContext("203.0.113.5");
        // First request: passes the bucket, delegates downstream (Continue=true).
        await rateLimit.ExecuteAsync(ctx, EmptyEvidence());
        // Second request: bucket empty -> OverLimitAction = sticky-deny.
        // Under threshold -> sticky-deny delegates to recording-fallback.
        await rateLimit.ExecuteAsync(ctx, EmptyEvidence());

        Assert.Equal(1, underThreshold.HitCount);
    }

    [Fact]
    public async Task StickyDeny_AtThreshold_FromRateLimitOverflow_Returns403()
    {
        // The other half: drive the same composition past the violation
        // threshold and confirm the sticky-deny phase fires and short-
        // circuits with 403.
        var underThreshold = new RecordingPolicy("recording-fallback");
        var stickyOpts = new StickyDenyActionOptions
        {
            ViolationThreshold = 3,
            WindowSeconds = 60,
            BlockTtlSeconds = 300,
            UnderThresholdAction = underThreshold.Name,
            KeyBy = RateLimitKey.Ip,
            SetCookie = false, // cookie randomness makes assertions fragile
        };
        var tracker = new InMemoryStickyDenyTracker(stickyOpts);
        var sticky = new StickyDenyActionPolicy("sticky-deny-test", stickyOpts, tracker, NewRegistry(underThreshold));

        var registry = NewRegistry(underThreshold, sticky);
        var rateLimit = new RateLimitActionPolicy("rl",
            new RateLimitActionOptions
            {
                RequestsPerMinute = 60,
                BurstSize = 1,
                OverLimitAction = "sticky-deny-test",
                KeyBy = RateLimitKey.Ip,
            },
            new InMemoryTokenBucketStore(),
            registry);

        // Burn the burst (request 1 = pass, requests 2/3/4 = violations).
        for (var i = 0; i < 4; i++)
            await rateLimit.ExecuteAsync(NewContext("203.0.113.5"), EmptyEvidence());

        // Request 5: bucket still empty (no time advance) AND sticky-deny is
        // now blocked because we accumulated 3 violations.
        var ctx = NewContext("203.0.113.5");
        var result = await rateLimit.ExecuteAsync(ctx, EmptyEvidence());

        Assert.False(result.Continue);
        Assert.Equal(403, result.StatusCode);
    }

    // === StickyDeny key resolver duplication ===============================
    //
    // StickyDenyActionPolicy.ResolveKey duplicates the RateLimit key derivation
    // (Subnet / Asn / AsnAndSignature) so the policy can be used standalone.
    // Pin that duplication so a future refactor of one side doesn't silently
    // diverge from the other.

    [Theory]
    [InlineData(RateLimitKey.Ip, "203.0.113.5", null, null, "203.0.113.5")]
    [InlineData(RateLimitKey.Subnet, "203.0.113.5", null, null, "203.0.113.0/24")]
    [InlineData(RateLimitKey.Subnet, "2001:db8:1::99", null, null, "2001:db8:1::/48")]
    [InlineData(RateLimitKey.Signature, "203.0.113.5", null, "sig:a", "sig:a")]
    [InlineData(RateLimitKey.Asn, "203.0.113.5", 12345, null, "12345")]
    [InlineData(RateLimitKey.AsnAndSignature, "203.0.113.5", 12345, "sig:a", "AS12345:sig:a")]
    public void StickyDeny_ResolveKey_HonoursAllModes(
        RateLimitKey mode, string ip, int? asn, string? sig, string expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        var signals = new Dictionary<string, object>();
        if (asn.HasValue) signals[SignalKeys.IpAsn] = asn.Value;
        if (sig is not null) signals[SignalKeys.PrimarySignature] = sig;

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.5,
            Confidence = 0.5,
            RiskBand = RiskBand.Medium,
            Signals = signals,
        };

        Assert.Equal(expected, StickyDenyActionPolicy.ResolveKey(ctx, evidence, mode));
    }

    [Theory]
    [InlineData(RateLimitKey.Asn, "203.0.113.5", null, null)]                  // no ASN -> null
    [InlineData(RateLimitKey.AsnAndSignature, "203.0.113.5", 12345, null)]     // ASN but no sig -> null
    [InlineData(RateLimitKey.AsnAndSignature, "203.0.113.5", null, "sig:a")]   // sig but no ASN -> null
    public void StickyDeny_ResolveKey_FailsOpenWhenComponentMissing(
        RateLimitKey mode, string ip, int? asn, string? sig)
    {
        // Fail-open contract: if a component is missing, ResolveKey
        // returns null so the policy delegates to the under-threshold
        // action instead of routing every visitor through one global
        // counter. This was the same contract the rate-limit policy
        // already had; the duplication must preserve it.
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        var signals = new Dictionary<string, object>();
        if (asn.HasValue) signals[SignalKeys.IpAsn] = asn.Value;
        if (sig is not null) signals[SignalKeys.PrimarySignature] = sig;

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.5,
            Confidence = 0.5,
            RiskBand = RiskBand.Medium,
            Signals = signals,
        };

        Assert.Null(StickyDenyActionPolicy.ResolveKey(ctx, evidence, mode));
    }

    // === Helpers ===========================================================

    private static ActionPolicyRegistry NewRegistry(params IActionPolicy[] extra) =>
        new(Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>(),
            extra,
            NullLogger<ActionPolicyRegistry>.Instance);

    private static HttpContext NewContext(string ip)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Request.Path = "/";
        return ctx;
    }

    private static AggregatedEvidence EmptyEvidence() => new()
    {
        BotProbability = 0.5,
        Confidence = 0.8,
        RiskBand = RiskBand.Medium,
        Signals = new Dictionary<string, object>(),
    };

    private sealed class RecordingPolicy : IActionPolicy
    {
        public RecordingPolicy(string name) => Name = name;
        public string Name { get; }
        public ActionType ActionType => ActionType.Throttle;
        public PolicyIntent Intent => PolicyIntent.Throttle;
        public int HitCount { get; private set; }
        public Task<ActionResult> ExecuteAsync(HttpContext c, AggregatedEvidence e, CancellationToken ct = default)
        {
            HitCount++;
            return Task.FromResult(ActionResult.Blocked(429, "recording stub"));
        }
    }
}
