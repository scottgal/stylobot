using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that analyzes HTTP cache behaviour to
///     separate real browsers from bots. Real browsers send If-None-Match /
///     If-Modified-Since on repeat visits, accept compressed content, and
///     don't re-request the same resource in rapid succession. Bots often
///     skip cache validation entirely and re-fetch static assets fresh.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>CacheBehaviorContributor</c>.
///     </para>
///     <para>
///         State-vs-signal: per-client resource-count / last-request-time /
///         per-client CacheBehaviorProfile are all HELD ON THE ATOM in
///         <see cref="IMemoryCache"/> keyed by client IP. Client IP is PII
///         and never enters the sink. The sink only sees "cache validation
///         missing" (bool), "resource request count" (int), "cache
///         validation rate" (double) -- values not identifiers.
///     </para>
///     <para>
///         Ran-vs-value: emits <c>"cache.behavior.ran"</c> at entry so
///         downstream can distinguish "not checked" from "checked, no
///         findings". Streaming-skip and cache-warm-skip early-exits also
///         raise ledger presence signals.
///     </para>
///     <para>
///         Priority 15, RequiredSignals [<see cref="SignalKeys.TransportProtocol"/>].
///         Inline sequence-guard: skip when sequence is active AND on-track AND
///         not diverged AND position &lt; 3 (matches
///         <c>SequenceGuardTrigger.Default</c> semantics).
///     </para>
/// </remarks>
public sealed class CacheBehaviorAtom : DetectorAtomBase
{
    private const int SequenceMinPosition = 3;

    private readonly IMemoryCache _cache;
    private readonly ILogger<CacheBehaviorAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CacheBehaviorAtom(
        ILogger<CacheBehaviorAtom> logger,
        IMemoryCache cache,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "CacheBehavior", category: "CacheBehavior")
    {
        _logger = logger;
        _cache = cache;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 15;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.TransportProtocol };

    private double RepeatBaseConfidence => _configProvider.GetParameter(Name, "repeat_base_confidence", 0.2);
    private double RepeatIncrementConfidence => _configProvider.GetParameter(Name, "repeat_increment_confidence", 0.1);
    private double RepeatMaxConfidence => _configProvider.GetParameter(Name, "repeat_max_confidence", 0.5);
    private double RepeatWeight => _configProvider.GetParameter(Name, "repeat_weight", 1.2);
    private double NoCompressionConfidence => _configProvider.GetParameter(Name, "no_compression_confidence", 0.25);
    private double NoCompressionWeight => _configProvider.GetParameter(Name, "no_compression_weight", 1.0);
    private double RapidRepeatThresholdSeconds => _configProvider.GetParameter(Name, "rapid_repeat_threshold_seconds", 5.0);
    private double RapidRepeatFastConfidence => _configProvider.GetParameter(Name, "rapid_repeat_fast_confidence", 0.4);
    private double RapidRepeatSlowConfidence => _configProvider.GetParameter(Name, "rapid_repeat_slow_confidence", 0.3);
    private double RapidRepeatWeight => _configProvider.GetParameter(Name, "rapid_repeat_weight", 1.3);
    private int ProfileMinRequests => _configProvider.GetParameter(Name, "profile_min_requests", 10);
    private int ProfileMinStaticRequests => _configProvider.GetParameter(Name, "profile_min_static_requests", 5);
    private double CacheValidationRateThreshold => _configProvider.GetParameter(Name, "cache_validation_rate_threshold", 0.3);
    private double ProfileAnomalyConfidence => _configProvider.GetParameter(Name, "profile_anomaly_confidence", 0.3);
    private double ProfileAnomalyWeight => _configProvider.GetParameter(Name, "profile_anomaly_weight", 1.5);
    private double GoodCacheConfidence => _configProvider.GetParameter(Name, "good_cache_confidence", -0.15);
    private double GoodCacheWeight => _configProvider.GetParameter(Name, "good_cache_weight", 1.0);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // Inline sequence-guard: skip when sequence is active AND on-track
        // AND not diverged AND position < 3. Cheaper to check first before
        // touching HttpContext.
        if (!ShouldRunUnderSequenceGuard(sink))
            return Task.FromResult(None());

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        // Ledger: we entered the check. Absent hint means we bailed on
        // guard (above), so downstream can distinguish.
        sink.Raise("cache.behavior.ran", sessionId);

        // Streaming transports are non-cacheable by design -- skip cache
        // analysis but still ledger the skip so downstream sees the reason.
        if (sink.ReadBoolHint(SignalKeys.TransportIsStreaming))
        {
            sink.Raise("cache.skipped_streaming", sessionId);
            return Task.FromResult(Single(DetectionContribution.Info(
                Name, Category, "Cache analysis skipped for streaming transport (non-cacheable by design)")));
        }

        // Sequence indicates browser cache warm -- no static requests expected,
        // no If-None-Match on first visit either.
        if (sink.ReadBoolHint(SignalKeys.SequenceCacheWarm))
        {
            sink.Raise("cache.skipped_cache_warm", sessionId);
            return Task.FromResult(Single(DetectionContribution.Info(
                Name, Category, "Cache analysis skipped: sequence indicates browser cache warm (no static requests expected)")));
        }

        var request = context.Request;
        var contributions = new List<DetectionContribution>();

        // Client identifier -- prefer resolved IP from the IP atom. If neither
        // that hint nor a request-side fallback yields anything, we cannot
        // key the cache and must abort.
        var clientIp = sink.ReadHint(SignalKeys.ClientIp) ?? GetClientIp(context);
        if (string.IsNullOrEmpty(clientIp))
            return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);

        var path = request.Path.ToString();
        var isStaticResource = IsStaticResource(path);

        // Track resource requests per client -- KEY LIVES ON ATOM (IMemoryCache),
        // never in the sink. Client IP is PII.
        var resourceKey = $"cache_behavior:{clientIp}:{path}";
        var requestCount = IncrementResourceRequestCount(resourceKey);

        var hasIfNoneMatch = request.Headers.ContainsKey("If-None-Match");
        var hasIfModifiedSince = request.Headers.ContainsKey("If-Modified-Since");
        var hasCacheValidation = hasIfNoneMatch || hasIfModifiedSince;

        var acceptEncoding = request.Headers["Accept-Encoding"].ToString();
        var supportsCompression = acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase)
                                  || acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase);

        // 1. Static resources requested multiple times without cache validation
        if (isStaticResource && requestCount > 1 && !hasCacheValidation)
        {
            var impact = Math.Min(RepeatBaseConfidence + (requestCount - 1) * RepeatIncrementConfidence, RepeatMaxConfidence);
            sink.Raise($"{SignalKeys.CacheValidationMissing}:true", sessionId);
            sink.Raise($"cache.resource_request_count:{requestCount}", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = impact,
                Weight = RepeatWeight,
                Reason = $"Static resource requested {requestCount} times without cache headers"
            });
        }

        // 2. No compression support (very rare for modern browsers)
        if (!supportsCompression && !string.IsNullOrEmpty(acceptEncoding))
        {
            sink.Raise($"{SignalKeys.CompressionSupported}:false", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = NoCompressionConfidence,
                Weight = NoCompressionWeight,
                Reason = "Client does not support data compression (unusual for real browsers)"
            });
        }

        // 3. Rapid repeated requests for the same resource. API endpoints are
        // inherently non-cacheable so skip the rapid-repeat check for them.
        var isApiRequest = !isStaticResource && IsApiRequest(request);

        var timingKey = $"cache_timing:{clientIp}:{path}";
        var lastRequestTime = _cache.Get<DateTime?>(timingKey);
        var currentTime = DateTime.UtcNow;

        if (lastRequestTime.HasValue)
        {
            var timeSinceLastRequest = (currentTime - lastRequestTime.Value).TotalSeconds;
            if (timeSinceLastRequest < RapidRepeatThresholdSeconds && !hasCacheValidation && !isApiRequest)
            {
                var impact = timeSinceLastRequest < 1 ? RapidRepeatFastConfidence : RapidRepeatSlowConfidence;
                sink.Raise($"{SignalKeys.RapidRepeatedRequest}:true", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = impact,
                    Weight = RapidRepeatWeight,
                    Reason = $"Same page re-requested after {timeSinceLastRequest:F1} seconds without using browser cache"
                });
            }
        }

        _cache.Set(timingKey, currentTime, TimeSpan.FromMinutes(10));

        // 4. Profile: overall cache behaviour patterns. Profile stays on atom
        // (in IMemoryCache keyed by IP), profile *values* enter the sink.
        var profileKey = $"cache_profile:{clientIp}";
        var profile = _cache.GetOrCreate(profileKey, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromHours(1);
            return new CacheBehaviorProfile();
        }) ?? new CacheBehaviorProfile();

        profile.RecordRequest(isStaticResource, hasCacheValidation);

        if (profile.TotalRequests >= ProfileMinRequests && profile.StaticResourceRequests > ProfileMinStaticRequests)
        {
            var cacheValidationRate = (double)profile.RequestsWithCacheValidation / profile.StaticResourceRequests;
            if (cacheValidationRate < CacheValidationRateThreshold)
            {
                sink.Raise($"{SignalKeys.CacheBehaviorAnomaly}:true", sessionId);
                sink.Raise($"cache.validation_rate:{cacheValidationRate.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = ProfileAnomalyConfidence,
                    Weight = ProfileAnomalyWeight,
                    Reason = $"Client rarely reuses cached resources ({cacheValidationRate:P0} of static files) unlike real browsers"
                });
            }
        }

        // 5. Positive signal: good cache behaviour
        if (contributions.Count == 0 && hasCacheValidation && supportsCompression)
        {
            sink.Raise($"{SignalKeys.CacheValidationMissing}:false", sessionId);
            sink.Raise($"{SignalKeys.CompressionSupported}:true", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GoodCacheConfidence,
                Weight = GoodCacheWeight,
                Reason = "Normal cache behavior detected"
            });
        }

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    /// <summary>
    ///     Inline port of <c>SequenceGuardTrigger.Default</c>. Runs when no
    ///     sequence signal exists, on-track is false, diverged is true, or
    ///     position has reached the minimum threshold.
    /// </summary>
    private static bool ShouldRunUnderSequenceGuard(SignalSink sink)
    {
        var positionHint = sink.ReadHint(SignalKeys.SequencePosition);
        if (positionHint is null) return true;

        if (!sink.ReadBoolHint(SignalKeys.SequenceOnTrack, fallback: true)) return true;
        if (sink.ReadBoolHint(SignalKeys.SequenceDiverged)) return true;

        return int.TryParse(positionHint, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos)
               && pos >= SequenceMinPosition;
    }

    private static bool IsStaticResource(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".css" or ".js" or ".jpg" or ".jpeg" or ".png" or ".gif" or ".svg"
                or ".woff" or ".woff2" or ".ttf" or ".eot" or ".ico" or ".webp" or ".avif" => true,
            _ => false
        };
    }

    /// <summary>
    ///     Detects browser-initiated API requests that are inherently
    ///     non-cacheable. Requires a browser-origin marker (Sec-Fetch-*,
    ///     HX-Request, X-Requested-With=XMLHttpRequest) so a raw bot hitting
    ///     /api/* doesn't slip through.
    /// </summary>
    private static bool IsApiRequest(HttpRequest request)
    {
        var hasBrowserOrigin = request.Headers.ContainsKey("Sec-Fetch-Mode")
                               || request.Headers.ContainsKey("HX-Request")
                               || (request.Headers.TryGetValue("X-Requested-With", out var xrw)
                                   && xrw.ToString().Equals("XMLHttpRequest", StringComparison.OrdinalIgnoreCase));
        if (!hasBrowserOrigin) return false;

        var path = request.Path.Value ?? "";
        if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase)) return true;

        var accept = request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetClientIp(HttpContext context)
    {
        return context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
               ?? context.Connection.RemoteIpAddress?.ToString();
    }

    private int IncrementResourceRequestCount(string key)
    {
        var counter = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return new StrongBox<int>(0);
        })!;
        return Interlocked.Increment(ref counter.Value);
    }

    private sealed class CacheBehaviorProfile
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
