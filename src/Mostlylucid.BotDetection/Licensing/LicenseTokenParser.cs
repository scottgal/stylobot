using System.Text;
using System.Text.Json;

namespace Mostlylucid.BotDetection.Licensing;

internal sealed record LicenseTokenClaims(DateTimeOffset? ExpiresAt, bool GraceEligible);

internal static class LicenseTokenParser
{
    /// <summary>
    ///     Decodes the JWT payload section (base64url) and extracts exp + grace_eligible.
    ///     Does NOT verify the Ed25519 signature - the token is admin-provided via config.
    ///     Returns null if the token is missing, malformed, or not a 3-part JWT.
    /// </summary>
    public static LicenseTokenClaims? TryParse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            var padded = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var expUnix))
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(expUnix);

            var graceEligible = true; // default true when claim absent (existing tokens)
            if (root.TryGetProperty("grace_eligible", out var graceEl))
                graceEligible = graceEl.GetBoolean();

            return new LicenseTokenClaims(expiresAt, graceEligible);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Immutable snapshot of computed license state.</summary>
internal sealed record LicenseStateSnapshot(
    bool IsActive,
    bool IsInGrace,
    bool LearningFrozen,
    bool LogOnly,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? GraceEndsAt)
{
    public static readonly LicenseStateSnapshot Foss =
        new(true, false, false, false, null, null);

    public static LicenseStateSnapshot Compute(
        DateTimeOffset? expiresAt,
        bool graceEligible,
        DateTimeOffset? graceStartedAt)
    {
        var now = DateTimeOffset.UtcNow;
        var isActive = expiresAt == null || now < expiresAt;

        if (isActive)
            return new(true, false, false, false, expiresAt, null);

        // Expired. Check grace.
        if (!graceEligible || graceStartedAt == null)
            return new(false, false, true, true, expiresAt, null); // immediate log-only

        var graceEndsAt = graceStartedAt.Value.AddDays(30);
        if (now < graceEndsAt)
            return new(false, true, true, false, expiresAt, graceEndsAt); // in grace

        return new(false, false, true, true, expiresAt, graceEndsAt); // post-grace
    }
}
