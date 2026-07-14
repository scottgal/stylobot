using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) combining basic rate/pattern
///     detection via <see cref="BehavioralDetector"/> with statistical
///     entropy, timing anomaly, regular-pattern, navigation, and burst
///     analysis via <see cref="BehavioralPatternAnalyzer"/>.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>BehavioralContributor</c>. Priority 20 -- Wave 0.
///     </para>
///     <para>
///         Per-conversation history in <see cref="BehavioralPatternAnalyzer"/>
///         (backed by <see cref="IMemoryCache"/> keyed by PrimarySignature -- NOT the client IP,
///         which is shared behind an edge and would mix multiple clients' patterns). Sink
///         learns aggregate scalars only -- entropy scores, CV, burst
///         counts, timing z-scores; no raw path or timestamp series leaks.
///     </para>
///     <para>
///         Repetition (low path-entropy, A→A navigation, burst) is bot-evidence in a content-
///         browsing mode but the EXPECTED baseline in a streaming one. So it is neutralized when
///         this request is genuinely streaming (<c>TransportIsStreaming</c>) OR the conversation is
///         an established streaming one (<c>SessionEstablishedStreaming</c>, from
///         <see cref="SessionModeResolverAtom"/>) AND still mode-consistent (not scraping many
///         distinct paths). A flip to high path-entropy re-engages the penalties -- no latch.
///     </para>
/// </remarks>
public sealed class BehavioralAtom : DetectorAtomBase
{
    private readonly BehavioralPatternAnalyzer _analyzer;
    private readonly BehavioralDetector _detector;
    private readonly ILogger<BehavioralAtom> _logger;
    private readonly BotDetectionOptions _options;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public BehavioralAtom(
        ILogger<BehavioralAtom> logger,
        BehavioralDetector detector,
        IMemoryCache cache,
        IOptions<BotDetectionOptions> options,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "Behavioral", category: "Behavioral")
    {
        _logger = logger;
        _detector = detector;
        _options = options.Value;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _analyzer = new BehavioralPatternAnalyzer(
            cache,
            _options.Behavioral.AnalysisWindow,
            _options.Behavioral.IdentityHashSalt);
    }

    public override int Priority => 20;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double WeightBase => _configProvider.GetDefaults(Name).Weights.Base;
    private double BehavioralWeightMultiplier => _configProvider.GetParameter(Name, "behavioral_weight_multiplier", 1.5);
    private double PathEntropyHigh => _configProvider.GetParameter(Name, "path_entropy_high", 3.5);
    private double PathEntropyLow => _configProvider.GetParameter(Name, "path_entropy_low", 0.5);
    private double PathEntropyHighConfidence => _configProvider.GetParameter(Name, "path_entropy_high_confidence", 0.35);
    private double PathEntropyLowConfidence => _configProvider.GetParameter(Name, "path_entropy_low_confidence", 0.25);
    private double PathEntropyHighWeight => _configProvider.GetParameter(Name, "path_entropy_high_weight", 1.3);
    private double PathEntropyLowWeight => _configProvider.GetParameter(Name, "path_entropy_low_weight", 1.2);
    private double TimingEntropyLow => _configProvider.GetParameter(Name, "timing_entropy_low", 0.3);
    private double TimingEntropyConfidence => _configProvider.GetParameter(Name, "timing_entropy_confidence", 0.3);
    private double TimingEntropyWeight => _configProvider.GetParameter(Name, "timing_entropy_weight", 1.3);
    private double TimingAnomalyConfidence => _configProvider.GetParameter(Name, "timing_anomaly_confidence", 0.25);
    private double TimingAnomalyWeight => _configProvider.GetParameter(Name, "timing_anomaly_weight", 1.1);
    private double RegularPatternConfidence => _configProvider.GetParameter(Name, "regular_pattern_confidence", 0.35);
    private double RegularPatternWeight => _configProvider.GetParameter(Name, "regular_pattern_weight", 1.4);
    private double NavigationPatternWeight => _configProvider.GetParameter(Name, "navigation_pattern_weight", 1.2);
    private int BurstWindowSeconds => _configProvider.GetParameter(Name, "burst_window_seconds", 30);
    private double BurstConfidence => _configProvider.GetParameter(Name, "burst_confidence", 0.4);
    private double BurstWeight => _configProvider.GetParameter(Name, "burst_weight", 1.5);
    private double NaturalPatternsConfidence => _configProvider.GetParameter(Name, "natural_patterns_confidence", -0.2);
    private double NaturalPatternsWeight => _configProvider.GetParameter(Name, "natural_patterns_weight", 1.0);
    private double NaturalEntropyMin => _configProvider.GetParameter(Name, "natural_entropy_min", 0.5);
    private double NaturalEntropyMax => _configProvider.GetParameter(Name, "natural_entropy_max", 3.0);
    private double NaturalCvMin => _configProvider.GetParameter(Name, "natural_cv_min", 0.3);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var contributions = new List<DetectionContribution>(8);

        // Basic behavioral detector
        try
        {
            var result = await _detector.DetectAsync(context, ct).ConfigureAwait(false);

            if (result.Reasons.Count == 0)
            {
                sink.Raise($"{SignalKeys.BehavioralAnomalyDetected}:false", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = -0.15,
                    Weight = 1.0,
                    Reason = "Request patterns appear normal"
                });
            }
            else
            {
                var hasRate = false;
                foreach (var r in result.Reasons)
                {
                    if (r.Detail.Contains("rate", StringComparison.OrdinalIgnoreCase))
                    {
                        hasRate = true;
                        break;
                    }
                }
                sink.Raise($"{SignalKeys.BehavioralAnomalyDetected}:true", sessionId);
                sink.Raise($"{SignalKeys.BehavioralRateExceeded}:{(hasRate ? "true" : "false")}", sessionId);
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

        if (!_options.Behavioral.EnableAdvancedPatternDetection) return contributions;

        var clientIp = sink.ReadHint(SignalKeys.ClientIp) ?? GetClientIp(context);
        if (string.IsNullOrEmpty(clientIp)) return contributions;

        var currentPath = context.Request.Path.ToString();
        var currentTime = DateTime.UtcNow;

        try
        {
            // Behavioral state is keyed on PrimarySignature (a stable per-conversation identity),
            // NOT the client IP. Behind an edge/proxy the peer IP is shared across many clients, so an
            // IP-keyed analyzer mixes their path/timing/burst patterns under one bucket -- a
            // false-positive engine AND a latent cross-client taint. PrimarySignature is computed by
            // the Signature atom (priority 1), so it is available here at 20; fall back to clientIp
            // only if it is absent.
            var behaviorKey = sink.ReadHint(SignalKeys.PrimarySignature) ?? clientIp;

            _analyzer.RecordRequest(behaviorKey, currentPath, currentTime);

            var sequenceDiverged = sink.ReadBoolHint(SignalKeys.SequenceDiverged);
            var sequenceOnTrack = sink.ReadBoolHint(SignalKeys.SequenceOnTrack);
            var centroidStale = sink.ReadBoolHint(SignalKeys.SequenceCentroidStale);

            // This-request transport truth: a genuine WebSocket/SSE/SignalR request. Neutralizes ALL
            // repetition signals (repetition IS the interaction).
            var perRequestStreaming = sink.ReadBoolHint(SignalKeys.TransportIsStreaming);
            if (!perRequestStreaming)
            {
                var isWebSocket = context.Request.Headers.TryGetValue("Upgrade", out var upgradeHeader)
                                  && upgradeHeader.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase);
                var isSse = context.Request.Headers.TryGetValue("Accept", out var acceptHeader)
                            && acceptHeader.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
                var query = context.Request.QueryString.Value ?? string.Empty;
                var pathVal = context.Request.Path.Value ?? string.Empty;
                var isSignalR = (pathVal.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase)
                                 && query.Contains("negotiateVersion", StringComparison.OrdinalIgnoreCase))
                                || query.Contains("id=", StringComparison.OrdinalIgnoreCase);
                perRequestStreaming = isWebSocket || isSse || isSignalR;
            }

            // Cross-request inference (SessionModeResolverAtom): the conversation ESTABLISHED a
            // streaming mode. Repetition is then the expected baseline -- but only while the current
            // pattern stays consistent with streaming. A flip to content-scraping (high path-entropy
            // across many distinct paths) is mode INCONSISTENCY: deference is withdrawn and the
            // repetition signals re-engage (inconsistency raises, never lowers), so a scraper cannot
            // latch "streaming" with one negotiate and then scrape under the umbrella.
            var sessionStreaming = sink.ReadBoolHint(SignalKeys.SessionEstablishedStreaming);

            // 1. Path entropy. Real value unless this request is genuinely streaming (then 1.0).
            double pathEntropy = perRequestStreaming ? 1.0 : _analyzer.CalculatePathEntropy(behaviorKey);
            var sessionStreamingConsistent = sessionStreaming && pathEntropy <= PathEntropyHigh;
            // Repetition is neutral within a genuinely-streaming interaction, or an established +
            // still-consistent streaming conversation.
            var streamingNeutral = perRequestStreaming || sessionStreamingConsistent;

            if (pathEntropy > 0)
            {
                if (!perRequestStreaming && pathEntropy > PathEntropyHigh && !sequenceOnTrack)
                {
                    // High path-entropy = random scanning. Fires even for an established-streaming
                    // session (that IS the mode inconsistency), never for a genuine streaming request
                    // (whose entropy is forced to 1.0, below the high threshold).
                    sink.Raise($"behavioral.path_entropy:{pathEntropy.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                    sink.Raise("behavioral.path_entropy_high:true", sessionId);
                    var confidence = sequenceDiverged ? PathEntropyHighConfidence * 1.3 : PathEntropyHighConfidence;
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = confidence,
                        Weight = PathEntropyHighWeight,
                        Reason = sequenceDiverged
                            ? "Random URL scanning confirmed by content-sequence divergence"
                            : "Visiting many random URLs in no logical order (random scanning pattern)"
                    });
                }
                else if (!streamingNeutral && pathEntropy < PathEntropyLow)
                {
                    // Low path-entropy = repetitive. Neutral within a streaming interaction (hub
                    // polling): suppressed when this request is streaming, or the conversation is
                    // established-streaming and still consistent.
                    sink.Raise($"behavioral.path_entropy:{pathEntropy.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                    sink.Raise("behavioral.path_entropy_low:true", sessionId);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = PathEntropyLowConfidence,
                        Weight = PathEntropyLowWeight,
                        Reason = "Repeatedly visiting the same few URLs (too repetitive for a real user)"
                    });
                }
            }

            // 2. Timing entropy
            var timingEntropy = _analyzer.CalculateTimingEntropy(behaviorKey);
            if (timingEntropy > 0 && timingEntropy < TimingEntropyLow && !sequenceOnTrack)
            {
                sink.Raise($"behavioral.timing_entropy:{timingEntropy.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                sink.Raise("behavioral.timing_too_regular:true", sessionId);
                var confidence = sequenceDiverged ? TimingEntropyConfidence * 1.3 : TimingEntropyConfidence;
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = confidence,
                    Weight = TimingEntropyWeight,
                    Reason = sequenceDiverged
                        ? "Machine-like timing confirmed by content-sequence divergence"
                        : "Requests arrive at suspiciously regular intervals (machine-like timing)"
                });
            }

            // 3. Timing anomaly
            var (isAnomaly, zScore, anomalyDesc) = _analyzer.DetectTimingAnomaly(behaviorKey, currentTime);
            if (isAnomaly)
            {
                sink.Raise($"behavioral.timing_anomaly_zscore:{zScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                sink.Raise("behavioral.timing_anomaly_detected:true", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = TimingAnomalyConfidence,
                    Weight = TimingAnomalyWeight,
                    Reason = anomalyDesc
                });
            }

            // 4. Regular pattern (CV)
            var (isTooRegular, cv, cvDesc) = _analyzer.DetectRegularPattern(behaviorKey);
            if (isTooRegular && !sequenceOnTrack && !centroidStale)
            {
                sink.Raise($"behavioral.cv:{cv.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                sink.Raise("behavioral.pattern_too_regular:true", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = RegularPatternConfidence,
                    Weight = RegularPatternWeight,
                    Reason = cvDesc
                });
            }

            // 5. Navigation pattern
            if (!streamingNeutral)
            {
                var (transitionScore, navPattern) = _analyzer.AnalyzeNavigationPattern(behaviorKey, currentPath);
                if (transitionScore > 0)
                {
                    sink.Raise($"behavioral.navigation_anomaly:{transitionScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                    sink.Raise("behavioral.navigation_pattern_unusual:true", sessionId);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = transitionScore,
                        Weight = NavigationPatternWeight,
                        Reason = navPattern
                    });
                }
            }

            // 6. Burst detection
            if (!streamingNeutral)
            {
                var burstWindow = TimeSpan.FromSeconds(BurstWindowSeconds);
                var (isBurst, burstSize, burstDuration) = _analyzer.DetectBurstPattern(behaviorKey, burstWindow);
                if (isBurst)
                {
                    sink.Raise("behavioral.burst_detected:true", sessionId);
                    sink.Raise($"behavioral.burst_size:{burstSize}", sessionId);
                    sink.Raise($"behavioral.burst_duration_seconds:{burstDuration.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}", sessionId);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = BurstConfidence,
                        Weight = BurstWeight,
                        Reason = $"Burst detected: {burstSize} requests in {burstDuration.TotalSeconds:F0} seconds"
                    });
                }
            }

            // 7. Positive human signal
            if (contributions.Count == 0
                && pathEntropy > NaturalEntropyMin && pathEntropy < NaturalEntropyMax
                && cv > NaturalCvMin && !sequenceDiverged)
            {
                sink.Raise("behavioral.natural_patterns:true", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
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

        return contributions;
    }

    private static string? GetClientIp(HttpContext context)
    {
        return context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
               ?? context.Connection.RemoteIpAddress?.ToString();
    }
}
