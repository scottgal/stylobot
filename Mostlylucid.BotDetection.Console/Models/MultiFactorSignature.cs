namespace Mostlylucid.BotDetection.Console.Models;

/// <summary>
///     Multi-factor signature with privacy-safe HMAC hashes
/// </summary>
public record MultiFactorSignature
{
    public required string Primary { get; init; } // Combined hash
    public required string UaHash { get; init; } // User-Agent hash
    public required string IpHash { get; init; } // IP address hash
    public required string PathHash { get; init; } // Request path hash
    public required string RefererHash { get; init; } // Referer hash
}