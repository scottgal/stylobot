using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Advanced behavioral analysis using statistical pattern detection.
///     Applies entropy analysis, Markov chains, and time-series anomaly detection.
///     Runs after basic behavioral detection to provide deeper insights.
/// </summary>
public class AdvancedBehavioralContributor : ContributingDetectorBase
{
    private readonly BehavioralPatternAnalyzer _analyzer;
    private readonly ILogger<AdvancedBehavioralContributor> _logger;
    private readonly BotDetectionOptions _options;

    public AdvancedBehavioralContributor(
        ILogger<AdvancedBehavioralContributor> logger,
        IMemoryCache cache,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _analyzer = new BehavioralPatternAnalyzer(
            cache,
            _options.Behavioral.AnalysisWindow,
            _options.Behavioral.IdentityHashSalt);
    }

    public override string Name => "AdvancedBehavioral";
    public override int Priority => 25; // Run after basic behavioral (priority 20)

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

            // Detect streaming transports — these need special handling to avoid false positives.
            // SignalR hub reconnections are normal (same URL, bursty), but we still want timing analysis
            // to catch machine-gun reconnects at exact intervals.
            var isWebSocket = context.Request.Headers.TryGetValue("Upgrade", out var upgradeHeader)
                              && upgradeHeader.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase);
            var isSse = context.Request.Headers.TryGetValue("Accept", out var acceptHeader)
                        && acceptHeader.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
            var query = context.Request.QueryString.Value ?? "";
            var path = context.Request.Path.Value ?? "";
            var isSignalR = (path.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase)
                             && query.Contains("negotiateVersion", StringComparison.OrdinalIgnoreCase))
                            || query.Contains("id=", StringComparison.OrdinalIgnoreCase);
            var isStreaming = isWebSocket || isSse || isSignalR;

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
                    if (pathEntropy > 3.5)
                    {
                        state.WriteSignals([new("PathEntropy", pathEntropy), new("PathEntropyHigh", true)]);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "AdvancedBehavioral",
                            ConfidenceDelta = 0.35,
                            Weight = 1.3,
                            Reason = "Visiting many random URLs in no logical order (random scanning pattern)"
                        });
                    }
                    else if (pathEntropy < 0.5)
                    {
                        state.WriteSignals([new("PathEntropy", pathEntropy), new("PathEntropyLow", true)]);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "AdvancedBehavioral",
                            ConfidenceDelta = 0.25,
                            Weight = 1.2,
                            Reason = "Repeatedly visiting the same few URLs (too repetitive for a real user)"
                        });
                    }
                }
            }

            // 2. Timing Entropy — still applies to streaming (machine-gun reconnects are suspicious)
            var timingEntropy = _analyzer.CalculateTimingEntropy(clientIp);
            if (timingEntropy > 0)
                // Very low timing entropy (<0.3) = too regular (bot)
                if (timingEntropy < 0.3)
                {
                    state.WriteSignals([new("TimingEntropy", timingEntropy), new("TimingTooRegular", true)]);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "AdvancedBehavioral",
                        ConfidenceDelta = 0.3,
                        Weight = 1.3,
                        Reason = "Requests arrive at suspiciously regular intervals (machine-like timing)"
                    });
                }

            // 3. Timing Anomaly Detection — still applies to streaming
            var (isAnomaly, zScore, anomalyDesc) = _analyzer.DetectTimingAnomaly(clientIp, currentTime);
            if (isAnomaly)
            {
                state.WriteSignals([new("TimingAnomalyZScore", zScore), new("TimingAnomalyDetected", true)]);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "AdvancedBehavioral",
                    ConfidenceDelta = 0.25,
                    Weight = 1.1,
                    Reason = anomalyDesc
                });
            }

            // 4. Regular Pattern Detection (Coefficient of Variation) — still applies to streaming
            var (isTooRegular, cv, cvDesc) = _analyzer.DetectRegularPattern(clientIp);
            if (isTooRegular)
            {
                state.WriteSignals([new("CoefficientOfVariation", cv), new("PatternTooRegular", true)]);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "AdvancedBehavioral",
                    ConfidenceDelta = 0.35,
                    Weight = 1.4,
                    Reason = cvDesc
                });
            }

            // 5. Navigation Pattern Analysis (Markov) — skip for streaming
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
                        Weight = 1.2,
                        Reason = navPattern
                    });
                }
            }

            // 6. Burst Detection — skip for streaming (SignalR reconnect storms are normal;
            // BehavioralWaveformContributor handles streaming-specific bursts with higher thresholds)
            if (!isStreaming)
            {
                var burstWindow = TimeSpan.FromSeconds(30);
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
                        ConfidenceDelta = 0.4,
                        Weight = 1.5,
                        Reason = $"Burst detected: {burstSize} requests in {burstDuration.TotalSeconds:F0} seconds"
                    });
                }
            }

            // 7. Positive signal: Good patterns detected
            if (contributions.Count == 0 && pathEntropy > 0.5 && pathEntropy < 3.0 && cv > 0.3)
            {
                state.WriteSignal("NaturalPatterns", true);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "AdvancedBehavioral",
                    ConfidenceDelta = -0.2,
                    Weight = 1.0,
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