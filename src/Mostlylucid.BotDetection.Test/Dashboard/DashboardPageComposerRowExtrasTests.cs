using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Stage 2a: <see cref="DefaultDashboardPageComposer"/> widened to also resolve the
///     Clusters/TopBots/Sessions/Threats row extras (<see cref="DashboardRowWidgetKeys"/>)
///     when a manifest asks for them, using the SAME fetch logic
///     (<see cref="DashboardRowRawFetchers"/>) the request-thread Build* methods use.
///     Gating is per-widget-key: a manifest that doesn't list a given key must not
///     populate (or attempt to fetch) that row's slice.
/// </summary>
public sealed class DashboardPageComposerRowExtrasTests
{
    private static DashboardWidgetCatalog EmptyCatalog() => DashboardWidgetCatalog.BuildFrom(Array.Empty<Type>());

    private static DashboardPageWindow Window() => new(null, null, null, null, null, TopN: 50, BucketMinutes: 60);

    [Fact]
    public async Task Manifest_without_any_row_extra_key_leaves_all_new_slices_null()
    {
        var composer = new DefaultDashboardPageComposer(EmptyCatalog(), new RecordingStore());
        var manifest = new DashboardPageManifest("dashboard.traffic", new[] { "summary" });

        var result = await composer.ComposeAsync(manifest, Window(), default);

        Assert.Null(result.ClustersRaw);
        Assert.Null(result.TopBotsRaw);
        Assert.Null(result.SessionsRaw);
        Assert.Null(result.ThreatsRaw);
    }

    [Fact]
    public async Task TopBotsRaw_key_fetches_via_GetTopBotsAsync_with_the_fixed_24h_window()
    {
        var store = new RecordingStore();
        var composer = new DefaultDashboardPageComposer(
            EmptyCatalog(), store, signatureCache: new SignatureAggregateCache(new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions()));
        var manifest = new DashboardPageManifest("dashboard.topbots", new[] { DashboardRowWidgetKeys.TopBotsRaw });

        var result = await composer.ComposeAsync(manifest, Window(), default);

        Assert.NotNull(result.TopBotsRaw);
        Assert.Single(result.TopBotsRaw!);
        Assert.Equal("sig-topbots", result.TopBotsRaw![0].PrimarySignature);
        Assert.Equal("all", store.LastTopBotsAudienceFilter);
        // Other extras weren't requested -- must stay null (no clusters/sessions/threats fetch attempted).
        Assert.Null(result.ClustersRaw);
        Assert.Null(result.SessionsRaw);
        Assert.Null(result.ThreatsRaw);
    }

    [Fact]
    public async Task ThreatsRaw_key_fetches_via_GetThreatsAsync()
    {
        var store = new RecordingStore();
        var composer = new DefaultDashboardPageComposer(EmptyCatalog(), store);
        var manifest = new DashboardPageManifest("dashboard.threats", new[] { DashboardRowWidgetKeys.ThreatsRaw });

        var result = await composer.ComposeAsync(manifest, Window(), default);

        Assert.NotNull(result.ThreatsRaw);
        Assert.Single(result.ThreatsRaw!);
        Assert.Equal("sig-threat", result.ThreatsRaw![0].Signature);
    }

    [Fact]
    public async Task ClustersRaw_key_with_no_cluster_reader_registered_yields_empty_not_null()
    {
        var composer = new DefaultDashboardPageComposer(EmptyCatalog(), new RecordingStore(), clusterReader: null);
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });

        var result = await composer.ComposeAsync(manifest, Window(), default);

        Assert.NotNull(result.ClustersRaw);
        Assert.Empty(result.ClustersRaw!);
        Assert.Null(result.ClusterDiagnosticsRaw);
    }

    [Fact]
    public async Task ClustersRaw_key_with_a_registered_reader_maps_the_snapshot()
    {
        var reader = new FakeClusterReader();
        var composer = new DefaultDashboardPageComposer(EmptyCatalog(), new RecordingStore(), clusterReader: reader);
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });

        var result = await composer.ComposeAsync(manifest, Window(), default);

        var cluster = Assert.Single(result.ClustersRaw!);
        Assert.Equal("c1", cluster.ClusterId);
        Assert.NotNull(result.ClusterDiagnosticsRaw);
        Assert.Equal("leiden", result.ClusterDiagnosticsRaw!.Algorithm);
    }

    [Fact]
    public async Task SessionsRaw_key_with_the_null_archive_yields_empty_not_null()
    {
        var composer = new DefaultDashboardPageComposer(
            EmptyCatalog(), new RecordingStore(),
            signatureCache: new SignatureAggregateCache(new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions()), detectionArchive: new NullDetectionArchive());
        var manifest = new DashboardPageManifest("dashboard.sessions", new[] { DashboardRowWidgetKeys.SessionsRaw });

        var result = await composer.ComposeAsync(manifest, Window(), default);

        Assert.NotNull(result.SessionsRaw);
        Assert.Empty(result.SessionsRaw!);
        Assert.Equal(0, result.SessionsRawTotalCount);
    }

    [Fact]
    public async Task SessionsRaw_key_with_no_archive_registered_yields_null()
    {
        // No IDetectionArchive registered at all (viewer-mode host without the session
        // store) -- FetchSessionsRawAsync short-circuits to empty, distinguishable from
        // "not requested" only by the manifest not listing the key; here it IS listed,
        // so the slice is populated (empty), matching the existing BuildSessionsModel
        // early-return contract.
        var composer = new DefaultDashboardPageComposer(
            EmptyCatalog(), new RecordingStore(), signatureCache: new SignatureAggregateCache(new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions()), detectionArchive: null);
        var manifest = new DashboardPageManifest("dashboard.sessions", new[] { DashboardRowWidgetKeys.SessionsRaw });

        var result = await composer.ComposeAsync(manifest, Window(), default);

        Assert.NotNull(result.SessionsRaw);
        Assert.Empty(result.SessionsRaw!);
    }

    private sealed class FakeClusterReader : IBotClusterReader
    {
        public Task<IReadOnlyList<BotCluster>> GetClustersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BotCluster>>(new List<BotCluster>
            {
                new()
                {
                    ClusterId = "c1",
                    MemberSignatures = new List<string> { "sig1", "sig2" },
                    Type = BotClusterType.BotNetwork,
                    MemberCount = 2,
                    Label = "Test cluster"
                }
            });

        public Task<BotClusterService.ClusterDiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken ct = default) =>
            Task.FromResult(new BotClusterService.ClusterDiagnosticsSnapshot { Algorithm = "leiden", Status = "ok" });
    }

    private sealed class RecordingStore : IDashboardEventStore
    {
        public string? LastTopBotsAudienceFilter { get; private set; }

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
            => Task.FromResult(new DashboardDatasetBundle(null, null, null, null, null));

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            LastTopBotsAudienceFilter = audienceFilter;
            return Task.FromResult(new List<DashboardTopBotEntry> { new() { PrimarySignature = "sig-topbots" } });
        }

        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
            => Task.FromResult(new List<ThreatEntry> { new() { Signature = "sig-threat", Path = "/wp-admin" } });

        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
    }
}
