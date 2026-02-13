using Mostlylucid.BotDetection.Console.Models;
using Serilog;

namespace Mostlylucid.BotDetection.Console.Helpers;

/// <summary>
///     Validates configuration settings with fail-fast on critical issues
/// </summary>
public static class ConfigValidator
{
    /// <summary>
    ///     Validate HMAC signature key configuration
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when using default key in production mode</exception>
    public static void ValidateHmacKey(SignatureLoggingConfig config, string mode)
    {
        // Check for insecure default key
        if (config.SignatureHashKey == "DEFAULT_INSECURE_KEY_CHANGE_ME" ||
            config.SignatureHashKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("⚠️  INSECURE HMAC KEY DETECTED - Using default key is DANGEROUS!");
            Log.Warning("   Generate a secure key with: openssl rand -base64 32");
            Log.Warning("   Set in appsettings.json: SignatureLogging:SignatureHashKey");

            // FAIL-FAST in production mode
            if (mode.Equals("production", StringComparison.OrdinalIgnoreCase))
            {
                Log.Fatal("Cannot use default HMAC key in production mode - TERMINATING");
                throw new InvalidOperationException(
                    "Cannot use default HMAC key in production mode. " +
                    "Set SignatureLogging:SignatureHashKey to a secure random value. " +
                    "Generate with: openssl rand -base64 32");
            }

            Log.Warning("   Demo mode: Continuing with default key (NOT SECURE FOR PRODUCTION)");
        }
        else if (config.SignatureHashKey.Length < 32)
        {
            Log.Warning("⚠️  SHORT HMAC KEY DETECTED - Key should be at least 32 characters for HMAC-SHA256");
            Log.Warning("   Generate a secure key with: openssl rand -base64 32");
        }
        else
        {
            Log.Information("✓ HMAC key validated ({Length} chars)", config.SignatureHashKey.Length);
        }
    }
}