using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Analyzes caching behavior to detect bots.
///     Real browsers typically:
///     - Send If-None-Match (ETag) and If-Modified-Since headers on repeat visits
///     - Accept compressed content (gzip, br)
///     - Cache static resources (CSS, JS, images)
///     - Don't request the same resource multiple times in rapid succession
///     Bots often:
///     - Never send cache validation headers
///     - Request same resources repeatedly without caching
///     - Don't respect cache-control directives
///     Configuration loaded from: cachebehavior.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:CacheBehaviorContributor:*
/// </summary>
public class CacheBehaviorContributor : ConfiguredContributorBase
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<CacheBehaviorContributor> _logger;

    public CacheBehaviorContributor(
        ILogger<CacheBehaviorContributor> logger,
        IMemoryCache cache,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "CacheBehavior";
    public override int Priority => Manifest?.Priority ?? 15;

    // Config-driven thresholds
    private double RepeatBaseConfidence => GetParam("repeat_base_confidence", 0.2);
    private double RepeatIncrementConfidence => GetParam("repeat_increment_confidence", 0.1);
    private double RepeatMaxConfidence => GetParam("repeat_max_confidence", 0.5);
    private double RepeatWeight => GetParam("repeat_weight", 1.2);
    private double NoCompressionConfidence => GetParam("no_compression_confidence", 0.25);
    private double NoCompressionWeight => GetParam("no_compression_weight", 1.0);
    private double RapidRepeatThresholdSeconds => GetParam("rapid_repeat_threshold_seconds", 5.0);
    private double RapidRepeatFastConfidence => GetParam("rapid_repeat_fast_confidence", 0.4);
    private double RapidRepeatSlowConfidence => GetParam("rapid_repeat_slow_confidence", 0.3);
    private double RapidRepeatWeight => GetParam("rapid_repeat_weight", 1.3);
    private int ProfileMinRequests => GetParam("profile_min_requests", 10);
    private int ProfileMinStaticRequests => GetParam("profile_min_static_requests", 5);
    private double CacheValidationRateThreshold => GetParam("cache_validation_rate_threshold", 0.3);
    private double ProfileAnomalyConfidence => GetParam("profile_anomaly_confidence", 0.3);
    private double ProfileAnomalyWeight => GetParam("profile_anomaly_weight", 1.5);
    private double GoodCacheConfidence => GetParam("good_cache_confidence", -0.15);
    private double GoodCacheWeight => GetParam("good_cache_weight", 1.0);

    // Triggered by TransportProtocol signal - moves to Wave 1 so we can read streaming classification
    // SequenceGuard: skip when on-track at positions 0-2 (not enough data for meaningful cache signal).
    public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
    {
        new SignalExistsTrigger(SignalKeys.TransportProtocol),
        SequenceGuardTrigger.Default
    };

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var context = state.HttpContext;
        var request = context.Request;

        // Skip cache analysis for streaming transports - they are non-cacheable by design.
        // SSE, WebSocket, and SignalR requests never send cache validation headers,
        // and rapid repeat requests are normal (reconnects, long-polling).
        var isStreaming = state.GetSignal<bool?>(SignalKeys.TransportIsStreaming) ?? false;
        if (isStreaming)
        {
            state.WriteSignal("cache.skipped_streaming", true);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CacheBehavior",
                ConfidenceDelta = 0.0,
                Weight = 1.0,
                Reason = "Cache analysis skipped for streaming transport (non-cacheable by design)"
            });
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        // Skip cache validation checks when sequence.cache_warm — browsers with warm CDN caches
        // make no static asset requests and never send If-None-Match on first visit.
        var cacheWarm = state.Signals.TryGetValue(SignalKeys.SequenceCacheWarm, out var cwObj) && cwObj is bool cw && cw;
        if (cacheWarm)
        {
            state.WriteSignal("cache.skipped_cache_warm", true);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CacheBehavior",
                ConfidenceDelta = 0.0,
                Weight = 1.0,
                Reason = "Cache analysis skipped: sequence indicates browser cache warm (no static requests expected)"
            });
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        // Get client identifier - prefer resolved IP from IpContributor
        var clientIp = state.Signals.TryGetValue(SignalKeys.ClientIp, out var ipObj)
            ? ipObj?.ToString()
            : GetClientIp(context);
        if (string.IsNullOrEmpty(clientIp))
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        var path = request.Path.ToString();
        var isStaticResource = IsStaticResource(path);

        // Track resource requests per client
        var resourceKey = $"cache_behavior:{clientIp}:{path}";
        var requestCount = IncrementResourceRequestCount(resourceKey);

        // Check for cache validation headers
        var hasIfNoneMatch = request.Headers.ContainsKey("If-None-Match");
        var hasIfModifiedSince = request.Headers.ContainsKey("If-Modified-Since");
        var hasCacheValidation = hasIfNoneMatch || hasIfModifiedSince;

        // Check for compression support
        var acceptEncoding = request.Headers["Accept-Encoding"].ToString();
        var supportsCompression = acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase) ||
                                  acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase);

        // 1. Static resources requested multiple times without cache validation
        if (isStaticResource && requestCount > 1 && !hasCacheValidation)
        {
            var impact = Math.Min(RepeatBaseConfidence + (requestCount - 1) * RepeatIncrementConfidence, RepeatMaxConfidence);
            state.WriteSignals([new(SignalKeys.CacheValidationMissing, true), new("ResourceRequestCount", requestCount)]);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CacheBehavior",
                ConfidenceDelta = impact,
                Weight = RepeatWeight,
                Reason = $"Static resource requested {requestCount} times without cache headers"
            });
        }

        // 2. No compression support (very rare for modern browsers)
        if (!supportsCompression && !string.IsNullOrEmpty(acceptEncoding))
        {
            state.WriteSignal(SignalKeys.CompressionSupported, false);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CacheBehavior",
                ConfidenceDelta = NoCompressionConfidence,
                Weight = NoCompressionWeight,
                Reason = "Client does not support data compression (unusual for real browsers)"
            });
        }

        // 3. Rapid repeated requests for the same resource
        // Only flag static resources and HTML pages - API endpoints (no file extension,
        // or Accept: application/json) are designed for repeated calls without cache.
        // Browsers never send If-None-Match/If-Modified-Since on API calls because
        // servers don't send ETag/Last-Modified on JSON responses.
        var isApiRequest = !isStaticResource && IsApiRequest(request);

        var timingKey = $"cache_timing:{clientIp}:{path}";
        var lastRequestTime = GetLastRequestTime(timingKey);
        var currentTime = DateTime.UtcNow;

        if (lastRequestTime.HasValue)
        {
            var timeSinceLastRequest = (currentTime - lastRequestTime.Value).TotalSeconds;

            // Same resource requested within threshold seconds (without cache validation)
            // Skip for API requests - these are inherently non-cacheable
            if (timeSinceLastRequest < RapidRepeatThresholdSeconds && !hasCacheValidation && !isApiRequest)
            {
                var impact = timeSinceLastRequest < 1 ? RapidRepeatFastConfidence : RapidRepeatSlowConfidence;
                state.WriteSignal(SignalKeys.RapidRepeatedRequest, true);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "CacheBehavior",
                    ConfidenceDelta = impact,
                    Weight = RapidRepeatWeight,
                    Reason = $"Same page re-requested after {timeSinceLastRequest:F1} seconds without using browser cache"
                });
            }
        }

        UpdateLastRequestTime(timingKey, currentTime);

        // 4. Profile: Track overall cache behavior patterns
        var profileKey = $"cache_profile:{clientIp}";
        var profile = GetOrCreateProfile(profileKey);

        profile.RecordRequest(isStaticResource, hasCacheValidation);

        // Analyze profile after sufficient requests
        if (profile.TotalRequests >= ProfileMinRequests)
        {
            // Browser should use cache validation on at least threshold% of static resource revisits
            // Guard: only compute rate when there are enough static requests to be meaningful
            if (profile.StaticResourceRequests > ProfileMinStaticRequests)
            {
                var cacheValidationRate = (double)profile.RequestsWithCacheValidation / profile.StaticResourceRequests;
                if (cacheValidationRate < CacheValidationRateThreshold)
                {
                    state.WriteSignals([new(SignalKeys.CacheBehaviorAnomaly, true), new("CacheValidationRate", cacheValidationRate)]);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "CacheBehavior",
                        ConfidenceDelta = ProfileAnomalyConfidence,
                        Weight = ProfileAnomalyWeight,
                        Reason =
                            $"Client rarely reuses cached resources ({cacheValidationRate:P0} of static files) unlike real browsers"
                    });
                }
            }
        }

        // 5. Positive signal: Good cache behavior
        if (contributions.Count == 0 && hasCacheValidation && supportsCompression)
        {
            state.WriteSignals([new(SignalKeys.CacheValidationMissing, false), new(SignalKeys.CompressionSupported, true)]);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CacheBehavior",
                ConfidenceDelta = GoodCacheConfidence,
                Weight = GoodCacheWeight,
                Reason = "Normal cache behavior detected"
            });
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private static bool IsStaticResource(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".css" => true,
            ".js" => true,
            ".jpg" => true,
            ".jpeg" => true,
            ".png" => true,
            ".gif" => true,
            ".svg" => true,
            ".woff" => true,
            ".woff2" => true,
            ".ttf" => true,
            ".eot" => true,
            ".ico" => true,
            ".webp" => true,
            ".avif" => true,
            _ => false
        };
    }

    /// <summary>
    ///     Detects browser-initiated API requests that are inherently non-cacheable.
    ///     API endpoints return dynamic JSON and don't send ETag/Last-Modified,
    ///     so browsers never send cache validation headers on repeat calls.
    ///     Only exempts requests that also carry browser-origin markers (Sec-Fetch-*,
    ///     HX-Request, X-Requested-With) - a raw bot hitting /api/* won't have these.
    /// </summary>
    private static bool IsApiRequest(HttpRequest request)
    {
        // Gate: require at least one browser-origin marker.
        // Sec-Fetch-* headers are set by the browser Fetch API and cannot be spoofed
        // from simple HTTP libraries. HX-Request and X-Requested-With are set by
        // HTMX/jQuery respectively - not proof of a browser, but combined with /api/
        // path they indicate legitimate AJAX.
        var hasBrowserOrigin = request.Headers.ContainsKey("Sec-Fetch-Mode")
                               || request.Headers.ContainsKey("HX-Request")
                               || (request.Headers.TryGetValue("X-Requested-With", out var xrw)
                                   && xrw.ToString().Equals("XMLHttpRequest", StringComparison.OrdinalIgnoreCase));

        if (!hasBrowserOrigin)
            return false;

        var path = request.Path.Value ?? "";

        // Paths containing /api/ are almost always API endpoints
        if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Accept: application/json indicates an API/fetch call
        var accept = request.Headers.Accept.ToString();
        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private string? GetClientIp(HttpContext context)
    {
        return context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
               ?? context.Connection.RemoteIpAddress?.ToString();
    }

    private int IncrementResourceRequestCount(string key)
    {
        // Use GetOrCreate with a mutable wrapper to avoid TOCTOU race.
        // Two concurrent requests could otherwise both read 0, increment to 1, write 1.
        var counter = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return new StrongBox<int>(0);
        })!;

        return Interlocked.Increment(ref counter.Value);
    }

    private DateTime? GetLastRequestTime(string key)
    {
        return _cache.Get<DateTime?>(key);
    }

    private void UpdateLastRequestTime(string key, DateTime time)
    {
        _cache.Set(key, time, TimeSpan.FromMinutes(10));
    }

    private CacheBehaviorProfile GetOrCreateProfile(string key)
    {
        return _cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromHours(1);
            return new CacheBehaviorProfile();
        }) ?? new CacheBehaviorProfile();
    }

    private class CacheBehaviorProfile
    {
        private int _totalRequests;
        private int _staticResourceRequests;
        private int _requestsWithCacheValidation;

        public int TotalRequests => _totalRequests;
        public int StaticResourceRequests => _staticResourceRequests;
        public int RequestsWithCacheValidation => _requestsWithCacheValidation;

        public void RecordRequest(bool isStaticResource, bool hasCacheValidation)
        {
            Interlocked.Increment(ref _totalRequests);
            if (isStaticResource) Interlocked.Increment(ref _staticResourceRequests);
            if (hasCacheValidation) Interlocked.Increment(ref _requestsWithCacheValidation);
        }
    }
}