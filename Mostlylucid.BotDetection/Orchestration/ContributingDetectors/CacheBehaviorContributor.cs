using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
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
/// </summary>
public class CacheBehaviorContributor : ContributingDetectorBase
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<CacheBehaviorContributor> _logger;

    public CacheBehaviorContributor(
        ILogger<CacheBehaviorContributor> logger,
        IMemoryCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "CacheBehavior";
    public override int Priority => 15; // Run early, lightweight

    // No triggers - runs in first wave
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var context = state.HttpContext;
        var request = context.Request;

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
            var impact = Math.Min(0.2 + (requestCount - 1) * 0.1, 0.5);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CacheBehavior",
                ConfidenceDelta = impact,
                Weight = 1.2,
                Reason = $"Static resource requested {requestCount} times without cache headers",
                Signals = ImmutableDictionary<string, object>.Empty
                    .Add(SignalKeys.CacheValidationMissing, true)
                    .Add("ResourceRequestCount", requestCount)
            });
        }

        // 2. No compression support (very rare for modern browsers)
        if (!supportsCompression && !string.IsNullOrEmpty(acceptEncoding))
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CacheBehavior",
                ConfidenceDelta = 0.25,
                Weight = 1.0,
                Reason = "Client does not support data compression (unusual for real browsers)",
                Signals = ImmutableDictionary<string, object>.Empty
                    .Add(SignalKeys.CompressionSupported, false)
            });

        // 3. Rapid repeated requests for the same resource
        var timingKey = $"cache_timing:{clientIp}:{path}";
        var lastRequestTime = GetLastRequestTime(timingKey);
        var currentTime = DateTime.UtcNow;

        if (lastRequestTime.HasValue)
        {
            var timeSinceLastRequest = (currentTime - lastRequestTime.Value).TotalSeconds;

            // Same resource requested within 5 seconds (without cache validation)
            if (timeSinceLastRequest < 5 && !hasCacheValidation)
            {
                var impact = timeSinceLastRequest < 1 ? 0.4 : 0.3;
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "CacheBehavior",
                    ConfidenceDelta = impact,
                    Weight = 1.3,
                    Reason = $"Same page re-requested after {timeSinceLastRequest:F1} seconds without using browser cache",
                    Signals = ImmutableDictionary<string, object>.Empty
                        .Add(SignalKeys.RapidRepeatedRequest, true)
                });
            }
        }

        UpdateLastRequestTime(timingKey, currentTime);

        // 4. Profile: Track overall cache behavior patterns
        var profileKey = $"cache_profile:{clientIp}";
        var profile = GetOrCreateProfile(profileKey);

        profile.TotalRequests++;
        profile.StaticResourceRequests += isStaticResource ? 1 : 0;
        profile.RequestsWithCacheValidation += hasCacheValidation ? 1 : 0;

        UpdateProfile(profileKey, profile);

        // Analyze profile after sufficient requests
        if (profile.TotalRequests >= 10)
        {
            // Browser should use cache validation on at least 30% of static resource revisits
            // Guard: only compute rate when there are enough static requests to be meaningful
            if (profile.StaticResourceRequests > 5)
            {
                var cacheValidationRate = (double)profile.RequestsWithCacheValidation / profile.StaticResourceRequests;
                if (cacheValidationRate < 0.3)
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "CacheBehavior",
                        ConfidenceDelta = 0.3,
                        Weight = 1.5,
                        Reason =
                            $"Client rarely reuses cached resources ({cacheValidationRate:P0} of static files) unlike real browsers",
                        Signals = ImmutableDictionary<string, object>.Empty
                            .Add(SignalKeys.CacheBehaviorAnomaly, true)
                            .Add("CacheValidationRate", cacheValidationRate)
                    });
            }
        }

        // 5. Positive signal: Good cache behavior
        if (contributions.Count == 0 && hasCacheValidation && supportsCompression)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CacheBehavior",
                ConfidenceDelta = -0.15,
                Weight = 1.0,
                Reason = "Normal cache behavior detected",
                Signals = ImmutableDictionary<string, object>.Empty
                    .Add(SignalKeys.CacheValidationMissing, false)
                    .Add(SignalKeys.CompressionSupported, true)
            });

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

    private string? GetClientIp(HttpContext context)
    {
        return context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
               ?? context.Connection.RemoteIpAddress?.ToString();
    }

    private int IncrementResourceRequestCount(string key)
    {
        var count = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return 0;
        });

        count++;
        _cache.Set(key, count, TimeSpan.FromMinutes(10));
        return count;
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

    private void UpdateProfile(string key, CacheBehaviorProfile profile)
    {
        _cache.Set(key, profile, TimeSpan.FromHours(1));
    }

    private class CacheBehaviorProfile
    {
        public int TotalRequests { get; set; }
        public int StaticResourceRequests { get; set; }
        public int RequestsWithCacheValidation { get; set; }
    }
}