using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     Default scoped implementation of <see cref="IDashboardPageComposer"/>.
///     Maps manifest widget keys → <see cref="DatasetKind"/> via the catalog,
///     deduplicates, builds one <see cref="DashboardBatchRequest"/>, calls
///     <see cref="IDashboardEventStore.ComposeBatchAsync"/>, and wraps the result
///     in a <see cref="DashboardPageResult"/>.
/// </summary>
public sealed class DefaultDashboardPageComposer : IDashboardPageComposer
{
    private readonly DashboardWidgetCatalog _catalog;
    private readonly IDashboardEventStore _store;

    public DefaultDashboardPageComposer(DashboardWidgetCatalog catalog, IDashboardEventStore store)
    {
        _catalog = catalog;
        _store = store;
    }

    public async Task<DashboardPageResult> ComposeAsync(
        DashboardPageManifest manifest,
        DashboardPageWindow w,
        CancellationToken ct)
    {
        // Resolve widget keys → DatasetKind, skip unknown keys, dedupe via HashSet.
        var kinds = manifest.WidgetKeys
            .Select(k => _catalog.NeedsFor(k))
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .ToHashSet();

        var req = new DashboardBatchRequest(
            w.StartTime,
            w.EndTime,
            kinds.Select(k => new DatasetRequest(k, w.TopN, w.BucketMinutes)).ToList(),
            w.AudienceFilter,
            w.ProbMin,
            w.Domains);

        var bundle = await _store.ComposeBatchAsync(req, ct);
        return new DashboardPageResult(bundle);
    }
}
