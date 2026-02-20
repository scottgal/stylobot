using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Detects bots based on behavioural patterns (rate limiting, request patterns).
///     Tracks behavior at multiple identity levels:
///     - IP address (default)
///     - Browser fingerprint hash (if client-side detection enabled)
///     - API key (via configurable header)
///     - User ID (via configurable header or claim)
/// </summary>
public class BehavioralDetector : IDetector
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<BehavioralDetector> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;

    public BehavioralDetector(
        ILogger<BehavioralDetector> logger,
        IOptions<BotDetectionOptions> options,
        IMemoryCache cache,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _cache = cache;
        _metrics = metrics;
    }

    public string Name => "Behavioral Detector";

    /// <summary>Stage 1: Behavioral analysis - reads raw signals from stage 0</summary>
    public DetectorStage Stage => DetectorStage.Behavioral;

    public Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DetectorResult();
        var confidence = 0.0;
        var reasons = new List<DetectionReason>();

        var ipAddress = GetClientIp(context);
        if (string.IsNullOrEmpty(ipAddress))
        {
            stopwatch.Stop();
            return Task.FromResult(result);
        }

        // Get all identity keys for this request
        var identities = GetIdentityKeys(context, ipAddress);
        var currentTime = DateTime.UtcNow;

        // ===== Session warmup tracking (avoid false positives on first load) =====
        var sessionKey = $"bot_detect_session_{ipAddress}";
        var sessionAge = GetOrCreateSessionAge(sessionKey);
        var isWarmingUp = sessionAge.TotalMinutes < 2; // First 2 minutes is warmup

        // ===== Content-Aware Rate Limiting (per-IP) =====
        // HTTP/2+ multiplexes many asset requests per page load — counting all requests
        // produces false positives. Only page navigations matter for rate limiting.
        var isPageRequest = IsPageRequest(context);
        var totalRequestCount = IncrementRequestCount(ipAddress);
        var pageRequestCount = isPageRequest ? IncrementRequestCount($"page:{ipAddress}") : GetCurrentCount($"page:{ipAddress}");

        // Use page-only rate when we have enough asset traffic to indicate HTTP/2+ multiplexing
        var hasSignificantAssets = totalRequestCount > pageRequestCount * 3;
        var effectiveCount = hasSignificantAssets ? pageRequestCount : totalRequestCount;

        // Apply lenient threshold during warmup (normal browser startup)
        var effectiveLimit = isWarmingUp
            ? _options.MaxRequestsPerMinute * 2
            : _options.MaxRequestsPerMinute;

        if (effectiveCount > effectiveLimit)
        {
            var excess = effectiveCount - effectiveLimit;
            var impact = Math.Min(0.3 + excess * 0.05, 0.9);
            confidence += impact;
            var rateLabel = hasSignificantAssets ? "page navigation" : "request";
            reasons.Add(new DetectionReason
            {
                Category = "Behavioral",
                Detail = isWarmingUp
                    ? $"Excessive {rateLabel} rate during warmup: {effectiveCount} {rateLabel}s/min (warmup limit: {effectiveLimit}, total: {totalRequestCount})"
                    : $"Excessive {rateLabel} rate: {effectiveCount} {rateLabel}s/min (limit: {effectiveLimit}, total: {totalRequestCount})",
                ConfidenceImpact = impact
            });
        }

        // ===== Session/Identity Behavioral Analysis =====

        // Check fingerprint-based rate limiting (if available)
        if (!string.IsNullOrEmpty(identities.FingerprintHash))
        {
            var fpTotalCount = IncrementRequestCount($"fp:{identities.FingerprintHash}");
            var fpPageCount = isPageRequest
                ? IncrementRequestCount($"fp_page:{identities.FingerprintHash}")
                : GetCurrentCount($"fp_page:{identities.FingerprintHash}");
            var fpHasAssets = fpTotalCount > fpPageCount * 3;
            var fpEffective = fpHasAssets ? fpPageCount : fpTotalCount;

            if (fpEffective > _options.MaxRequestsPerMinute * 1.5)
            {
                var impact = 0.25;
                confidence += impact;
                reasons.Add(new DetectionReason
                {
                    Category = "Behavioral",
                    Detail = $"Fingerprint rate limit exceeded: {fpEffective} page requests/min (total: {fpTotalCount})",
                    ConfidenceImpact = impact
                });
            }

            // Check for sudden behavior change for this fingerprint
            var behaviorChange = CheckBehaviorChange($"fp:{identities.FingerprintHash}", context);
            if (behaviorChange.IsAnomaly)
            {
                confidence += behaviorChange.Impact;
                reasons.Add(new DetectionReason
                {
                    Category = "Behavioral",
                    Detail = behaviorChange.Description,
                    ConfidenceImpact = behaviorChange.Impact
                });
            }
        }

        // Check API key rate limiting (if available)
        if (!string.IsNullOrEmpty(identities.ApiKey))
        {
            var apiRequestCount = IncrementRequestCount($"api:{identities.ApiKey}");
            // API keys have stricter limits
            var apiLimit = _options.Behavioral.ApiKeyRateLimit > 0
                ? _options.Behavioral.ApiKeyRateLimit
                : _options.MaxRequestsPerMinute * 2;

            if (apiRequestCount > apiLimit)
            {
                var impact = 0.35;
                confidence += impact;
                reasons.Add(new DetectionReason
                {
                    Category = "Behavioral",
                    Detail = $"API key rate limit exceeded: {apiRequestCount} requests/min (limit: {apiLimit})",
                    ConfidenceImpact = impact
                });
            }
        }

        // Check authenticated user rate limiting
        if (!string.IsNullOrEmpty(identities.UserId))
        {
            var userRequestCount = IncrementRequestCount($"user:{identities.UserId}");
            var userLimit = _options.Behavioral.UserRateLimit > 0
                ? _options.Behavioral.UserRateLimit
                : _options.MaxRequestsPerMinute * 3;

            if (userRequestCount > userLimit)
            {
                var impact = 0.3;
                confidence += impact;
                reasons.Add(new DetectionReason
                {
                    Category = "Behavioral",
                    Detail = $"User rate limit exceeded: {userRequestCount} requests/min",
                    ConfidenceImpact = impact
                });
            }

            // Check for authenticated user behavior anomaly
            var userBehaviorChange = CheckBehaviorChange($"user:{identities.UserId}", context);
            if (userBehaviorChange.IsAnomaly)
            {
                confidence += userBehaviorChange.Impact;
                reasons.Add(new DetectionReason
                {
                    Category = "Behavioral",
                    Detail = $"User behavior anomaly: {userBehaviorChange.Description}",
                    ConfidenceImpact = userBehaviorChange.Impact
                });
            }
        }

        // ===== Request Timing Analysis =====
        // Always record timings, but only flag during non-warmup.
        // Browser page loads produce regular-interval JS-triggered requests that
        // would otherwise cause false positives.
        var timingPattern = AnalyzeRequestTiming(ipAddress);
        if (!isWarmingUp && timingPattern.IsSuspicious)
        {
            confidence += 0.3;
            reasons.Add(new DetectionReason
            {
                Category = "Behavioral",
                Detail = $"Suspicious request timing: {timingPattern.Description}",
                ConfidenceImpact = 0.3
            });
        }

        // Check for rapid sequential page requests (no human delay)
        // Only check page-to-page intervals. Asset requests at 0ms intervals are
        // normal HTTP/2+ multiplexing and should NOT be flagged.
        // During warmup, skip this entirely.
        if (!isWarmingUp && isPageRequest)
        {
            var lastPageTime = GetLastRequestTime($"page_time:{ipAddress}");
            if (lastPageTime.HasValue)
            {
                var timeSinceLastPage = (currentTime - lastPageTime.Value).TotalMilliseconds;

                if (timeSinceLastPage < 100)
                {
                    var impact = timeSinceLastPage < 50 ? 0.4 : 0.25;
                    confidence += impact;
                    reasons.Add(new DetectionReason
                    {
                        Category = "Behavioral",
                        Detail = $"Extremely fast page navigation: {timeSinceLastPage:F0}ms between page requests",
                        ConfidenceImpact = impact
                    });
                }
            }

            UpdateLastRequestTime($"page_time:{ipAddress}", currentTime);
        }

        UpdateLastRequestTime(ipAddress, currentTime);

        // ===== Session Consistency Checks =====

        // HTMX/fetch sub-requests prove JavaScript execution — strong human signal.
        // These are AJAX partials triggered by the parent page's JS framework.
        var isHtmxRequest = context.Request.Headers.ContainsKey("HX-Request");
        var isFetchRequest = context.Request.Headers["Sec-Fetch-Mode"].FirstOrDefault() == "cors" ||
                             context.Request.Headers["X-Requested-With"].FirstOrDefault() == "XMLHttpRequest";

        if (isHtmxRequest)
        {
            confidence -= 0.15;
            reasons.Add(new DetectionReason
            {
                Category = "Behavioral",
                Detail = "HTMX sub-request detected (proves JavaScript execution)",
                ConfidenceImpact = -0.15
            });
        }

        // Check for no referrer on non-initial requests
        // Skip during warmup as initial page load doesn't have referrer
        // Skip for HTMX/fetch sub-requests — they may legitimately omit Referer
        if (!isWarmingUp &&
            !isHtmxRequest && !isFetchRequest &&
            !context.Request.Headers.ContainsKey("Referer") &&
            context.Request.Path != "/" &&
            totalRequestCount > 1) // After first request
        {
            confidence += 0.15;
            reasons.Add(new DetectionReason
            {
                Category = "Behavioral",
                Detail = "No referrer on subsequent request",
                ConfidenceImpact = 0.15
            });
        }

        // Check for missing cookies (bots often don't maintain sessions)
        // Skip during warmup - cookies are set after first response
        // Skip for HTMX/fetch sub-requests — many sites don't set cookies at all,
        // and penalizing cookie-less AJAX from real browsers is a false positive.
        if (!isWarmingUp && !isHtmxRequest && !isFetchRequest &&
            !context.Request.Cookies.Any() && totalRequestCount > 2)
        {
            confidence += 0.25;
            reasons.Add(new DetectionReason
            {
                Category = "Behavioral",
                Detail = "No cookies maintained across multiple requests",
                ConfidenceImpact = 0.25
            });
        }

        result.Confidence = Math.Min(confidence, 1.0);
        result.Reasons = reasons;

        if (result.Confidence > 0.6) result.BotType = BotType.Scraper;

        stopwatch.Stop();
        _metrics?.RecordDetection(result.Confidence, result.Confidence > _options.BotThreshold, stopwatch.Elapsed,
            Name);

        return Task.FromResult(result);
    }

    /// <summary>
    ///     Gets all identity keys for behavioral tracking.
    /// </summary>
    private RequestIdentities GetIdentityKeys(HttpContext context, string ipAddress)
    {
        var identities = new RequestIdentities { IpAddress = ipAddress };

        // Get fingerprint hash from context items (set by ClientSideDetector)
        if (context.Items.TryGetValue("BotDetection.FingerprintHash", out var fpHash) && fpHash is string hash)
            identities.FingerprintHash = hash;

        // Get API key from configured header
        var apiKeyHeader = _options.Behavioral.ApiKeyHeader;
        if (!string.IsNullOrEmpty(apiKeyHeader) &&
            context.Request.Headers.TryGetValue(apiKeyHeader, out var apiKey))
            identities.ApiKey = apiKey.ToString();

        // Get user ID from claims or configured header
        var userIdClaim = _options.Behavioral.UserIdClaim;
        if (!string.IsNullOrEmpty(userIdClaim) && context.User.Identity?.IsAuthenticated == true)
            identities.UserId = context.User.FindFirst(userIdClaim)?.Value;

        // Fallback to configured user ID header
        if (string.IsNullOrEmpty(identities.UserId))
        {
            var userIdHeader = _options.Behavioral.UserIdHeader;
            if (!string.IsNullOrEmpty(userIdHeader) &&
                context.Request.Headers.TryGetValue(userIdHeader, out var userId))
                identities.UserId = userId.ToString();
        }

        return identities;
    }

    /// <summary>
    ///     Checks for sudden behavior changes that might indicate account takeover or bot activity.
    /// </summary>
    private (bool IsAnomaly, double Impact, string Description) CheckBehaviorChange(string identityKey,
        HttpContext context)
    {
        var profileKey = $"bot_detect_profile_{identityKey}";

        // Get or create behavior profile
        var profile = _cache.GetOrCreate(profileKey, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromHours(24);
            return new BehaviorProfile();
        }) ?? new BehaviorProfile();

        var currentTime = DateTime.UtcNow;
        var currentPath = context.Request.Path.ToString();

        int pathCount, reqCount;
        double avgRate = 0;

        // Lock the profile to prevent concurrent mutation from parallel requests
        lock (profile.SyncRoot)
        {
            // Track this request
            profile.RequestCount++;
            profile.LastSeen = currentTime;

            if (!profile.SeenPaths.Contains(currentPath))
            {
                profile.SeenPaths.Add(currentPath);
                if (profile.SeenPaths.Count > 100) // Limit size
                    profile.SeenPaths.Remove(profile.SeenPaths.First());
            }

            // Capture state for anomaly checks outside the lock
            pathCount = profile.SeenPaths.Count;
            reqCount = profile.RequestCount;

            // 1. Sudden spike in request rate
            // Compare current 1-minute window count against historical average
            if (reqCount > 10)
            {
                avgRate = reqCount / Math.Max(1, (currentTime - profile.FirstSeen).TotalMinutes);
                var recentCount = GetRecentRequestCount(identityKey);
                if (recentCount > avgRate * 5 && recentCount > 20)
                    return (true, 0.35, $"Sudden request spike: {recentCount}/min vs {avgRate:F1}/min average");
            }
        }

        // 2. Accessing many new paths suddenly (outside lock to avoid nested locking)
        if (pathCount > 20 && reqCount > 50)
        {
            var newPathRate = GetNewPathRate(identityKey, currentPath);
            if (newPathRate > 0.8) // 80%+ new paths in recent requests
                return (true, 0.25, "Accessing many new endpoints suddenly");
        }

        return (false, 0, "");
    }

    private int GetRecentRequestCount(string key)
    {
        var cacheKey = $"bot_detect_count_{key}";
        return _cache.TryGetValue(cacheKey, out int[]? counter) ? Volatile.Read(ref counter![0]) : 0;
    }

    private double GetNewPathRate(string identityKey, string currentPath)
    {
        var key = $"bot_detect_paths_{identityKey}";
        var wrapper = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return new PathWrapper();
        })!;

        bool wasNew;
        lock (wrapper.SyncRoot)
        {
            wasNew = wrapper.Paths.Add(currentPath);
            if (wrapper.Paths.Count > 50)
                wrapper.Paths.Remove(wrapper.Paths.First());
        }

        return wasNew ? 1.0 : 0.0;
    }

    private string? GetClientIp(HttpContext context)
    {
        return context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
               ?? context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    ///     Atomically increment a request counter using a boxed int array to avoid
    ///     read-then-write race conditions with concurrent requests.
    /// </summary>
    private int IncrementRequestCount(string ipAddress)
    {
        var key = $"bot_detect_count_{ipAddress}";
        // Use int[] as a mutable reference type so GetOrCreate returns the same
        // boxed array for concurrent callers, and Interlocked.Increment is atomic.
        var counter = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return new int[] { 0 };
        })!;

        return Interlocked.Increment(ref counter[0]);
    }

    private int GetCurrentCount(string ipAddress)
    {
        var key = $"bot_detect_count_{ipAddress}";
        return _cache.TryGetValue(key, out int[]? counter) ? Volatile.Read(ref counter![0]) : 0;
    }

    /// <summary>
    ///     Determines if a request is a page navigation (HTML document) vs an asset/API request.
    ///     Uses Sec-Fetch-Dest (modern browsers), file extension, and Accept header as fallbacks.
    /// </summary>
    private static bool IsPageRequest(HttpContext context)
    {
        // Best signal: Sec-Fetch-Dest header (Chrome 80+, Firefox 90+, Safari 16.4+)
        var fetchDest = context.Request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fetchDest))
            return fetchDest.Equals("document", StringComparison.OrdinalIgnoreCase)
                   || fetchDest.Equals("iframe", StringComparison.OrdinalIgnoreCase);

        // File extension check — known asset extensions are NOT page requests
        var path = context.Request.Path.Value ?? "/";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".js" or ".css" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico"
            or ".woff" or ".woff2" or ".ttf" or ".eot" or ".map" or ".webp" or ".avif"
            or ".mp4" or ".webm" or ".json" or ".xml")
            return false;

        // API path pattern
        if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase))
            return false;

        // Accept header fallback
        var accept = context.Request.Headers.Accept.FirstOrDefault() ?? "";
        if (accept.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
            accept.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase))
            return true;

        // No extension = likely page, otherwise assume asset
        return string.IsNullOrEmpty(ext);
    }

    private DateTime? GetLastRequestTime(string ipAddress)
    {
        var key = $"bot_detect_time_{ipAddress}";
        return _cache.Get<DateTime?>(key);
    }

    private void UpdateLastRequestTime(string ipAddress, DateTime time)
    {
        var key = $"bot_detect_time_{ipAddress}";
        _cache.Set(key, time, TimeSpan.FromMinutes(5));
    }

    private (bool IsSuspicious, string Description) AnalyzeRequestTiming(string ipAddress)
    {
        var key = $"bot_detect_timing_{ipAddress}";
        var wrapper = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return new TimingWrapper();
        })!;

        List<DateTime> snapshot;
        lock (wrapper.SyncRoot)
        {
            wrapper.Timings.Add(DateTime.UtcNow);

            // Keep only last 10 requests
            if (wrapper.Timings.Count > 10)
                wrapper.Timings.RemoveRange(0, wrapper.Timings.Count - 10);

            snapshot = wrapper.Timings.ToList();
        }

        // Check if requests are too evenly spaced (bot-like)
        // Require 8+ requests for statistical reliability.
        // stdDev < 0.2 catches real bots (near-identical intervals) while
        // allowing normal browser JS-triggered XHR which has stdDev 0.3-0.6.
        if (snapshot.Count >= 8)
        {
            var intervals = new List<double>();
            for (var i = 1; i < snapshot.Count; i++) intervals.Add((snapshot[i] - snapshot[i - 1]).TotalSeconds);

            // Calculate standard deviation
            var mean = intervals.Average();
            var variance = intervals.Average(x => Math.Pow(x - mean, 2));
            var stdDev = Math.Sqrt(variance);

            // Very low standard deviation means requests are too regular
            if (stdDev < 0.2 && mean < 5) return (true, $"Too regular interval: {mean:F2}s ± {stdDev:F2}s");
        }

        return (false, string.Empty);
    }

    /// <summary>
    ///     Get or create session age to track warmup period.
    ///     Returns how long ago this session started.
    /// </summary>
    private TimeSpan GetOrCreateSessionAge(string sessionKey)
    {
        var sessionStart = _cache.GetOrCreate(sessionKey, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30); // Keep session tracking for 30 min
            return DateTime.UtcNow;
        });

        return DateTime.UtcNow - sessionStart;
    }

    private record RequestIdentities
    {
        public string IpAddress { get; set; } = "";
        public string? FingerprintHash { get; set; }
        public string? ApiKey { get; set; }
        public string? UserId { get; set; }
    }

    private class BehaviorProfile
    {
        internal readonly object SyncRoot = new();
        public int RequestCount { get; set; }
        public DateTime FirstSeen { get; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public HashSet<string> SeenPaths { get; } = new();
    }

    private class TimingWrapper
    {
        internal readonly object SyncRoot = new();
        public List<DateTime> Timings { get; } = new();
    }

    private class PathWrapper
    {
        internal readonly object SyncRoot = new();
        public HashSet<string> Paths { get; } = new();
    }
}