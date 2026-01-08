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

        // ===== Rate Limiting (per-IP) =====
        var requestCount = IncrementRequestCount(ipAddress);

        // Apply lenient threshold during warmup (normal browser startup)
        // Browsers often make 5-15 rapid requests on initial page load (HTML + CSS + JS + images)
        var effectiveLimit = isWarmingUp
            ? _options.MaxRequestsPerMinute * 2
            : _options.MaxRequestsPerMinute;

        if (requestCount > effectiveLimit)
        {
            var excess = requestCount - effectiveLimit;
            var impact = Math.Min(0.3 + excess * 0.05, 0.9);
            confidence += impact;
            reasons.Add(new DetectionReason
            {
                Category = "Behavioral",
                Detail = isWarmingUp
                    ? $"Excessive request rate during warmup: {requestCount} requests/min (warmup limit: {effectiveLimit})"
                    : $"Excessive request rate: {requestCount} requests/min (limit: {effectiveLimit})",
                ConfidenceImpact = impact
            });
        }

        // ===== Session/Identity Behavioral Analysis =====

        // Check fingerprint-based rate limiting (if available)
        if (!string.IsNullOrEmpty(identities.FingerprintHash))
        {
            var fpRequestCount = IncrementRequestCount($"fp:{identities.FingerprintHash}");
            // Fingerprints should have similar rate limits
            if (fpRequestCount > _options.MaxRequestsPerMinute * 1.5)
            {
                var impact = 0.25;
                confidence += impact;
                reasons.Add(new DetectionReason
                {
                    Category = "Behavioral",
                    Detail = $"Fingerprint rate limit exceeded: {fpRequestCount} requests/min",
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
        var timingPattern = AnalyzeRequestTiming(ipAddress);
        if (timingPattern.IsSuspicious)
        {
            confidence += 0.3;
            reasons.Add(new DetectionReason
            {
                Category = "Behavioral",
                Detail = $"Suspicious request timing: {timingPattern.Description}",
                ConfidenceImpact = 0.3
            });
        }

        // Check for rapid sequential requests (no human delay)
        var lastRequestTime = GetLastRequestTime(ipAddress);
        if (lastRequestTime.HasValue)
        {
            var timeSinceLastRequest = (currentTime - lastRequestTime.Value).TotalMilliseconds;

            // During warmup, allow rapid parallel requests (browser loading resources)
            // But still flag sub-50ms as suspicious (likely automated)
            var rapidThreshold = isWarmingUp ? 50.0 : 100.0;

            if (timeSinceLastRequest < rapidThreshold)
            {
                var impact = timeSinceLastRequest < 50 ? 0.4 : 0.25;
                confidence += impact;
                reasons.Add(new DetectionReason
                {
                    Category = "Behavioral",
                    Detail = isWarmingUp
                        ? $"Extremely fast requests during warmup: {timeSinceLastRequest:F0}ms between requests"
                        : $"Extremely fast requests: {timeSinceLastRequest:F0}ms between requests",
                    ConfidenceImpact = impact
                });
            }
        }

        UpdateLastRequestTime(ipAddress, currentTime);

        // ===== Session Consistency Checks =====

        // Check for no referrer on non-initial requests
        // Skip during warmup as initial page load doesn't have referrer
        if (!isWarmingUp &&
            !context.Request.Headers.ContainsKey("Referer") &&
            context.Request.Path != "/" &&
            requestCount > 1) // After first request
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
        if (!isWarmingUp && !context.Request.Cookies.Any() && requestCount > 2)
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
        var currentMethod = context.Request.Method;

        // Track this request
        profile.RequestCount++;
        profile.LastSeen = currentTime;

        if (!profile.SeenPaths.Contains(currentPath))
        {
            profile.SeenPaths.Add(currentPath);
            if (profile.SeenPaths.Count > 100) // Limit size
                profile.SeenPaths.Remove(profile.SeenPaths.First());
        }

        // Detect anomalies

        // 1. Sudden spike in request rate
        if (profile.RequestCount > 10)
        {
            var avgRate = profile.RequestCount / Math.Max(1, (currentTime - profile.FirstSeen).TotalMinutes);
            var recentCount = GetRecentRequestCount($"recent:{identityKey}");
            if (recentCount > avgRate * 5 && recentCount > 20)
            {
                _cache.Set(profileKey, profile, TimeSpan.FromHours(24));
                return (true, 0.35, $"Sudden request spike: {recentCount}/min vs {avgRate:F1}/min average");
            }
        }

        // 2. Accessing many new paths suddenly
        if (profile.SeenPaths.Count > 20 && profile.RequestCount > 50)
        {
            var newPathRate = GetNewPathRate(identityKey, currentPath);
            if (newPathRate > 0.8) // 80%+ new paths in recent requests
            {
                _cache.Set(profileKey, profile, TimeSpan.FromHours(24));
                return (true, 0.25, "Accessing many new endpoints suddenly");
            }
        }

        _cache.Set(profileKey, profile, TimeSpan.FromHours(24));
        return (false, 0, "");
    }

    private int GetRecentRequestCount(string key)
    {
        var cacheKey = $"bot_detect_count_{key}";
        return _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return 0;
        });
    }

    private double GetNewPathRate(string identityKey, string currentPath)
    {
        var key = $"bot_detect_paths_{identityKey}";
        var recentPaths = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return new HashSet<string>();
        }) ?? new HashSet<string>();

        var wasNew = recentPaths.Add(currentPath);
        if (recentPaths.Count > 50)
            // Remove oldest (approximate)
            recentPaths.Remove(recentPaths.First());

        _cache.Set(key, recentPaths, TimeSpan.FromMinutes(5));

        // This is a simplified check - in production you'd track this more precisely
        return wasNew ? 1.0 : 0.0;
    }

    private string? GetClientIp(HttpContext context)
    {
        return context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
               ?? context.Connection.RemoteIpAddress?.ToString();
    }

    private int IncrementRequestCount(string ipAddress)
    {
        var key = $"bot_detect_count_{ipAddress}";
        var count = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return 0;
        });

        count++;
        _cache.Set(key, count, TimeSpan.FromMinutes(1));
        return count;
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
        var timings = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return new List<DateTime>();
        }) ?? new List<DateTime>();

        timings.Add(DateTime.UtcNow);

        // Keep only last 10 requests
        if (timings.Count > 10) timings = timings.Skip(timings.Count - 10).ToList();

        _cache.Set(key, timings, TimeSpan.FromMinutes(5));

        // Check if requests are too evenly spaced (bot-like)
        if (timings.Count >= 5)
        {
            var intervals = new List<double>();
            for (var i = 1; i < timings.Count; i++) intervals.Add((timings[i] - timings[i - 1]).TotalSeconds);

            // Calculate standard deviation
            var mean = intervals.Average();
            var variance = intervals.Average(x => Math.Pow(x - mean, 2));
            var stdDev = Math.Sqrt(variance);

            // Very low standard deviation means requests are too regular
            if (stdDev < 0.5 && mean < 5) return (true, $"Too regular interval: {mean:F2}s ± {stdDev:F2}s");
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
        public int RequestCount { get; set; }
        public DateTime FirstSeen { get; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public HashSet<string> SeenPaths { get; } = new();
    }
}