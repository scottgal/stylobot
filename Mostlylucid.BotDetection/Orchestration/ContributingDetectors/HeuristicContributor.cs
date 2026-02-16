using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Heuristic model contributor - uses learned weights for bot classification.
///     Runs in Wave 1+ after initial detectors have run.
///
///     Configuration loaded from: heuristic.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:HeuristicContributor:*
/// </summary>
public class HeuristicContributor : ConfiguredContributorBase
{
    private readonly HeuristicDetector _detector;
    private readonly ILogger<HeuristicContributor> _logger;

    public HeuristicContributor(
        ILogger<HeuristicContributor> logger,
        HeuristicDetector detector,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _detector = detector;
    }

    public override string Name => "Heuristic";
    public override int Priority => Manifest?.Priority ?? 50;

    // Config-driven parameters from YAML
    private double HeuristicWeight => GetParam("heuristic_weight", 2.0);

    // No triggers — heuristic runs for every request, including those with missing UAs.
    // Previously required SignalKeys.UserAgent which caused the heuristic to be skipped
    // entirely when the UA header was absent (a strong bot signal in itself).
    public override IReadOnlyList<TriggerCondition> TriggerConditions => [];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var result = await _detector.DetectAsync(state.HttpContext, cancellationToken);

            if (result.Reasons.Count == 0)
            {
                // Heuristic disabled or skipped
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "HeuristicEarly",
                    ConfidenceDelta = 0,
                    Weight = 0,
                    Reason = "Heuristic detection disabled or skipped"
                });
            }
            else
            {
                // Heuristic made a prediction (use reason's ConfidenceImpact which is negative for human)
                var reason = result.Reasons.First();
                var isBot = reason.ConfidenceImpact > 0;

                state.WriteSignals([
                    new(SignalKeys.HeuristicPrediction, isBot ? "bot" : "human"),
                    new(SignalKeys.HeuristicConfidence, result.Confidence),
                    new(SignalKeys.HeuristicEarlyCompleted, true) // Signal for late heuristic
                ]);

                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "HeuristicEarly",
                    ConfidenceDelta = reason.ConfidenceImpact,
                    Weight = HeuristicWeight, // Heuristic predictions are weighted heavily
                    Reason = reason.Detail,
                    BotType = result.BotType?.ToString(),
                    BotName = result.BotName
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heuristic detection failed");
        }

        return contributions;
    }
}