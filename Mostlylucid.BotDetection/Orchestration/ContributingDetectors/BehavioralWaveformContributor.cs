using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
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
///     Raises signals:
///     - waveform.request_interval_stddev
///     - waveform.request_rate
///     - waveform.path_pattern
///     - waveform.timing_regularity_score
///     - waveform.burst_detection
/// </summary>
public partial class BehavioralWaveformContributor : ContributingDetectorBase
{
    // Cache request history per signature for waveform analysis
    private const string CacheKeyPrefix = "waveform:";
    private const int MaxHistorySize = 100; // Keep last 100 requests per signature
    private static readonly TimeSpan HistoryExpiration = TimeSpan.FromMinutes(30);
    private readonly IMemoryCache _cache;
    private readonly ILogger<BehavioralWaveformContributor> _logger;

    public BehavioralWaveformContributor(
        ILogger<BehavioralWaveformContributor> logger,
        IMemoryCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "BehavioralWaveform";
    public override int Priority => 3; // Run late, after individual detectors

    // Requires basic Wave 0 detection to have completed (UA signal is always present after Wave 0)
    public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
    {
        new SignalExistsTrigger(SignalKeys.UserAgent)
    };

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = ImmutableDictionary.CreateBuilder<string, object>();

        try
        {
            // Get or create signature for this client
            var signature = GetClientSignature(state);
            signals.Add(SignalKeys.WaveformSignature, signature);

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
            AnalyzeTimingPatterns(history, contributions, signals);

            // Analyze path traversal patterns
            AnalyzePathPatterns(history, contributions, signals);

            // Analyze request transitions (Markov chain content class analysis)
            AnalyzeRequestTransitions(history, contributions, signals);

            // Analyze request rate and bursts (content-class-aware)
            AnalyzeRequestRate(history, contributions, signals);

            // Analyze session behavior
            AnalyzeSessionBehavior(state, history, contributions, signals);

            // Analyze mouse/keyboard interaction signals (if available from client-side)
            AnalyzeInteractionPatterns(state, contributions, signals);

            // Update cache with new history
            UpdateHistory(signature, history);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in behavioral waveform analysis");
            signals.Add("waveform.analysis_error", ex.Message);
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
                Reason = "Behavioral waveform analysis complete (insufficient history)",
                Signals = signals.ToImmutable()
            });
        }
        else
        {
            // Ensure last contribution has all signals
            var last = contributions[^1];
            contributions[^1] = last with { Signals = signals.ToImmutable() };
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private void AnalyzeTimingPatterns(List<RequestSnapshot> history, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
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

        signals.Add("waveform.interval_mean", mean);
        signals.Add("waveform.interval_stddev", stdDev);

        // Coefficient of variation (CV = stddev / mean)
        var cv = mean > 0 ? stdDev / mean : 0;
        signals.Add(SignalKeys.WaveformTimingRegularity, cv);

        // Very low CV = too regular = likely bot
        if (cv < 0.15 && intervals.Count >= 5)
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.7,
                $"Requests arrive at almost identical intervals (typical automated behavior)",
                weight: 1.6,
                botType: BotType.Scraper.ToString()));
        // Moderate CV = human-like
        else if (cv >= 0.3 && cv <= 2.0)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = -0.15,
                Weight = 1.3,
                Reason = "Request timing shows natural human variation",
                Signals = signals.ToImmutable()
            });

        // Check for burst patterns (many requests in short time)
        var recentRequests = history.Where(r => r.Timestamp > DateTimeOffset.UtcNow.AddSeconds(-10)).Count();
        if (recentRequests >= 10)
        {
            signals.Add(SignalKeys.WaveformBurstDetected, true);
            signals.Add("waveform.burst_size", recentRequests);

            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.65,
                $"Burst pattern detected: {recentRequests} requests in 10 seconds",
                weight: 1.5,
                botType: BotType.Scraper.ToString()));
        }
    }

    private void AnalyzePathPatterns(List<RequestSnapshot> history, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        if (history.Count < 5) return;

        var recentPaths = history.TakeLast(20).Select(r => r.Path).ToList();

        // Calculate path diversity
        var uniquePaths = recentPaths.Distinct().Count();
        var pathDiversity = (double)uniquePaths / recentPaths.Count;
        signals.Add(SignalKeys.WaveformPathDiversity, pathDiversity);

        // Very low diversity = scanning/crawling same paths
        if (pathDiversity < 0.3 && recentPaths.Count >= 10)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = 0.3,
                Weight = 1.2,
                Reason = $"Only visiting {uniquePaths} unique pages out of {recentPaths.Count} requests (possible automated scanning)",
                Signals = signals.ToImmutable()
            });

        // Detect sequential/systematic path traversal (e.g., /page/1, /page/2, /page/3)
        var sequentialPattern = DetectSequentialPattern(recentPaths);
        if (sequentialPattern)
        {
            signals.Add("waveform.sequential_pattern", true);

            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.6,
                "Sequential path traversal detected (systematic crawling pattern)",
                weight: 1.4,
                botType: BotType.Scraper.ToString()));
        }

        // Depth-first vs breadth-first traversal analysis
        var traversalPattern = AnalyzeTraversalPattern(recentPaths);
        signals.Add("waveform.traversal_pattern", traversalPattern);

        if (traversalPattern == "depth-first-strict")
            // Bots often do strict depth-first (go deep, then backtrack)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = 0.25,
                Weight = 1.1,
                Reason = "Strict depth-first traversal (common for crawlers)",
                Signals = signals.ToImmutable()
            });
    }

    private void AnalyzeRequestRate(List<RequestSnapshot> history, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        if (history.Count < 2) return;

        var timeSpan = (history[^1].Timestamp - history[0].Timestamp).TotalMinutes;
        if (timeSpan <= 0) return;

        var totalRate = history.Count / timeSpan; // total requests per minute
        signals.Add("waveform.request_rate", totalRate);

        // Calculate page-only rate (content-class aware for HTTP/2+)
        // HTTP/2+ multiplexes many asset requests per page load - that's normal.
        // Only page navigation requests matter for "excessive rate" detection.
        var pageRequests = history.Count(r => r.ContentClass == ContentClass.Page);
        var assetRequests = history.Count(r => r.ContentClass == ContentClass.Asset);
        var pageRate = timeSpan > 0 ? pageRequests / timeSpan : 0;

        signals.Add("waveform.page_rate", pageRate);

        // Determine effective rate: if significant asset traffic exists, use page-only rate
        // (HTTP/2+ browsers load 5-15 assets per page navigation)
        var hasAssetTraffic = assetRequests > pageRequests * 2;
        var effectiveRate = hasAssetTraffic ? pageRate : totalRate;
        var rateLabel = hasAssetTraffic ? "page navigation" : "request";

        // Very high rate = likely bot
        if (effectiveRate > 30) // 30+ page navigations/minute = definitely bot
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.75,
                $"High {rateLabel} rate: {effectiveRate:F1}/min (total: {totalRate:F1}/min)",
                weight: 1.7,
                botType: BotType.Scraper.ToString()));
        else if (effectiveRate > 10) // 10-30 page navigations/minute = elevated
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = 0.3,
                Weight = 1.3,
                Reason = $"Elevated request rate: {effectiveRate:F0} page requests per minute",
                Signals = signals.ToImmutable()
            });
        // High total rate but normal page rate = probably HTTP/2 multiplexing (human-like)
        else if (totalRate > 30 && hasAssetTraffic && pageRate <= 10)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = -0.15,
                Weight = 1.2,
                Reason = $"Normal browser multiplexing: high total traffic but only {pageRate:F0} page visits per minute ({assetRequests} sub-resources loaded)"
            });
    }

    private void AnalyzeSessionBehavior(BlackboardState state, List<RequestSnapshot> history,
        List<DetectionContribution> contributions, ImmutableDictionary<string, object>.Builder signals)
    {
        // Check if User-Agent changes across requests (suspicious)
        var userAgents = history.Select(r => r.UserAgent).Distinct().Count();
        signals.Add("waveform.user_agent_changes", userAgents);

        if (userAgents > 1 && history.Count >= 5)
            contributions.Add(DetectionContribution.Bot(
                Name, "Waveform", 0.8,
                $"User-Agent changed {userAgents} times in session (IP rotation or spoofing)",
                weight: 1.8,
                botType: BotType.MaliciousBot.ToString()));

        // Session duration analysis
        if (history.Count >= 2)
        {
            var sessionDuration = (history[^1].Timestamp - history[0].Timestamp).TotalMinutes;
            signals.Add("waveform.session_duration_minutes", sessionDuration);

            // Very short session with many requests = bot
            if (sessionDuration < 1 && history.Count >= 10)
                contributions.Add(DetectionContribution.Bot(
                    Name, "Waveform", 0.7,
                    $"High-speed session: {history.Count} requests in {sessionDuration:F1} minutes",
                    weight: 1.6,
                    botType: BotType.Scraper.ToString()));
        }
    }

    private void AnalyzeInteractionPatterns(BlackboardState state, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        // Check for client-side interaction signals (mouse movement, keyboard events)
        // These would be sent from JavaScript tracking

        if (state.Signals.TryGetValue(SignalKeys.ClientMouseEvents, out var mouseEvents) &&
            mouseEvents is int mouseCount)
        {
            signals.Add("waveform.mouse_events", mouseCount);

            if (mouseCount == 0)
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Waveform",
                    ConfidenceDelta = 0.4,
                    Weight = 1.5,
                    Reason = "No mouse movement detected (headless browser indicator)",
                    Signals = signals.ToImmutable()
                });
        }

        if (state.Signals.TryGetValue(SignalKeys.ClientKeyboardEvents, out var keyboardEvents) &&
            keyboardEvents is int keyCount)
            signals.Add("waveform.keyboard_events", keyCount);
    }

    private string GetClientSignature(BlackboardState state)
    {
        // Create signature from IP + User-Agent hash
        var ip = state.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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

        var cacheKey = CacheKeyPrefix + clientSignature;
        if (!_cache.TryGetValue(cacheKey, out List<RequestSnapshot>? history) || history == null || history.Count == 0)
            return;

        var last = history[^1];
        var actualClass = ClassifyResponseContentType(responseContentType);
        if (actualClass != last.ContentClass)
        {
            // Replace last entry with corrected content class
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

    private static ContentClass ClassifyResponseContentType(string contentType)
    {
        var ct = contentType.ToLowerInvariant();
        if (ct.StartsWith("text/html") || ct.StartsWith("application/xhtml"))
            return ContentClass.Page;
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
        return input.Length > 0 ? input.GetHashCode().ToString("X8") : "empty";
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
    private void AnalyzeRequestTransitions(List<RequestSnapshot> history, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        if (history.Count < 5) return;

        // Count requests by content class
        var classes = history.Select(r => r.ContentClass).ToList();
        var pageCt = classes.Count(c => c == ContentClass.Page);
        var assetCt = classes.Count(c => c == ContentClass.Asset);
        var apiCt = classes.Count(c => c == ContentClass.Api);
        var total = classes.Count;

        signals.Add("waveform.page_requests", pageCt);
        signals.Add("waveform.asset_requests", assetCt);
        signals.Add("waveform.api_requests", apiCt);

        // Build transition matrix (3x3: Page, Asset, Api)
        var transitions = new int[3, 3];
        var fromCounts = new int[3];
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

            signals.Add("waveform.transition_page_to_asset", pageToAsset);
            signals.Add("waveform.transition_page_to_page", pageToPage);

            // High Page→Page ratio = scraper (doesn't load assets, just fetches HTML pages)
            if (pageToPage > 0.7 && pageCt >= 5)
                contributions.Add(DetectionContribution.Bot(
                    Name, "Waveform", 0.6,
                    $"Pages requested without loading images, scripts, or stylesheets (scraper-like behavior)",
                    weight: 1.5,
                    botType: BotType.Scraper.ToString()));
            // Normal Page→Asset ratio = human-like (browser loads sub-resources)
            else if (pageToAsset > 0.5 && assetCt > pageCt * 2)
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Waveform",
                    ConfidenceDelta = -0.2,
                    Weight = 1.3,
                    Reason = $"Normal browsing pattern: page loads trigger {assetCt} sub-resource requests (images, scripts, stylesheets)"
                });
        }

        // Pure API access pattern (no page requests at all)
        if (apiCt > 5 && pageCt == 0 && assetCt == 0)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Waveform",
                ConfidenceDelta = 0.35,
                Weight = 1.4,
                Reason = $"API-only access: {apiCt} API calls with no page visits (programmatic access)"
            });

        // Asset-to-page ratio for rate adjustment
        // Store as signal so AnalyzeRequestRate can use adjusted thresholds
        if (total > 0)
        {
            var assetRatio = (double)assetCt / total;
            signals.Add("waveform.asset_ratio", assetRatio);
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
        Page = 0,  // text/html navigation requests
        Asset = 1, // JS, CSS, images, fonts, etc.
        Api = 2    // JSON/XML API endpoints
    }

    /// <summary>
    ///     Classify a request by its content class using Sec-Fetch-Dest, Accept header, and path extension.
    /// </summary>
    private static ContentClass ClassifyRequest(HttpContext httpContext)
    {
        // Best signal: Sec-Fetch-Dest header (modern browsers)
        var fetchDest = httpContext.Request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fetchDest))
        {
            return fetchDest.ToLowerInvariant() switch
            {
                "document" or "iframe" => ContentClass.Page,
                "script" or "style" or "image" or "font" or "video" or "audio" or "manifest" or "worker"
                    => ContentClass.Asset,
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