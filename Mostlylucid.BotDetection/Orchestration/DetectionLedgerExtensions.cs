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
        IReadOnlyDictionary<string, object>? premergedSignals = null)
    {
        var botProbability = ledger.BotProbability;
        var confidence = ledger.Confidence;

        // Clamp probability when AI hasn't run.
        // Floor of 0.05 allows strong human evidence to express near-zero scores;
        // ceiling of 0.80 prevents high-confidence bot verdicts without AI confirmation.
        if (!aiRan)
        {
            botProbability = Math.Clamp(botProbability, 0.05, 0.80);
        }

        // Compute coverage-based confidence
        var coverageConfidence = ComputeCoverageConfidence(ledger.ContributingDetectors, aiRan);
        confidence = Math.Min(confidence, coverageConfidence);

        var riskBand = DetermineRiskBand(botProbability, confidence, aiRan);

        // Only set BotType/BotName if actually a bot
        var isActuallyBot = botProbability >= 0.5;
        var primaryBotType = isActuallyBot ? ParseBotType(ledger.BotType) : null;
        var primaryBotName = isActuallyBot ? ledger.BotName : null;

        // Handle early exit
        if (ledger.EarlyExit && ledger.EarlyExitContribution != null)
        {
            return CreateEarlyExitResult(ledger, aiRan, policyName, premergedSignals);
        }

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = botProbability,
            Confidence = confidence,
            RiskBand = riskBand,
            EarlyExit = false,
            PrimaryBotType = primaryBotType,
            PrimaryBotName = primaryBotName,
            Signals = premergedSignals != null
                ? new Dictionary<string, object>(premergedSignals)
                : ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            TotalProcessingTimeMs = ledger.TotalProcessingTimeMs,
            CategoryBreakdown = ledger.CategoryBreakdown,
            ContributingDetectors = ledger.ContributingDetectors,
            FailedDetectors = ledger.FailedDetectors,
            PolicyName = policyName,
            PolicyAction = policyAction,
            TriggeredActionPolicyName = actionPolicyName,
            AiRan = aiRan
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
        var isGood = verdict is EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.Whitelisted;
        var isBot = verdict is EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.VerifiedBadBot;

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = isBot ? 1.0 : 0.0,
            Confidence = 1.0,
            RiskBand = isGood ? RiskBand.Verified : RiskBand.VeryHigh,
            EarlyExit = true,
            EarlyExitVerdict = verdict,
            PrimaryBotType = ParseBotType(exitContrib.BotType),
            PrimaryBotName = exitContrib.BotName,
            Signals = premergedSignals != null
                ? new Dictionary<string, object>(premergedSignals)
                : ledger.MergedSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            TotalProcessingTimeMs = ledger.TotalProcessingTimeMs,
            CategoryBreakdown = ledger.CategoryBreakdown,
            ContributingDetectors = ledger.ContributingDetectors,
            FailedDetectors = ledger.FailedDetectors,
            PolicyName = policyName,
            AiRan = aiRan
        };
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

    private static RiskBand DetermineRiskBand(double botProbability, double confidence, bool aiRan)
    {
        // Low confidence = not enough data to assess. Use probability to disambiguate:
        // low probability + low confidence = probably fine (Unknown/Low)
        // high probability + low confidence = uncertain but suspicious (Medium)
        if (confidence < 0.3)
            return botProbability >= 0.5 ? RiskBand.Medium : RiskBand.Unknown;

        if (aiRan)
            return botProbability switch
            {
                < 0.05 => RiskBand.VeryLow,
                < 0.2 => RiskBand.Low,
                < 0.5 => RiskBand.Medium,
                < 0.8 => RiskBand.High,
                _ => RiskBand.VeryHigh
            };

        return botProbability switch
        {
            < 0.15 => RiskBand.VeryLow,
            < 0.35 => RiskBand.Low,
            < 0.55 => RiskBand.Medium,
            < 0.75 => RiskBand.High,
            _ => RiskBand.VeryHigh
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
