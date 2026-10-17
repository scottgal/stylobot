using Microsoft.AspNetCore.Identity;

namespace Mostlylucid.BotDetection.UI.Services.Auth;

/// <summary>
///     Shared PBKDF2 hasher for the FOSS config-credential dashboard login. The
///     <c>stylobot dashboard hash-password</c> CLI and the login-POST verifier MUST
///     go through this one type so a CLI-generated hash always verifies against a
///     login attempt. Uses ASP.NET Core Identity's <see cref="PasswordHasher{T}"/>
///     (already in the AOT closure); the generic type argument does not participate
///     in the hash string, so a marker object is sufficient and keeps this decoupled
///     from any user type.
/// </summary>
public static class DashboardPasswordHasher
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Marker = new();

    /// <summary>Hash a plaintext password into a self-contained PBKDF2 string.</summary>
    public static string Hash(string password) => Hasher.HashPassword(Marker, password);

    /// <summary>Verify a plaintext password against a stored hash. False on any null/empty/mismatch.</summary>
    public static bool Verify(string? hash, string? password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password)) return false;
        return Hasher.VerifyHashedPassword(Marker, hash, password) != PasswordVerificationResult.Failed;
    }
}
