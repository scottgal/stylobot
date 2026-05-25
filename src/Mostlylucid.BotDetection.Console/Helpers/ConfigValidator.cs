using System.Security.Cryptography;
using Mostlylucid.BotDetection.Console.Models;
using Serilog;

namespace Mostlylucid.BotDetection.Console.Helpers;

/// <summary>
///     Validates and resolves configuration settings. Loud warnings, no
///     fail-fast walls -- the gateway always starts (6.8.2+).
/// </summary>
public static class ConfigValidator
{
    /// <summary>
    ///     Resolve the HMAC signature key for the active mode.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Previous behaviour (pre-6.8.2):</strong> production
    ///         mode threw on a default/missing key. This was operator-hostile
    ///         -- the brownfield retrofit ran into a wall on first start.
    ///     </para>
    ///     <para>
    ///         <strong>Current behaviour:</strong> if no key is configured,
    ///         generate a fresh 32-byte random key in-memory for this process
    ///         lifetime, log a loud warning naming the trade-off (signatures
    ///         do not survive a restart -- searches by signature across
    ///         restarts will miss), and continue. Operators who care about
    ///         cross-restart signature continuity set
    ///         <c>SignatureLogging:SignatureHashKey</c> explicitly (env var,
    ///         appsettings, Key Vault, etc.).
    ///     </para>
    ///     <para>
    ///         Crashes always trump features: the gateway must start. The
    ///         security goal (no shared default key across deployments) is
    ///         preserved because each process generates its own unique key.
    ///     </para>
    /// </remarks>
    /// <param name="configuredKey">The key value pulled from configuration (may be null / empty / a placeholder).</param>
    /// <param name="mode">Active runtime mode (<c>demo</c> / <c>production</c>).</param>
    /// <returns>The effective HMAC key the gateway should use.</returns>
    public static string ResolveHmacKey(string? configuredKey, string mode)
    {
        var isProduction = mode.Equals("production", StringComparison.OrdinalIgnoreCase);
        var configured = configuredKey ?? string.Empty;
        var isDefaultKey = string.IsNullOrWhiteSpace(configured) ||
                           configured == "DEFAULT_INSECURE_KEY_CHANGE_ME" ||
                           configured.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);

        if (!isDefaultKey)
        {
            if (configured.Length < 32)
            {
                Log.Warning("⚠️  SHORT HMAC KEY DETECTED - Key should be at least 32 characters for HMAC-SHA256");
                Log.Warning("   Generate a secure key with: stylobot genkey  (or: openssl rand -base64 32)");
            }
            else
            {
                Log.Information("✓ HMAC key validated ({Length} chars)", configured.Length);
            }
            return configured;
        }

        // No key configured. Auto-generate a fresh random one for this
        // process; warn loud and continue.
        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        if (isProduction)
        {
            Log.Warning("⚠️  No SignatureLogging:SignatureHashKey configured.");
            Log.Warning("   Generated a random key for this process. Signatures will NOT match across restarts --");
            Log.Warning("   visitors will appear as new on restart, and dashboard search by signature will miss.");
            Log.Warning("   To persist signatures across restarts, set SignatureLogging:SignatureHashKey to a");
            Log.Warning("   stable secret (`stylobot genkey` produces one). The key never needs to rotate.");
        }
        else
        {
            Log.Warning(
                "No SignatureLogging:SignatureHashKey configured -- using a per-process random key " +
                "(signatures will not survive restart). Set the key explicitly for stable signatures.");
        }

        return generated;
    }

    /// <summary>
    ///     Back-compat shim for callers that still pass the full config and
    ///     expected a void return. Returns nothing; logs warnings. Does NOT
    ///     mutate the config (init-only properties) -- new callers should
    ///     use <see cref="ResolveHmacKey"/> and feed the returned value into
    ///     the config initializer.
    /// </summary>
    [Obsolete("Use ResolveHmacKey(configuredKey, mode) and pass the returned value into the SignatureLoggingConfig initializer.")]
    public static void ValidateHmacKey(SignatureLoggingConfig config, string mode)
    {
        _ = ResolveHmacKey(config.SignatureHashKey, mode);
    }
}
