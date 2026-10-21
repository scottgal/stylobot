using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.UI.Helpers;

/// <summary>
///     One resolver for the dashboard's selected time window.
///     <para>
///         <see cref="AnalyticsRangeParser.Parse"/> understands duration tokens only
///         (6h / 24h / 7d). The custom range is expressed as
///         <c>?window=custom&amp;from=…&amp;to=…</c>, and "custom" is not a duration, so
///         parsing it alone returns <c>(null, null)</c>. Every widget that then falls
///         back to its own default silently widens or narrows the read — the exact
///         "one canonical window reaches all reads" defect class.
///     </para>
///     <para>
///         Use this instead of calling the parser directly so the list components, the
///         summary, the top-bots strip and the detail builder cannot drift apart.
///     </para>
/// </summary>
public static class DashboardRangeScope
{
    /// <summary>
    ///     Resolve the window for the current request. Duration tokens parse as before;
    ///     <c>custom</c> resolves its explicit pair from the request's <c>from</c>/<c>to</c>
    ///     query values. An unparseable custom pair intentionally returns
    ///     <c>(null, null)</c>, matching the parser's contract (callers fall back to their
    ///     documented default rather than inventing a range).
    /// </summary>
    public static (DateTime? StartTime, DateTime? EndTime) Resolve(HttpContext? httpContext, string? range)
    {
        if (string.Equals(range, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var from = httpContext?.Request.Query["from"].ToString();
            var to = httpContext?.Request.Query["to"].ToString();
            var (customStart, customEnd) = AnalyticsRangeParser.ParseCustom(from, to);
            if (customStart is not null && customEnd is not null)
            {
                return (customStart, customEnd);
            }
            return (null, null);
        }

        return AnalyticsRangeParser.Parse(range);
    }
}
