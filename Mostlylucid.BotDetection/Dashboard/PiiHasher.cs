using System.Security.Cryptography;
using System.Text;

namespace Mostlylucid.BotDetection.Dashboard;

/// <summary>
///     ZERO-PII signature generator using keyed HMAC-SHA256.
///     This is NOT "nice hashing for logs" - this is the core of Stylobot's ability to claim
///     "we store literally zero PII". Raw IP/UA/location never touches disk - only
///     non-reversible, keyed signatures are persisted.
///     Uses HMAC-SHA-256 with hardware-grade random keys (optionally derived via HKDF
///     for time-bounded or per-tenant isolation).
/// </summary>
public sealed class PiiHasher
{
    private readonly byte[] _key;

    /// <summary>
    ///     Create a PII hasher with a secret key.
    ///     The key MUST be:
    ///     - At least 128 bits (16 bytes)
    ///     - Randomly generated (not a password)
    ///     - Stored securely (Key Vault, KMS, env var)
    ///     - NEVER stored alongside the signatures in the database
    /// </summary>
    public PiiHasher(byte[] key)
    {
        if (key is null || key.Length < 16)
            throw new ArgumentException("Key must be at least 128 bits (16 bytes)", nameof(key));

        _key = key;
    }

    /// <summary>
    ///     Create from base64-encoded key (for config file usage).
    /// </summary>
    public static PiiHasher FromBase64Key(string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        return new PiiHasher(key);
    }

    /// <summary>
    ///     Generate a new random key for deployment.
    ///     Call this ONCE per deployment and store in secure configuration.
    /// </summary>
    public static byte[] GenerateKey()
    {
        var key = new byte[32]; // 256 bits
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    ///     Generate a new key and return as base64 (for easy config storage).
    /// </summary>
    public static string GenerateKeyBase64()
    {
        return Convert.ToBase64String(GenerateKey());
    }

    /// <summary>
    ///     Hash an IP address to a consistent signature.
    ///     Same IP always produces same hash (with same salt).
    /// </summary>
    public string HashIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip))
            return "unknown";

        return ComputeHash(ip);
    }

    /// <summary>
    ///     Hash a location (country, city, etc.) to preserve privacy while allowing pattern detection.
    /// </summary>
    public string HashLocation(string? location)
    {
        if (string.IsNullOrEmpty(location))
            return "unknown";

        return ComputeHash(location);
    }

    /// <summary>
    ///     Hash user agent to a fingerprint.
    ///     Allows tracking patterns without storing full UA string.
    /// </summary>
    public string HashUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return "unknown";

        return ComputeHash(userAgent);
    }

    /// <summary>
    ///     Create a composite signature hash from multiple components.
    ///     Example: IP + UserAgent + Path = unique visitor signature
    /// </summary>
    public string ComputeSignature(params string?[] components)
    {
        var combined = string.Join("|", components.Where(c => !string.IsNullOrEmpty(c)));
        return ComputeHash(combined);
    }

    /// <summary>
    ///     Extract prefix from IP for network-level grouping (e.g., /24 subnet).
    ///     Allows detecting attacks from same network without storing full IP.
    /// </summary>
    public string HashIpSubnet(string? ip, int prefixLength = 24)
    {
        if (string.IsNullOrEmpty(ip))
            return "unknown";

        // Extract subnet (simple implementation for IPv4)
        var parts = ip.Split('.');
        if (parts.Length != 4)
            return HashIp(ip); // Fallback for IPv6 or invalid

        var subnet = prefixLength switch
        {
            8 => $"{parts[0]}.0.0.0/8",
            16 => $"{parts[0]}.{parts[1]}.0.0/16",
            24 => $"{parts[0]}.{parts[1]}.{parts[2]}.0/24",
            _ => ip
        };

        return ComputeHash(subnet);
    }

    private string ComputeHash(string input)
    {
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));

        // Truncate to 128 bits (16 bytes) for compact, collision-resistant IDs
        // Still cryptographically strong, but shorter for storage/display
        return Convert.ToBase64String(hash[..16])
            .TrimEnd('=') // Remove padding
            .Replace('+', '-') // URL-safe (RFC 4648 base64url)
            .Replace('/', '_');
    }

    /// <summary>
    ///     Hash pure behavioral features (no PII).
    ///     These signatures are "zero-PII by construction" - they never touch identifying data.
    ///     Examples:
    ///     - Timing patterns (inter-request intervals, jitter)
    ///     - Header presence patterns (accept, sec-ch-ua, combinations)
    ///     - Path shape (/blog/*/comments, not exact slugs)
    ///     - Error ratios, status code buckets
    ///     - Client-side event patterns (JS timing, scroll behavior)
    /// </summary>
    public string HashBehavior(params string[] behaviorFeatures)
    {
        return ComputeSignature(behaviorFeatures);
    }

    /// <summary>
    ///     Create a derived key for time-bounded tracking.
    ///     Enables correlation within a time window, but not across periods.
    ///     Example: Daily rotation prevents long-term tracking even with master key.
    /// </summary>
    public static PiiHasher WithDailyRotation(byte[] masterKey, DateTime date)
    {
        var info = $"stylobot:pii:v1:{date:yyyy-MM-dd}";
        var dailyKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            32,
            info: Encoding.UTF8.GetBytes(info),
            salt: null);

        return new PiiHasher(dailyKey);
    }

    /// <summary>
    ///     Create a derived key for per-tenant isolation.
    ///     Prevents cross-tenant correlation even if one key leaks.
    /// </summary>
    public static PiiHasher ForTenant(byte[] masterKey, string tenantId)
    {
        var info = $"stylobot:tenant:{tenantId}:v1";
        var tenantKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            32,
            info: Encoding.UTF8.GetBytes(info),
            salt: null);

        return new PiiHasher(tenantKey);
    }
}

/// <summary>
///     Extension methods for easily hashing PII in detection records.
/// </summary>
public static class PiiHasherExtensions
{
    /// <summary>
    ///     Create a privacy-preserving signature from common request attributes.
    /// </summary>
    public static string CreateRequestSignature(
        this PiiHasher hasher,
        string? ip,
        string? userAgent,
        string? path = null)
    {
        return hasher.ComputeSignature(ip, userAgent, path);
    }

    /// <summary>
    ///     Create a geo-aware signature (IP subnet + country).
    /// </summary>
    public static string CreateGeoSignature(
        this PiiHasher hasher,
        string? ip,
        string? countryCode)
    {
        var subnet = hasher.HashIpSubnet(ip);
        return hasher.ComputeSignature(subnet, countryCode);
    }
}