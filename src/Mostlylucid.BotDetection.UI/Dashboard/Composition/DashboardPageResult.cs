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
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        _bundle = bundle;
        _extensions = extensions;
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
}
