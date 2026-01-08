using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Version age analysis contributor - detects outdated browser/OS combinations.
///     Runs in Wave 1 after UserAgent provides the UA signal.
/// </summary>
public class VersionAgeContributor : ContributingDetectorBase
{
    private readonly VersionAgeDetector _detector;
    private readonly ILogger<VersionAgeContributor> _logger;

    public VersionAgeContributor(
        ILogger<VersionAgeContributor> logger,
        VersionAgeDetector detector)
    {
        _logger = logger;
        _detector = detector;
    }

    public override string Name => "VersionAge";
    public override int Priority => 25; // Run after UserAgent (10)

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

            if (result.Reasons.Count == 0)
                // No version age issues detected - add negative signal (human indicator)
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "VersionAge",
                    ConfidenceDelta = -0.05,
                    Weight = 0.8,
                    Reason = "Browser/OS versions appear current",
                    Signals = ImmutableDictionary<string, object>.Empty
                        .Add(SignalKeys.VersionAgeAnalyzed, true)
                });
            else
                // Convert each reason to a contribution
                foreach (var reason in result.Reasons)
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = reason.Category,
                        ConfidenceDelta = reason.ConfidenceImpact,
                        Weight = 1.2, // Version age is a strong signal
                        Reason = reason.Detail,
                        BotType = result.BotType?.ToString(),
                        Signals = ImmutableDictionary<string, object>.Empty
                            .Add(SignalKeys.VersionAgeAnalyzed, true)
                    });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VersionAge detection failed");
        }

        return contributions;
    }
}