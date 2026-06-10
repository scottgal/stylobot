using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Pins the sticky-deny escalation contract: N violations in T seconds
///     promote a key to a hard-deny for the configured block-TTL, after
///     which the counter resets. Mirrors what Cloudflare and Akamai
///     "ban-on-Nth-violation" rules ship; the missing escalation layer the
///     audit identified.
///
///     Sticky-deny plugs in as the <c>OverLimitAction</c> of
///     <see cref="RateLimitActionPolicy"/>: each bucket-overflow is one
///     violation, the policy delegates back to a fast-429 inner action
///     until the threshold is crossed, then takes over with a 403 and an
///     opaque cookie marker.
/// </summary>
public class StickyDenyActionPolicyTests
{
    [Fact]
    public async Task BelowThreshold_DelegatesToInnerAction()
    {
        var inner = new RecordingPolicy("throttle-status");
        var registry = BuildRegistry(inner);
        var tracker = new FakeTracker();
        var policy = new StickyDenyActionPolicy("sticky-deny", new StickyDenyActionOptions
        {
            ViolationThreshold = 3,
            UnderThresholdAction = "throttle-status",
        }, tracker, registry, () => DateTimeOffset.UtcNow);

        var result = await policy.ExecuteAsync(NewContext("203.0.113.5"), EvidenceWithSig("sig:a"));

        Assert.True(result.Continue || result.StatusCode != 403,
            "below threshold, the under-threshold action handles the request");
        Assert.Equal(1, inner.HitCount);
    }

    [Fact]
    public async Task AtThreshold_BlocksWithStickyDeny()
    {
        var inner = new RecordingPolicy("throttle-status");
        var registry = BuildRegistry(inner);
        var tracker = new FakeTracker { BlockOnNthCall = 3 };
        var policy = new StickyDenyActionPolicy("sticky-deny", new StickyDenyActionOptions
        {
            ViolationThreshold = 3,
            UnderThresholdAction = "throttle-status",
        }, tracker, registry, () => DateTimeOffset.UtcNow);

        await policy.ExecuteAsync(NewContext("203.0.113.5"), EvidenceWithSig("sig:a"));
        await policy.ExecuteAsync(NewContext("203.0.113.5"), EvidenceWithSig("sig:a"));
        var third = await policy.ExecuteAsync(NewContext("203.0.113.5"), EvidenceWithSig("sig:a"));

        Assert.False(third.Continue, "the threshold-crossing call must short-circuit");
        Assert.Equal(403, third.StatusCode);
        Assert.Equal(2, inner.HitCount); // only the first two delegated
    }

    [Fact]
    public async Task WhileBlocked_DeniesWithoutDelegating()
    {
        // After the block is in effect, subsequent requests must short-circuit
        // immediately without delegating to the under-threshold action AND
        // without incrementing the violation counter (the bot would otherwise
        // keep the block extended forever just by retrying).
        var inner = new RecordingPolicy("throttle-status");
        var registry = BuildRegistry(inner);
        var tracker = new FakeTracker { IsBlockedResult = true };
        var policy = new StickyDenyActionPolicy("sticky-deny", new StickyDenyActionOptions
        {
            ViolationThreshold = 3,
            UnderThresholdAction = "throttle-status",
        }, tracker, registry, () => DateTimeOffset.UtcNow);

        var result = await policy.ExecuteAsync(NewContext("203.0.113.5"), EvidenceWithSig("sig:a"));

        Assert.False(result.Continue);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal(0, inner.HitCount);
        Assert.Equal(0, tracker.RecordViolationCalls); // no count bump while blocked
    }

    [Fact]
    public async Task Blocked_SetsOpaqueCookie()
    {
        // Operators want a fast way to debug "is this user actually blocked?"
        // without exposing the violation count or any identifier. A short
        // opaque cookie indicates membership; the value need not be
        // verifiable (the server is the source of truth).
        var inner = new RecordingPolicy("throttle-status");
        var registry = BuildRegistry(inner);
        var tracker = new FakeTracker { IsBlockedResult = true };
        var policy = new StickyDenyActionPolicy("sticky-deny", new StickyDenyActionOptions
        {
            ViolationThreshold = 3,
            UnderThresholdAction = "throttle-status",
            SetCookie = true,
        }, tracker, registry, () => DateTimeOffset.UtcNow);

        var ctx = NewContext("203.0.113.5");
        await policy.ExecuteAsync(ctx, EvidenceWithSig("sig:a"));

        Assert.True(ctx.Response.Headers.ContainsKey("Set-Cookie"),
            "policy must set a marker cookie when blocking");
        var cookie = ctx.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains("sb_blocked=", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoKey_FailsOpen_DelegatesToInner()
    {
        // When the rate-limit key can't be resolved (no signature, no IP,
        // no ASN depending on KeyBy) the policy must NOT route every
        // visitor through one global violation counter. Fail open.
        var inner = new RecordingPolicy("throttle-status");
        var registry = BuildRegistry(inner);
        var tracker = new FakeTracker();
        var policy = new StickyDenyActionPolicy("sticky-deny", new StickyDenyActionOptions
        {
            ViolationThreshold = 3,
            UnderThresholdAction = "throttle-status",
            KeyBy = RateLimitKey.Asn,
        }, tracker, registry, () => DateTimeOffset.UtcNow);

        // No IpAsn signal, no IP set: key is null.
        var ctx = new DefaultHttpContext();
        var result = await policy.ExecuteAsync(ctx, new AggregatedEvidence
        {
            BotProbability = 0.5,
            Confidence = 0.5,
            RiskBand = RiskBand.Medium,
            Signals = new Dictionary<string, object>(),
        });

        Assert.Equal(1, inner.HitCount);
        Assert.Equal(0, tracker.RecordViolationCalls);
        Assert.Equal(0, tracker.IsBlockedCalls);
    }

    // === InMemoryStickyDenyTracker contract ================================

    [Fact]
    public void Tracker_ViolationsInsideWindow_AccumulateAndTripBlock()
    {
        var opts = new StickyDenyActionOptions
        {
            ViolationThreshold = 3,
            WindowSeconds = 60,
            BlockTtlSeconds = 300,
        };
        var t = new InMemoryStickyDenyTracker(opts);
        var now = DateTimeOffset.UnixEpoch;

        Assert.False(t.RecordViolation("k1", now));
        Assert.False(t.RecordViolation("k1", now.AddSeconds(10)));
        Assert.True(t.RecordViolation("k1", now.AddSeconds(20)),
            "3rd violation inside the 60s window must trip the block");

        Assert.True(t.IsBlocked("k1", now.AddSeconds(21)));
    }

    [Fact]
    public void Tracker_ViolationsOutsideWindow_ResetCounter()
    {
        var opts = new StickyDenyActionOptions
        {
            ViolationThreshold = 3,
            WindowSeconds = 60,
            BlockTtlSeconds = 300,
        };
        var t = new InMemoryStickyDenyTracker(opts);
        var now = DateTimeOffset.UnixEpoch;

        t.RecordViolation("k1", now);
        t.RecordViolation("k1", now.AddSeconds(10));

        // 61s later: outside the window. Counter should reset to 1 on this
        // violation, NOT trip the block (which would have happened at the
        // 3rd inside-window violation).
        Assert.False(t.RecordViolation("k1", now.AddSeconds(71)),
            "violation outside the window must not push the count to 3");
        Assert.False(t.IsBlocked("k1", now.AddSeconds(72)));
    }

    [Fact]
    public void Tracker_BlockExpires_AfterTtl()
    {
        var opts = new StickyDenyActionOptions
        {
            ViolationThreshold = 1,  // trip on first violation for test brevity
            WindowSeconds = 60,
            BlockTtlSeconds = 30,
        };
        var t = new InMemoryStickyDenyTracker(opts);
        var now = DateTimeOffset.UnixEpoch;

        Assert.True(t.RecordViolation("k1", now), "threshold 1 trips on first");
        Assert.True(t.IsBlocked("k1", now.AddSeconds(15)));
        Assert.False(t.IsBlocked("k1", now.AddSeconds(31)),
            "block must expire after BlockTtlSeconds");
    }

    // === Helpers ===========================================================

    private static ActionPolicyRegistry BuildRegistry(IActionPolicy inner) =>
        new(Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>(),
            new[] { inner });

    private static HttpContext NewContext(string remoteIp)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        ctx.Request.Path = "/";
        return ctx;
    }

    private static AggregatedEvidence EvidenceWithSig(string sig) => new()
    {
        BotProbability = 0.5,
        Confidence = 0.8,
        RiskBand = RiskBand.Medium,
        Signals = new Dictionary<string, object> { [SignalKeys.PrimarySignature] = sig },
    };

    /// <summary>
    ///     Test double for <see cref="IStickyDenyTracker"/>. Records call
    ///     counts and lets each test set deterministic outcomes for
    ///     <see cref="IsBlocked"/> / <see cref="RecordViolation"/>.
    /// </summary>
    private sealed class FakeTracker : IStickyDenyTracker
    {
        public int IsBlockedCalls { get; private set; }
        public int RecordViolationCalls { get; private set; }
        public bool IsBlockedResult { get; set; }

        /// <summary>
        ///     Make the Nth call to <see cref="RecordViolation"/> return true
        ///     (i.e. the violation that pushes the key into blocked).
        /// </summary>
        public int? BlockOnNthCall { get; set; }

        public bool IsBlocked(string key, DateTimeOffset now)
        {
            IsBlockedCalls++;
            return IsBlockedResult;
        }

        public bool RecordViolation(string key, DateTimeOffset now)
        {
            RecordViolationCalls++;
            return BlockOnNthCall.HasValue && RecordViolationCalls == BlockOnNthCall.Value;
        }
    }

    /// <summary>
    ///     Inner-action stub. Records each delegation and short-circuits
    ///     with a 429 (so the test can tell delegated calls from
    ///     pass-through ones).
    /// </summary>
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
            return Task.FromResult(ActionResult.Blocked(429, "stub under-threshold"));
        }
    }
}
