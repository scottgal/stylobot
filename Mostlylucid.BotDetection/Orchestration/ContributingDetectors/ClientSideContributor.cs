using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Client-side fingerprint analysis contributor - uses browser fingerprint data.
///     Runs in Wave 0 (no dependencies) when client-side detection is enabled.
/// </summary>
public class ClientSideContributor : ContributingDetectorBase
{
    private readonly ClientSideDetector _detector;
    private readonly ILogger<ClientSideContributor> _logger;

    public ClientSideContributor(
        ILogger<ClientSideContributor> logger,
        ClientSideDetector detector)
    {
        _logger = logger;
        _detector = detector;
    }

    public override string Name => "ClientSide";
    public override int Priority => 18; // Run early

    // No triggers - runs in first wave to check for fingerprint data
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var result = await _detector.DetectAsync(state.HttpContext, cancellationToken);

            // Only contribute if we have actual client-side data
            // Empty result means client-side detection is disabled or no fingerprint available
            // Don't penalize requests just because client-side data is missing
            if (result.Reasons.Count == 0)
                // No contribution - client-side detection is disabled or no data available
                return contributions;

            // Convert each reason to a contribution
            foreach (var reason in result.Reasons)
            {
                // Skip neutral reasons (ConfidenceImpact = 0)
                if (Math.Abs(reason.ConfidenceImpact) < 0.001) continue;

                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = reason.Category,
                    ConfidenceDelta = reason.ConfidenceImpact,
                    Weight = 1.8, // Client-side fingerprint is a very strong signal
                    Reason = reason.Detail,
                    BotType = result.BotType?.ToString(),
                    BotName = result.BotName,
                    Signals = ImmutableDictionary<string, object>.Empty
                        .Add(SignalKeys.FingerprintHeadlessScore, reason.Detail.Contains("Headless") ? 1.0 : 0.0)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClientSide detection failed");
        }

        return contributions;
    }
}