using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for the dashboard "License" card. Combines the runtime warn-never-lock
///     stats from <see cref="DomainEntitlementValidator"/> with parsed license JWT claims
///     so the operator sees both <i>what was issued</i> and <i>what we're observing</i>.
///
///     <para>
///     The card has five visible states (see <see cref="LicenseStatusKind"/>); the partial
///     view picks the rendering off this enum. The OSS / unconfigured state intentionally
///     renders as a single muted line - never nag, per
///     <c>stylobot-commercial/docs/licensing-simplified.md §admin-dashboard</c>.
///     </para>
/// </summary>
public sealed class LicenseCardModel
{
    public required string BasePath { get; init; }

    /// <summary>Live runtime stats - counts, top mismatched hosts. Always present (validator returns NotEnforced when nothing's configured).</summary>
    public required DomainEntitlementStats Stats { get; init; }

    /// <summary>Parsed JWT claims, or null when no token / unparseable token.</summary>
    public LicenseClaims? Claims { get; init; }

    /// <summary>Computed at model build time (combines token expiry + validator stats).</summary>
    public required LicenseStatusKind Status { get; init; }

    /// <summary>Days until expiry, or null when no expiry / unconfigured.</summary>
    public int? DaysUntilExpiry { get; init; }

    /// <summary>Days until grace ends (only relevant when <see cref="Status"/> is <see cref="LicenseStatusKind.Grace"/>).</summary>
    public int? DaysUntilGraceEnds { get; init; }
}

/// <summary>
///     Builds <see cref="LicenseCardModel"/> from the current request scope. Both the
///     middleware-hosted dashboard and the controller-hosted dashboard call this so the
///     status logic stays in one place.
/// </summary>
public static class LicenseCardModelBuilder
{
    public static LicenseCardModel Build(HttpContext context, string basePath)
    {
        var validator = context.RequestServices.GetService<DomainEntitlementValidator>();
        var stats = validator?.GetStatistics()
            ?? new DomainEntitlementStats(false, Array.Empty<string>(), 0, 0, 0, 0, Array.Empty<HostObservation>());

        var token = context.RequestServices
            .GetService<IOptions<BotDetectionOptions>>()?.Value.Licensing?.Token;
        var claims = LicenseClaimsParser.TryParse(token);

        var (status, daysToExpiry, daysToGraceEnd) = ComputeStatus(claims, stats);

        return new LicenseCardModel
        {
            BasePath = basePath,
            Stats = stats,
            Claims = claims,
            Status = status,
            DaysUntilExpiry = daysToExpiry,
            DaysUntilGraceEnds = daysToGraceEnd
        };
    }

    /// <summary>
    ///     Status precedence: explicit token claims > observed mismatch > unconfigured.
    ///     Trial flag wins over Active label even when both could apply (a trial is still
    ///     "active" but operators want to see the trial countdown specifically).
    /// </summary>
    public static (LicenseStatusKind Status, int? DaysToExpiry, int? DaysToGraceEnd) ComputeStatus(
        LicenseClaims? claims,
        DomainEntitlementStats stats)
    {
        if (claims is null && !stats.IsEnforcing)
            return (LicenseStatusKind.Unconfigured, null, null);

        if (claims is null)
        {
            return stats.RequestsMismatched > 0
                ? (LicenseStatusKind.DomainMismatchOnly, null, null)
                : (LicenseStatusKind.Unconfigured, null, null);
        }

        if (claims.ExpiresAt is not { } expiry)
            return (claims.IsTrial ? LicenseStatusKind.Trial : LicenseStatusKind.Active, null, null);

        var now = DateTimeOffset.UtcNow;
        var graceEnd = expiry.AddDays(claims.GraceDays);

        if (now < expiry)
        {
            var days = (int)Math.Ceiling((expiry - now).TotalDays);
            return (claims.IsTrial ? LicenseStatusKind.Trial : LicenseStatusKind.Active, days, null);
        }

        if (now < graceEnd)
        {
            var graceDaysLeft = (int)Math.Ceiling((graceEnd - now).TotalDays);
            return (LicenseStatusKind.Grace, 0, graceDaysLeft);
        }

        return (LicenseStatusKind.Expired, 0, 0);
    }
}

/// <summary>The five UI states the license card renders.</summary>
public enum LicenseStatusKind
{
    /// <summary>OSS / no license configured. Renders the single muted line.</summary>
    Unconfigured,

    /// <summary>Paid license, in effect, expiry > 0 days away.</summary>
    Active,

    /// <summary>Trial license (<c>is_trial</c> claim true), still within trial window.</summary>
    Trial,

    /// <summary>Past expiry but inside the grace window - premium features still work, banner shown.</summary>
    Grace,

    /// <summary>Past expiry + grace - premium features lock, core detection continues.</summary>
    Expired,

    /// <summary>Domains configured (likely from gateway-side config) but no JWT, and we're seeing mismatches in traffic.</summary>
    DomainMismatchOnly
}

/// <summary>
///     Subset of the signed license payload we surface in the dashboard. We only parse what
///     the card displays - additional claims like <c>limits</c> or <c>features</c> are kept
///     in <see cref="Features"/> for future expansion but otherwise unused by v1.
/// </summary>
public sealed record LicenseClaims(
    string IssuedTo,
    string Tier,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    bool IsTrial,
    int GraceDays,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> Features,
    string? LicenseId);

/// <summary>
///     Decode the signed-license JWT into <see cref="LicenseClaims"/>. The JWT here is the
///     JSON object produced by <c>LicenseSigningService.SignLicense</c> - payload fields plus
///     a <c>signature</c> field. We don't verify the signature here (the caller can use
///     <see cref="VendorKeys"/> + <c>Ed25519Signer</c> for that); this method's job is to
///     surface what's <i>claimed</i> so the dashboard can warn even if the signature is bad.
/// </summary>
public static class LicenseClaimsParser
{
    /// <summary>Returns null on any parse failure; never throws.</summary>
    public static LicenseClaims? TryParse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            using var doc = JsonDocument.Parse(token);
            var root = doc.RootElement;

            // Field names match LicenseIssuer.LicensePayload - PascalCase (no naming policy).
            var issuedTo = ReadString(root, "IssuedTo") ?? "Unknown";
            var tier = ReadString(root, "Tier") ?? "unknown";
            var issuedAt = ReadOffset(root, "IssuedAt");
            var expiry = ReadOffset(root, "Expiry");
            var isTrial = ReadBool(root, "IsTrial") ?? false;
            var graceDays = ReadInt(root, "GraceDays") ?? 14;
            var domains = ReadStringList(root, "Domains");
            var features = ReadStringList(root, "Features");
            var licenseId = ReadString(root, "LicenseId");

            return new LicenseClaims(
                IssuedTo: issuedTo,
                Tier: tier,
                IssuedAt: issuedAt,
                ExpiresAt: expiry,
                IsTrial: isTrial,
                GraceDays: graceDays,
                Domains: domains,
                Features: features,
                LicenseId: licenseId);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? ReadBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    private static int? ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static DateTimeOffset? ReadOffset(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return DateTimeOffset.TryParse(s, out var dto) ? dto : (DateTimeOffset?)null;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) list.Add(s);
        return list;
    }
}