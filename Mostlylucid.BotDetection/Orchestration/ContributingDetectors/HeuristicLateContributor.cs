using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Late-stage heuristic contributor - runs AFTER AI/LLM to provide final classification.
///     Consumes all evidence including AI results for comprehensive learned classification.
/// </summary>
/// <remarks>
///     <para>
///         The detection pipeline runs in stages:
///         <list type="number">
///             <item><b>Early Heuristic</b> (HeuristicContributor): Uses basic request features</item>
///             <item><b>AI/LLM</b>: Uses early heuristic + other signals for classification</item>
///             <item><b>Late Heuristic</b> (this): Consumes ALL evidence including AI results</item>
///         </list>
///     </para>
///     <para>
///         This late-stage heuristic is the final meta-layer that learns from the entire pipeline,
///         including whether the AI/LLM agreed with earlier signals or detected something new.
///     </para>
///     <para>
///         <b>Signal Flow:</b>
///         <code>
///         AiClassificationCompleted/LlmClassificationCompleted → HeuristicLate → HeuristicLateCompleted
///         </code>
///     </para>
/// </remarks>
public class HeuristicLateContributor : ContributingDetectorBase
{
    private readonly HeuristicDetector _detector;
    private readonly ILogger<HeuristicLateContributor> _logger;

    public HeuristicLateContributor(
        ILogger<HeuristicLateContributor> logger,
        HeuristicDetector detector)
    {
        _logger = logger;
        _detector = detector;
    }

    public override string Name => "HeuristicLate";
    public override int Priority => 100; // Run after AI detectors (priority ~80-90)

    // Trigger conditions: Run as final meta-layer
    // - When AI has run (AiPrediction signal exists), OR
    // - When enough static detectors have run (fallback when no AI)
    // NOTE: These are OR'd via outer AnyOf. The orchestrator evaluates TriggerConditions
    // with ALL semantics, so we must wrap in a single AnyOf for OR behavior.
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.AnyOf(
            Triggers.WhenSignalExists(SignalKeys.AiPrediction),
            Triggers.WhenSignalExists(SignalKeys.AiConfidence),
            Triggers.WhenDetectorCount(5))
    ];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            // Build temporary AggregatedEvidence from blackboard state so the detector sees "full mode"
            // This includes all contributions from earlier detectors (including AI if it ran)
            var tempEvidence = BuildTempEvidence(state);
            state.HttpContext.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = tempEvidence;

            // Run the heuristic detector - it will now see evidence and run in "full mode"
            var result = await _detector.DetectAsync(state.HttpContext, cancellationToken);

            if (result.Reasons.Count == 0)
                // Heuristic disabled or skipped
                return contributions;

            // Get the reason - should now say "full" mode
            var reason = result.Reasons.First();
            var isBot = reason.ConfidenceImpact > 0;

            state.WriteSignals([
                new(SignalKeys.HeuristicLatePrediction, isBot ? "bot" : "human"),
                new(SignalKeys.HeuristicLateConfidence, result.Confidence)
            ]);

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "HeuristicLate",
                ConfidenceDelta = reason.ConfidenceImpact,
                Weight = 2.5, // Late heuristic is weighted heavily - it's the final say
                Reason = reason.Detail.Replace("(early)", "(late)").Replace("(full)", "(late)"),
                BotType = result.BotType?.ToString(),
                BotName = result.BotName
            });

            _logger.LogDebug(
                "Late heuristic completed: {Prediction} with confidence {Confidence:F2}",
                isBot ? "bot" : "human",
                result.Confidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Late heuristic detection failed");
        }

        return contributions;
    }

    /// <summary>
    ///     Build a temporary AggregatedEvidence from the blackboard state.
    ///     This allows the HeuristicDetector to see "full mode" with all prior contributions.
    /// </summary>
    private static AggregatedEvidence BuildTempEvidence(BlackboardState state)
    {
        // Aggregate signals from blackboard state first
        var signals = new Dictionary<string, object>();
        foreach (var signal in state.Signals) signals[signal.Key] = signal.Value;

        // Then overlay signals from all contributions (these take precedence)
        foreach (var contrib in state.Contributions)
        foreach (var signal in contrib.Signals)
            signals[signal.Key] = signal.Value;

        // Check if AI detectors contributed
        var aiDetectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Onnx", "Llm" };
        var detectorNames = state.Contributions.Select(c => c.DetectorName).ToHashSet();
        var aiRan = detectorNames.Any(d => aiDetectors.Contains(d));

        // Build a DetectionLedger with all contributions
        var tempLedger = new DetectionLedger("temp-heuristic-late");
        foreach (var contrib in state.Contributions)
            tempLedger.AddContribution(contrib);

        return new AggregatedEvidence
        {
            Ledger = tempLedger,
            BotProbability = state.CurrentRiskScore,
            Confidence = 0.5, // Intermediate confidence - will be recalculated
            RiskBand = RiskBand.Medium, // Intermediate - will be recalculated
            PrimaryBotType = null,
            PrimaryBotName = null,
            Signals = signals,
            TotalProcessingTimeMs = state.Elapsed.TotalMilliseconds,
            CategoryBreakdown = tempLedger.CategoryBreakdown,
            ContributingDetectors = detectorNames,
            FailedDetectors = state.FailedDetectors,
            AiRan = aiRan
        };
    }
}