using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     Service for generating and validating signed tokens for browser fingerprint requests.
///     Uses HMAC-SHA256 with request context binding to prevent token replay/spoofing.
/// </summary>
public interface IBrowserTokenService
{
    /// <summary>
    ///     Generates a signed token for the current request context.
    ///     Token is bound to IP address and has a limited lifetime.
    /// </summary>
    string GenerateToken(HttpContext context);

    /// <summary>
    ///     Validates a token received with a fingerprint submission.
    ///     Returns the token payload if valid, null if invalid.
    /// </summary>
    BrowserTokenPayload? ValidateToken(HttpContext context, string token);
}

/// <summary>
///     Payload embedded in the browser token.
/// </summary>
public record BrowserTokenPayload
{
    public string RequestId { get; init; } = "";
    public string IpHash { get; init; } = "";
    public long IssuedAt { get; init; }
    public long ExpiresAt { get; init; }
}

public class BrowserTokenService : IBrowserTokenService
{
    // Cache used tokens to prevent replay attacks
    private const string UsedTokenCachePrefix = "MLBotD:UsedToken:";
    private readonly IMemoryCache _cache;
    private readonly byte[] _key;
    private readonly ILogger<BrowserTokenService> _logger;
    private readonly BotDetectionOptions _options;

    public BrowserTokenService(
        IOptions<BotDetectionOptions> options,
        ILogger<BrowserTokenService> logger,
        IMemoryCache cache)
    {
        _options = options.Value;
        _logger = logger;
        _cache = cache;

        // Derive signing key from configured secret or generate one
        var secret = _options.ClientSide.TokenSecret;
        if (string.IsNullOrEmpty(secret))
        {
            // Generate a random key for this instance (tokens won't survive restarts)
            _key = RandomNumberGenerator.GetBytes(32);
            _logger.LogWarning(
                "No ClientSide.TokenSecret configured - using random key. " +
                "Browser tokens will not survive application restarts.");
        }
        else
        {
            // Derive key from secret using HKDF
            _key = DeriveKey(secret);
        }
    }

    public string GenerateToken(HttpContext context)
    {
        var opts = _options.ClientSide;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var payload = new BrowserTokenPayload
        {
            RequestId = Guid.NewGuid().ToString("N")[..16],
            IpHash = HashIp(GetClientIp(context)),
            IssuedAt = now,
            ExpiresAt = now + opts.TokenLifetimeSeconds
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var payloadBase64 = Convert.ToBase64String(payloadBytes);

        // Sign payload
        using var hmac = new HMACSHA256(_key);
        var signature = hmac.ComputeHash(payloadBytes);
        var signatureBase64 = Convert.ToBase64String(signature);

        // Token format: payload.signature (both base64)
        return $"{payloadBase64}.{signatureBase64}";
    }

    public BrowserTokenPayload? ValidateToken(HttpContext context, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogDebug("Empty token received");
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            _logger.LogDebug("Invalid token format");
            return null;
        }

        try
        {
            var payloadBase64 = parts[0];
            var signatureBase64 = parts[1];

            // Decode payload
            var payloadBytes = Convert.FromBase64String(payloadBase64);
            var expectedSignature = Convert.FromBase64String(signatureBase64);

            // Verify signature
            using var hmac = new HMACSHA256(_key);
            var actualSignature = hmac.ComputeHash(payloadBytes);

            if (!CryptographicOperations.FixedTimeEquals(actualSignature, expectedSignature))
            {
                _logger.LogDebug("Token signature mismatch");
                return null;
            }

            // Parse payload
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);
            var payload = JsonSerializer.Deserialize<BrowserTokenPayload>(payloadJson);

            if (payload == null)
            {
                _logger.LogDebug("Failed to deserialize token payload");
                return null;
            }

            // Check expiration
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now > payload.ExpiresAt)
            {
                _logger.LogDebug("Token expired at {ExpiresAt}, now is {Now}",
                    payload.ExpiresAt, now);
                return null;
            }

            // Check IP binding
            var currentIpHash = HashIp(GetClientIp(context));
            if (payload.IpHash != currentIpHash)
            {
                _logger.LogDebug("Token IP mismatch");
                return null;
            }

            // Check for replay attack
            var cacheKey = $"{UsedTokenCachePrefix}{payload.RequestId}";
            if (_cache.TryGetValue(cacheKey, out _))
            {
                _logger.LogWarning("Token replay detected for request {RequestId}",
                    payload.RequestId);
                return null;
            }

            // Mark token as used
            var expiry = TimeSpan.FromSeconds(payload.ExpiresAt - now + 60);
            _cache.Set(cacheKey, true, expiry);

            return payload;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Token validation failed");
            return null;
        }
    }

    private static byte[] DeriveKey(string secret)
    {
        // Use HKDF to derive a proper key from the secret
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var salt = Encoding.UTF8.GetBytes("MLBotD-v1-TokenKey");
        var info = Encoding.UTF8.GetBytes("browser-token-signing");

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, secretBytes, 32, salt, info);
    }

    private static string HashIp(string ip)
    {
        // SHA256 for IP hashing - MUST match ClientSideDetector.HashIp
        var ipBytes = Encoding.UTF8.GetBytes(ip + ":MLBotD-IP-Salt-v2");
        var hash = SHA256.HashData(ipBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetClientIp(HttpContext context)
    {
        // Check common proxy headers
        var headers = context.Request.Headers;

        if (headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrEmpty(cfIp))
            return cfIp.ToString();

        if (headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrEmpty(xff))
        {
            var firstIp = xff.ToString().Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(firstIp))
                return firstIp;
        }

        if (headers.TryGetValue("X-Real-IP", out var realIp) && !string.IsNullOrEmpty(realIp))
            return realIp.ToString();

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}