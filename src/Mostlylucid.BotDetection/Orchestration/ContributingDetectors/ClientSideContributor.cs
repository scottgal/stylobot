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
    private readonly FingerprintPopulationTracker _population;

    public ClientSideContributor(
        ILogger<ClientSideContributor> logger,
        ClientSideDetector detector,
        FingerprintPopulationTracker population,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _detector = detector;
        _population = population;
    }

    public override string Name => "ClientSide";
    public override int Priority => Manifest?.Priority ?? 18;

    // TransportProtocol (5) and UserAgent (10) run before us, so their signals are already written.
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var result = await _detector.DetectAsync(state.HttpContext, cancellationToken);

            // Empty = disabled (ClientSideDetector returns empty when Enabled = false)
            if (result.Reasons.Count == 0)
                return contributions;

            // Fetch once; reused by both the no-fingerprint and fingerprint-found paths.
            var transportClass = state.GetSignal<string>(SignalKeys.TransportProtocolClass) ?? TransportClasses.Unknown;
            var uaFamily = state.GetSignal<string>(SignalKeys.UserAgentFamily) ?? TransportClasses.Unknown;

            var isNoFingerprint = result.Reasons.Count == 1
                                  && result.Reasons[0].Detail == ClientSideReasons.NoFingerprint;

            // Adblocker probe path: when the analyzer recognised an adblocker-only
            // beacon, the detector emits a single AdblockerDetected sentinel reason.
            // Read the suppression signal flag, write the blackboard signal so
            // downstream consumers (heuristic, archetype matcher) can see it, and
            // emit a small human-affinity contribution -- adblocker users are
            // overwhelmingly human. Returns early so the no-fingerprint penalty
            // does NOT fire on the same request. Magnitude is configurable.
            var isAdblockerOnly = result.Reasons.Count == 1
                                  && result.Reasons[0].Detail == ClientSideReasons.AdblockerDetected;
            if (isAdblockerOnly)
            {
                state.WriteSignal(SignalKeys.ClientSideAdblockerDetected, true);
                if (state.HttpContext.Items["__mlbotd_fingerprint"] is BrowserFingerprintResult fpAd
                    && !string.IsNullOrEmpty(fpAd.AdblockerProvider))
                {
                    state.WriteSignal(SignalKeys.ClientSideAdblockerProvider, fpAd.AdblockerProvider);
                }

                if (transportClass == TransportClasses.Document)
                    _population.Record(uaFamily, transportClass, hasFingerprint: true);

                var bias = GetParam("adblocker_human_bias", -0.05);
                if (Math.Abs(bias) >= 0.001)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "ClientSide",
                        ConfidenceDelta = bias,
                        Weight = GetParam("fingerprint_weight", 1.8),
                        Reason = "Adblocker probe blocked — human-affinity bias (adblocker users skew strongly human)"
                    });
                }
                return contributions;
            }

            if (isNoFingerprint)
            {
                // Only meaningful for document requests — API, static, and WebSocket never run the fingerprint script.
                if (transportClass == TransportClasses.Document)
                {
                    // Record population even when not penalizing so the rate stays accurate.
                    var (rate, samples) = _population.Record(uaFamily, transportClass, hasFingerprint: false);

                    if (GetParam("penalize_no_fingerprint", true))
                    {
                        var minSamples = GetParam("population_min_samples", 20);
                        var rateThreshold = GetParam("population_rate_threshold", 0.7);
                        var penaltyBase = GetParam("penalize_confidence", 0.15);

                        double bias;
                        string reason;

                        if (samples >= minSamples)
                        {
                            // Below threshold: this UA/context doesn't normally send fingerprints.
                            if (rate < rateThreshold)
                                return contributions;

                            bias = penaltyBase * rate;
                            reason = $"Document request without fingerprint; {rate:P0} of similar requests carry one ({samples} samples)";
                        }
                        else
                        {
                            // Insufficient population data — conservative half-penalty.
                            bias = penaltyBase * 0.5;
                            reason = $"Document request without fingerprint; population data insufficient ({samples} samples)";
                        }

                        state.WriteSignal(SignalKeys.ClientSideNoFingerprintBias, bias);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "ClientSide",
                            ConfidenceDelta = bias,
                            Weight = GetParam("fingerprint_weight", 1.8),
                            Reason = reason
                        });
                    }
                }

                return contributions;
            }

            // Fingerprint found: record population for document requests so no-fingerprint cases are calibrated.
            if (transportClass == TransportClasses.Document)
                _population.Record(uaFamily, transportClass, hasFingerprint: true);

            foreach (var reason in result.Reasons)
            {
                if (Math.Abs(reason.ConfidenceImpact) < 0.001) continue;

                state.WriteSignal(SignalKeys.FingerprintHeadlessScore, reason.Detail.Contains("Headless") ? 1.0 : 0.0);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = reason.Category,
                    ConfidenceDelta = reason.ConfidenceImpact,
                    Weight = GetParam("fingerprint_weight", 1.8),
                    Reason = reason.Detail,
                    BotType = result.BotType?.ToString(),
                    BotName = result.BotName
                });
            }

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
