using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Behavioral waveform analysis contributor.
///     Analyzes request patterns over time to detect bot behavior patterns.
///     Best-in-breed approach:
///     - Request timing analysis (too regular = bot, too random = human)
///     - Path traversal patterns (depth-first vs breadth-first)
///     - Request rate analysis (bursts vs steady)
///     - Session behavior tracking (cookie consistency, session lifetime)
///     - Mouse/keyboard interaction patterns (from client-side signals)
///     This runs late and correlates signals across multiple requests from the same signature.
///     Configuration loaded from: behavioralwaveform.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:BehavioralWaveformContributor:*
/// </summary>
public partial class BehavioralWaveformContributor : ConfiguredContributorBase
{
    // Cache request history per signature for waveform analysis
    private const string CacheKeyPrefix = "waveform:";
    private const int MaxHistorySize = 100; // Keep last 100 requests per signature
    private static readonly TimeSpan HistoryExpiration = TimeSpan.FromMinutes(30);
    private readonly IMemoryCache _cache;
    private readonly ILogger<BehavioralWaveformContributor> _logger;
    // Per-signature locks to prevent concurrent List<T> mutation
    private readonly ConcurrentDictionary<string, object> _signatureLocks = new();

    public BehavioralWaveformContributor(
        ILogger<BehavioralWaveformContributor> logger,
        IMemoryCache cache,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "BehavioralWaveform";
    public override int Priority => Manifest?.Priority ?? 3;

    // Requires basic Wave 0 detection to have completed (UA signal is always present after Wave 0)
    // SequenceGuard: skip when on-track at positions 0-2 (not enough data for meaningful signal).
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.UserAgent),
        SequenceGuardTrigger.Default
    ];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            // Use the unified HMAC primary signature when available. Fall back only for
            // unusual custom pipelines that run waveform analysis before signatures.
            var signature = state.GetSignal<string>(SignalKeys.PrimarySignature) ?? GetClientSignature(state);

            // Lock per-signature to prevent concurrent List<T> mutation
            var signatureLock = _signatureLocks.GetOrAdd(signature, _ => new object());
            lock (signatureLock)
            {
                // Get request history for this signature
                var history = GetOrCreateHistory(signature);

                // Add current request to history
                var currentRequest = new RequestSnapshot
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Path = state.HttpContext.Request.Path.ToString(),
                    Method = state.HttpContext.Request.Method,
                    StatusCode = state.HttpContext.Response.StatusCode,
                    UserAgent = state.HttpContext.Request.Headers.UserAgent.ToString(),
                    RefererHash = GetRefererHash(state.HttpContext.Request.Headers.Referer.ToString()),
                    ContentClass = ClassifyRequest(state.HttpContext)
                };

                history.Add(currentRequest);

                // Analyze timing patterns
                AnalyzeTimingPatterns(state, history, contributions);

                // Analyze path traversal patterns
                AnalyzePathPatterns(state, history, contributions);

                // Analyze request transitions (Markov chain content class analysis)
                AnalyzeRequestTransitions(state, history, contributions);

                // Analyze request rate and bursts (content-class-aware)
                AnalyzeRequestRate(state, history, contributions);

                // Analyze session behavior
                AnalyzeSessionBehavior(state, history, contributions);

                // Update cache with new history
                UpdateHistory(signature, history);
            }

            // Analyze mouse/keyboard interaction signals (if available from client-side - no history needed)
            AnalyzeInteractionPatterns(state, contributions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in behavioral waveform analysis");
            state.WriteSignal("waveform.analysis_error", ex.Message);
        }

        // Always add at least one contribution
        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = 0.0,
                Weight = 1.0,
                Reason = "Behavioral waveform analysis complete (insufficient history)"
            });
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private void AnalyzeTimingPatterns(BlackboardState state, List<RequestSnapshot> history, List<DetectionContribution> contributions)
    {
        if (history.Count < 3) return; // Need at least 3 requests for timing analysis

        // Calculate intervals between requests
        var intervals = new List<double>();
        for (var i = 1; i < history.Count; i++)
        {
            var interval = (history[i].Timestamp - history[i - 1].Timestamp).TotalSeconds;
            intervals.Add(interval);
        }

        if (intervals.Count == 0) return;

        // Calculate standard deviation of intervals
        var mean = intervals.Average();
        var variance = intervals.Sum(x => Math.Pow(x - mean, 2)) / intervals.Count;
        var stdDev = Math.Sqrt(variance);

        // Coefficient of variation (CV = stddev / mean)
        var cv = mean > 0 ? stdDev / mean : 0;
        state.WriteSignals([
            new("waveform.interval_mean", mean),
            new("waveform.interval_stddev", stdDev),
            new(SignalKeys.WaveformTimingRegularity, cv)
        ]);

        // Read sequence signals - ContentSequenceContributor (Priority 4) runs before this via SequenceGuardTrigger.
        var sequenceOnTrack = state.GetSignal<bool?>(SignalKeys.SequenceOnTrack) ?? false;
        var sequenceDiverged = state.GetSignal<bool?>(SignalKeys.SequenceDiverged) ?? false;
        var centroidStale = state.GetSignal<bool?>(SignalKeys.SequenceCentroidStale) ?? false;

        // Very low CV = too regular = likely bot.
        // Suppress if sequence is on-track: API polling at fixed intervals after a valid page load is normal.
        if (cv < GetParam("timing_cv_too_regular", 0.15) && intervals.Count >= GetParam("timing_min_intervals", 5) && !sequenceOnTrack)
        {
            var confidence = sequenceDiverged
                ? GetParam("timing_regular_confidence", 0.7) * 1.2
                : GetParam("timing_regular_confidence", 0.7);
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", confidence,
                sequenceDiverged
                    ? "Machine-speed timing confirmed by content-sequence divergence"
                    : "Requests arrive at almost identical intervals (typical automated behavior)",
                weight: GetParam("timing_regular_weight", 1.6),
                botType: BotType.Scraper.ToString()));
        }
        // Moderate CV = human-like (suppress if sequence diverged - we know the pattern was wrong)
        else if (cv >= GetParam("timing_cv_human_low", 0.3) && cv <= GetParam("timing_cv_human_high", 2.0) && !sequenceDiverged)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = GetParam("timing_human_confidence", -0.15),
                Weight = GetParam("timing_human_weight", 1.3),
                Reason = "Request timing shows natural human variation"
            });

        // Check for burst patterns (many requests in short time)
        var burstWindow = GetParam("burst_window_seconds", 10);
        var recentCutoff = DateTimeOffset.UtcNow.AddSeconds(-burstWindow);
        var recentNonStreaming = history.Count(r => r.Timestamp > recentCutoff
            && r.ContentClass is not (ContentClass.WebSocket or ContentClass.SSE or ContentClass.SignalR));
        var recentWs = history.Count(r => r.Timestamp > recentCutoff && r.ContentClass == ContentClass.WebSocket);
        var recentSse = history.Count(r => r.Timestamp > recentCutoff && r.ContentClass == ContentClass.SSE);
        var recentSignalR = history.Count(r => r.Timestamp > recentCutoff && r.ContentClass == ContentClass.SignalR);
        var recentRequests = recentNonStreaming;
        // Suppress burst scoring when centroid is stale: a deploy causes all real users to reload
        // simultaneously, producing burst patterns that look like bot activity.
        if (recentRequests >= GetParam("burst_threshold", 10) && !centroidStale)
        {
            state.WriteSignals([
                new(SignalKeys.WaveformBurstDetected, true),
                new("waveform.burst_size", recentRequests)
            ]);

            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", GetParam("burst_confidence", 0.65),
                $"Burst pattern detected: {recentRequests} requests in {burstWindow} seconds",
                weight: GetParam("burst_weight", 1.5),
                botType: BotType.Scraper.ToString()));
        }

        // WebSocket-specific burst detection with a higher threshold
        // SignalR reconnects a few times on disconnect - that's normal.
        // But 20+ WebSocket upgrades in 10 seconds is abuse (connection flooding).
        if (recentWs >= GetParam("ws_burst_threshold", 20))
        {
            state.WriteSignals([
                new(SignalKeys.WaveformBurstDetected, true),
                new("waveform.ws_burst_size", recentWs)
            ]);

            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", GetParam("ws_burst_confidence", 0.7),
                $"WebSocket connection flood: {recentWs} upgrade requests in {burstWindow} seconds",
                weight: GetParam("ws_burst_weight", 1.6),
                botType: BotType.MaliciousBot.ToString()));
        }

        // SSE burst detection - reconnect storms from broken EventSource implementations
        // Normal SSE: 1-3 reconnects on disconnect. 30+ in 10s = reconnect loop.
        if (recentSse >= GetParam("sse_burst_threshold", 30))
        {
            state.WriteSignals([
                new(SignalKeys.WaveformBurstDetected, true),
                new("waveform.sse_burst_size", recentSse)
            ]);

            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", GetParam("sse_burst_confidence", 0.6),
                $"SSE reconnect storm: {recentSse} event-stream requests in {burstWindow} seconds",
                weight: GetParam("sse_burst_weight", 1.5),
                botType: BotType.MaliciousBot.ToString()));
        }

        // SignalR burst detection - long-polling is inherently high-frequency,
        // but 40+ in 10s still indicates abuse or a broken reconnect loop.
        if (recentSignalR >= GetParam("signalr_burst_threshold", 40))
        {
            state.WriteSignals([
                new(SignalKeys.WaveformBurstDetected, true),
                new("waveform.signalr_burst_size", recentSignalR)
            ]);

            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", GetParam("signalr_burst_confidence", 0.55),
                $"SignalR connection flood: {recentSignalR} requests in {burstWindow} seconds",
                weight: GetParam("signalr_burst_weight", 1.4),
                botType: BotType.MaliciousBot.ToString()));
        }
    }

    private void AnalyzePathPatterns(BlackboardState state, List<RequestSnapshot> history, List<DetectionContribution> contributions)
    {
        if (history.Count < 5) return;

        // Separate streaming from non-streaming for path analysis.
        // Hub reconnections to the same URL are normal and shouldn't reduce path diversity,
        // but WebSocket upgrades to many different paths is suspicious (probing).
        var recent = history.TakeLast(20).ToList();
        var recentNonWs = recent.Where(r => r.ContentClass is not (ContentClass.WebSocket or ContentClass.SSE or ContentClass.SignalR)).ToList();
        var recentWsPaths = recent.Where(r => r.ContentClass == ContentClass.WebSocket)
            .Select(r => r.Path).Distinct().ToList();

        // Bot signal: WebSocket upgrades to many distinct endpoints (probing for hubs)
        if (recentWsPaths.Count >= GetParam("ws_probe_min_paths", 3))
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", GetParam("ws_probe_confidence", 0.5),
                $"WebSocket upgrades to {recentWsPaths.Count} distinct endpoints (hub probing)",
                weight: GetParam("ws_probe_weight", 1.3),
                botType: BotType.Scraper.ToString()));

        if (recentNonWs.Count < 5) return;
        var recentPaths = recentNonWs.Select(r => r.Path).ToList();

        // Calculate path diversity
        var uniquePaths = recentPaths.Distinct().Count();
        var pathDiversity = (double)uniquePaths / recentPaths.Count;
        state.WriteSignal(SignalKeys.WaveformPathDiversity, pathDiversity);

        // Very low diversity = scanning/crawling same paths
        if (pathDiversity < GetParam("path_diversity_threshold", 0.3) && recentPaths.Count >= GetParam("path_diversity_min_requests", 10))
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = GetParam("path_low_diversity_confidence", 0.3),
                Weight = GetParam("path_low_diversity_weight", 1.2),
                Reason = $"Only visiting {uniquePaths} unique pages out of {recentPaths.Count} requests (possible automated scanning)"
            });

        // Detect sequential/systematic path traversal (e.g., /page/1, /page/2, /page/3)
        var sequentialPattern = DetectSequentialPattern(recentPaths);
        if (sequentialPattern)
        {
            state.WriteSignal("waveform.sequential_pattern", true);

            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", GetParam("path_sequential_confidence", 0.6),
                "Sequential path traversal detected (systematic crawling pattern)",
                weight: GetParam("path_sequential_weight", 1.4),
                botType: BotType.Scraper.ToString()));
        }

        // Depth-first vs breadth-first traversal analysis
        var traversalPattern = AnalyzeTraversalPattern(recentPaths);
        state.WriteSignal("waveform.traversal_pattern", traversalPattern);

        if (traversalPattern == "depth-first-strict")
            // Bots often do strict depth-first (go deep, then backtrack)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = GetParam("path_depth_first_confidence", 0.25),
                Weight = GetParam("path_depth_first_weight", 1.1),
                Reason = "Strict depth-first traversal (common for crawlers)"
            });
    }

    private void AnalyzeRequestRate(BlackboardState state, List<RequestSnapshot> history, List<DetectionContribution> contributions)
    {
        if (history.Count < 2) return;

        // Exclude streaming requests - they don't represent page navigations or API calls
        var nonStreamingHistory = history.Where(r => r.ContentClass is not (ContentClass.WebSocket or ContentClass.SSE or ContentClass.SignalR)).ToList();
        if (nonStreamingHistory.Count < 2) return;

        // Use non-streaming history boundaries for timespan so streaming-only periods don't inflate the window
        var timeSpan = (nonStreamingHistory[^1].Timestamp - nonStreamingHistory[0].Timestamp).TotalMinutes;
        if (timeSpan <= 0) return;

        var totalRate = nonStreamingHistory.Count / timeSpan; // total non-streaming requests per minute
        state.WriteSignal("waveform.request_rate", totalRate);

        // Calculate page-only rate (content-class aware for HTTP/2+)
        // HTTP/2+ multiplexes many asset requests per page load - that's normal.
        var pageRequests = nonStreamingHistory.Count(r => r.ContentClass == ContentClass.Page);
        var assetRequests = nonStreamingHistory.Count(r => r.ContentClass == ContentClass.Asset);
        var pageRate = timeSpan > 0 ? pageRequests / timeSpan : 0;

        state.WriteSignal("waveform.page_rate", pageRate);

        // Determine effective rate: if significant asset traffic exists, use page-only rate
        // (HTTP/2+ browsers load 5-15 assets per page navigation)
        var hasAssetTraffic = assetRequests > pageRequests * 2;
        var effectiveRate = hasAssetTraffic ? pageRate : totalRate;
        var rateLabel = hasAssetTraffic ? "page navigation" : "request";

        // Very high rate = likely bot
        if (effectiveRate > GetParam("rate_very_high_threshold", 30.0)) // 30+ page navigations/minute = definitely bot
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", GetParam("rate_very_high_confidence", 0.75),
                $"High {rateLabel} rate: {effectiveRate:F1}/min (total: {totalRate:F1}/min)",
                weight: GetParam("rate_very_high_weight", 1.7),
                botType: BotType.Scraper.ToString()));
        else if (effectiveRate > GetParam("rate_elevated_threshold", 10.0)) // 10-30 page navigations/minute = elevated
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = GetParam("rate_elevated_confidence", 0.3),
                Weight = GetParam("rate_elevated_weight", 1.3),
                Reason = $"Elevated request rate: {effectiveRate:F0} page requests per minute"
            });
        // High total rate but normal page rate = probably HTTP/2 multiplexing (human-like)
        else if (totalRate > GetParam("rate_very_high_threshold", 30.0) && hasAssetTraffic && pageRate <= GetParam("rate_multiplex_max_page_rate", 10.0))
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = GetParam("rate_multiplex_human_confidence", -0.15),
                Weight = GetParam("rate_multiplex_weight", 1.2),
                Reason = $"Normal browser multiplexing: high total traffic but only {pageRate:F0} page visits per minute ({assetRequests} sub-resources loaded)"
            });

        // Streaming-specific rate analysis
        // Normal SignalR: 1-3 upgrades/minute (initial + reconnects). Excessive = abuse.
        var wsRequests = history.Count(r => r.ContentClass == ContentClass.WebSocket);
        if (wsRequests > 0)
        {
            var wsRate = wsRequests / timeSpan;
            state.WriteSignal("waveform.ws_rate", wsRate);

            if (wsRate > GetParam("ws_rate_threshold", 15.0)) // 15+ WebSocket upgrades/minute = connection flooding
                contributions.Add(DetectionContribution.Bot(
                    Name, "Waveform", GetParam("ws_rate_confidence", 0.6),
                    $"Excessive WebSocket upgrade rate: {wsRate:F0}/min ({wsRequests} upgrades)",
                    weight: GetParam("ws_rate_weight", 1.4),
                    botType: BotType.MaliciousBot.ToString()));
        }
    }

    private void AnalyzeSessionBehavior(BlackboardState state, List<RequestSnapshot> history,
        List<DetectionContribution> contributions)
    {
        // Check if User-Agent changes across requests (suspicious)
        var userAgents = history.Select(r => r.UserAgent).Distinct().Count();
        state.WriteSignal("waveform.user_agent_changes", userAgents);

        if (userAgents > 1 && history.Count >= GetParam("session_ua_change_min_requests", 5))
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", GetParam("session_ua_change_confidence", 0.8),
                $"User-Agent changed {userAgents} times in session (IP rotation or spoofing)",
                weight: GetParam("session_ua_change_weight", 1.8),
                botType: BotType.MaliciousBot.ToString()));

        // Session duration analysis
        if (history.Count >= 2)
        {
            var sessionDuration = (history[^1].Timestamp - history[0].Timestamp).TotalMinutes;
            state.WriteSignal("waveform.session_duration_minutes", sessionDuration);

            // Very short session with many requests = bot
            if (sessionDuration < GetParam("session_short_duration_minutes", 1.0) && history.Count >= GetParam("session_short_duration_min_requests", 10))
                contributions.Add(DetectionContribution.Bot(
                    Name, "Waveform", GetParam("session_short_confidence", 0.7),
                    $"High-speed session: {history.Count} requests in {sessionDuration:F1} minutes",
                    weight: GetParam("session_short_weight", 1.6),
                    botType: BotType.Scraper.ToString()));
        }
    }

    private void AnalyzeInteractionPatterns(BlackboardState state, List<DetectionContribution> contributions)
    {
        // Check for client-side interaction signals (mouse movement, keyboard events)
        // These would be sent from JavaScript tracking

        if (state.Signals.TryGetValue(SignalKeys.ClientMouseEvents, out var mouseEvents) &&
            mouseEvents is int mouseCount)
        {
            state.WriteSignal("waveform.mouse_events", mouseCount);

            if (mouseCount == 0)
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Waveform",
                    ConfidenceDelta = GetParam("interaction_no_mouse_confidence", 0.4),
                    Weight = GetParam("interaction_no_mouse_weight", 1.5),
                    Reason = "No mouse movement detected (headless browser indicator)"
                });
        }

        if (state.Signals.TryGetValue(SignalKeys.ClientKeyboardEvents, out var keyboardEvents) &&
            keyboardEvents is int keyCount)
            state.WriteSignal("waveform.keyboard_events", keyCount);
    }

    private string GetClientSignature(BlackboardState state)
    {
        // Use resolved IP from IpContributor (handles X-Forwarded-For behind proxies)
        var ip = state.GetSignal<string>(SignalKeys.ClientIp)
                 ?? state.HttpContext.Connection.RemoteIpAddress?.ToString()
                 ?? "unknown";
        var ua = state.HttpContext.Request.Headers.UserAgent.ToString();
        return $"{ip}:{GetHash(ua)}";
    }

    /// <summary>
    ///     Update the most recent request's content class based on actual response Content-Type.
    ///     Called from middleware after the response is generated, feeding actual response data
    ///     back into the behavioral model for more accurate Markov chain transitions.
    /// </summary>
    public void UpdateResponseContentType(string clientSignature, string? responseContentType)
    {
        if (string.IsNullOrEmpty(responseContentType)) return;

        var signatureLock = _signatureLocks.GetOrAdd(clientSignature, _ => new object());
        lock (signatureLock)
        {
            var cacheKey = CacheKeyPrefix + clientSignature;
            if (!_cache.TryGetValue(cacheKey, out List<RequestSnapshot>? history) || history == null || history.Count == 0)
                return;

            var last = history[^1];
            var actualClass = ClassifyResponseContentType(responseContentType);
            if (actualClass != last.ContentClass)
            {
                history[^1] = new RequestSnapshot
                {
                    Timestamp = last.Timestamp,
                    Path = last.Path,
                    Method = last.Method,
                    StatusCode = last.StatusCode,
                    UserAgent = last.UserAgent,
                    RefererHash = last.RefererHash,
                    ContentClass = actualClass
                };
            }
        }
    }

    private static ContentClass ClassifyResponseContentType(string contentType)
    {
        var ct = contentType.ToLowerInvariant();
        if (ct.StartsWith("text/html") || ct.StartsWith("application/xhtml"))
            return ContentClass.Page;
        if (ct.StartsWith("text/event-stream"))
            return ContentClass.SSE;
        if (ct.StartsWith("application/json") || ct.StartsWith("application/xml") ||
            ct.StartsWith("text/xml") || ct.Contains("graphql"))
            return ContentClass.Api;
        // Everything else (JS, CSS, images, fonts, etc.)
        return ContentClass.Asset;
    }

    private List<RequestSnapshot> GetOrCreateHistory(string signature)
    {
        var cacheKey = CacheKeyPrefix + signature;
        return _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = HistoryExpiration;
            return new List<RequestSnapshot>();
        })!;
    }

    private void UpdateHistory(string signature, List<RequestSnapshot> history)
    {
        // Keep only last N requests
        while (history.Count > MaxHistorySize) history.RemoveAt(0);

        var cacheKey = CacheKeyPrefix + signature;
        _cache.Set(cacheKey, history, HistoryExpiration);
    }

    private bool DetectSequentialPattern(List<string> paths)
    {
        if (paths.Count < 3) return false;

        var numbers = paths.Select(p => NumberPattern().Match(p))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Value))
            .ToList();

        if (numbers.Count < 3) return false;

        // Check if numbers are sequential (difference of 1 or -1)
        for (var i = 1; i < numbers.Count; i++)
        {
            var diff = Math.Abs(numbers[i] - numbers[i - 1]);
            if (diff != 1) return false;
        }

        return true;
    }

    private string AnalyzeTraversalPattern(List<string> paths)
    {
        if (paths.Count < 5) return "insufficient-data";

        // Depth-first: paths get progressively deeper, then jump back
        // Example: /a, /a/b, /a/b/c, /x, /x/y
        var depths = paths.Select(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries).Length).ToList();

        var increasingRuns = 0;
        var strictDepthFirst = true;

        for (var i = 1; i < depths.Count; i++)
            if (depths[i] > depths[i - 1])
                increasingRuns++;
            else if (depths[i] < depths[i - 1] - 1) strictDepthFirst = false;

        if (increasingRuns > paths.Count * 0.7) return strictDepthFirst ? "depth-first-strict" : "depth-first-loose";

        return "mixed";
    }

    private static string GetHash(string input)
    {
        if (input.Length == 0) return "empty";
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        return System.IO.Hashing.XxHash32.HashToUInt32(bytes).ToString("X8");
    }

    private static string GetRefererHash(string referer)
    {
        return string.IsNullOrEmpty(referer) ? "none" : GetHash(referer);
    }

    /// <summary>
    ///     Markov chain request transition analysis.
    ///     Models the expected transition probabilities between request content classes.
    ///     Normal browsing: Page → Asset (high probability), Asset → Asset (medium), Asset → Page (low).
    ///     Scrapers: Page → Page (high), missing Page → Asset transitions.
    ///     API bots: Api → Api (high), no Page requests.
    /// </summary>
    private void AnalyzeRequestTransitions(BlackboardState state, List<RequestSnapshot> history, List<DetectionContribution> contributions)
    {
        if (history.Count < 5) return;

        // Count requests by content class
        var classes = history.Select(r => r.ContentClass).ToList();
        var pageCt = classes.Count(c => c == ContentClass.Page);
        var assetCt = classes.Count(c => c == ContentClass.Asset);
        var apiCt = classes.Count(c => c == ContentClass.Api);
        var wsCt = classes.Count(c => c == ContentClass.WebSocket);
        var sseCt = classes.Count(c => c == ContentClass.SSE);
        var signalRCt = classes.Count(c => c == ContentClass.SignalR);
        var total = classes.Count;

        state.WriteSignals([
            new("waveform.page_requests", pageCt),
            new("waveform.asset_requests", assetCt),
            new("waveform.api_requests", apiCt),
            new("waveform.websocket_requests", wsCt),
            new("waveform.sse_requests", sseCt),
            new("waveform.signalr_requests", signalRCt)
        ]);

        // Build transition matrix sized to match ContentClass enum
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

        // Calculate transition probabilities
        // Normal browser pattern: Page→Asset should be dominant (70-90% of Page transitions)
        if (pageCt >= 3 && fromCounts[(int)ContentClass.Page] > 0)
        {
            var pageToAsset = (double)transitions[(int)ContentClass.Page, (int)ContentClass.Asset]
                              / fromCounts[(int)ContentClass.Page];
            var pageToPage = (double)transitions[(int)ContentClass.Page, (int)ContentClass.Page]
                             / fromCounts[(int)ContentClass.Page];

            state.WriteSignals([
                new("waveform.transition_page_to_asset", pageToAsset),
                new("waveform.transition_page_to_page", pageToPage)
            ]);

            // High Page→Page ratio = scraper (doesn't load assets, just fetches HTML pages)
            if (pageToPage > GetParam("transition_page_to_page_threshold", 0.7) && pageCt >= GetParam("transition_min_page_requests", 5))
                contributions.Add(DetectionContribution.Bot(
                    Name, "Waveform", GetParam("transition_scraper_confidence", 0.6),
                    $"Pages requested without loading images, scripts, or stylesheets (scraper-like behavior)",
                    weight: GetParam("transition_scraper_weight", 1.5),
                    botType: BotType.Scraper.ToString()));
            // Normal Page→Asset ratio = human-like (browser loads sub-resources)
            else if (pageToAsset > GetParam("transition_normal_page_asset_ratio", 0.5) && assetCt > pageCt * 2)
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Waveform",
                    ConfidenceDelta = GetParam("transition_normal_confidence", -0.2),
                    Weight = GetParam("transition_normal_weight", 1.3),
                    Reason = $"Normal browsing pattern: page loads trigger {assetCt} sub-resource requests (images, scripts, stylesheets)"
                });
        }

        // Pure API access pattern (no page requests at all)
        if (apiCt > GetParam("transition_pure_api_min", 5) && pageCt == 0 && assetCt == 0)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = GetParam("transition_pure_api_confidence", 0.35),
                Weight = GetParam("transition_pure_api_weight", 1.4),
                Reason = $"Only accessing data endpoints ({apiCt} calls) without visiting any web pages"
            });

        // Asset-to-page ratio for rate adjustment
        // Store as signal so AnalyzeRequestRate can use adjusted thresholds
        if (total > 0)
        {
            var assetRatio = (double)assetCt / total;
            state.WriteSignal("waveform.asset_ratio", assetRatio);
        }
    }

    private class RequestSnapshot
    {
        public DateTimeOffset Timestamp { get; init; }
        public string Path { get; init; } = string.Empty;
        public string Method { get; init; } = string.Empty;
        public int StatusCode { get; init; }
        public string UserAgent { get; init; } = string.Empty;
        public string RefererHash { get; init; } = string.Empty;
        public ContentClass ContentClass { get; init; }
    }

    /// <summary>
    ///     Content class for request transition analysis.
    ///     Ordered as enum for use as transition matrix index.
    /// </summary>
    private enum ContentClass
    {
        Page = 0,      // text/html navigation requests
        Asset = 1,     // JS, CSS, images, fonts, etc.
        Api = 2,       // JSON/XML API endpoints
        WebSocket = 3, // WebSocket upgrade requests
        SSE = 4,       // Server-Sent Events (Accept: text/event-stream)
        SignalR = 5    // SignalR negotiate/connect/long-poll requests
    }

    /// <summary>
    ///     Classify a request by its content class using Sec-Fetch-Dest, Accept header, and path extension.
    /// </summary>
    private static ContentClass ClassifyRequest(HttpContext httpContext)
    {
        // Best signal: Sec-Fetch-Dest header (modern browsers)
        // Detect WebSocket upgrades via Upgrade header (works for all browsers)
        if (httpContext.Request.Headers.TryGetValue("Upgrade", out var upgrade)
            && upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase))
            return ContentClass.WebSocket;

        // Detect SSE via Accept header
        if (httpContext.Request.Headers.TryGetValue("Accept", out var acceptHdr)
            && acceptHdr.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return ContentClass.SSE;

        // Detect SignalR negotiate/connect requests (spec-based, generic)
        var reqPath = httpContext.Request.Path.Value ?? "";
        var reqQuery = httpContext.Request.QueryString.Value ?? "";
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
                "script" or "style" or "image" or "font" or "video" or "audio" or "manifest" or "worker"
                    => ContentClass.Asset,
                "websocket" => ContentClass.WebSocket,
                "empty" => ContentClass.Api, // fetch/XHR
                _ => ClassifyByPathAndAccept(httpContext)
            };
        }

        return ClassifyByPathAndAccept(httpContext);
    }

    private static ContentClass ClassifyByPathAndAccept(HttpContext httpContext)
    {
        var path = httpContext.Request.Path.Value ?? "/";
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        // Asset extensions
        if (ext is ".js" or ".css" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" or
            ".woff" or ".woff2" or ".ttf" or ".eot" or ".map" or ".webp" or ".avif" or ".mp4" or ".webm")
            return ContentClass.Asset;

        // API patterns
        if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase) ||
            ext is ".json" or ".xml" ||
            httpContext.Request.ContentType?.Contains("application/json") == true)
            return ContentClass.Api;

        // Accept header fallback
        var accept = httpContext.Request.Headers.Accept.FirstOrDefault() ?? "";
        if (accept.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
            accept.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase))
            return ContentClass.Page;

        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return ContentClass.Api;

        // Default: page if no extension, otherwise asset
        return string.IsNullOrEmpty(ext) ? ContentClass.Page : ContentClass.Asset;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();
}
