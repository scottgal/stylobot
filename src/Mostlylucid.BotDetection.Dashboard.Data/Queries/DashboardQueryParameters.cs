namespace Mostlylucid.BotDetection.Dashboard.Data.Queries;

/// <summary>
///     Shared normalization for dashboard query filters. The HTTP surface and the
///     in-process traffic query must agree on what a scope means before either one
///     hands it to a store: repeated <c>domain</c> params are trimmed, lower-cased,
///     de-duplicated and ordered, and audience tokens are trimmed/lower-cased.
///     <para>
///     This is deliberately normalization only — validation (count/length/control
///     characters) stays with the caller that owns the user-facing error message.
///     </para>
/// </summary>
public static class DashboardQueryParameters
{
    /// <summary>
    ///     Normalize repeated domain filters to the canonical set used by every store:
    ///     trimmed, lower-cased, distinct (ordinal) and ordered (ordinal). Returns
    ///     <c>null</c> when nothing usable was supplied so callers can keep the
    ///     "no filter" path unchanged.
    /// </summary>
    public static IReadOnlyList<string>? NormalizeDomains(IEnumerable<string>? domains)
    {
        if (domains is null) return null;
        var normalized = domains
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>Normalize an audience token; blank means "no audience filter".</summary>
    public static string? NormalizeAudience(string? audience) =>
        string.IsNullOrWhiteSpace(audience) ? null : audience.Trim().ToLowerInvariant();
}
