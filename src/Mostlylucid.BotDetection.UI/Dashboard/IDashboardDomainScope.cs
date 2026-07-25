using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     DI seam that supplies the DASHBOARD-WIDE domain selection for the current
///     request. Every batch-composed data widget (SbVisitorList / SbEndpointsList /
///     SbCountriesList and friends) and the three view-components' self-fetch
///     fallback thread the result of <see cref="GetSelectedDomains"/> into the
///     underlying store reads (<c>GetTopBotsAsync</c> / <c>GetEndpointStatsAsync</c> /
///     <c>GetCountryStatsAsync</c> / <c>GetVisitorSegmentCountsAsync</c>), all of
///     which accept an optional <c>IReadOnlyList&lt;string&gt;? domains</c> filter.
///     <para>
///         The FOSS default (<see cref="NullDashboardDomainScope"/>) returns
///         <c>null</c> — i.e. NO domain filter, byte-for-byte today's behavior.
///         A commercial pack registers its own implementation BEFORE the dashboard
///         DI runs (or replaces the <c>TryAddSingleton</c> default) to source the
///         operator's selected domain(s) from a server-side selector.
///     </para>
/// </summary>
public interface IDashboardDomainScope
{
    /// <summary>
    ///     Returns the domains the dashboard is currently scoped to, or <c>null</c>
    ///     (or an empty list) for "all domains / no filter". A non-null, non-empty
    ///     result restricts every scoped widget to those hosts.
    /// </summary>
    IReadOnlyList<string>? GetSelectedDomains(HttpContext context);
}

/// <summary>
///     FOSS default: no dashboard-wide domain scoping. Always returns <c>null</c>
///     so the default (unselected) path is identical to pre-seam behavior.
///     Registered via <c>TryAddSingleton</c> so a commercial impl overrides it.
/// </summary>
public sealed class NullDashboardDomainScope : IDashboardDomainScope
{
    public IReadOnlyList<string>? GetSelectedDomains(HttpContext context) => null;
}
