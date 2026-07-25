using System.Security.Cryptography;
using System.Text;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Services.Auth;

/// <summary>
///     Verifies a submitted username/password against the configured FOSS
///     view-auth credential (<see cref="DashboardAuthOptions"/>). Both the username
///     comparison and the password verification always run so a caller can't use
///     response timing to distinguish "unknown username" from "wrong password".
/// </summary>
public sealed class DashboardViewCredentialVerifier
{
    /// <summary>
    ///     True only when Login mode is configured and both the username and password match.
    /// </summary>
    public bool Verify(DashboardAuthOptions options, string username, string password)
    {
        if (options is null || !options.IsConfigured) return false;

        var usernameMatches = FixedTimeEquals(username, options.Username!);
        var passwordMatches = DashboardPasswordHasher.Verify(options.PasswordHash, password);
        return usernameMatches && passwordMatches;
    }

    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }
}
