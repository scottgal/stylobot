using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that analyses per-signature request
///     history from <see cref="WaveformHistoryStore"/> for timing patterns,
///     path traversal shape, request-transition Markov chain, request-rate
///     bursts (content-class aware), session behaviour, and client-side
///     interaction telemetry. Priority 3 -- runs early so downstream atoms
///     see waveform.* hints.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>BehavioralWaveformContributor</c>. Inline sequence-guard port
///         + RequiredSignals [<see cref="SignalKeys.UserAgent"/>].
///     </para>
///     <para>
///         Cross-request request-history stays on
///         <see cref="WaveformHistoryStore"/> (write-behind LFU + SQLite
///         durability). Per-request derived scalars replay to the sink as
///         Model-2 hints (rates, ratios, transition probabilities, burst
///         sizes); the timestamped RequestSnapshot list stays on the store.
///     </para>
///     <para>
///         Content-class classification is preserved verbatim (Sec-Fetch-Dest
///         first, then Upgrade / Accept header sniff, then path-extension /
///         SignalR heuristics).
///     </para>
/// </remarks>
public sealed partial class BehavioralWaveformAtom : DetectorAtomBase
{
    private const int SequenceMinPosition = 3;

    private readonly WaveformHistoryStore _store;
    private readonly ILogger<BehavioralWaveformAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public BehavioralWaveformAtom(
        ILogger<BehavioralWaveformAtom> logger,
        WaveformHistoryStore store,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "BehavioralWaveform", category: "Waveform")
    {
        _logger = logger;
        _store = store;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 3;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.UserAgent };

    private double GetParam(string name, double fallback) => _configProvider.GetParameter(Name, name, fallback);
    private int GetParam(string name, int fallback) => _configProvider.GetParameter(Name, name, fallback);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        if (!ShouldRunUnderSequenceGuard(sink))
            return Task.FromResult(None());

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var contributions = new List<DetectionContribution>();

        try
        {
            var signature = sink.ReadHint(SignalKeys.PrimarySignature) ?? GetClientSignature(context, sink);

            var currentRequest = new RequestSnapshot
            {
                Timestamp = DateTimeOffset.UtcNow,
                Path = context.Request.Path.ToString(),
                Method = context.Request.Method,
                StatusCode = context.Response.StatusCode,
                UserAgent = context.Request.Headers.UserAgent.ToString(),
                RefererHash = GetRefererHash(context.Request.Headers.Referer.ToString()),
                ContentClass = ClassifyRequest(context)
            };

            var historyValue = _store.Record(signature, currentRequest);
            var history = historyValue.Snapshots;

            AnalyzeTimingPatterns(sink, sessionId, history, contributions);
            AnalyzePathPatterns(sink, sessionId, history, contributions);
            AnalyzeRequestTransitions(sink, sessionId, history, contributions);
            AnalyzeRequestRate(sink, sessionId, history, contributions);
            AnalyzeSessionBehavior(sink, sessionId, history, contributions);
            AnalyzeInteractionPatterns(sink, sessionId, contributions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in behavioral waveform analysis");
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "Behavioral waveform analysis complete (insufficient history)"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private static bool ShouldRunUnderSequenceGuard(SignalSink sink)
    {
        var positionHint = sink.ReadHint(SignalKeys.SequencePosition);
        if (positionHint is null) return true;
        if (!sink.ReadBoolHint(SignalKeys.SequenceOnTrack, fallback: true)) return true;
        if (sink.ReadBoolHint(SignalKeys.SequenceDiverged)) return true;
        return int.TryParse(positionHint, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos)
               && pos >= SequenceMinPosition;
    }

    private void AnalyzeTimingPatterns(SignalSink sink, string sessionId, IReadOnlyList<RequestSnapshot> history, List<DetectionContribution> contributions)
    {
        if (history.Count < 3) return;

        var intervals = new List<double>();
        for (var i = 1; i < history.Count; i++)
            intervals.Add((history[i].Timestamp - history[i - 1].Timestamp).TotalSeconds);
        if (intervals.Count == 0) return;

        var mean = intervals.Average();
        var variance = intervals.Sum(x => Math.Pow(x - mean, 2)) / intervals.Count;
        var stdDev = Math.Sqrt(variance);
        var cv = mean > 0 ? stdDev / mean : 0;

        sink.Raise($"waveform.interval_mean:{mean.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"waveform.interval_stddev:{stdDev.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.WaveformTimingRegularity}:{cv.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        var sequenceOnTrack = sink.ReadBoolHint(SignalKeys.SequenceOnTrack);
        var sequenceDiverged = sink.ReadBoolHint(SignalKeys.SequenceDiverged);
        var centroidStale = sink.ReadBoolHint(SignalKeys.SequenceCentroidStale);

        if (cv < GetParam("timing_cv_too_regular", 0.15) && intervals.Count >= GetParam("timing_min_intervals", 5) && !sequenceOnTrack)
        {
            var confidence = sequenceDiverged
                ? GetParam("timing_regular_confidence", 0.7) * 1.2
                : GetParam("timing_regular_confidence", 0.7);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = confidence,
                Weight = GetParam("timing_regular_weight", 1.6),
                Reason = sequenceDiverged
                    ? "Machine-speed timing confirmed by content-sequence divergence"
                    : "Requests arrive at almost identical intervals (typical automated behavior)",
                BotType = BotType.Scraper.ToString()
            });
        }
        else if (cv >= GetParam("timing_cv_human_low", 0.3) && cv <= GetParam("timing_cv_human_high", 2.0) && !sequenceDiverged)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("timing_human_confidence", -0.15),
                Weight = GetParam("timing_human_weight", 1.3),
                Reason = "Request timing shows natural human variation"
            });
        }

        var burstWindow = GetParam("burst_window_seconds", 10);
        var recentCutoff = DateTimeOffset.UtcNow.AddSeconds(-burstWindow);
        var recentNonStreaming = history.Count(r => r.Timestamp > recentCutoff
            && r.ContentClass is not (ContentClass.WebSocket or ContentClass.SSE or ContentClass.SignalR));
        var recentWs = history.Count(r => r.Timestamp > recentCutoff && r.ContentClass == ContentClass.WebSocket);
        var recentSse = history.Count(r => r.Timestamp > recentCutoff && r.ContentClass == ContentClass.SSE);
        var recentSignalR = history.Count(r => r.Timestamp > recentCutoff && r.ContentClass == ContentClass.SignalR);
        var recentRequests = recentNonStreaming;

        if (recentRequests >= GetParam("burst_threshold", 10) && !centroidStale)
        {
            sink.Raise($"{SignalKeys.WaveformBurstDetected}:true", sessionId);
            sink.Raise($"waveform.burst_size:{recentRequests}", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("burst_confidence", 0.65),
                Weight = GetParam("burst_weight", 1.5),
                Reason = $"Burst pattern detected: {recentRequests} requests in {burstWindow} seconds",
                BotType = BotType.Scraper.ToString()
            });
        }

        if (recentWs >= GetParam("ws_burst_threshold", 20))
        {
            sink.Raise($"{SignalKeys.WaveformBurstDetected}:true", sessionId);
            sink.Raise($"waveform.ws_burst_size:{recentWs}", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("ws_burst_confidence", 0.7),
                Weight = GetParam("ws_burst_weight", 1.6),
                Reason = $"WebSocket connection flood: {recentWs} upgrade requests in {burstWindow} seconds",
                BotType = BotType.MaliciousBot.ToString()
            });
        }

        if (recentSse >= GetParam("sse_burst_threshold", 30))
        {
            sink.Raise($"{SignalKeys.WaveformBurstDetected}:true", sessionId);
            sink.Raise($"waveform.sse_burst_size:{recentSse}", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("sse_burst_confidence", 0.6),
                Weight = GetParam("sse_burst_weight", 1.5),
                Reason = $"SSE reconnect storm: {recentSse} event-stream requests in {burstWindow} seconds",
                BotType = BotType.MaliciousBot.ToString()
            });
        }

        if (recentSignalR >= GetParam("signalr_burst_threshold", 40))
        {
            sink.Raise($"{SignalKeys.WaveformBurstDetected}:true", sessionId);
            sink.Raise($"waveform.signalr_burst_size:{recentSignalR}", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("signalr_burst_confidence", 0.55),
                Weight = GetParam("signalr_burst_weight", 1.4),
                Reason = $"SignalR connection flood: {recentSignalR} requests in {burstWindow} seconds",
                BotType = BotType.MaliciousBot.ToString()
            });
        }
    }

    private void AnalyzePathPatterns(SignalSink sink, string sessionId, IReadOnlyList<RequestSnapshot> history, List<DetectionContribution> contributions)
    {
        if (history.Count < 5) return;

        var recent = history.TakeLast(20).ToList();
        var recentNonWs = recent.Where(r => r.ContentClass is not (ContentClass.WebSocket or ContentClass.SSE or ContentClass.SignalR)).ToList();
        var recentWsPaths = recent.Where(r => r.ContentClass == ContentClass.WebSocket).Select(r => r.Path).Distinct().ToList();

        if (recentWsPaths.Count >= GetParam("ws_probe_min_paths", 3))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("ws_probe_confidence", 0.5),
                Weight = GetParam("ws_probe_weight", 1.3),
                Reason = $"WebSocket upgrades to {recentWsPaths.Count} distinct endpoints (hub probing)",
                BotType = BotType.Scraper.ToString()
            });
        }

        if (recentNonWs.Count < 5) return;
        var recentPaths = recentNonWs.Select(r => r.Path).ToList();
        var uniquePaths = recentPaths.Distinct().Count();
        var pathDiversity = (double)uniquePaths / recentPaths.Count;

        sink.Raise($"{SignalKeys.WaveformPathDiversity}:{pathDiversity.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        if (pathDiversity < GetParam("path_diversity_threshold", 0.3) && recentPaths.Count >= GetParam("path_diversity_min_requests", 10))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("path_low_diversity_confidence", 0.3),
                Weight = GetParam("path_low_diversity_weight", 1.2),
                Reason = $"Only visiting {uniquePaths} unique pages out of {recentPaths.Count} requests (possible automated scanning)",
                BotType = BotType.Unknown.ToString()
            });
        }

        if (DetectSequentialPattern(recentPaths))
        {
            sink.Raise("waveform.sequential_pattern:true", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("path_sequential_confidence", 0.6),
                Weight = GetParam("path_sequential_weight", 1.4),
                Reason = "Sequential path traversal detected (systematic crawling pattern)",
                BotType = BotType.Scraper.ToString()
            });
        }

        var traversalPattern = AnalyzeTraversalPattern(recentPaths);
        sink.Raise($"waveform.traversal_pattern:{traversalPattern}", sessionId);

        if (traversalPattern == "depth-first-strict")
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("path_depth_first_confidence", 0.25),
                Weight = GetParam("path_depth_first_weight", 1.1),
                Reason = "Strict depth-first traversal (common for crawlers)",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeRequestRate(SignalSink sink, string sessionId, IReadOnlyList<RequestSnapshot> history, List<DetectionContribution> contributions)
    {
        if (history.Count < 2) return;
        var nonStreamingHistory = history.Where(r => r.ContentClass is not (ContentClass.WebSocket or ContentClass.SSE or ContentClass.SignalR)).ToList();
        if (nonStreamingHistory.Count < 2) return;

        var timeSpan = (nonStreamingHistory[^1].Timestamp - nonStreamingHistory[0].Timestamp).TotalMinutes;
        if (timeSpan <= 0) return;

        var totalRate = nonStreamingHistory.Count / timeSpan;
        sink.Raise($"waveform.request_rate:{totalRate.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        var pageRequests = nonStreamingHistory.Count(r => r.ContentClass == ContentClass.Page);
        var assetRequests = nonStreamingHistory.Count(r => r.ContentClass == ContentClass.Asset);
        var pageRate = timeSpan > 0 ? pageRequests / timeSpan : 0;
        sink.Raise($"waveform.page_rate:{pageRate.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        var hasAssetTraffic = assetRequests > pageRequests * 2;
        var effectiveRate = hasAssetTraffic ? pageRate : totalRate;
        var rateLabel = hasAssetTraffic ? "page navigation" : "request";

        if (effectiveRate > GetParam("rate_very_high_threshold", 30.0))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("rate_very_high_confidence", 0.75),
                Weight = GetParam("rate_very_high_weight", 1.7),
                Reason = $"High {rateLabel} rate: {effectiveRate:F1}/min (total: {totalRate:F1}/min)",
                BotType = BotType.Scraper.ToString()
            });
        }
        else if (effectiveRate > GetParam("rate_elevated_threshold", 10.0))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("rate_elevated_confidence", 0.3),
                Weight = GetParam("rate_elevated_weight", 1.3),
                Reason = $"Elevated request rate: {effectiveRate:F0} page requests per minute",
                BotType = BotType.Unknown.ToString()
            });
        }
        else if (totalRate > GetParam("rate_very_high_threshold", 30.0) && hasAssetTraffic && pageRate <= GetParam("rate_multiplex_max_page_rate", 10.0))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("rate_multiplex_human_confidence", -0.15),
                Weight = GetParam("rate_multiplex_weight", 1.2),
                Reason = $"Normal browser multiplexing: high total traffic but only {pageRate:F0} page visits per minute ({assetRequests} sub-resources loaded)"
            });
        }

        var wsRequests = history.Count(r => r.ContentClass == ContentClass.WebSocket);
        if (wsRequests > 0)
        {
            var wsRate = wsRequests / timeSpan;
            sink.Raise($"waveform.ws_rate:{wsRate.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

            if (wsRate > GetParam("ws_rate_threshold", 15.0))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = GetParam("ws_rate_confidence", 0.6),
                    Weight = GetParam("ws_rate_weight", 1.4),
                    Reason = $"Excessive WebSocket upgrade rate: {wsRate:F0}/min ({wsRequests} upgrades)",
                    BotType = BotType.MaliciousBot.ToString()
                });
            }
        }
    }

    private void AnalyzeSessionBehavior(SignalSink sink, string sessionId, IReadOnlyList<RequestSnapshot> history, List<DetectionContribution> contributions)
    {
        var userAgents = history.Select(r => r.UserAgent).Distinct().Count();
        sink.Raise($"waveform.user_agent_changes:{userAgents}", sessionId);

        if (userAgents > 1 && history.Count >= GetParam("session_ua_change_min_requests", 5))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("session_ua_change_confidence", 0.8),
                Weight = GetParam("session_ua_change_weight", 1.8),
                Reason = $"User-Agent changed {userAgents} times in session (IP rotation or spoofing)",
                BotType = BotType.MaliciousBot.ToString()
            });
        }

        if (history.Count >= 2)
        {
            var sessionDuration = (history[^1].Timestamp - history[0].Timestamp).TotalMinutes;
            sink.Raise($"waveform.session_duration_minutes:{sessionDuration.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

            if (sessionDuration < GetParam("session_short_duration_minutes", 1.0) && history.Count >= GetParam("session_short_duration_min_requests", 10))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = GetParam("session_short_confidence", 0.7),
                    Weight = GetParam("session_short_weight", 1.6),
                    Reason = $"High-speed session: {history.Count} requests in {sessionDuration:F1} minutes",
                    BotType = BotType.Scraper.ToString()
                });
            }
        }
    }

    private void AnalyzeInteractionPatterns(SignalSink sink, string sessionId, List<DetectionContribution> contributions)
    {
        var mouseHint = sink.ReadHint(SignalKeys.ClientMouseEvents);
        if (int.TryParse(mouseHint, out var mouseCount))
        {
            sink.Raise($"waveform.mouse_events:{mouseCount}", sessionId);
            if (mouseCount == 0)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = GetParam("interaction_no_mouse_confidence", 0.4),
                    Weight = GetParam("interaction_no_mouse_weight", 1.5),
                    Reason = "No mouse movement detected (headless browser indicator)",
                    BotType = BotType.Unknown.ToString()
                });
            }
        }

        var keyboardHint = sink.ReadHint(SignalKeys.ClientKeyboardEvents);
        if (int.TryParse(keyboardHint, out var keyCount))
            sink.Raise($"waveform.keyboard_events:{keyCount}", sessionId);
    }

    private void AnalyzeRequestTransitions(SignalSink sink, string sessionId, IReadOnlyList<RequestSnapshot> history, List<DetectionContribution> contributions)
    {
        if (history.Count < 5) return;

        var classes = history.Select(r => r.ContentClass).ToList();
        var pageCt = classes.Count(c => c == ContentClass.Page);
        var assetCt = classes.Count(c => c == ContentClass.Asset);
        var apiCt = classes.Count(c => c == ContentClass.Api);
        var wsCt = classes.Count(c => c == ContentClass.WebSocket);
        var sseCt = classes.Count(c => c == ContentClass.SSE);
        var signalRCt = classes.Count(c => c == ContentClass.SignalR);
        var total = classes.Count;

        sink.Raise($"waveform.page_requests:{pageCt}", sessionId);
        sink.Raise($"waveform.asset_requests:{assetCt}", sessionId);
        sink.Raise($"waveform.api_requests:{apiCt}", sessionId);
        sink.Raise($"waveform.websocket_requests:{wsCt}", sessionId);
        sink.Raise($"waveform.sse_requests:{sseCt}", sessionId);
        sink.Raise($"waveform.signalr_requests:{signalRCt}", sessionId);

        var classCount = Enum.GetValues<ContentClass>().Length;
        var transitions = new int[classCount, classCount];
        var fromCounts = new int[classCount];
        for (var i = 1; i < classes.Count; i++)
        {
            var from = (int)classes[i - 1];
            var to = (int)classes[i];
            transitions[from, to]++;
            fromCounts[from]++;
        }

        if (pageCt >= 3 && fromCounts[(int)ContentClass.Page] > 0)
        {
            var pageToAsset = (double)transitions[(int)ContentClass.Page, (int)ContentClass.Asset] / fromCounts[(int)ContentClass.Page];
            var pageToPage = (double)transitions[(int)ContentClass.Page, (int)ContentClass.Page] / fromCounts[(int)ContentClass.Page];
            sink.Raise($"waveform.transition_page_to_asset:{pageToAsset.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
            sink.Raise($"waveform.transition_page_to_page:{pageToPage.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

            if (pageToPage > GetParam("transition_page_to_page_threshold", 0.7) && pageCt >= GetParam("transition_min_page_requests", 5))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = GetParam("transition_scraper_confidence", 0.6),
                    Weight = GetParam("transition_scraper_weight", 1.5),
                    Reason = "Pages requested without loading images, scripts, or stylesheets (scraper-like behavior)",
                    BotType = BotType.Scraper.ToString()
                });
            }
            else if (pageToAsset > GetParam("transition_normal_page_asset_ratio", 0.5) && assetCt > pageCt * 2)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = GetParam("transition_normal_confidence", -0.2),
                    Weight = GetParam("transition_normal_weight", 1.3),
                    Reason = $"Normal browsing pattern: page loads trigger {assetCt} sub-resource requests (images, scripts, stylesheets)"
                });
            }
        }

        if (apiCt > GetParam("transition_pure_api_min", 5) && pageCt == 0 && assetCt == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GetParam("transition_pure_api_confidence", 0.35),
                Weight = GetParam("transition_pure_api_weight", 1.4),
                Reason = $"Only accessing data endpoints ({apiCt} calls) without visiting any web pages",
                BotType = BotType.Unknown.ToString()
            });
        }

        if (total > 0)
        {
            var assetRatio = (double)assetCt / total;
            sink.Raise($"waveform.asset_ratio:{assetRatio.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        }
    }

    private static string GetClientSignature(HttpContext context, SignalSink sink)
    {
        var ip = sink.ReadHint(SignalKeys.ClientIp) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        return $"{ip}:{GetHash(ua)}";
    }

    /// <summary>
    ///     Update the most recent request's content class based on actual
    ///     response Content-Type. Called from middleware after the response
    ///     is generated; feeds actual response data back into the behavioural
    ///     model for more accurate Markov chain transitions.
    /// </summary>
    public void UpdateResponseContentType(string clientSignature, string? responseContentType)
    {
        if (string.IsNullOrEmpty(responseContentType)) return;
        var actualClass = ClassifyResponseContentType(responseContentType);
        _store.UpdateLastContentClass(clientSignature, actualClass);
    }

    private static ContentClass ClassifyResponseContentType(string contentType)
    {
        var ct = contentType.ToLowerInvariant();
        if (ct.StartsWith("text/html") || ct.StartsWith("application/xhtml")) return ContentClass.Page;
        if (ct.StartsWith("text/event-stream")) return ContentClass.SSE;
        if (ct.StartsWith("application/json") || ct.StartsWith("application/xml")
            || ct.StartsWith("text/xml") || ct.Contains("graphql"))
            return ContentClass.Api;
        return ContentClass.Asset;
    }

    private static bool DetectSequentialPattern(List<string> paths)
    {
        if (paths.Count < 3) return false;
        var numbers = paths.Select(p => NumberPattern().Match(p))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Value))
            .ToList();
        if (numbers.Count < 3) return false;
        for (var i = 1; i < numbers.Count; i++)
        {
            var diff = Math.Abs(numbers[i] - numbers[i - 1]);
            if (diff != 1) return false;
        }
        return true;
    }

    private static string AnalyzeTraversalPattern(List<string> paths)
    {
        if (paths.Count < 5) return "insufficient-data";
        var depths = paths.Select(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries).Length).ToList();
        var increasingRuns = 0;
        var strictDepthFirst = true;
        for (var i = 1; i < depths.Count; i++)
            if (depths[i] > depths[i - 1]) increasingRuns++;
            else if (depths[i] < depths[i - 1] - 1) strictDepthFirst = false;
        if (increasingRuns > paths.Count * 0.7) return strictDepthFirst ? "depth-first-strict" : "depth-first-loose";
        return "mixed";
    }

    private static string GetHash(string input)
    {
        if (input.Length == 0) return "empty";
        var bytes = Encoding.UTF8.GetBytes(input);
        return XxHash32.HashToUInt32(bytes).ToString("X8");
    }

    private static string GetRefererHash(string referer) => string.IsNullOrEmpty(referer) ? "none" : GetHash(referer);

    private static ContentClass ClassifyRequest(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("Upgrade", out var upgrade)
            && upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase))
            return ContentClass.WebSocket;

        if (httpContext.Request.Headers.TryGetValue("Accept", out var acceptHdr)
            && acceptHdr.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return ContentClass.SSE;

        var reqPath = httpContext.Request.Path.Value ?? string.Empty;
        var reqQuery = httpContext.Request.QueryString.Value ?? string.Empty;
        if ((reqPath.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase)
             && reqQuery.Contains("negotiateVersion", StringComparison.OrdinalIgnoreCase))
            || reqQuery.Contains("id=", StringComparison.OrdinalIgnoreCase))
            return ContentClass.SignalR;

        var fetchDest = httpContext.Request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fetchDest))
        {
            return fetchDest.ToLowerInvariant() switch
            {
                "document" or "iframe" => ContentClass.Page,
                "script" or "style" or "image" or "font" or "video" or "audio" or "manifest" or "worker" => ContentClass.Asset,
                "websocket" => ContentClass.WebSocket,
                "empty" => ContentClass.Api,
                _ => ClassifyByPathAndAccept(httpContext)
            };
        }
        return ClassifyByPathAndAccept(httpContext);
    }

    private static ContentClass ClassifyByPathAndAccept(HttpContext httpContext)
    {
        var path = httpContext.Request.Path.Value ?? "/";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".js" or ".css" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico"
            or ".woff" or ".woff2" or ".ttf" or ".eot" or ".map" or ".webp" or ".avif" or ".mp4" or ".webm")
            return ContentClass.Asset;
        if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase)
            || ext is ".json" or ".xml"
            || httpContext.Request.ContentType?.Contains("application/json") == true)
            return ContentClass.Api;
        var accept = httpContext.Request.Headers.Accept.FirstOrDefault() ?? string.Empty;
        if (accept.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase))
            return ContentClass.Page;
        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return ContentClass.Api;
        return string.IsNullOrEmpty(ext) ? ContentClass.Page : ContentClass.Asset;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();
}
