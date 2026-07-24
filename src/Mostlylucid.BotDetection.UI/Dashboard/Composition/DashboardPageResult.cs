using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     The resolved per-page dataset bundle, produced by <see cref="IDashboardPageComposer"/>
///     and stashed in <c>HttpContext.Items["sb.dashboard.pageresult"]</c>.
///     ViewComponents read their slice from this result when present; otherwise they
///     fall back to their existing self-fetch (backward-compatible path).
/// </summary>
public sealed class DashboardPageResult
{
    private readonly DashboardDatasetBundle? _bundle;
    private readonly IReadOnlyDictionary<string, object?>? _extensions;

    public DashboardPageResult(
        DashboardDatasetBundle bundle,
        IReadOnlyDictionary<string, object?>? extensions = null,
        IReadOnlyList<ClusterViewModel>? clustersRaw = null,
        ClusterDiagnosticsViewModel? clusterDiagnosticsRaw = null,
        IReadOnlyList<DashboardTopBotEntry>? topBotsRaw = null,
        IReadOnlyList<SessionListEntry>? sessionsRaw = null,
        int? sessionsRawTotalCount = null,
        IReadOnlyList<ThreatEntry>? threatsRaw = null)
    {
        _bundle = bundle;
        _extensions = extensions;
        ClustersRaw = clustersRaw;
        ClusterDiagnosticsRaw = clusterDiagnosticsRaw;
        TopBotsRaw = topBotsRaw;
        SessionsRaw = sessionsRaw;
        SessionsRawTotalCount = sessionsRawTotalCount;
        ThreatsRaw = threatsRaw;
    }

    private DashboardPageResult(bool isWarming)
    {
        _bundle = null;
        IsWarming = isWarming;
    }

    /// <summary>
    ///     A cold-miss placeholder: no snapshot has EVER been warmed for this envelope yet
    ///     (a brand-new filter/window combination). Distinct from a normal result with null
    ///     slices — this is a hint to render a "still warming" state, not "no data exists."
    ///     Returned instead of synchronously composing on the request thread (structural —
    ///     see the compose-batch-overload measure-gate incident: no config value gates this).
    /// </summary>
    public static readonly DashboardPageResult Warming = new(isWarming: true);

    /// <summary>True for <see cref="Warming"/> — the request path never computes on a cold miss.</summary>
    public bool IsWarming { get; }

    /// <summary>
    ///     The resolved data for a pack/commercial extension kind (see
    ///     <see cref="IDashboardDatasetExtension"/>), cast to the widget's view model.
    ///     Null when the kind wasn't composed or the extension returned null — the
    ///     widget then falls back to its self-fetch.
    /// </summary>
    public T? GetExtension<T>(string kind) where T : class =>
        _extensions is not null && _extensions.TryGetValue(kind, out var v) ? v as T : null;

    /// <summary>Summary statistics slice; null when <see cref="DatasetKind.SummaryStats"/> was not requested.</summary>
    public DashboardSummary? Summary => _bundle?.Summary;

    /// <summary>Time-series buckets slice; null when <see cref="DatasetKind.TimeBuckets"/> was not requested.</summary>
    public IReadOnlyList<DashboardTimeSeriesPoint>? TimeBuckets => _bundle?.TimeBuckets;

    /// <summary>Top-bot aggregate slice; null when <see cref="DatasetKind.BotAggregate"/> was not requested.</summary>
    public IReadOnlyList<DashboardTopBotEntry>? BotAggregate => _bundle?.BotAggregate;

    /// <summary>Geo breakdown slice; null when <see cref="DatasetKind.GeoBreakdown"/> was not requested.</summary>
    public IReadOnlyList<DashboardCountryStats>? Geo => _bundle?.Geo;

    /// <summary>Endpoint stats slice; null when <see cref="DatasetKind.EndpointStats"/> was not requested.</summary>
    public IReadOnlyList<DashboardEndpointStats>? Endpoints => _bundle?.Endpoints;

    /// <summary>Site-health degradation history slice; null when <see cref="DatasetKind.DegradationHistory"/> was not requested.</summary>
    public IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>? Degradations => _bundle?.Degradations;

    // -------------------------------------------------------------------
    // Stage 2a: additional row datasets that don't come from
    // IDashboardEventStore.ComposeBatchAsync (Clusters/TopBots/Sessions/Threats
    // are sourced from IBotClusterReader / IDetectionArchive / a fixed-window
    // GetTopBotsAsync / GetThreatsAsync call respectively -- see
    // DashboardRowRawFetchers). Same "null when not composed" contract as the
    // five properties above; each rides alongside the batched bundle rather
    // than replacing it, so a page manifest can request either or both.
    // -------------------------------------------------------------------

    /// <summary>Raw cluster snapshot; null when the "clusters-raw" widget key was not requested or no cluster reader is registered.</summary>
    public IReadOnlyList<ClusterViewModel>? ClustersRaw { get; }

    /// <summary>Cluster-engine diagnostics paired with <see cref="ClustersRaw"/>; null when not composed.</summary>
    public ClusterDiagnosticsViewModel? ClusterDiagnosticsRaw { get; }

    /// <summary>
    ///     Raw top-bots snapshot for the TopBots row's OWN fixed window (trailing 24h,
    ///     audience "all") -- distinct from <see cref="BotAggregate"/>, which reflects
    ///     the Traffic page's user-selected window. Null when not composed.
    /// </summary>
    public IReadOnlyList<DashboardTopBotEntry>? TopBotsRaw { get; }

    /// <summary>Raw recent-sessions snapshot; null when not composed (no <c>IDetectionArchive</c> registered, or not requested).</summary>
    public IReadOnlyList<SessionListEntry>? SessionsRaw { get; }

    /// <summary>Total count paired with <see cref="SessionsRaw"/>; null when not composed.</summary>
    public int? SessionsRawTotalCount { get; }

    /// <summary>Raw threats snapshot (top-20, unpaginated); null when not composed.</summary>
    public IReadOnlyList<ThreatEntry>? ThreatsRaw { get; }
}
