namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Pure-function helpers used by <c>StyloBotDashboardMiddleware</c> to
///     parse the left-nav routes. Extracted from the middleware so they can
///     be unit-tested without the WebApplicationFactory dance.
/// </summary>
public static class DashboardRoutingHelpers
{
    public static DashboardRowRef ParseRowRef(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length switch
        {
            0    => DashboardRowRef.Default,
            1    => new DashboardRowRef(segments[0].ToLowerInvariant()),
            >= 2 => new DashboardRowRef(
                       segments[0].ToLowerInvariant(),
                       segments[1].ToLowerInvariant()),
            _    => DashboardRowRef.Default,
        };
    }

    public static bool IsDashboardRowPath(string relLower)
    {
        if (string.IsNullOrEmpty(relLower)) return false;
        if (relLower.StartsWith("api/",      StringComparison.Ordinal)) return false;
        if (relLower.StartsWith("auth/",     StringComparison.Ordinal)) return false;
        if (relLower.StartsWith("setup",     StringComparison.Ordinal)) return false;
        if (relLower.StartsWith("login",     StringComparison.Ordinal)) return false;
        if (relLower.StartsWith("hub",       StringComparison.Ordinal)) return false;
        if (relLower.StartsWith("partials/", StringComparison.Ordinal)) return false;
        if (relLower.StartsWith("static/",   StringComparison.Ordinal)) return false;
        var segments = relLower.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length is 1 or 2;
    }

    public static string StripTabParam(string queryString)
    {
        if (string.IsNullOrEmpty(queryString)) return "";
        var qs = queryString.StartsWith('?') ? queryString[1..] : queryString;
        var kept = qs.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("tab=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return kept.Count == 0 ? "" : "?" + string.Join('&', kept);
    }

    /// <summary>
    ///     The single window-token -> minutes mapping, shared between a real request's window
    ///     (<c>StyloBotDashboardMiddleware.BuildVisitorsPageWindow</c>) and the materializer's
    ///     Tier 1 pinned-prewarm construction, so the two can never independently drift into
    ///     computing different windows for what's meant to be the same cache envelope.
    /// </summary>
    public static int WindowTokenToMinutes(string token, int fallbackMinutes) => token switch
    {
        "15m" => 15,
        "60m" or "1h" => 60,
        "6h" => 6 * 60,
        "12h" => 12 * 60,
        "24h" or "1d" => 24 * 60,
        "7d" => 7 * 24 * 60,
        "30d" => 30 * 24 * 60,
        _ => fallbackMinutes
    };
}
