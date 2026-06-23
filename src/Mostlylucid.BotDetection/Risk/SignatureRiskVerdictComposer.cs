using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Risk;

/// <summary>
///     Composes a <see cref="SignatureRiskVerdict"/> from <see cref="SignatureRiskInputs"/>
///     using ordered gating (hostile-pin -> friendly-pin -> raw score buckets). This is the
///     single site that maps cross-request behavioural inferences (cluster type, archetype
///     anchor, drift, verification latches) onto the four operator-facing axes.
///     <para>
///     <b>Gating order matters.</b> Hostile-pin is checked FIRST so a verified-friendly UA
///     that just tripped a honeypot still lands hostile -- verification is identity, not
///     behaviour, and behaviour wins for negative signals. Friendly-pin runs only when the
///     hostile pin missed.
///     </para>
///     <para>
///     Pure, stateless, deterministic. Easy to unit-test for parity against the current
///     scattered band-derivation sites. Future axes (Risk Profile justification, drift-
///     as-risk-signal) become field additions to <see cref="SignatureRiskInputs"/> +
///     branches inside <see cref="Compose"/>, not new gating sites.
///     </para>
/// </summary>
public static class SignatureRiskVerdictComposer
{
    /// <summary>
    ///     Cosine-delta threshold past which a friendly archetype anchor revokes its own
    ///     friendly pin. A signature stamped "Mastodon at allocation" but whose current
    ///     centroid is &gt; <c>friendlyArchetypeDriftRevokeThreshold</c>
    ///     further from the origin archetype than from the current-nearest is treated as
    ///     drifted and loses its friendly-archetype pin. Closes the "spoofed-UA-then-
    ///     behave-like-a-scraper" attack surface.
    /// </summary>
    public const double DefaultFriendlyArchetypeDriftRevokeThreshold = 0.20;

    /// <summary>Raw threat-score above this clamps Threat to Critical.</summary>
    public const double DefaultThreatHostileThreshold = 0.60;

    /// <summary>Average cluster threat score above this makes BotNetwork clusters hostile.</summary>
    public const double DefaultClusterTypeHostileThreatGate = 0.60;

    public sealed record Settings
    {
        public double FriendlyArchetypeDriftRevokeThreshold { get; init; } = DefaultFriendlyArchetypeDriftRevokeThreshold;
        public double ThreatHostileThreshold { get; init; } = DefaultThreatHostileThreshold;
        public double ClusterTypeHostileThreatGate { get; init; } = DefaultClusterTypeHostileThreatGate;
    }

    public static SignatureRiskVerdict Compose(
        SignatureRiskInputs inputs,
        Settings? settings = null)
    {
        var s = settings ?? new Settings();
        var reasons = new List<string>();

        // === Hostile-pin (runs first; behaviour wins over identity for negative signals) ===
        var hostilePin = false;
        string? hostileWhy = null;

        // ConfirmedBad gate has two transport-layer carve-outs because the UA-
        // pattern reputation cache key is too coarse to identify a specific actor:
        //   1. FriendlyIpVerified -- vendor-IP corroboration for declared good
        //      bots (Googlebot, Bingbot, Applebot). Closes the case where an
        //      earlier spoofer from a residential IP latched the WHOLE bingbot
        //      UA pattern bad.
        //   2. BrowserAttestationVerified -- per-request Sec-Fetch-Site
        //      attestation present + raw threat score below the carve-out
        //      ceiling (gates on RiskVerdictOptions). Real browsers can never
        //      satisfy carve-out 1 because they have no published-CIDR identity
        //      to verify against; this is their transport-layer analogue.
        //
        // The BrowserAttestationVerified gate is computed from DIRECTLY-
        // OBSERVABLE request signals (Sec-Fetch header + threat score). It MUST
        // NOT consult bot probability or confidence -- those are the rolled-up
        // axes the carve-out is supposed to influence; coupling them would tie
        // risk to probability, which is exactly the parallel-axis bug the
        // unified verdict was designed to kill.
        //
        // Honeypot hits and high RawThreatScore are per-request behavioural
        // signals and are NOT suppressed by either carve-out -- behaviour wins
        // for negative signals.
        var ipVerifiedFriendly = inputs.FriendlyIpVerified == true;
        var browserAttestationVerified = inputs.BrowserAttestationVerified;
        if (inputs.ConfirmedBad && !ipVerifiedFriendly && !browserAttestationVerified)
        {
            hostilePin = true;
            hostileWhy = "Confirmed bad: reputation abort or honeypot hit on this signature";
            reasons.Add("hostile_pin: confirmed_bad latch true");
        }
        else if (inputs.ConfirmedBad && ipVerifiedFriendly)
        {
            reasons.Add("hostile_pin_suppressed: ip_verified beats reputation cache");
        }
        else if (inputs.ConfirmedBad && browserAttestationVerified)
        {
            reasons.Add("hostile_pin_suppressed: browser_attestation outweighs reputation latch (per-request transport evidence)");
        }
        else if (inputs.RawThreatScore >= s.ThreatHostileThreshold)
        {
            hostilePin = true;
            hostileWhy = $"Raw threat score {inputs.RawThreatScore:F2} at or above hostile gate {s.ThreatHostileThreshold:F2}";
            reasons.Add($"hostile_pin: raw_threat_score {inputs.RawThreatScore:F2} >= {s.ThreatHostileThreshold:F2}");
        }
        else if (inputs.ClusterType == BotClusterType.BotNetwork
                 && (inputs.ClusterAverageThreatScore ?? 0) >= s.ClusterTypeHostileThreatGate)
        {
            hostilePin = true;
            hostileWhy = $"Member of hostile BotNetwork cluster {inputs.ClusterLabel ?? inputs.ClusterId} (avg threat {inputs.ClusterAverageThreatScore:F2})";
            reasons.Add($"hostile_pin: cluster_type=BotNetwork avg_threat={inputs.ClusterAverageThreatScore:F2}");
        }

        // === Friendly-pin (runs only if hostile-pin missed) ===
        var friendlyPin = false;
        string? friendlyWhy = null;

        if (!hostilePin)
        {
            // Drift-aware archetype check: friendly-archetype pin only counts when the
            // current behaviour still looks like the friendly anchor it was assigned at.
            // origin=Mastodon@0.91, current=Mastodon@0.88 still alignedFriendly = true.
            // origin=Mastodon@0.91, current=Generic-Scraper@0.84 -> drift exceeds
            // threshold -> archetype pin revoked.
            var archetypeStillAligned = inputs.IsFriendlyArchetype
                && (inputs.CurrentArchetypeScore is null
                    || inputs.OriginArchetypeScore is null
                    || (inputs.OriginArchetypeScore.Value - inputs.CurrentArchetypeScore.Value)
                        <= s.FriendlyArchetypeDriftRevokeThreshold);

            // Friendly-pin corroboration: UA strings are trivially spoofable, so
            // a friendly identity claim only counts when at least one verification
            // channel says yes. ipVerified / domainVerified come from the IP-range
            // contributor and the FediverseDomainContributor (NodeInfo lookup).
            // FriendlyVerified is the convenience latch composed by the caller.
            var ipVerified = inputs.FriendlyIpVerified == true;
            var domainVerified = inputs.FriendlyDomainVerified == true;
            var ipExplicitlyFailed = inputs.FriendlyIpVerified == false;
            var domainExplicitlyFailed = inputs.FriendlyDomainVerified == false;
            var anyVerified = ipVerified || domainVerified || inputs.FriendlyVerified;
            var anyExplicitlyFailed = ipExplicitlyFailed || domainExplicitlyFailed;

            if (anyVerified)
            {
                friendlyPin = true;
                var source = (ipVerified, domainVerified) switch
                {
                    (true, true)  => "ip+domain",
                    (true, false) => "ip",
                    (false, true) => "domain",
                    _             => "verified_latch"
                };
                friendlyWhy = $"Verified-friendly ({source}): identity corroborated by non-UA signal";
                reasons.Add($"friendly_pin: verified source={source}");
            }
            // Network-trusted clamp: BotType.Internal is set ONLY when
            // NetworkHelper.IsLocalIp returned true at orchestration time, so the
            // verification already happened by network position -- no UA-declared
            // identity needed. Without this branch, Internal hits the neutral
            // bucketing path which probability-buckets it to VeryHigh (100% bot
            // probability) and the dashboard shows "Internal · Policy: Allow ·
            // Probability 100% · Risk Profile VeryHigh", which is the contradictory
            // pairing the operator-facing axes are supposed to prevent.
            // Spec basis: section 3 trusted-and-aligned clamp -- when the future
            // archetype-alignment evaluator ships, this rule becomes "Internal AND
            // archetype-aligned" instead. Until then, Internal alone is enough
            // because it carries network-position verification.
            else if (inputs.BotType == nameof(BotType.Internal))
            {
                friendlyPin = true;
                friendlyWhy = "Trusted internal client (network position verified)";
                reasons.Add("friendly_pin: bot_type=Internal (network-trusted)");
            }
            else if (anyExplicitlyFailed)
            {
                // A check ran and FAILED (e.g. UA claims Googlebot but the IP
                // doesn't match Google's published range). Record but don't pin --
                // the spoofed UA must not benefit from a friendly-archetype match
                // either. Suppress all friendly paths below.
                var which = ipExplicitlyFailed ? "ip_check_failed" : "domain_check_failed";
                reasons.Add($"friendly_pin_blocked: {which}");
            }
            else if (inputs.ClusterType == BotClusterType.Safe)
            {
                friendlyPin = true;
                friendlyWhy = $"Member of Safe cluster ({inputs.ClusterLabel ?? inputs.ClusterId})";
                reasons.Add($"friendly_pin: cluster_type=Safe label={inputs.ClusterLabel}");
            }
            else if (inputs.DeclaredBot && inputs.IsFriendlyBotType)
            {
                friendlyPin = true;
                friendlyWhy = $"Declared friendly bot ({inputs.BotType})";
                reasons.Add($"friendly_pin: declared_bot+friendly_type ({inputs.BotType})");
            }
            else if (archetypeStillAligned)
            {
                friendlyPin = true;
                friendlyWhy = $"Aligned with friendly archetype {inputs.CurrentArchetypeId} (score {inputs.CurrentArchetypeScore:F2})";
                reasons.Add($"friendly_pin: friendly_archetype_aligned archetype={inputs.CurrentArchetypeId}");
            }
            else if (inputs.IsFriendlyArchetype)
            {
                reasons.Add($"friendly_pin_revoked: drift {inputs.OriginArchetypeScore - inputs.CurrentArchetypeScore:F2} exceeds {s.FriendlyArchetypeDriftRevokeThreshold:F2}");
            }
        }

        // === Threat band ===
        var threatBand = hostilePin
            ? ThreatBand.Critical
            : friendlyPin
                ? ThreatBand.None
                : (inputs.RawThreatBand ?? BucketThreat(inputs.RawThreatScore));

        // === Risk band ===
        // Hostile / friendly pins still short-circuit. Neutral path runs the full
        // dimension-max bucketing migrated from the legacy DetermineRiskBand --
        // probability (AI-aware), threat score, persistence (session request
        // count), and confidence (low confidence demotes high probability to
        // Medium). When AiRan / SessionRequestCount / IntentCategory aren't
        // supplied (e.g. /api/v1/topbots overlay path), the helper falls back to
        // confidence-scaled BucketRisk -- the original composer behaviour, which
        // already passed unit tests for the overlay path.
        var riskBand = hostilePin
            ? RiskBand.VeryHigh
            : friendlyPin
                ? RiskBand.Low
                : ComputeNeutralRiskBand(inputs, reasons);

        // === Risk profile label ===
        var riskProfile = ComposeProfile(inputs, hostilePin, friendlyPin);

        // === Justification (one-liner) ===
        var justification = hostileWhy
            ?? friendlyWhy
            ?? ComposeNeutralJustification(inputs);

        return new SignatureRiskVerdict
        {
            PrimarySignature = inputs.PrimarySignature,
            RiskBand = riskBand,
            ThreatBand = threatBand,
            RiskProfile = riskProfile,
            Justification = justification,
            Reasons = reasons,
            FriendlyPinFired = friendlyPin,
            HostilePinFired = hostilePin,
        };
    }

    private static RiskProfileLabel ComposeProfile(
        SignatureRiskInputs inputs, bool hostilePin, bool friendlyPin)
    {
        if (hostilePin) return RiskProfileLabel.Hostile;
        if (inputs.ClusterType == BotClusterType.Safe || inputs.FriendlyVerified)
            return RiskProfileLabel.VerifiedCommunity;
        if (!string.IsNullOrEmpty(inputs.OriginArchetypeId)
            && !string.IsNullOrEmpty(inputs.CurrentArchetypeId)
            && !string.Equals(inputs.OriginArchetypeId, inputs.CurrentArchetypeId, StringComparison.OrdinalIgnoreCase))
        {
            return RiskProfileLabel.Drifting;
        }
        if (friendlyPin || !string.IsNullOrEmpty(inputs.OriginArchetypeId))
            return RiskProfileLabel.Aligned;
        return RiskProfileLabel.Unknown;
    }

    private static string ComposeNeutralJustification(SignatureRiskInputs inputs)
    {
        if (!string.IsNullOrEmpty(inputs.CurrentArchetypeId))
            return $"Behaviour matches archetype {inputs.CurrentArchetypeId} (score {inputs.CurrentArchetypeScore:F2})";
        if (!string.IsNullOrEmpty(inputs.BotType))
            return $"Classified {inputs.BotType} (probability {inputs.BotProbability:F2}, confidence {inputs.Confidence:F2})";
        return $"Probability {inputs.BotProbability:F2}, confidence {inputs.Confidence:F2}";
    }

    /// <summary>
    ///     Threat-score bucketing. Matches the band boundaries documented on
    ///     <see cref="ThreatBand"/>. Pure / single-source-of-truth so band derivation
    ///     never drifts between read sites.
    /// </summary>
    public static ThreatBand BucketThreat(double rawScore) => rawScore switch
    {
        < 0.15 => ThreatBand.None,
        < 0.35 => ThreatBand.Low,
        < 0.55 => ThreatBand.Elevated,
        < 0.80 => ThreatBand.High,
        _      => ThreatBand.Critical
    };

    /// <summary>
    ///     Reverse of <see cref="BucketThreat"/>: the lower-bound threat score that
    ///     produces each band. Used to lift a raw threat score onto a verdict-pinned
    ///     band (e.g. hostile-pin promoted ThreatBand from None to Critical because
    ///     the bad-actor signal was a reputation latch / cluster membership, not a
    ///     raw threat score). Without the lift, the dashboard renders (Critical,
    ///     null) because <c>DetectionBroadcastMiddleware</c> nulls score==0, and
    ///     operators see a band that no underlying number corroborates.
    /// </summary>
    public static double ThreatBandFloor(ThreatBand band) => band switch
    {
        ThreatBand.None     => 0.0,
        ThreatBand.Low      => 0.15,
        ThreatBand.Elevated => 0.35,
        ThreatBand.High     => 0.55,
        ThreatBand.Critical => 0.80,
        _                   => 0.0
    };

    /// <summary>
    ///     Probability + confidence bucketing for the per-request risk band when
    ///     no AI / persistence context is available (e.g. the /api/v1/topbots
    ///     overlay path). Confidence scales the band height so a 0.8 probability
    ///     at 0.3 confidence is Elevated, not High. The full-pipeline path uses
    ///     <see cref="ComputeNeutralRiskBand"/> which subsumes this and adds the
    ///     AI / persistence / threat dimensions.
    /// </summary>
    public static RiskBand BucketRisk(double probability, double confidence)
    {
        if (probability < 0.20) return RiskBand.VeryLow;
        var scaled = probability * (0.5 + 0.5 * confidence);
        if (scaled < 0.25) return RiskBand.Low;
        if (scaled < 0.45) return RiskBand.Elevated;
        if (scaled < 0.60) return RiskBand.Medium;
        if (scaled < 0.80) return RiskBand.High;
        return RiskBand.VeryHigh;
    }

    /// <summary>
    ///     Full neutral-path risk-band computation. Migrated from the legacy
    ///     <c>DetectionLedgerExtensions.DetermineRiskBand</c> dimension-max
    ///     logic so the composer is the single source of truth.
    ///
    ///     <list type="number">
    ///       <item>Low confidence (&lt; 0.30) demotes to Medium / Unknown.</item>
    ///       <item>Probability band: AI-aware thresholds when <c>AiRan = true</c>,
    ///             stricter thresholds otherwise (no AI -> need 0.85 prob for VeryHigh).</item>
    ///       <item>Threat dimension: independent of automation (humans can attack).</item>
    ///       <item>Persistence dimension: <c>SessionRequestCount</c> drives escalation
    ///             even at moderate probability (20+ req -> High, 5+ req at 0.70+ -> VeryHigh).</item>
    ///       <item>Final = max across all three dimensions.</item>
    ///     </list>
    ///
    ///     When AiRan / SessionRequestCount / IntentCategory are unset (the
    ///     overlay path), the AI thresholds and persistence dimension are
    ///     suppressed and the result degrades gracefully to <see cref="BucketRisk"/>.
    /// </summary>
    public static RiskBand ComputeNeutralRiskBand(
        SignatureRiskInputs inputs, List<string>? reasons = null)
    {
        // Low confidence carve-out
        if (inputs.Confidence < 0.30)
        {
            if (inputs.BotProbability >= 0.5)
            {
                reasons?.Add($"low_confidence: confidence={inputs.Confidence:F2}, probability={inputs.BotProbability:F2}");
                return RiskBand.Medium;
            }
            reasons?.Add($"low_confidence: confidence={inputs.Confidence:F2}");
            return RiskBand.Unknown;
        }

        // Dimension 1: probability band
        RiskBand probabilityBand;
        if (inputs.AiRan)
        {
            probabilityBand = inputs.BotProbability switch
            {
                >= 0.80 => RiskBand.VeryHigh,
                >= 0.50 => RiskBand.High,
                >= 0.20 => RiskBand.Medium,
                >= 0.05 => RiskBand.Low,
                _       => RiskBand.VeryLow
            };
            if (probabilityBand >= RiskBand.High)
                reasons?.Add($"ai_probability={inputs.BotProbability:F2}");
        }
        else
        {
            probabilityBand = inputs.BotProbability switch
            {
                >= 0.85 => RiskBand.VeryHigh,
                >= 0.65 => RiskBand.High,
                >= 0.50 => RiskBand.Medium,
                >= 0.35 => RiskBand.Elevated,
                >= 0.15 => RiskBand.Low,
                _       => RiskBand.VeryLow
            };
            if (probabilityBand >= RiskBand.High)
                reasons?.Add($"probability={inputs.BotProbability:F2}");
        }

        // Dimension 2: threat band (independent of automation)
        var threatBandRisk = inputs.RawThreatScore switch
        {
            >= 0.80 => RiskBand.VeryHigh,
            >= 0.55 => RiskBand.High,
            >= 0.35 => RiskBand.Medium,
            >= 0.15 => RiskBand.Elevated,
            _       => RiskBand.VeryLow
        };
        if (threatBandRisk >= RiskBand.Medium)
        {
            var threatLabel = !string.IsNullOrEmpty(inputs.IntentCategory)
                              && !string.Equals(inputs.IntentCategory, "browsing", StringComparison.OrdinalIgnoreCase)
                ? $"{inputs.IntentCategory} activity (threat={inputs.RawThreatScore:F2})"
                : $"threat_score={inputs.RawThreatScore:F2}";
            reasons?.Add(threatLabel);
        }

        // Dimension 3: persistence
        var persistenceBand = RiskBand.VeryLow;
        if (inputs.SessionRequestCount >= 5 && inputs.BotProbability >= 0.70)
        {
            persistenceBand = RiskBand.VeryHigh;
            reasons?.Add($"persistent_bot: requests={inputs.SessionRequestCount}");
        }
        else if (inputs.SessionRequestCount >= 20)
        {
            persistenceBand = RiskBand.High;
            reasons?.Add($"persistence: requests={inputs.SessionRequestCount}");
        }
        else if (inputs.SessionRequestCount >= 10)
        {
            persistenceBand = RiskBand.Medium;
            reasons?.Add($"persistence: requests={inputs.SessionRequestCount}");
        }

        // Final = max across dimensions
        var combined = (RiskBand)new[] { (int)probabilityBand, (int)threatBandRisk, (int)persistenceBand }.Max();
        return combined;
    }
}
