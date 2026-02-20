using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Result of API key validation.
/// </summary>
public sealed record ApiKeyValidationResult
{
    public required ApiKeyContext Context { get; init; }

    /// <summary>
    ///     The matched key value (for logging the key name, not the secret).
    /// </summary>
    public required string KeyId { get; init; }
}

/// <summary>
///     Reason an API key was rejected.
/// </summary>
public enum ApiKeyRejectionReason
{
    NotFound,
    Disabled,
    Expired,
    OutsideTimeWindow,
    PathDenied,
    RateLimitExceeded
}

/// <summary>
///     Result when a key is rejected.
/// </summary>
public sealed record ApiKeyRejection(ApiKeyRejectionReason Reason, string? Detail = null);

/// <summary>
///     Abstraction for API key validation. Default implementation reads from
///     <see cref="BotDetectionOptions.ApiKeys"/>. Can be overridden for database-backed keys.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>
    ///     Validates a provided API key against the request path.
    ///     Returns a validated context on success, or null if the key is not found.
    /// </summary>
    /// <param name="providedKey">The raw key value from the request header.</param>
    /// <param name="requestPath">The request path to check against allowed/denied patterns.</param>
    /// <returns>Validation result, or null if not found.</returns>
    ApiKeyValidationResult? ValidateKey(string providedKey, string requestPath);

    /// <summary>
    ///     Validates a key and returns the rejection reason if it fails.
    ///     Returns (result, null) on success or (null, rejection) on failure.
    /// </summary>
    (ApiKeyValidationResult? Result, ApiKeyRejection? Rejection) ValidateKeyWithReason(
        string providedKey, string requestPath);
}
