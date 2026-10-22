using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.Dashboard.Data.Queries;

/// <summary>
///     The truthfulness scope of an endpoint-stats read. The endpoint table must label
///     what the numbers actually are, not what the page's window selector asked for.
///     <para>
///     <see cref="Lifetime"/> — the bounded endpoint counter table, no time dimension.
///     <see cref="Windowed"/> — a store that can honour the requested range (PG hourly
///     rollup, or the live union over it).
///     <see cref="RetainedWindow"/> — the SQLite internal-audience fallback: raw retained
///     detections filtered by the requested window/retention, not lifetime counters.
///     </para>
/// </summary>
public static class EndpointStatsScope
{
    public const string Lifetime = "lifetime";
    public const string Windowed = "windowed";
    public const string RetainedWindow = "retained-window";
    public const string InternalUnavailable = "internal-unavailable";

    /// <summary>
    ///     Resolve the honest label for one read. A windowed-capable store always wins.
    ///     A lifetime-only store is lifetime EXCEPT for the explicit internal opt-in with
    ///     a real window: that path deliberately falls back to raw retained detections
    ///     and therefore must not be presented as lifetime totals.
    /// </summary>
    public static string Resolve(
        bool supportsWindowedStats, bool supportsInternalEndpointStats,
        string? audienceFilter, DateTime? startTime, DateTime? endTime)
    {
        var internalOptIn =
            string.Equals(audienceFilter, "all_incl_internal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(audienceFilter, "internal", StringComparison.OrdinalIgnoreCase);
        if (internalOptIn && !supportsInternalEndpointStats) return InternalUnavailable;
        if (supportsWindowedStats) return Windowed;
        return internalOptIn && (startTime.HasValue || endTime.HasValue)
            ? RetainedWindow
            : Lifetime;
    }

    /// <summary>Backward-compatible overload for callers that have not yet threaded the internal capability.</summary>
    public static string Resolve(
        bool supportsWindowedStats, string? audienceFilter, DateTime? startTime, DateTime? endTime) =>
        Resolve(supportsWindowedStats, false, audienceFilter, startTime, endTime);

    /// <summary>Request-scoped key for the partial/lean path (the composed path carries it on the bundle).</summary>
    public const string ItemsKey = "sb.dashboard.endpoints.scope";

    /// <summary>Stamp the resolved scope on the request for the HTMX/partial render path.</summary>
    public static void Set(
        HttpContext? context, bool supportsWindowedStats, bool supportsInternalEndpointStats,
        string? audienceFilter, DateTime? startTime, DateTime? endTime)
    {
        if (context is null) return;
        context.Items[ItemsKey] = Resolve(
            supportsWindowedStats, supportsInternalEndpointStats, audienceFilter, startTime, endTime);
    }

    /// <summary>Backward-compatible setter overload.</summary>
    public static void Set(
        HttpContext? context, bool supportsWindowedStats, string? audienceFilter,
        DateTime? startTime, DateTime? endTime) =>
        Set(context, supportsWindowedStats, false, audienceFilter, startTime, endTime);

    /// <summary>
    ///     Read the request-scoped scope, or null when this request carries no producer
    ///     metadata. Callers must fall back explicitly (legacy capability, then Lifetime)
    ///     instead of treating "no marker" as a windowed claim.
    /// </summary>
    public static string? ReadOrNull(HttpContext? context) =>
        context?.Items[ItemsKey] as string;

    /// <summary>Backward-compatible alias; nullable on purpose so callers can apply legacy fallbacks.</summary>
    public static string? Read(HttpContext? context) => ReadOrNull(context);

    /// <summary>
    ///     Validate a scope string from metadata/URL; case-insensitive, and unknown values
    ///     are conservative Lifetime.
    /// </summary>
    public static string Normalize(string? scope) =>
        string.Equals(scope, Lifetime, StringComparison.OrdinalIgnoreCase) ? Lifetime
        : string.Equals(scope, Windowed, StringComparison.OrdinalIgnoreCase) ? Windowed
        : string.Equals(scope, RetainedWindow, StringComparison.OrdinalIgnoreCase) ? RetainedWindow
        : string.Equals(scope, InternalUnavailable, StringComparison.OrdinalIgnoreCase) ? InternalUnavailable
        : Lifetime;
}
