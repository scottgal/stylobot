using Microsoft.Extensions.Logging;
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
    private readonly IReadOnlyDictionary<string, IDashboardDatasetExtension> _extensions;
    private readonly ILogger<DefaultDashboardPageComposer>? _logger;

    public DefaultDashboardPageComposer(
        DashboardWidgetCatalog catalog,
        IDashboardEventStore store,
        IEnumerable<IDashboardDatasetExtension>? extensions = null,
        ILogger<DefaultDashboardPageComposer>? logger = null)
    {
        _catalog = catalog;
        _store = store;
        _logger = logger;
        // Index pack extensions by their kind name (last registration wins on a dup —
        // logged so a pack silently hijacking another pack's kind is visible).
        var map = new Dictionary<string, IDashboardDatasetExtension>(StringComparer.Ordinal);
        if (extensions is not null)
            foreach (var e in extensions)
            {
                if (map.ContainsKey(e.DatasetKind))
                    _logger?.LogWarning(
                        "Duplicate dashboard dataset extension for kind '{DatasetKind}'; last registration wins.",
                        e.DatasetKind);
                map[e.DatasetKind] = e;
            }
        _extensions = map;
    }

    public async Task<DashboardPageResult> ComposeAsync(
        DashboardPageManifest manifest,
        DashboardPageWindow w,
        CancellationToken ct)
    {
        // FOSS batched datasets: widget keys → DatasetKind, skip unknown, dedupe.
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

        // Pack/commercial extension datasets: widget keys → extension kind → resolve.
        // Runs in-process where the extension is registered (typically wrapping a remote
        // provider); fail-closed per extension so one pack can't break the page.
        var extensionData = await ResolveExtensionsAsync(manifest, w, ct);

        return new DashboardPageResult(bundle, extensionData);
    }

    private async Task<IReadOnlyDictionary<string, object?>?> ResolveExtensionsAsync(
        DashboardPageManifest manifest, DashboardPageWindow w, CancellationToken ct)
    {
        if (_extensions.Count == 0) return null;

        var extKinds = manifest.WidgetKeys
            .Select(k => _catalog.ExtensionKindFor(k))
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (extKinds.Count == 0) return null;

        var ctx = new DashboardDatasetContext(w.StartTime, w.EndTime, w.AudienceFilter, w.Domains, Parameters: null);
        Dictionary<string, object?>? resolved = null;
        foreach (var kind in extKinds)
        {
            if (!_extensions.TryGetValue(kind, out var ext)) continue;
            object? data;
            try
            {
                data = await ext.ResolveAsync(ctx, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fail-closed: a throwing extension yields a null slice — but LOGGED, not
                // silent (no-silent-errors), so a tile vanishing has an operator trail.
                _logger?.LogWarning(ex,
                    "Dashboard dataset extension '{DatasetKind}' threw; slice omitted.", kind);
                data = null;
            }
            (resolved ??= new Dictionary<string, object?>(StringComparer.Ordinal))[kind] = data;
        }
        return resolved;
    }
}
