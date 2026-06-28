using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Models.Dashboard.Site;

/// <summary>
///     Si2 (dashboard IA collapse plan): per-endpoint detail page model rendered
///     at <c>/dashboard/site/endpoint?path=...&amp;method=...</c>. Composes four
///     existing primitives into one cohesive view:
///     <list type="bullet">
///         <item><description>
///             Per-endpoint timeseries (hits / bot hits) bucketed over the same
///             window <c>TrafficController</c> uses, so the chart axis matches
///             when the operator pivots from Traffic to a single endpoint.
///         </description></item>
///         <item><description>
///             Top visitors for this endpoint, filtered out of
///             <see cref="Services.SignatureAggregateCache"/> by
///             <see cref="CachedVisitor.LastPath"/>.
///         </description></item>
///         <item><description>
///             Optional PolicyStack indicator scoped to the endpoint via the
///             existing <c>SbPolicyStack</c> view component (StatusBadge embed).
///             Null on remote-mode hosts that don't have the resolver.
///         </description></item>
///         <item><description>
///             Performance (p50 / p95 / sample count) projected from
///             <c>IDashboardEventStore.GetEndpointStatsAsync</c> when available.
///         </description></item>
///     </list>
///     <para>
///     Below the built-in surface, the view walks the
///     <c>SiteEndpointDetailSection</c> contribution slot so packs (ASP.NET pack
///     in particular) can append their own per-endpoint cards.
///     </para>
/// </summary>
/// <param name="Method">HTTP method (uppercase). Defaults to <c>"GET"</c> when
/// the query string omits it -- matches <c>TopByEndpoint</c> in TrafficController
/// which also assumes GET when the cache doesn't carry a method.</param>
/// <param name="Path">URL path the operator is drilling into. Used verbatim in
/// the header and as the filter key for visitor + perf lookups.</param>
/// <param name="Timeseries">Bucketed hit counts split into total + bot-only
/// series; the view renders the two series as overlaid bars.</param>
/// <param name="TopVisitors">Top N visitors that touched this endpoint, sorted
/// by hits descending. Each row links to the existing visitor-detail page.</param>
/// <param name="PolicyScope">PolicyScope for the endpoint, passed to the
/// <c>SbPolicyStack</c> view component (StatusBadge embed). Null when the host
/// is empty (TestServer) -- the view skips the policy section in that case
/// rather than rendering a wildcard-scope badge that would be misleading.</param>
/// <param name="Perf">Endpoint perf snapshot (p50 / p95 / samples). Null when
/// no perf data is available for this (method, path) pair.</param>
public sealed record SiteEndpointDetailModel(
    string Method,
    string Path,
    SiteEndpointTimeseries Timeseries,
    IReadOnlyList<CachedVisitor> TopVisitors,
    PolicyScope? PolicyScope,
    SiteEndpointPerf? Perf);

/// <summary>
///     Per-endpoint timeseries with two parallel series: total hits and the
///     bot-only subset. Bucket boundaries are evenly spaced UTC instants from
///     <c>now - window</c> to <c>now</c>; both series share the same index.
/// </summary>
public sealed record SiteEndpointTimeseries(
    IReadOnlyList<DateTime> Buckets,
    IReadOnlyList<int> Hits,
    IReadOnlyList<int> BotHits);

/// <summary>
///     Lightweight perf projection sourced from
///     <c>IDashboardEventStore.GetEndpointStatsAsync</c>. Uses avg as a stand-in
///     for p50 (the dashboard event store doesn't expose a p50 column today --
///     avg is the closest already-aggregated number and the legacy endpoint
///     detail page makes the same call). p95 + sample count come straight off
///     the stats row.
/// </summary>
public sealed record SiteEndpointPerf(double P50Ms, double P95Ms, int Samples);
