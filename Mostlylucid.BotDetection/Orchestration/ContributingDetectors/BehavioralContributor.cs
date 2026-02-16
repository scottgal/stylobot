using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Behavioral analysis contributor - detects bots based on request patterns.
///     Runs in Wave 0 (no dependencies) to track all requests.
///
///     Configuration loaded from: behavioral.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:BehavioralContributor:*
/// </summary>
public class BehavioralContributor : ConfiguredContributorBase
{
    private readonly BehavioralDetector _detector;
    private readonly ILogger<BehavioralContributor> _logger;

    public BehavioralContributor(
        ILogger<BehavioralContributor> logger,
        BehavioralDetector detector,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _detector = detector;
    }

    public override string Name => "Behavioral";
    public override int Priority => Manifest?.Priority ?? 20;

    // No triggers - runs in first wave to track all requests
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters from YAML
    private double BehavioralWeightMultiplier => GetParam("behavioral_weight_multiplier", 1.5);

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
                // No behavioral issues detected - add negative signal (human indicator)
                state.WriteSignal(SignalKeys.BehavioralAnomalyDetected, false);
                contributions.Add(HumanContribution(
                    "Behavioral",
                    "Request patterns appear normal"));
            }
            else
            {
                // Write signals once, then convert each reason to a contribution
                var hasRate = result.Reasons.Any(r =>
                    r.Detail.Contains("rate", StringComparison.OrdinalIgnoreCase));
                state.WriteSignals([
                    new(SignalKeys.BehavioralAnomalyDetected, true),
                    new(SignalKeys.BehavioralRateExceeded, hasRate)
                ]);
                foreach (var reason in result.Reasons)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = reason.Category,
                        ConfidenceDelta = reason.ConfidenceImpact,
                        Weight = WeightBase * BehavioralWeightMultiplier,
                        Reason = reason.Detail,
                        BotType = result.BotType?.ToString()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Behavioral detection failed");
        }

        return contributions;
    }
}