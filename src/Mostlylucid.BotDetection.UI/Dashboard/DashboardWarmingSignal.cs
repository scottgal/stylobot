using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Per-request "this dataset is a cold placeholder, not genuinely empty" signal
///     (design doc §9 refinement: warming state should render a spinner, never an
///     empty/no-data state). Populated by a store-layer decorator (e.g. the
///     commercial <c>StaleWhileRevalidatingDashboardEventStore</c>) on a true cold
///     miss; read by view-layer code (FOSS ViewComponents, or the domain-breakdown
///     cards) deciding spinner-vs-empty. Tag format: <c>{datasetKind}</c> (e.g.
///     "summary") or, for a single-domain-scoped call, <c>{datasetKind}:{domain}</c>
///     -- matches the <c>DashboardFreshnessBeacon.Surfaces</c> naming convention so
///     the same dataset-kind vocabulary is used everywhere.
///     <para>
///         Lives in FOSS (moved from the commercial website project) so FOSS
///         ViewComponents can read it directly instead of only the commercial
///         layer that writes it -- a plain <c>HttpContext.Items</c> tag, no
///         commercial-specific dependency.
///     </para>
/// </summary>
public static class DashboardWarmingSignal
{
    public const string HttpContextItemsKey = "sb.dashboard.warming";

    /// <summary>True if <paramref name="datasetKind"/> (optionally scoped to <paramref name="domain"/>) was served as a cold placeholder this request.</summary>
    public static bool IsWarming(HttpContext? context, string datasetKind, string? domain = null)
    {
        if (context?.Items.TryGetValue(HttpContextItemsKey, out var raw) == true && raw is HashSet<string> tags)
            return tags.Contains(domain is null ? datasetKind : $"{datasetKind}:{domain}");
        return false;
    }

    /// <summary>Public (not internal) so a store-layer decorator in a different assembly (e.g. the commercial website) can stamp it.</summary>
    public static void MarkWarming(HttpContext? context, string tag)
    {
        if (context is null) return;
        if (context.Items[HttpContextItemsKey] is not HashSet<string> tags)
        {
            tags = new HashSet<string>(StringComparer.Ordinal);
            context.Items[HttpContextItemsKey] = tags;
        }
        tags.Add(tag);
    }
}
