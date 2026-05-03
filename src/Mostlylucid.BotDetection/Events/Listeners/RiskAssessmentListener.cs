using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Events.Listeners;

/// <summary>
///     Listens for Finalising signal to compute final risk assessment.
///     Aggregates all prior signals into RiskBand and RecommendedAction.
/// </summary>
public class RiskAssessmentListener : IBotSignalListener, ISignalSubscriber
{
    private readonly ILogger<RiskAssessmentListener> _logger;
    private readonly BotDetectionOptions _options;

    public RiskAssessmentListener(
        ILogger<RiskAssessmentListener> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public ValueTask OnSignalAsync(
        BotSignalType signal,
        DetectionContext context,
        CancellationToken ct = default)
    {
        if (signal != BotSignalType.Finalising)
            return ValueTask.CompletedTask;

        // Use weighted average of positive scores, not max of individual scores.
        // Risk should start neutral and only escalate with corroborating evidence.
        // A single detector firing high should NOT produce VeryHigh on its own.
        var scores = context.Scores;
        var positiveScores = scores.Values.Where(s => s > 0.1).ToList();
        var aggregatedProbability = positiveScores.Count > 0
            ? positiveScores.Average()
            : 0.0;
        var contributorCount = positiveScores.Count(s => s > 0.3); // detectors that actually flagged

        // Check for specific signals
        var inconsistencyScore = context.GetSignal<double>(SignalKeys.InconsistencyScore);
        var headlessLikelihood = context.GetSignal<double>(SignalKeys.FingerprintHeadlessScore);
        var isDatacenter = context.GetSignal<bool>(SignalKeys.IpIsDatacenter);

        // Compute risk band from aggregated probability, not max single score
        var riskBand = ComputeRiskBand(aggregatedProbability, contributorCount, inconsistencyScore, headlessLikelihood, isDatacenter);
        var recommendedAction = MapRiskToAction(riskBand);

        // Store in context for result building
        context.SetSignal(SignalKeys.RiskBand, riskBand);
        context.SetSignal(SignalKeys.RiskScore, aggregatedProbability);

        _logger.LogDebug(
            "Risk assessment: band={Band}, action={Action}, aggProb={Score:F2}, contributors={Count}",
            riskBand, recommendedAction, aggregatedProbability, contributorCount);

        return ValueTask.CompletedTask;
    }

    public IEnumerable<BotSignalType> SubscribedSignals => new[] { BotSignalType.Finalising };

    private RiskBand ComputeRiskBand(
        double aggregatedProbability,
        int contributorCount,
        double? inconsistencyScore,
        double? headlessLikelihood,
        bool? isDatacenter)
    {
        // Start neutral. Risk escalates only with evidence from the aggregated probability
        // (weighted combination of all detectors), not from any single detector's max score.
        // A single detector firing high should NOT produce VeryHigh on its own.
        var band = aggregatedProbability switch
        {
            >= 0.90 => RiskBand.VeryHigh,
            >= 0.75 => RiskBand.High,
            >= 0.55 => RiskBand.Medium,
            >= 0.35 => RiskBand.Elevated,
            >= 0.15 => RiskBand.Low,
            _ => RiskBand.VeryLow
        };

        // Boost requires BOTH multi-signal agreement AND multiple detectors flagging.
        // A single detector + datacenter IP is not enough for escalation.
        var boostCount = 0;
        if (inconsistencyScore > 0.5) boostCount++;
        if (headlessLikelihood > 0.7) boostCount++;
        if (isDatacenter == true) boostCount++;

        // Only boost if 3+ detectors flagged AND 2+ corroborating signals agree
        if (boostCount >= 2 && contributorCount >= 3 && band < RiskBand.VeryHigh)
            band = (RiskBand)((int)band + 1);

        return band;
    }

    private RecommendedAction MapRiskToAction(RiskBand band)
    {
        return band switch
        {
            RiskBand.VeryHigh => RecommendedAction.Block,
            RiskBand.High => RecommendedAction.Block,
            RiskBand.Medium => RecommendedAction.Challenge,
            RiskBand.Elevated => RecommendedAction.Throttle,
            _ => RecommendedAction.Allow
        };
    }
}

// NOTE: RiskBand and RecommendedAction enums are now in Mostlylucid.BotDetection.Orchestration.DetectionContribution