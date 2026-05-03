using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Version age analysis contributor - detects outdated browser/OS combinations.
///     Runs in Wave 1 after UserAgent provides the UA signal.
///     Configuration loaded from: versionage.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:VersionAgeContributor:*
/// </summary>
public class VersionAgeContributor : ConfiguredContributorBase
{
    private readonly VersionAgeDetector _detector;
    private readonly ILogger<VersionAgeContributor> _logger;

    public VersionAgeContributor(
        ILogger<VersionAgeContributor> logger,
        VersionAgeDetector detector,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _detector = detector;
    }

    public override string Name => "VersionAge";
    public override int Priority => Manifest?.Priority ?? 25;

    // Config-driven thresholds
    private double VersionAgeWeight => GetParam("version_age_weight", 1.2);
    private double CurrentVersionConfidence => GetParam("current_version_confidence", -0.05);
    private double CurrentVersionWeight => GetParam("current_version_weight", 0.8);

    // Trigger after UserAgent has run and provided the UA signal
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.WhenSignalExists(SignalKeys.UserAgent)
    ];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var result = await _detector.DetectAsync(state.HttpContext, cancellationToken);

            state.WriteSignal(SignalKeys.VersionAgeAnalyzed, true);

            if (result.Reasons.Count == 0)
                // No version age issues detected - add negative signal (human indicator)
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "VersionAge",
                    ConfidenceDelta = CurrentVersionConfidence,
                    Weight = CurrentVersionWeight,
                    Reason = "Browser/OS versions appear current"
                });
            else
                // Convert each reason to a contribution
                foreach (var reason in result.Reasons)
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = reason.Category,
                        ConfidenceDelta = reason.ConfidenceImpact,
                        Weight = VersionAgeWeight,
                        Reason = reason.Detail,
                        BotType = result.BotType?.ToString()
                    });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VersionAge detection failed");
        }

        return contributions;
    }
}