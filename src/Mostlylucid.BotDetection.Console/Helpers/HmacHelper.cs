using System.Security.Cryptography;
using System.Text;
using Mostlylucid.BotDetection.Console.Models;

namespace Mostlylucid.BotDetection.Console.Helpers;

/// <summary>
///     HMAC-SHA256 hashing utilities for zero-PII signature generation
/// </summary>
public static class HmacHelper
{
    /// <summary>
    ///     Compute HMAC-SHA256 hash and return truncated hex string
    /// </summary>
    public static string ComputeHmacHash(byte[] key, string input)
    {
        if (string.IsNullOrEmpty(input))
            return "unknown";
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));

        // Truncate to 128 bits (16 bytes) for compact, collision-resistant IDs
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    /// <summary>
    ///     Compute multi-factor signature using HMAC-SHA256 for zero-PII logging
    /// </summary>
    public static MultiFactorSignature ComputeMultiFactorSignature(
        string secretKey,
        string userAgent,
        string clientIp,
        string path,
        string referer)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secretKey);

        return new MultiFactorSignature
        {
            Primary = ComputeHmacHash(keyBytes, $"{userAgent}|{clientIp}|{path}"),
            UaHash = ComputeHmacHash(keyBytes, userAgent),
            IpHash = ComputeHmacHash(keyBytes, clientIp),
            PathHash = ComputeHmacHash(keyBytes, path),
            RefererHash = string.IsNullOrEmpty(referer) ? "none" : ComputeHmacHash(keyBytes, referer)
        };
    }
}