using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Client-side fingerprint analysis contributor - uses browser fingerprint data.
///     Runs in Wave 0 (no dependencies) when client-side detection is enabled.
///     Configuration loaded from: clientside.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:ClientSideContributor:*
/// </summary>
public class ClientSideContributor : ConfiguredContributorBase
{
    private readonly ClientSideDetector _detector;
    private readonly ILogger<ClientSideContributor> _logger;

    public ClientSideContributor(
        ILogger<ClientSideContributor> logger,
        ClientSideDetector detector,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _detector = detector;
    }

    public override string Name => "ClientSide";
    public override int Priority => Manifest?.Priority ?? 18;

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

                state.WriteSignal(SignalKeys.FingerprintHeadlessScore, reason.Detail.Contains("Headless") ? 1.0 : 0.0);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = reason.Category,
                    ConfidenceDelta = reason.ConfidenceImpact,
                    Weight = GetParam("fingerprint_weight", 1.8), // Client-side fingerprint is a very strong signal
                    Reason = reason.Detail,
                    BotType = result.BotType?.ToString(),
                    BotName = result.BotName
                });
            }

            // Write JS execution timing signals for cross-detector consumption
            if (state.HttpContext.Items["__mlbotd_fingerprint"] is BrowserFingerprintResult fp)
            {
                if (fp.LayoutTimeMs.HasValue)
                    state.WriteSignal(SignalKeys.JsLayoutTimeMs, fp.LayoutTimeMs.Value);
                if (fp.SetTimeoutDrift.HasValue)
                    state.WriteSignal(SignalKeys.JsSetTimeoutDrift, fp.SetTimeoutDrift.Value);
                if (fp.PerformanceResolution.HasValue)
                    state.WriteSignal(SignalKeys.JsPerformanceResolution, fp.PerformanceResolution.Value);
                if (fp.TimingAnomaly)
                    state.WriteSignal(SignalKeys.JsTimingAnomaly, true);

                // Write headless automation framework signal for cross-detector consumption
                if (!string.IsNullOrEmpty(fp.DetectedAutomation))
                    state.WriteSignal(SignalKeys.HeadlessFramework, fp.DetectedAutomation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClientSide detection failed");
        }

        return contributions;
    }
}