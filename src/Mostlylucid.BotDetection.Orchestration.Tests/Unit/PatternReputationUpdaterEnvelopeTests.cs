using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Pins the closed-loop envelope on
///     <see cref="PatternReputationUpdater.ApplyEvidence"/> (audit #1+#3 +
///     <c>project_centroid_learning_feedback_loop</c>). Evidence writes off
///     stylobot-shaped requests (fromUpstream=false) or cold-start traffic
///     (warmup=true) must short-circuit so the prior isn't poisoned by our
///     own enforcement / under-sampled cold-start signal. Source tags
///     persist on the record so downstream consumers can differentiate
///     LLM-sourced evidence from deterministic FCrDNS / honeypot writes.
/// </summary>
public class PatternReputationUpdaterEnvelopeTests
{
    private readonly PatternReputationUpdater _updater;

    public PatternReputationUpdaterEnvelopeTests()
    {
        var optionsWrapper = Options.Create(new BotDetectionOptions());
        _updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance,
            optionsWrapper);
    }

    [Fact]
    public void ApplyEvidence_FromUpstreamFalse_NoOpsOnExistingPattern()
    {
        // Stylobot-shaped response (load-shed 503, policy block 403, etc.)
        // -- the gate refuses to fold this evidence into the prior. The
        // existing reputation passes through unchanged except LastSeen so
        // the decay sweep can still observe activity.
        var current = new PatternReputation
        {
            PatternId = "ua:x",
            PatternType = "UserAgent",
            Pattern = "TestUa",
            BotScore = 0.5,
            Support = 10,
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-30)
        };

        var result = _updater.ApplyEvidence(
            current,
            "ua:x",
            "UserAgent",
            "TestUa",
            label: 1.0,
            evidenceWeight: 1.0,
            fromUpstream: false);

        Assert.Equal(0.5, result.BotScore);
        Assert.Equal(10, result.Support);
        Assert.True(result.LastSeen > current.LastSeen);
    }

    [Fact]
    public void ApplyEvidence_FromUpstreamFalse_OnNullCurrent_ReturnsNeutralSeed()
    {
        // Stylobot-shaped first observation of a new pattern: we mint a
        // neutral seed so the caller's Update() call is safe but no
        // enforcement-shaped evidence lands on the prior. Score sits at
        // the configured Prior; Support is zero so promotion gates don't
        // fire.
        var result = _updater.ApplyEvidence(
            current: null,
            patternId: "ua:new",
            patternType: "UserAgent",
            pattern: "NewUa",
            label: 1.0,
            fromUpstream: false);

        Assert.Equal(0.5, result.BotScore);
        Assert.Equal(0.0, result.Support);
        Assert.Equal(ReputationState.Neutral, result.State);
    }

    [Fact]
    public void ApplyEvidence_WarmupTrue_NoOpsOnExistingPattern()
    {
        // Same gate, different axis: gateway is in cold-start warmup.
        // Behavioural classifiers are under-sampled so the verdict is
        // unreliable; refuse to write.
        var current = new PatternReputation
        {
            PatternId = "ua:y",
            PatternType = "UserAgent",
            Pattern = "TestUa",
            BotScore = 0.5,
            Support = 10,
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-30)
        };

        var result = _updater.ApplyEvidence(
            current,
            "ua:y",
            "UserAgent",
            "TestUa",
            label: 1.0,
            evidenceWeight: 1.0,
            warmup: true);

        Assert.Equal(0.5, result.BotScore);
        Assert.Equal(10, result.Support);
    }

    [Fact]
    public void ApplyEvidence_NaturalRequest_AppliesAsBefore()
    {
        // Regression guardrail. Default fromUpstream=true + warmup=false
        // must walk the EMA exactly as it did before the gate landed.
        var current = new PatternReputation
        {
            PatternId = "ua:z",
            PatternType = "UserAgent",
            Pattern = "TestUa",
            BotScore = 0.5,
            Support = 10,
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        var result = _updater.ApplyEvidence(
            current,
            "ua:z",
            "UserAgent",
            "TestUa",
            label: 1.0,
            evidenceWeight: 1.0);

        // EMA: (1-0.1)*0.5 + 0.1*1.0 = 0.55
        Assert.Equal(0.55, result.BotScore, 2);
        Assert.Equal(11, result.Support);
    }

    [Fact]
    public void ApplyEvidence_LlmSource_PersistsOnRecord()
    {
        // LLM writers tag source="llm" so downstream consumers (reputation
        // maintenance / decay sweep) can age stale LLM evidence faster
        // than deterministic FCrDNS / honeypot writes. The tag must
        // round-trip on the record.
        var result = _updater.ApplyEvidence(
            current: null,
            patternId: "ua:llm",
            patternType: "UserAgent",
            pattern: "LlmTagged",
            label: 1.0,
            source: "llm");

        Assert.Equal("llm", result.Source);
    }

    [Fact]
    public void ApplyEvidence_NullSourceOnUpdate_PreservesPreviousTag()
    {
        // The tag is preserved across calls: an LLM-tagged reputation stays
        // LLM-flagged after a later untagged write (honeypot / FCrDNS that
        // hasn't been migrated to thread its tag). Prevents the source
        // signal from being silently dropped on overlap.
        var llmTagged = _updater.ApplyEvidence(
            current: null,
            patternId: "ua:keep",
            patternType: "UserAgent",
            pattern: "KeepTag",
            label: 1.0,
            source: "llm");

        var second = _updater.ApplyEvidence(
            llmTagged,
            "ua:keep",
            "UserAgent",
            "KeepTag",
            label: 1.0,
            evidenceWeight: 1.0);

        Assert.Equal("llm", second.Source);
    }
}
