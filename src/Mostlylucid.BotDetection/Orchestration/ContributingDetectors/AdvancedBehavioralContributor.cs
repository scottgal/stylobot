using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Advanced behavioral analysis using statistical pattern detection.
///     Applies entropy analysis, Markov chains, and time-series anomaly detection.
///     Runs after basic behavioral detection to provide deeper insights.
///     Configuration loaded from: advancedbehavioral.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:AdvancedBehavioralContributor:*
/// </summary>
public class AdvancedBehavioralContributor : ConfiguredContributorBase
{
    private readonly BehavioralPatternAnalyzer _analyzer;
    private readonly ILogger<AdvancedBehavioralContributor> _logger;
    private readonly BotDetectionOptions _options;

    public AdvancedBehavioralContributor(
        ILogger<AdvancedBehavioralContributor> logger,
        IMemoryCache cache,
        IOptions<BotDetectionOptions> options,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _options = options.Value;
        _analyzer = new BehavioralPatternAnalyzer(
            cache,
            _options.Behavioral.AnalysisWindow,
            _options.Behavioral.IdentityHashSalt);
    }

    public override string Name => "AdvancedBehavioral";
    public override int Priority => Manifest?.Priority ?? 25;

    // Config-driven thresholds
    private double PathEntropyHigh => GetParam("path_entropy_high", 3.5);
    private double PathEntropyLow => GetParam("path_entropy_low", 0.5);
    private double PathEntropyHighConfidence => GetParam("path_entropy_high_confidence", 0.35);
    private double PathEntropyLowConfidence => GetParam("path_entropy_low_confidence", 0.25);
    private double PathEntropyHighWeight => GetParam("path_entropy_high_weight", 1.3);
    private double PathEntropyLowWeight => GetParam("path_entropy_low_weight", 1.2);
    private double TimingEntropyLow => GetParam("timing_entropy_low", 0.3);
    private double TimingEntropyConfidence => GetParam("timing_entropy_confidence", 0.3);
    private double TimingEntropyWeight => GetParam("timing_entropy_weight", 1.3);
    private double TimingAnomalyConfidence => GetParam("timing_anomaly_confidence", 0.25);
    private double TimingAnomalyWeight => GetParam("timing_anomaly_weight", 1.1);
    private double RegularPatternConfidence => GetParam("regular_pattern_confidence", 0.35);
    private double RegularPatternWeight => GetParam("regular_pattern_weight", 1.4);
    private double NavigationPatternWeight => GetParam("navigation_pattern_weight", 1.2);
    private int BurstWindowSeconds => GetParam("burst_window_seconds", 30);
    private double BurstConfidence => GetParam("burst_confidence", 0.4);
    private double BurstWeight => GetParam("burst_weight", 1.5);
    private double NaturalPatternsConfidence => GetParam("natural_patterns_confidence", -0.2);
    private double NaturalPatternsWeight => GetParam("natural_patterns_weight", 1.0);
    private double NaturalEntropyMin => GetParam("natural_entropy_min", 0.5);
    private double NaturalEntropyMax => GetParam("natural_entropy_max", 3.0);
    private double NaturalCvMin => GetParam("natural_cv_min", 0.3);

    // No triggers - runs in first wave alongside basic behavioral
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        // Skip if advanced pattern detection is disabled
        if (!_options.Behavioral.EnableAdvancedPatternDetection)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        var context = state.HttpContext;
        // Prefer resolved IP from IpContributor to handle proxy scenarios
        var clientIp = state.Signals.TryGetValue(SignalKeys.ClientIp, out var ipObj)
            ? ipObj?.ToString()
            : GetClientIp(context);
        if (string.IsNullOrEmpty(clientIp))
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        var currentPath = context.Request.Path.ToString();
        var currentTime = DateTime.UtcNow;

        try
        {
            // Record this request for pattern analysis
            _analyzer.RecordRequest(clientIp, currentPath, currentTime);

            // Read sequence signals from ContentSequenceContributor (Priority 4, runs before this at Priority 25).
            // sequenceDiverged = confirmed bot-like content pattern; sequenceOnTrack = confirmed human-like pattern.
            var sequenceDiverged = state.GetSignal<bool?>(SignalKeys.SequenceDiverged) ?? false;
            var sequenceOnTrack = state.GetSignal<bool?>(SignalKeys.SequenceOnTrack) ?? false;
            var centroidStale = state.GetSignal<bool?>(SignalKeys.SequenceCentroidStale) ?? false;

            // Read transport classification from TransportProtocolContributor (Priority 5, runs first).
            // Falls back to local header detection if the signal hasn't been written yet.
            var isStreaming = state.GetSignal<bool?>(SignalKeys.TransportIsStreaming) ?? false;
            if (!isStreaming)
            {
                // Fallback: TransportProtocolContributor may not have run yet in this wave
                var isWebSocket = context.Request.Headers.TryGetValue("Upgrade", out var upgradeHeader)
                                  && upgradeHeader.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase);
                var isSse = context.Request.Headers.TryGetValue("Accept", out var acceptHeader)
                            && acceptHeader.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
                var query = context.Request.QueryString.Value ?? "";
                var pathVal = context.Request.Path.Value ?? "";
                var isSignalR = (pathVal.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase)
                                 && query.Contains("negotiateVersion", StringComparison.OrdinalIgnoreCase))
                                || query.Contains("id=", StringComparison.OrdinalIgnoreCase);
                isStreaming = isWebSocket || isSse || isSignalR;
            }

            // Only analyze if we have enough data
            var minRequests = _options.Behavioral.MinRequestsForPatternAnalysis;

            // 1. Entropy Analysis - Path entropy
            // Skip for streaming: hub reconnections to the same URL produce low entropy by design
            double pathEntropy = isStreaming ? 1.0 : _analyzer.CalculatePathEntropy(clientIp);
            if (!isStreaming)
            {
                if (pathEntropy > 0)
                {
                    // Very high entropy (>3.5) = random scanning (bot)
                    // Very low entropy (<0.5) = too repetitive (bot)
                    if (pathEntropy > PathEntropyHigh)
                    {
                        // Suppress if sequence is on-track: high-entropy navigation after a valid page-load
                        // sequence is legitimate user exploration (e.g., browsing many product pages).
                        if (!sequenceOnTrack)
                        {
                            state.WriteSignals([new("PathEntropy", pathEntropy), new("PathEntropyHigh", true)]);
                            // Boost confidence when sequence divergence independently confirmed scanning.
                            var confidence = sequenceDiverged
                                ? PathEntropyHighConfidence * 1.3
                                : PathEntropyHighConfidence;
                            contributions.Add(new DetectionContribution
                            {
                                DetectorName = Name,
                                Category = "AdvancedBehavioral",
                                ConfidenceDelta = confidence,
                                Weight = PathEntropyHighWeight,
                                Reason = sequenceDiverged
                                    ? "Random URL scanning confirmed by content-sequence divergence"
                                    : "Visiting many random URLs in no logical order (random scanning pattern)"
                            });
                        }
                    }
                    else if (pathEntropy < PathEntropyLow)
                    {
                        state.WriteSignals([new("PathEntropy", pathEntropy), new("PathEntropyLow", true)]);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "AdvancedBehavioral",
                            ConfidenceDelta = PathEntropyLowConfidence,
                            Weight = PathEntropyLowWeight,
                            Reason = "Repeatedly visiting the same few URLs (too repetitive for a real user)"
                        });
                    }
                }
            }

            // 2. Timing Entropy - still applies to streaming (machine-gun reconnects are suspicious)
            var timingEntropy = _analyzer.CalculateTimingEntropy(clientIp);
            if (timingEntropy > 0)
                // Very low timing entropy (<0.3) = too regular (bot)
                if (timingEntropy < TimingEntropyLow)
                {
                    // Suppress if sequence is on-track: regular-interval API polling after a valid page
                    // load is normal (notification checks, heartbeats).
                    if (!sequenceOnTrack)
                    {
                        state.WriteSignals([new("TimingEntropy", timingEntropy), new("TimingTooRegular", true)]);
                        // Boost when sequence divergence confirms machine-speed pattern.
                        var confidence = sequenceDiverged
                            ? TimingEntropyConfidence * 1.3
                            : TimingEntropyConfidence;
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "AdvancedBehavioral",
                            ConfidenceDelta = confidence,
                            Weight = TimingEntropyWeight,
                            Reason = sequenceDiverged
                                ? "Machine-like timing confirmed by content-sequence divergence"
                                : "Requests arrive at suspiciously regular intervals (machine-like timing)"
                        });
                    }
                }

            // 3. Timing Anomaly Detection - still applies to streaming
            var (isAnomaly, zScore, anomalyDesc) = _analyzer.DetectTimingAnomaly(clientIp, currentTime);
            if (isAnomaly)
            {
                state.WriteSignals([new("TimingAnomalyZScore", zScore), new("TimingAnomalyDetected", true)]);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "AdvancedBehavioral",
                    ConfidenceDelta = TimingAnomalyConfidence,
                    Weight = TimingAnomalyWeight,
                    Reason = anomalyDesc
                });
            }

            // 4. Regular Pattern Detection (Coefficient of Variation) - still applies to streaming
            var (isTooRegular, cv, cvDesc) = _analyzer.DetectRegularPattern(clientIp);
            if (isTooRegular)
            {
                // Suppress if on-track: regular polling after a valid page-load sequence is normal.
                // Centroid stale also suppresses: deploy traffic causes uniform timing patterns.
                if (!sequenceOnTrack && !centroidStale)
                {
                    state.WriteSignals([new("CoefficientOfVariation", cv), new("PatternTooRegular", true)]);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "AdvancedBehavioral",
                        ConfidenceDelta = RegularPatternConfidence,
                        Weight = RegularPatternWeight,
                        Reason = cvDesc
                    });
                }
            }

            // 5. Navigation Pattern Analysis (Markov) - skip for streaming
            if (!isStreaming)
            {
                var (transitionScore, navPattern) = _analyzer.AnalyzeNavigationPattern(clientIp, currentPath);
                if (transitionScore > 0)
                {
                    state.WriteSignals([new("NavigationAnomalyScore", transitionScore), new("NavigationPatternUnusual", true)]);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "AdvancedBehavioral",
                        ConfidenceDelta = transitionScore,
                        Weight = NavigationPatternWeight,
                        Reason = navPattern
                    });
                }
            }

            // 6. Burst Detection - skip for streaming (SignalR reconnect storms are normal;
            // BehavioralWaveformContributor handles streaming-specific bursts with higher thresholds)
            if (!isStreaming)
            {
                var burstWindow = TimeSpan.FromSeconds(BurstWindowSeconds);
                var (isBurst, burstSize, burstDuration) = _analyzer.DetectBurstPattern(clientIp, burstWindow);
                if (isBurst)
                {
                    state.WriteSignals([
                        new("BurstDetected", true),
                        new("BurstSize", burstSize),
                        new("BurstDurationSeconds", burstDuration.TotalSeconds)
                    ]);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "AdvancedBehavioral",
                        ConfidenceDelta = BurstConfidence,
                        Weight = BurstWeight,
                        Reason = $"Burst detected: {burstSize} requests in {burstDuration.TotalSeconds:F0} seconds"
                    });
                }
            }

            // 7. Positive signal: Good patterns detected
            // Skip if sequence diverged - we know the content pattern was bot-like, don't emit human signal.
            if (contributions.Count == 0 && pathEntropy > NaturalEntropyMin && pathEntropy < NaturalEntropyMax && cv > NaturalCvMin && !sequenceDiverged)
            {
                state.WriteSignal("NaturalPatterns", true);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "AdvancedBehavioral",
                    ConfidenceDelta = NaturalPatternsConfidence,
                    Weight = NaturalPatternsWeight,
                    Reason = "Natural browsing patterns detected (entropy, timing variation)"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Advanced behavioral analysis failed for {ClientIp}", clientIp);
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private static string? GetClientIp(HttpContext context)
    {
        return context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
               ?? context.Connection.RemoteIpAddress?.ToString();
    }
}