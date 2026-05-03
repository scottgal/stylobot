using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Late-stage heuristic contributor - runs as the final meta-layer after the other
///     contributing detectors have populated the blackboard.
///     Consumes all accumulated evidence, including any AI results when present.
/// </summary>
/// <remarks>
///     <para>
///         The detection pipeline runs in stages:
///         <list type="number">
///             <item><b>Early Heuristic</b> (HeuristicContributor): Uses basic request features</item>
///             <item><b>AI/LLM</b>: Uses early heuristic + other signals for classification</item>
///             <item><b>Late Heuristic</b> (this): Consumes all accumulated evidence as the final pass</item>
///         </list>
///     </para>
///     <para>
///         This late-stage heuristic is the final meta-layer that learns from the entire pipeline,
///         including whether the AI/LLM agreed with earlier signals or detected something new.
///     </para>
///     <para>
///         The orchestrator defers this detector until it is the only ready detector left
///         in the current request, so it executes after the other contributing detectors.
///     </para>
///     <para>
///         Configuration loaded from: heuristiclate.detector.yaml
///         Override via: appsettings.json -> BotDetection:Detectors:HeuristicLateContributor:*
///     </para>
/// </remarks>
public class HeuristicLateContributor : ConfiguredContributorBase
{
    private readonly HeuristicDetector _detector;
    private readonly ILogger<HeuristicLateContributor> _logger;

    public HeuristicLateContributor(
        ILogger<HeuristicLateContributor> logger,
        HeuristicDetector detector,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _detector = detector;
    }

    public override string Name => "HeuristicLate";
    public override int Priority => Manifest?.Priority ?? 100;

    // Trigger once the early heuristic has produced a feature-based prediction, or when
    // inline AI has produced direct signals. The orchestrator then defers execution until
    // HeuristicLate is the only ready detector remaining, guaranteeing final-pass ordering.
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.AnyOf(
            Triggers.WhenSignalExists(SignalKeys.AiPrediction),
            Triggers.WhenSignalExists(SignalKeys.AiConfidence),
            Triggers.WhenSignalExists(SignalKeys.HeuristicPrediction))
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
                Weight = GetParam("late_weight", 2.5), // Late heuristic is weighted heavily - it's the final say
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
    ///     Infers the primary bot type from upstream contributions.
    ///     Uses the highest-weighted contribution that has a BotType set.
    /// </summary>
    private static BotType? InferPrimaryBotType(BlackboardState state)
    {
        var botTypeStr = state.Contributions
            .Where(c => !string.IsNullOrEmpty(c.BotType))
            .OrderByDescending(c => c.Weight)
            .Select(c => c.BotType)
            .FirstOrDefault();

        if (botTypeStr != null && Enum.TryParse<BotType>(botTypeStr, true, out var bt))
            return bt;

        return null;
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
            PrimaryBotType = InferPrimaryBotType(state),
            PrimaryBotName = state.Contributions
                .Where(c => !string.IsNullOrEmpty(c.BotName))
                .OrderByDescending(c => c.Weight)
                .Select(c => c.BotName)
                .FirstOrDefault(),
            Signals = signals,
            TotalProcessingTimeMs = state.Elapsed.TotalMilliseconds,
            CategoryBreakdown = tempLedger.CategoryBreakdown,
            ContributingDetectors = detectorNames,
            FailedDetectors = state.FailedDetectors,
            AiRan = aiRan
        };
    }
}
