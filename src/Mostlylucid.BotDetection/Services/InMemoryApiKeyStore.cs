using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     In-memory API key store backed by <see cref="BotDetectionOptions.ApiKeys"/>.
///     Uses constant-time comparison for key validation to prevent timing attacks.
///     Supports per-key rate limiting via sliding window counters.
/// </summary>
public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly ILogger<InMemoryApiKeyStore> _logger;
    private readonly BotDetectionOptions _options;

    // Pre-computed UTF8 byte arrays for constant-time comparison
    private readonly Dictionary<string, (byte[] KeyBytes, string KeyId, ApiKeyConfig Config)> _keyLookup;

    // Rate limit tracking: keyId -> (minuteWindow, minuteCount, hourWindow, hourCount)
    private readonly ConcurrentDictionary<string, RateLimitState> _rateLimits = new();

    public InMemoryApiKeyStore(
        IOptions<BotDetectionOptions> options,
        ILogger<InMemoryApiKeyStore> logger)
    {
        _logger = logger;
        _options = options.Value;

        // Pre-compute UTF8 bytes for all configured keys
        _keyLookup = new Dictionary<string, (byte[], string, ApiKeyConfig)>();
        foreach (var (keyId, config) in _options.ApiKeys)
        {
            var keyBytes = Encoding.UTF8.GetBytes(keyId);
            _keyLookup[keyId] = (keyBytes, keyId, config);
        }

        _logger.LogInformation("Initialized API key store with {Count} keys", _keyLookup.Count);
    }

    public ApiKeyValidationResult? ValidateKey(string providedKey, string requestPath)
    {
        var (result, _) = ValidateKeyWithReason(providedKey, requestPath);
        return result;
    }

    public (ApiKeyValidationResult? Result, ApiKeyRejection? Rejection) ValidateKeyWithReason(
        string providedKey, string requestPath)
    {
        if (_keyLookup.Count == 0)
            return (null, new ApiKeyRejection(ApiKeyRejectionReason.NotFound));

        var providedBytes = Encoding.UTF8.GetBytes(providedKey);

        // Find matching key using constant-time comparison
        string? matchedKeyId = null;
        ApiKeyConfig? matchedConfig = null;

        foreach (var (_, (keyBytes, keyId, config)) in _keyLookup)
        {
            if (CryptographicOperations.FixedTimeEquals(providedBytes, keyBytes))
            {
                matchedKeyId = keyId;
                matchedConfig = config;
                break;
            }
        }

        if (matchedKeyId == null || matchedConfig == null)
            return (null, new ApiKeyRejection(ApiKeyRejectionReason.NotFound));

        // Check enabled
        if (!matchedConfig.Enabled)
        {
            _logger.LogWarning("API key '{KeyName}' is disabled", matchedConfig.Name);
            return (null, new ApiKeyRejection(ApiKeyRejectionReason.Disabled, matchedConfig.Name));
        }

        // Check expiry
        if (matchedConfig.ExpiresAt.HasValue && DateTimeOffset.UtcNow >= matchedConfig.ExpiresAt.Value)
        {
            _logger.LogWarning("API key '{KeyName}' has expired", matchedConfig.Name);
            return (null, new ApiKeyRejection(ApiKeyRejectionReason.Expired, matchedConfig.Name));
        }

        // Check time window
        if (!string.IsNullOrEmpty(matchedConfig.AllowedTimeWindow))
        {
            if (!IsWithinTimeWindow(matchedConfig.AllowedTimeWindow))
            {
                _logger.LogWarning("API key '{KeyName}' is outside allowed time window '{Window}'",
                    matchedConfig.Name, matchedConfig.AllowedTimeWindow);
                return (null, new ApiKeyRejection(ApiKeyRejectionReason.OutsideTimeWindow, matchedConfig.Name));
            }
        }

        // Check path permissions
        if (!IsPathAllowed(requestPath, matchedConfig))
        {
            _logger.LogWarning("API key '{KeyName}' is not allowed for path '{Path}'",
                matchedConfig.Name, requestPath);
            return (null, new ApiKeyRejection(ApiKeyRejectionReason.PathDenied, matchedConfig.Name));
        }

        // Check rate limits
        if (!CheckRateLimit(matchedKeyId, matchedConfig))
        {
            _logger.LogWarning("API key '{KeyName}' rate limit exceeded", matchedConfig.Name);
            return (null, new ApiKeyRejection(ApiKeyRejectionReason.RateLimitExceeded, matchedConfig.Name));
        }

        var context = new ApiKeyContext
        {
            KeyName = matchedConfig.Name,
            DisabledDetectors = matchedConfig.DisabledDetectors.AsReadOnly(),
            WeightOverrides = matchedConfig.WeightOverrides.AsReadOnly(),
            DetectionPolicyName = matchedConfig.DetectionPolicyName,
            ActionPolicyName = matchedConfig.ActionPolicyName,
            Tags = matchedConfig.Tags.AsReadOnly()
        };

        return (new ApiKeyValidationResult { Context = context, KeyId = matchedKeyId }, null);
    }

    private static bool IsWithinTimeWindow(string window)
    {
        // Format: "HH:mm-HH:mm" UTC
        var parts = window.Split('-');
        if (parts.Length != 2) return false; // Invalid format = deny (fail closed)

        if (!TimeOnly.TryParse(parts[0].Trim(), out var start) ||
            !TimeOnly.TryParse(parts[1].Trim(), out var end))
            return false; // Invalid format = deny (fail closed)

        var now = TimeOnly.FromDateTime(DateTime.UtcNow);

        // Handle overnight windows (e.g., "22:00-06:00")
        if (start <= end)
            return now >= start && now <= end;
        else
            return now >= start || now <= end;
    }

    private static bool IsPathAllowed(string requestPath, ApiKeyConfig config)
    {
        // If no allowed paths specified, all paths are allowed
        if (config.AllowedPaths.Count == 0 && config.DeniedPaths.Count == 0)
            return true;

        // Check denied paths first (if denied, reject regardless of allowed)
        foreach (var denied in config.DeniedPaths)
        {
            if (PathMatchesGlob(requestPath, denied))
                return false;
        }

        // If allowed paths specified, path must match at least one
        if (config.AllowedPaths.Count > 0)
        {
            foreach (var allowed in config.AllowedPaths)
            {
                if (PathMatchesGlob(requestPath, allowed))
                    return true;
            }
            return false; // No allowed pattern matched
        }

        return true; // No allowed paths = all allowed (denied already checked)
    }

    private static bool PathMatchesGlob(string path, string pattern)
    {
        // Simple glob matching: supports ** (any path) and * (single segment)
        if (pattern == "/**" || pattern == "**")
            return true;

        // Normalize
        var normalizedPath = path.TrimEnd('/');
        var normalizedPattern = pattern.TrimEnd('/');

        // Exact match
        if (string.Equals(normalizedPath, normalizedPattern, StringComparison.OrdinalIgnoreCase))
            return true;

        // Handle /** suffix (match path prefix)
        if (normalizedPattern.EndsWith("/**"))
        {
            var prefix = normalizedPattern[..^3]; // Remove /**
            return StartsWithPathSegment(normalizedPath, prefix);
        }

        // Handle /* suffix (match single segment)
        if (normalizedPattern.EndsWith("/*"))
        {
            var prefix = normalizedPattern[..^2]; // Remove /*
            if (!normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            var remainder = normalizedPath[prefix.Length..].TrimStart('/');
            return !remainder.Contains('/'); // Only one more segment
        }

        // StartsWith for simple prefix matching
        return StartsWithPathSegment(normalizedPath, normalizedPattern);
    }

    private static bool StartsWithPathSegment(string path, string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix == "/")
            return true;

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Length == prefix.Length)
            return true;

        return path[prefix.Length] == '/';
    }

    private bool CheckRateLimit(string keyId, ApiKeyConfig config)
    {
        if (config.RateLimitPerMinute <= 0 && config.RateLimitPerHour <= 0)
            return true; // No rate limits

        var now = DateTimeOffset.UtcNow;
        var state = _rateLimits.GetOrAdd(keyId, _ => new RateLimitState());

        lock (state)
        {
            // Check minute window
            if (config.RateLimitPerMinute > 0)
            {
                var minuteWindow = now.ToUnixTimeSeconds() / 60;
                if (state.MinuteWindow != minuteWindow)
                {
                    state.MinuteWindow = minuteWindow;
                    state.MinuteCount = 0;
                }

                if (state.MinuteCount >= config.RateLimitPerMinute)
                    return false;
            }

            // Check hour window
            if (config.RateLimitPerHour > 0)
            {
                var hourWindow = now.ToUnixTimeSeconds() / 3600;
                if (state.HourWindow != hourWindow)
                {
                    state.HourWindow = hourWindow;
                    state.HourCount = 0;
                }

                if (state.HourCount >= config.RateLimitPerHour)
                    return false;
            }

            // Increment counters
            state.MinuteCount++;
            state.HourCount++;
            return true;
        }
    }

    private sealed class RateLimitState
    {
        public long MinuteWindow;
        public int MinuteCount;
        public long HourWindow;
        public int HourCount;
    }
}
