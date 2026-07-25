using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     Widget-key constants for the Stage 2a row datasets that don't map to a
///     <see cref="DatasetKind"/> (Clusters/TopBots/Sessions/Threats -- sourced from
///     <see cref="IBotClusterReader"/> / <see cref="IDetectionArchive"/> / a fixed
///     window, not <c>IDashboardEventStore.ComposeBatchAsync</c>). Shared between
///     <see cref="DefaultDashboardPageManifestSource"/> (which seeds the manifest)
///     and <see cref="DefaultDashboardPageComposer"/> (which checks for the key) so
///     the two sides can't drift.
/// </summary>
public static class DashboardRowWidgetKeys
{
    public const string ClustersRaw = "clusters-raw";
    public const string TopBotsRaw = "topbots-raw";
    public const string SessionsRaw = "sessions-raw";
    public const string ThreatsRaw = "threats-raw";

    /// <summary>
    ///     User-agent family summary, windowed to the manifest's page window. Added to the
    ///     "dashboard.traffic" manifest (not its own page) since UserAgents shares the SAME
    ///     window/audience/domain scope as Summary/Countries/Endpoints on that page -- see
    ///     <see cref="DefaultDashboardPageManifestSource"/>.
    /// </summary>
    public const string UserAgentsRaw = "useragents-raw";
}

/// <summary>
///     Default scoped implementation of <see cref="IDashboardPageComposer"/>.
///     Maps manifest widget keys → <see cref="DatasetKind"/> via the catalog,
///     deduplicates, builds one <see cref="DashboardBatchRequest"/>, calls
///     <see cref="IDashboardEventStore.ComposeBatchAsync"/>, and wraps the result
///     in a <see cref="DashboardPageResult"/>. Also resolves the Stage 2a row
///     extras (<see cref="DashboardRowWidgetKeys"/>) via
///     <see cref="DashboardRowRawFetchers"/> when the manifest asks for them.
/// </summary>
public sealed class DefaultDashboardPageComposer : IDashboardPageComposer
{
    private readonly DashboardWidgetCatalog _catalog;
    private readonly IDashboardEventStore _store;
    private readonly IReadOnlyDictionary<string, IDashboardDatasetExtension> _extensions;
    private readonly ILogger<DefaultDashboardPageComposer>? _logger;
    private readonly SignatureAggregateCache? _signatureCache;
    private readonly IDetectionArchive? _detectionArchive;
    private readonly IBotClusterReader? _clusterReader;
    private readonly TimeSpan _detectionRetention;
    private readonly DashboardUserAgentAggregator _userAgentAggregator;

    public DefaultDashboardPageComposer(
        DashboardWidgetCatalog catalog,
        IDashboardEventStore store,
        IEnumerable<IDashboardDatasetExtension>? extensions = null,
        ILogger<DefaultDashboardPageComposer>? logger = null,
        SignatureAggregateCache? signatureCache = null,
        IDetectionArchive? detectionArchive = null,
        IBotClusterReader? clusterReader = null,
        TimeSpan? detectionRetention = null,
        DashboardUserAgentAggregator? userAgentAggregator = null)
    {
        _catalog = catalog;
        _store = store;
        _logger = logger;
        _signatureCache = signatureCache;
        _detectionArchive = detectionArchive;
        _clusterReader = clusterReader;
        _detectionRetention = detectionRetention ?? DashboardRowRawFetchers.DefaultDetectionRetention;
        // DashboardUserAgentAggregator is a thin stateless wrapper over IDashboardEventStore
        // (see its own doc comment) -- constructing a private instance when DI doesn't supply
        // one (e.g. a hand-built composer in a unit test) is cheap and keeps this constructor
        // usable without wiring the whole DI container, mirroring how _detectionRetention
        // falls back to a documented default above.
        _userAgentAggregator = userAgentAggregator ?? new DashboardUserAgentAggregator(store);
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

        DashboardDatasetBundle bundle = new(null, null, null, null, null);
        if (kinds.Count > 0)
        {
            var req = new DashboardBatchRequest(
                w.StartTime,
                w.EndTime,
                kinds.Select(k => new DatasetRequest(k, w.TopN, w.BucketMinutes)).ToList(),
                w.AudienceFilter,
                w.ProbMin,
                w.Domains);

            bundle = await _store.ComposeBatchAsync(req, ct);
        }

        // Pack/commercial extension datasets: widget keys → extension kind → resolve.
        // Runs in-process where the extension is registered (typically wrapping a remote
        // provider); fail-closed per extension so one pack can't break the page.
        var extensionData = await ResolveExtensionsAsync(manifest, w, ct);

        // Stage 2a row extras: Clusters/TopBots/Sessions/Threats aren't DatasetKind-backed
        // (they come from IBotClusterReader / IDetectionArchive / a fixed window), so they
        // resolve alongside the batched bundle via DashboardRowRawFetchers -- the SAME fetch
        // logic StyloBotDashboardMiddleware's request-thread Build* methods use. Fail-closed
        // per row so one failing dependency doesn't blank the whole page bundle.
        var hasKey = manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.ClustersRaw)
            || manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.TopBotsRaw)
            || manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.SessionsRaw)
            || manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.ThreatsRaw)
            || manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.UserAgentsRaw);
        if (!hasKey) return new DashboardPageResult(bundle, extensionData);

        IReadOnlyList<ClusterViewModel>? clusters = null;
        ClusterDiagnosticsViewModel? clusterDiagnostics = null;
        IReadOnlyList<DashboardTopBotEntry>? topBots = null;
        IReadOnlyList<SessionListEntry>? sessions = null;
        int? sessionsTotalCount = null;
        IReadOnlyList<ThreatEntry>? threats = null;
        IReadOnlyList<DashboardUserAgentSummary>? userAgents = null;

        // Every row extra below is scoped to w.StartTime/w.EndTime -- the manifest's own
        // resolved page window (the dashboard's currently-selected ?window= period) -- so
        // each row's ACTIVITY data varies with the window exactly like the batched
        // Summary/Countries/Endpoints dataset above. Clusters is the one exception:
        // membership stays current-state (see FetchClustersRawAsync's doc comment); only
        // its per-cluster activity metric is windowed.
        if (manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.ClustersRaw))
        {
            try
            {
                (clusters, clusterDiagnostics) = await DashboardRowRawFetchers.FetchClustersRawAsync(
                    _clusterReader, _store, w.StartTime, w.EndTime, _signatureCache?.MaxEntries ?? 500, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger?.LogWarning(ex, "Dashboard row extra 'clusters-raw' failed to compose."); }
        }

        if (manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.TopBotsRaw))
        {
            try
            {
                topBots = await DashboardRowRawFetchers.FetchTopBotsRawAsync(
                    _store, _signatureCache?.MaxEntries ?? 500, w.StartTime, w.EndTime, w.Domains, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger?.LogWarning(ex, "Dashboard row extra 'topbots-raw' failed to compose."); }
        }

        if (manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.SessionsRaw) && _signatureCache is not null)
        {
            try
            {
                var fetched = await DashboardRowRawFetchers.FetchSessionsRawAsync(
                    _detectionArchive, _store, _signatureCache,
                    _detectionRetention, page: 1, pageSize: 25, filter: null, w.StartTime, ct);
                sessions = fetched.Entries;
                sessionsTotalCount = fetched.TotalCount;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger?.LogWarning(ex, "Dashboard row extra 'sessions-raw' failed to compose."); }
        }

        if (manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.ThreatsRaw))
        {
            try
            {
                threats = await DashboardRowRawFetchers.FetchThreatsRawAsync(_store, 20, w.StartTime, w.EndTime, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger?.LogWarning(ex, "Dashboard row extra 'threats-raw' failed to compose."); }
        }

        if (manifest.WidgetKeys.Contains(DashboardRowWidgetKeys.UserAgentsRaw))
        {
            try
            {
                userAgents = await DashboardRowRawFetchers.FetchUserAgentsRawAsync(
                    _userAgentAggregator, w.StartTime, w.EndTime, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger?.LogWarning(ex, "Dashboard row extra 'useragents-raw' failed to compose."); }
        }

        return new DashboardPageResult(
            bundle, extensionData,
            clustersRaw: clusters, clusterDiagnosticsRaw: clusterDiagnostics,
            topBotsRaw: topBots,
            sessionsRaw: sessions, sessionsRawTotalCount: sessionsTotalCount,
            threatsRaw: threats,
            userAgentsRaw: userAgents);
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
