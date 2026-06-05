using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Risk;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Risk;

/// <summary>
///     Operator-visible scenarios the unified <see cref="SignatureRiskVerdictComposer"/>
///     has to honour before any Phase B / C site swaps land. These are the cases that
///     justify the refactor in the first place -- each one is a thing today's scattered
///     band-derivation gets wrong (or has to special-case in four different sites).
/// </summary>
public sealed class SignatureRiskVerdictComposerTests
{
    /// <summary>
    ///     Baseline inputs for a "fresh signature, no evidence yet" request -- a brand-new
    ///     visitor with no detection signals, no archetype anchor, no cluster membership.
    ///     Tests mutate from this so each scenario only changes the inputs it cares about.
    /// </summary>
    private static SignatureRiskInputs Base(
        double probability = 0.5,
        double confidence = 0.5,
        double rawThreat = 0.0)
        => new()
        {
            PrimarySignature = "sig-test",
            BotProbability = probability,
            Confidence = confidence,
            RawThreatScore = rawThreat,
            FriendlyVerified = false,
            ConfirmedBad = false,
            DeclaredBot = false,
        };

    // ============================================================================
    // Hostile-pin always wins (negative behaviour beats identity claims)
    // ============================================================================

    [Fact]
    public void ConfirmedBad_WithIpVerified_DefersToFriendlyPin()
    {
        // Live repro from sig BcRrawwUImTNmQxoKIrOEw on www.stylobot.net:
        // real Bingbot UA from a real Microsoft Azure IP (52.167.144), but the
        // fastpath reputation cache had latched ConfirmedBad on the bingbot UA
        // pattern after a residential-IP impersonator visited days earlier.
        // The UA-pattern cache key is too coarse to distinguish the real bot
        // from its impersonator; IP-range verification is authoritative for
        // transport-level identity and must beat the cache.
        var inputs = Base(probability: 1.0, confidence: 1.0) with
        {
            FriendlyIpVerified = true,      // vendor IP-range check passed
            BotType = "SearchEngine",
            BotName = "bingbot",
            IsFriendlyBotType = true,
            ConfirmedBad = true,            // UA-pattern cache latch from earlier spoofer
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.False(verdict.HostilePinFired);
        Assert.True(verdict.FriendlyPinFired);
        Assert.Equal(ThreatBand.None, verdict.ThreatBand);
        Assert.Contains(verdict.Reasons, r => r.Contains("hostile_pin_suppressed"));
    }

    [Fact]
    public void ConfirmedBad_WithBrowserAttestation_SuppressesHostilePin()
    {
        // Live repro from sig o9jGwItMMckqUIyrYxOMhA on staging.stylobot.net
        // (bug U/V): a real Chrome XHR request whose UA pattern had been latched
        // ConfirmedBad in the reputation cache by an earlier abuser. Per-request
        // signals (Sec-Fetch-Site attestation, 96% human heuristic, threat=none)
        // unambiguously identify this visit as human, but with no IP-vendor range
        // to verify against the carve-out at <see cref="FriendlyIpVerified"/> can
        // never fire. <see cref="BrowserAttestationVerified"/> is the transport-
        // layer analogue and must suppress the hostile pin -- otherwise the
        // composer pins RiskBand=VeryHigh + the ledger overrides PrimaryBotType
        // to MaliciousBot on the persisted row.
        var inputs = Base(probability: 0.06, confidence: 0.68) with
        {
            ConfirmedBad = true,
            BrowserAttestationVerified = true,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.False(verdict.HostilePinFired);
        Assert.NotEqual(RiskBand.VeryHigh, verdict.RiskBand);
        Assert.Contains(verdict.Reasons, r => r.Contains("hostile_pin_suppressed: browser_attestation"));
    }

    [Fact]
    public void HostilePin_FromRawThreat_NotSuppressedByBrowserAttestation()
    {
        // Carve-out is reputation-cache-specific. Raw threat score above the
        // hostile gate is per-request behavioural evidence -- a human running
        // SQLi scans from a browser still trips hostile even with Sec-Fetch
        // present. Behaviour wins for crisp negative signals.
        var inputs = Base(probability: 0.06, confidence: 0.68, rawThreat: 0.85) with
        {
            BrowserAttestationVerified = true,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.HostilePinFired);
        Assert.Equal(RiskBand.VeryHigh, verdict.RiskBand);
    }

    [Fact]
    public void HostilePin_FromConfirmedBad_OverridesFriendlyVerification()
    {
        // A verified Googlebot UA that has previously tripped a honeypot or reputation
        // abort is still hostile -- verification is identity, behaviour is the verdict.
        var inputs = Base(probability: 1.0, confidence: 1.0) with
        {
            FriendlyVerified = true,        // legacy convenience latch (no IP check)
            DeclaredBot = true,
            IsFriendlyBotType = true,
            BotType = "Crawler",
            BotName = "Googlebot",
            ConfirmedBad = true,            // hostile-pin wins
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.HostilePinFired);
        Assert.False(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.VeryHigh, verdict.RiskBand);
        Assert.Equal(ThreatBand.Critical, verdict.ThreatBand);
        Assert.Equal(RiskProfileLabel.Hostile, verdict.RiskProfile);
        Assert.Contains("Confirmed bad", verdict.Justification);
    }

    [Fact]
    public void HostilePin_FromRawThreatScore_OverridesSafeCluster()
    {
        // A Safe-cluster member whose own request just tripped a high-confidence
        // threat signal is hostile. Cluster membership is community evidence, raw
        // threat is the per-request behavioural verdict.
        var inputs = Base(rawThreat: 0.95) with
        {
            ClusterType = BotClusterType.Safe,    // would normally clamp friendly
            ClusterLabel = "Fediverse-Fanout (Mastodon)",
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.HostilePinFired);
        Assert.False(verdict.FriendlyPinFired);
        Assert.Equal(ThreatBand.Critical, verdict.ThreatBand);
        Assert.Equal(RiskProfileLabel.Hostile, verdict.RiskProfile);
    }

    [Fact]
    public void HostilePin_FromHostileCluster_FiresWhenAvgThreatHigh()
    {
        // BotNetwork cluster with avg threat above the gate -- coordinated campaign
        // shouldn't get the benefit of the doubt.
        var inputs = Base() with
        {
            ClusterType = BotClusterType.BotNetwork,
            ClusterAverageThreatScore = 0.7,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.HostilePinFired);
        Assert.Equal(RiskBand.VeryHigh, verdict.RiskBand);
    }

    [Fact]
    public void HostilePin_BotNetworkBelowGate_DoesNotFire()
    {
        // BotNetwork is a structural classification (temporal correlation + moderate
        // similarity); it doesn't imply hostile by itself. Only flips hostile when
        // the cluster's average threat clears the configured gate.
        var inputs = Base() with
        {
            ClusterType = BotClusterType.BotNetwork,
            ClusterAverageThreatScore = 0.3,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.False(verdict.HostilePinFired);
    }

    // ============================================================================
    // Friendly-pin clamps when no hostile signal present
    // ============================================================================

    [Fact]
    public void FriendlyPin_FromFriendlyVerified_ClampsBothAxes()
    {
        // NodeInfo confirmed Mastodon, vendor-IP confirmed Googlebot, etc.
        // High bot probability is correct (it IS a bot) but threat and risk both
        // clamp because we know who it is.
        var inputs = Base(probability: 1.0, confidence: 1.0) with
        {
            FriendlyVerified = true,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.Low, verdict.RiskBand);
        Assert.Equal(ThreatBand.None, verdict.ThreatBand);
        Assert.Equal(RiskProfileLabel.VerifiedCommunity, verdict.RiskProfile);
    }

    [Fact]
    public void FriendlyPin_FromSafeCluster_ClampsBothAxes()
    {
        // The Mastodon-fanout case that motivated the whole refactor. The cluster
        // recognition (added in 644b492c) lands the member in Safe; the verdict
        // composer reads that and clamps the per-row threat band.
        var inputs = Base(probability: 1.0, confidence: 0.5) with
        {
            ClusterType = BotClusterType.Safe,
            ClusterLabel = "Fediverse-Fanout (Mastodon)",
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.Low, verdict.RiskBand);
        Assert.Equal(ThreatBand.None, verdict.ThreatBand);
        Assert.Equal(RiskProfileLabel.VerifiedCommunity, verdict.RiskProfile);
        Assert.Contains("Safe cluster", verdict.Justification);
    }

    [Fact]
    public void FriendlyPin_FromDeclaredBotAndFriendlyType_Clamps()
    {
        // UA self-declared as Googlebot, classified as Crawler. Even without
        // vendor-IP verification this is the historical friendly-pin path the
        // current DetermineRiskBand uses.
        var inputs = Base(probability: 1.0, confidence: 1.0) with
        {
            DeclaredBot = true,
            IsFriendlyBotType = true,
            BotType = "Crawler",
            BotName = "Googlebot",
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.Low, verdict.RiskBand);
    }

    [Fact]
    public void FriendlyPin_FromAlignedFriendlyArchetype_Clamps()
    {
        // The matcher anchored this signature at the Mastodon archetype; the current
        // centroid is still close to it (cosine drift below the revoke threshold).
        // Friendly pin holds.
        var inputs = Base(probability: 1.0, confidence: 0.7) with
        {
            IsFriendlyArchetype = true,
            OriginArchetypeId = "mastodon-previewer",
            CurrentArchetypeId = "mastodon-previewer",
            OriginArchetypeScore = 0.91,
            CurrentArchetypeScore = 0.88,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.Low, verdict.RiskBand);
    }

    // ============================================================================
    // Drift revokes the friendly-archetype pin
    // ============================================================================

    [Fact]
    public void FriendlyArchetype_DriftedFar_RevokesPin()
    {
        // Mastodon UA at allocation -- but the centroid has drifted into generic-
        // scraper territory (cosine delta > 0.20). This is the "spoofed UA, behave
        // like a scraper" attack surface; the friendly pin must revoke.
        var inputs = Base(probability: 0.85, confidence: 0.8) with
        {
            IsFriendlyArchetype = true,
            OriginArchetypeId = "mastodon-previewer",
            CurrentArchetypeId = "generic-scraper",
            OriginArchetypeScore = 0.91,
            CurrentArchetypeScore = 0.62,   // delta 0.29 -- exceeds 0.20 revoke threshold
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.False(verdict.FriendlyPinFired);
        Assert.Contains("friendly_pin_revoked", string.Join('|', verdict.Reasons));
    }

    [Fact]
    public void FriendlyArchetype_DriftWithinThreshold_PinHolds()
    {
        // Origin 0.91, current 0.78 -> delta 0.13, below the 0.20 revoke threshold.
        // Still aligned -- pin holds. Inputs match a healthy verified friendly bot
        // whose behaviour is slowly accumulating idiosyncrasy.
        var inputs = Base(probability: 0.9, confidence: 0.9) with
        {
            IsFriendlyArchetype = true,
            OriginArchetypeId = "mastodon-previewer",
            CurrentArchetypeId = "mastodon-previewer",
            OriginArchetypeScore = 0.91,
            CurrentArchetypeScore = 0.78,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.FriendlyPinFired);
    }

    // ============================================================================
    // No verification + no friendly cluster + no friendly archetype = standard
    // ============================================================================

    [Fact]
    public void NoFriendlyInputs_FallsThroughToProbabilityBucket()
    {
        // The default detector pipeline result for an unknown automated visitor.
        // No verification, no cluster, no archetype. Probability + confidence
        // determine the band.
        var inputs = Base(probability: 0.9, confidence: 0.9);

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.False(verdict.FriendlyPinFired);
        Assert.False(verdict.HostilePinFired);
        Assert.Equal(RiskBand.VeryHigh, verdict.RiskBand);
        Assert.Equal(RiskProfileLabel.Unknown, verdict.RiskProfile);
    }

    // ============================================================================
    // Risk profile label cases
    // ============================================================================

    [Fact]
    public void RiskProfile_Drifting_WhenOriginAndCurrentDiffer()
    {
        var inputs = Base(probability: 0.7) with
        {
            OriginArchetypeId = "chrome-desktop",
            CurrentArchetypeId = "generic-scraper",
            OriginArchetypeScore = 0.85,
            CurrentArchetypeScore = 0.55,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.Equal(RiskProfileLabel.Drifting, verdict.RiskProfile);
    }

    [Fact]
    public void RiskProfile_Aligned_WhenOriginEqualsCurrent()
    {
        var inputs = Base() with
        {
            OriginArchetypeId = "chrome-desktop",
            CurrentArchetypeId = "chrome-desktop",
            OriginArchetypeScore = 0.85,
            CurrentArchetypeScore = 0.83,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.Equal(RiskProfileLabel.Aligned, verdict.RiskProfile);
    }

    // ============================================================================
    // Bucket purity (consistency across read sites once Phase C lands)
    // ============================================================================

    [Theory]
    [InlineData(0.0,  ThreatBand.None)]
    [InlineData(0.14, ThreatBand.None)]
    [InlineData(0.15, ThreatBand.Low)]
    [InlineData(0.34, ThreatBand.Low)]
    [InlineData(0.35, ThreatBand.Elevated)]
    [InlineData(0.54, ThreatBand.Elevated)]
    [InlineData(0.55, ThreatBand.High)]
    [InlineData(0.79, ThreatBand.High)]
    [InlineData(0.80, ThreatBand.Critical)]
    [InlineData(1.0,  ThreatBand.Critical)]
    public void BucketThreat_BoundariesAreStable(double rawScore, ThreatBand expected)
    {
        Assert.Equal(expected, SignatureRiskVerdictComposer.BucketThreat(rawScore));
    }

    [Theory]
    // At confidence=1.0 scaled == probability, so each prob hits the bucket directly.
    [InlineData(0.0,  1.0, RiskBand.VeryLow)]
    [InlineData(0.19, 1.0, RiskBand.VeryLow)]
    [InlineData(0.20, 1.0, RiskBand.Low)]
    [InlineData(0.24, 1.0, RiskBand.Low)]
    [InlineData(0.25, 1.0, RiskBand.Elevated)]
    [InlineData(0.44, 1.0, RiskBand.Elevated)]
    [InlineData(0.45, 1.0, RiskBand.Medium)]
    [InlineData(0.59, 1.0, RiskBand.Medium)]
    [InlineData(0.60, 1.0, RiskBand.High)]
    [InlineData(0.79, 1.0, RiskBand.High)]
    [InlineData(0.80, 1.0, RiskBand.VeryHigh)]
    [InlineData(1.0,  1.0, RiskBand.VeryHigh)]
    public void BucketRisk_AtFullConfidence_FollowsProbabilityCurve(
        double probability, double confidence, RiskBand expected)
    {
        Assert.Equal(expected, SignatureRiskVerdictComposer.BucketRisk(probability, confidence));
    }

    [Fact]
    public void BucketRisk_LowConfidence_DampensTheBand()
    {
        // 0.85 probability at 1.0 confidence: scaled == 0.85 >= 0.80 -> VeryHigh.
        // Same probability at 0.0 confidence: scaled = 0.85 * 0.5 = 0.425 -> Elevated.
        // The dampening is the whole point of pairing probability with confidence
        // instead of treating them as orthogonal axes downstream.
        var highConfidence = SignatureRiskVerdictComposer.BucketRisk(0.85, 1.0);
        var lowConfidence  = SignatureRiskVerdictComposer.BucketRisk(0.85, 0.0);

        Assert.Equal(RiskBand.VeryHigh, highConfidence);
        Assert.Equal(RiskBand.Elevated, lowConfidence);
        Assert.True(lowConfidence < highConfidence,
            $"Low confidence should produce a lower (less severe) band; got high={highConfidence}, low={lowConfidence}");
    }

    // ============================================================================
    // Reasons[] trace -- audit panel feed
    // ============================================================================

    [Fact]
    public void Reasons_RecordsTheGateThatFired()
    {
        var inputs = Base() with
        {
            ClusterType = BotClusterType.Safe,
            ClusterLabel = "Fediverse-Fanout (Mastodon)",
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.Contains(verdict.Reasons,
            r => r.Contains("friendly_pin") && r.Contains("Safe"));
    }

    [Fact]
    public void Reasons_HostileWinsRecordedFirst()
    {
        var inputs = Base() with
        {
            FriendlyVerified = true,
            ConfirmedBad = true,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.Contains(verdict.Reasons, r => r.Contains("hostile_pin"));
        Assert.DoesNotContain(verdict.Reasons, r => r.Contains("friendly_pin:"));
    }
}