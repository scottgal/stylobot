using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
/// Extensions to build BotDetection's AggregatedEvidence from taxonomy's DetectionLedger.
/// </summary>
public static class DetectionLedgerExtensions
{
    /// <summary>
    /// Builds an AggregatedEvidence from the detection ledger.
    /// </summary>
    public static AggregatedEvidence ToAggregatedEvidence(
        this DetectionLedger ledger,
        string? policyName = null,
        PolicyAction? policyAction = null,
        string? actionPolicyName = null,
        bool aiRan = false,
        IReadOnlyDictionary<string, object>? premergedSignals = null,
        BotDetectionOptions? options = null)
    {
        var botProbability = ledger.BotProbability;
        var confidence = ledger.Confidence;

        // Clamp probability when AI hasn't run.
        // Floor defaults to 0.0 (allowing scores to reach zero on strong human evidence).
        // Ceiling prevents high-confidence bot verdicts without AI confirmation.
        // Configurable via BotDetection:NonAiMinProbability / NonAiMaxProbability.
        if (!aiRan)
        {
            var minProb = options?.NonAiMinProbability ?? 0.05;
            var maxProb = options?.NonAiMaxProbability ?? 0.90;
            botProbability = Math.Clamp(botProbability, minProb, maxProb);
        }

        // Compute coverage-based confidence
        var coverageConfidence = ComputeCoverageConfidence(ledger.ContributingDetectors, aiRan);
        confidence = Math.Min(confidence, coverageConfidence);

        // Extract signals needed for context-aware risk band before building evidence
        // (signals dict built below; extract from ledger merged signals here)
        var preSignals = premergedSignals != null
            ? premergedSignals
            : (IReadOnlyDictionary<string, object>)ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var earlyThreatForBand = ExtractThreatScoreRaw(preSignals);
        var isConfirmedBadForBand = IsConfirmedBad(preSignals);
        var sessionCountForBand = ExtractSessionCount(preSignals);
        var intentCategory = preSignals.TryGetValue(SignalKeys.IntentCategory, out var ic) ? ic as string : null;

        var (riskBand, riskJustification) = DetermineRiskBand(botProbability, confidence, aiRan,
            earlyThreatForBand, isConfirmedBadForBand, sessionCountForBand, intentCategory);

        // Only set BotType/BotName if actually a bot
        var isActuallyBot = botProbability >= 0.5;
        var primaryBotType = isActuallyBot ? ParseBotType(ledger.BotType) : null;
        var primaryBotName = isActuallyBot ? ledger.BotName : null;

        // Handle early exit
        if (ledger.EarlyExit && ledger.EarlyExitContribution != null)
        {
            return CreateEarlyExitResult(ledger, aiRan, policyName, premergedSignals);
        }

        var signals = premergedSignals != null
            ? new Dictionary<string, object>(premergedSignals)
            : ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var (threatScore, threatBand) = ExtractThreatScore(signals);

        // Write risk justification back to signals so downstream consumers can read it
        if (!string.IsNullOrEmpty(riskJustification))
            signals[SignalKeys.RiskJustification] = riskJustification;

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = botProbability,
            Confidence = confidence,
            RiskBand = riskBand,
            RiskJustification = riskJustification,
            EarlyExit = false,
            PrimaryBotType = primaryBotType,
            PrimaryBotName = primaryBotName,
            Signals = signals,
            TotalProcessingTimeMs = ledger.TotalProcessingTimeMs,
            CategoryBreakdown = ledger.CategoryBreakdown,
            ContributingDetectors = ledger.ContributingDetectors,
            FailedDetectors = ledger.FailedDetectors,
            PolicyName = policyName,
            PolicyAction = policyAction,
            TriggeredActionPolicyName = actionPolicyName,
            AiRan = aiRan,
            ThreatScore = threatScore,
            ThreatBand = threatBand
        };
    }

    private static AggregatedEvidence CreateEarlyExitResult(
        DetectionLedger ledger,
        bool aiRan,
        string? policyName,
        IReadOnlyDictionary<string, object>? premergedSignals = null)
    {
        var exitContrib = ledger.EarlyExitContribution!;
        var verdict = ParseEarlyExitVerdict(exitContrib.EarlyExitVerdict);
        var isBot = verdict is EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.VerifiedBadBot;

        var earlySignals = premergedSignals != null
            ? new Dictionary<string, object>(premergedSignals)
            : ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var (earlyThreatScore, earlyThreatBand) = ExtractThreatScore(earlySignals);

        var earlyRiskBand = verdict switch
        {
            EarlyExitVerdict.VerifiedGoodBot => RiskBand.VeryLow,
            EarlyExitVerdict.Whitelisted     => RiskBand.VeryLow,
            EarlyExitVerdict.VerifiedBadBot  => RiskBand.VeryHigh,
            EarlyExitVerdict.Blacklisted     => RiskBand.VeryHigh,
            _                                => RiskBand.Medium
        };
        var earlyRiskJustification = verdict switch
        {
            EarlyExitVerdict.VerifiedGoodBot => "Cryptographically verified good bot",
            EarlyExitVerdict.Whitelisted     => "Explicitly whitelisted",
            EarlyExitVerdict.VerifiedBadBot  => "Verified bad bot",
            EarlyExitVerdict.Blacklisted     => "Explicitly blacklisted",
            _                                => "Early exit policy"
        };

        if (!string.IsNullOrEmpty(earlyRiskJustification))
            earlySignals[SignalKeys.RiskJustification] = earlyRiskJustification;

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = isBot ? 1.0 : 0.0,
            Confidence = 1.0,
            RiskBand = earlyRiskBand,
            RiskJustification = earlyRiskJustification,
            EarlyExit = true,
            EarlyExitVerdict = verdict,
            PrimaryBotType = ParseBotType(exitContrib.BotType),
            PrimaryBotName = exitContrib.BotName,
            Signals = earlySignals,
            TotalProcessingTimeMs = ledger.TotalProcessingTimeMs,
            CategoryBreakdown = ledger.CategoryBreakdown,
            ContributingDetectors = ledger.ContributingDetectors,
            FailedDetectors = ledger.FailedDetectors,
            PolicyName = policyName,
            AiRan = aiRan,
            ThreatScore = earlyThreatScore,
            ThreatBand = earlyThreatBand
        };
    }

    private static (double ThreatScore, ThreatBand Band) ExtractThreatScore(
        IReadOnlyDictionary<string, object> signals)
    {
        double threatScore = 0.0;
        if (signals.TryGetValue(SignalKeys.IntentThreatScore, out var rawScore))
        {
            threatScore = rawScore switch
            {
                double d => d,
                float f => f,
                int i => i,
                _ => 0.0
            };
        }

        var band = threatScore switch
        {
            >= 0.80 => ThreatBand.Critical,
            >= 0.55 => ThreatBand.High,
            >= 0.35 => ThreatBand.Elevated,
            >= 0.15 => ThreatBand.Low,
            _ => ThreatBand.None
        };

        return (threatScore, band);
    }

    private static double ComputeCoverageConfidence(IReadOnlySet<string> detectorsRan, bool aiRan)
    {
        var maxScore = 0.0;
        var score = 0.0;

        void Add(string name, double weight)
        {
            maxScore += weight;
            if (detectorsRan.Contains(name))
                score += weight;
        }

        Add("UserAgent", 1.0);
        Add("Ip", 0.5);
        Add("Header", 1.0);
        Add("ClientSide", 1.0);
        Add("Behavioral", 1.0);
        Add("VersionAge", 0.8);
        Add("Inconsistency", 0.8);
        Add("Heuristic", 2.0);

        // Only include AI in denominator when AI actually ran.
        // When AI is not configured/enabled, it should not penalize confidence.
        if (aiRan)
        {
            maxScore += 2.5;
            score += 2.5;
        }

        return maxScore == 0 ? 0 : score / maxScore;
    }

    /// <summary>
    /// Multi-dimensional risk band classification.
    ///
    /// Risk = max(probability_band, threat_band, persistence_band)
    ///
    /// This correctly handles:
    /// - A human manually running SQLi scans (low bot probability but high threat score = VeryHigh)
    /// - A persistent scraper with no threat indicators (high probability + many requests = VeryHigh)
    /// - A single wget request with no threat (high probability but no context = High, not VeryHigh)
    /// - An automated crawler confirmed in reputation history (confirmed bad = VeryHigh regardless)
    ///
    /// VeryHigh without AI requires one of: probability >= 0.85, OR confirmed bad actor, OR
    /// probability >= 0.70 with active threat OR >= 5 requests.
    /// </summary>
    private static (RiskBand Band, string Justification) DetermineRiskBand(
        double botProbability, double confidence, bool aiRan,
        double threatScore, bool isConfirmedBad, int sessionRequestCount,
        string? intentCategory = null)
    {
        // Low confidence: not enough data to assess reliably
        if (confidence < 0.3)
            return botProbability >= 0.5
                ? (RiskBand.Medium, $"Low detection confidence ({confidence:F2}); probability {botProbability:F2}")
                : (RiskBand.Unknown, "Insufficient data for reliable risk assessment");

        var reasons = new List<string>(4);

        // Dimension 1: bot probability band
        RiskBand probabilityBand;
        if (aiRan)
        {
            probabilityBand = botProbability switch
            {
                >= 0.80 => RiskBand.VeryHigh,
                >= 0.50 => RiskBand.High,
                >= 0.20 => RiskBand.Medium,
                >= 0.05 => RiskBand.Low,
                _       => RiskBand.VeryLow
            };
            if (probabilityBand >= RiskBand.High)
                reasons.Add($"AI probability {botProbability:F2}");
        }
        else
        {
            // Without AI, require stronger evidence for VeryHigh:
            // pure probability alone can reach VeryHigh at 0.85 (matching the middleware threshold).
            // Below that, persistence or threat must be present to escalate further.
            probabilityBand = botProbability switch
            {
                >= 0.85 => RiskBand.VeryHigh,
                >= 0.65 => RiskBand.High,
                >= 0.50 => RiskBand.Medium,
                >= 0.35 => RiskBand.Elevated,
                >= 0.15 => RiskBand.Low,
                _       => RiskBand.VeryLow
            };
            if (probabilityBand >= RiskBand.High)
                reasons.Add($"probability {botProbability:F2}");
        }

        // Dimension 2: threat score (independent of automation - a human can attack too)
        var threatBandRisk = threatScore switch
        {
            >= 0.80 => RiskBand.VeryHigh,
            >= 0.55 => RiskBand.High,
            >= 0.35 => RiskBand.Medium,
            >= 0.15 => RiskBand.Elevated,
            _       => RiskBand.VeryLow
        };
        if (threatBandRisk >= RiskBand.Medium)
        {
            var threatLabel = !string.IsNullOrEmpty(intentCategory) && intentCategory != "browsing"
                ? $"{intentCategory} activity (threat={threatScore:F2})"
                : $"threat score {threatScore:F2}";
            reasons.Add(threatLabel);
        }

        // Dimension 3: persistence (repeated confirmed behavior adds weight regardless of bot probability)
        var persistenceBand = RiskBand.VeryLow;
        if (isConfirmedBad)
        {
            persistenceBand = RiskBand.VeryHigh;
            reasons.Add("confirmed bad actor");
        }
        else if (botProbability >= 0.70 && sessionRequestCount >= 5)
        {
            // Persistent suspected bot: multiple requests + elevated probability = escalate to VeryHigh
            persistenceBand = RiskBand.VeryHigh;
            reasons.Add($"{sessionRequestCount} requests");
        }
        else if (sessionRequestCount >= 20)
        {
            persistenceBand = RiskBand.High;
            reasons.Add($"{sessionRequestCount} requests");
        }
        else if (sessionRequestCount >= 10)
        {
            persistenceBand = RiskBand.Medium;
            reasons.Add($"{sessionRequestCount} requests");
        }

        // Final band = max across all three dimensions
        var finalBand = (RiskBand)new[] { (int)probabilityBand, (int)threatBandRisk, (int)persistenceBand }.Max();

        if (reasons.Count == 0)
        {
            var lowLabel = finalBand <= RiskBand.Low ? "No significant indicators" : $"probability {botProbability:F2}";
            return (finalBand, lowLabel);
        }

        return (finalBand, string.Join("; ", reasons));
    }

    private static double ExtractThreatScoreRaw(IReadOnlyDictionary<string, object> signals)
    {
        if (!signals.TryGetValue(SignalKeys.IntentThreatScore, out var rawScore)) return 0.0;
        return rawScore switch
        {
            double d => d,
            float f  => f,
            int i    => i,
            _        => 0.0
        };
    }

    private static bool IsConfirmedBad(IReadOnlyDictionary<string, object> signals)
    {
        if (signals.TryGetValue(SignalKeys.ReputationCanAbort, out var canAbort) && canAbort is true)
            return true;
        if (signals.TryGetValue(SignalKeys.ReputationFastAbortActive, out var abortActive) && abortActive is true)
            return true;
        return false;
    }

    private static int ExtractSessionCount(IReadOnlyDictionary<string, object> signals)
    {
        if (!signals.TryGetValue(SignalKeys.SessionRequestCount, out var raw)) return 0;
        return raw switch
        {
            int i    => i,
            long l   => (int)l,
            double d => (int)d,
            _        => 0
        };
    }

    private static BotType? ParseBotType(string? botType)
    {
        if (string.IsNullOrEmpty(botType))
            return null;

        if (Enum.TryParse<BotType>(botType, true, out var result))
            return result;

        // Handle atoms library values that don't map directly to enum names
        if (botType.Equals("VerifiedGood", StringComparison.OrdinalIgnoreCase))
            return BotType.VerifiedBot;

        return null;
    }

    private static EarlyExitVerdict? ParseEarlyExitVerdict(string? verdict)
    {
        if (string.IsNullOrEmpty(verdict))
            return null;

        if (Enum.TryParse<EarlyExitVerdict>(verdict, true, out var result))
            return result;

        return null;
    }
}
