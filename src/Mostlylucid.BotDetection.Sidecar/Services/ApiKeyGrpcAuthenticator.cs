using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Sidecar.Services;

/// <summary>
///     Validates the <c>x-sb-api-key</c> gRPC metadata against the configured API key
///     store. Kept separate from <see cref="ApiKeyInterceptor" /> so the decision logic
///     can be unit tested without a <see cref="Grpc.Core.ServerCallContext" />.
/// </summary>
public sealed class ApiKeyGrpcAuthenticator
{
    /// <summary>gRPC metadata entry carrying the API key. Lower-case per HTTP/2 convention.</summary>
    public const string MetadataKey = "x-sb-api-key";

    private readonly IApiKeyStore _store;

    public ApiKeyGrpcAuthenticator(IApiKeyStore store, IOptions<BotDetectionOptions> options)
    {
        _store = store;
        KeysConfigured = options.Value.ApiKeys.Count > 0;
    }

    /// <summary>
    ///     True when at least one API key is configured. gRPC authentication is enforced
    ///     only when keys exist; when none are configured the host-level startup guard
    ///     decides whether running unauthenticated is permitted.
    /// </summary>
    public bool KeysConfigured { get; }

    /// <summary>
    ///     Validates a presented key. <paramref name="method" /> is the gRPC method path,
    ///     passed through for per-key path scoping. Returns the rejection reason on failure.
    /// </summary>
    public (bool Ok, string? Error) Validate(string? presentedKey, string method)
    {
        if (string.IsNullOrEmpty(presentedKey))
            return (false, "missing x-sb-api-key metadata");

        var (result, rejection) = _store.ValidateKeyWithReason(presentedKey, method);
        if (result is not null)
            return (true, null);

        return (false, $"API key rejected: {rejection?.Reason.ToString() ?? "Unknown"}");
    }
}
