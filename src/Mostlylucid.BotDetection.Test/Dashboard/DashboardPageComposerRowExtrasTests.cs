using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Stage 2a: <see cref="DefaultDashboardPageComposer"/> widened to also resolve the
///     Clusters/TopBots/Sessions/Threats/UserAgents row extras (<see cref="DashboardRowWidgetKeys"/>)
///     when a manifest asks for them, using the SAME fetch logic
///     (<see cref="DashboardRowRawFetchers"/>) the request-thread Build* methods use.
///     Gating is per-widget-key: a manifest that doesn't list a given key must not
///     populate (or attempt to fetch) that row's slice.
///
///     <para>
///         Window-threading fix: TopBots/Threats/Sessions/Clusters(-activity)/UserAgents
///         previously ignored the manifest's resolved <see cref="DashboardPageWindow"/> --
///         TopBots/Threats used a fixed lookback, Sessions used only its configured
///         retention, Clusters had no activity metric at all, and UserAgents wasn't routed
///         through the composer. The tests below prove each row's underlying store/archive
///         call now receives the WINDOW'S start/end (or, for Clusters, that its per-cluster
///         activity number is computed from a windowed lookup) instead of a hardcoded value.
///     </para>
/// </summary>
public sealed class DashboardPageComposerRowExtrasTests
{
    private static DashboardWidgetCatalog EmptyCatalog() => DashboardWidgetCatalog.BuildFrom(Array.Empty<Type>());

    private static DashboardPageWindow Window() => new(null, null, null, null, null, TopN: 50, BucketMinutes: 60);

    private static DashboardPageWindow WindowedWindow(DateTime start, DateTime end) =>
        new(start, end, "all", null, null, TopN: 50, BucketMinutes: 60);

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
        Assert.Null(result.UserAgentsRaw);
    }

    [Fact]
    public async Task TopBotsRaw_key_with_no_window_falls_back_to_the_default_24h_lookback()
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
        Assert.NotNull(store.LastTopBotsStartTime);
        Assert.Null(result.ClustersRaw);
        Assert.Null(result.SessionsRaw);
        Assert.Null(result.ThreatsRaw);
    }

    [Fact]
    public async Task TopBotsRaw_key_passes_the_manifests_resolved_window_through_to_the_store()
    {
        // RED before the fix: FetchTopBotsRawAsync hardcoded DateTime.UtcNow.AddHours(-24)
        // regardless of the manifest's window, so a caller asking for a 30-day window still
        // only ever saw the trailing 24h. GREEN after: the manifest's own start/end reach
        // GetTopBotsAsync verbatim.
        var store = new RecordingStore();
        var composer = new DefaultDashboardPageComposer(
            EmptyCatalog(), store, signatureCache: new SignatureAggregateCache(new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions()));
        var manifest = new DashboardPageManifest("dashboard.topbots", new[] { DashboardRowWidgetKeys.TopBotsRaw });
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        await composer.ComposeAsync(manifest, WindowedWindow(start, end), default);

        Assert.Equal(start, store.LastTopBotsStartTime);
        Assert.Equal(end, store.LastTopBotsEndTime);
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
    public async Task ThreatsRaw_key_passes_the_manifests_resolved_window_through_to_the_store()
    {
        // RED before the fix: FetchThreatsRawAsync(store, count) never passed a window at
        // all, so GetThreatsAsync always saw null/null (all-time) regardless of ?window=.
        var store = new RecordingStore();
        var composer = new DefaultDashboardPageComposer(EmptyCatalog(), store);
        var manifest = new DashboardPageManifest("dashboard.threats", new[] { DashboardRowWidgetKeys.ThreatsRaw });
        var start = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);

        await composer.ComposeAsync(manifest, WindowedWindow(start, end), default);

        Assert.Equal(start, store.LastThreatsStartTime);
        Assert.Equal(end, store.LastThreatsEndTime);
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
        // Membership (MemberCount, ClusterId, Label) stays current-state -- unaffected by
        // the window -- per the operator ruling: Leiden is not re-run per window.
        Assert.Equal(2, cluster.MemberCount);
        Assert.NotNull(result.ClusterDiagnosticsRaw);
        Assert.Equal("leiden", result.ClusterDiagnosticsRaw!.Algorithm);
    }

    [Fact]
    public async Task ClustersRaw_key_computes_a_windowed_activity_hit_count_from_member_signatures()
    {
        // RED before the fix: ClusterViewModel had no activity field at all, and
        // FetchClustersRawAsync never consulted the store -- every cluster's card showed
        // only current-state membership numbers (MemberCount, AvgBotProb), nothing scoped
        // to the dashboard's selected window. GREEN after: WindowHitCount sums the windowed
        // per-signature hit counts (via the SAME GetTopBotsAsync call TopBots itself makes)
        // across the cluster's member signatures -- membership (MemberCount/ClusterId/Label)
        // is untouched.
        var reader = new FakeClusterReader(); // members: sig1, sig2
        var store = new RecordingStore();
        store.TopBotsResult = new List<DashboardTopBotEntry>
        {
            new() { PrimarySignature = "sig1", HitCount = 7 },
            new() { PrimarySignature = "sig2", HitCount = 5 },
            new() { PrimarySignature = "sig-not-in-cluster", HitCount = 999 },
        };
        var composer = new DefaultDashboardPageComposer(EmptyCatalog(), store, clusterReader: reader);
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

        var result = await composer.ComposeAsync(manifest, WindowedWindow(start, end), default);

        var cluster = Assert.Single(result.ClustersRaw!);
        Assert.Equal(12, cluster.WindowHitCount); // 7 + 5, excludes the non-member signature
        Assert.Equal(start, store.LastTopBotsStartTime);
        Assert.Equal(end, store.LastTopBotsEndTime);
    }

    [Fact]
    public async Task ClustersRaw_key_with_no_clusters_skips_the_windowed_activity_lookup()
    {
        var store = new RecordingStore();
        var composer = new DefaultDashboardPageComposer(EmptyCatalog(), store, clusterReader: new EmptyClusterReader());
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });

        var result = await composer.ComposeAsync(manifest, Window(), default);

        Assert.Empty(result.ClustersRaw!);
        Assert.False(store.GetTopBotsWasCalled);
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

    [Fact]
    public async Task SessionsRaw_key_uses_the_manifests_window_start_as_the_since_cutoff()
    {
        // RED before the fix: FetchSessionsRawAsync always computed
        // since = DateTime.UtcNow - retention, ignoring the manifest's own resolved window
        // start entirely (retention defaults to 30 days -- so a 6h window still queried 30
        // days back). GREEN after: the window's StartTime, when supplied, wins.
        var archive = new RecordingDetectionArchive();
        var composer = new DefaultDashboardPageComposer(
            EmptyCatalog(), new RecordingStore(),
            signatureCache: new SignatureAggregateCache(new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions()),
            detectionArchive: archive);
        var manifest = new DashboardPageManifest("dashboard.sessions", new[] { DashboardRowWidgetKeys.SessionsRaw });
        var start = DateTime.UtcNow.AddHours(-6);
        var end = DateTime.UtcNow;

        await composer.ComposeAsync(manifest, WindowedWindow(start, end), default);

        Assert.NotNull(archive.LastSince);
        Assert.Equal(start, archive.LastSince!.Value);
    }

    [Fact]
    public async Task UserAgentsRaw_key_fetches_via_the_windowed_aggregator()
    {
        // RED before the fix: DashboardPageResult had no UserAgentsRaw field at all, and
        // the composer never resolved a "useragents-raw" widget key -- the shell model's
        // UserAgents field read the fixed-window DashboardAggregateCache snapshot directly,
        // bypassing the content cache (and the window) entirely.
        var store = new RecordingStore();
        store.DetectionsResult = new List<DashboardDetectionEvent>
        {
            new()
            {
                RequestId = "req-1", Timestamp = DateTime.UtcNow, IsBot = true, BotProbability = 0.9,
                Confidence = 0.9, RiskBand = "High", Method = "GET", Path = "/",
                UserAgent = "Mozilla/5.0 (compatible; Googlebot/2.1)",
            },
        };
        var composer = new DefaultDashboardPageComposer(EmptyCatalog(), store);
        var manifest = new DashboardPageManifest("dashboard.traffic", new[] { DashboardRowWidgetKeys.UserAgentsRaw });
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc);

        var result = await composer.ComposeAsync(manifest, WindowedWindow(start, end), default);

        Assert.NotNull(result.UserAgentsRaw);
        Assert.Single(result.UserAgentsRaw!);
        Assert.Equal(start, store.LastDetectionsFilterStartTime);
        Assert.Equal(end, store.LastDetectionsFilterEndTime);
        // Other extras weren't requested.
        Assert.Null(result.ClustersRaw);
        Assert.Null(result.TopBotsRaw);
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

    private sealed class EmptyClusterReader : IBotClusterReader
    {
        public Task<IReadOnlyList<BotCluster>> GetClustersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BotCluster>>(Array.Empty<BotCluster>());

        public Task<BotClusterService.ClusterDiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken ct = default) =>
            Task.FromResult(BotClusterService.ClusterDiagnosticsSnapshot.Empty);
    }

    /// <summary>
    ///     Minimal <see cref="IDetectionArchive"/> fake recording only the "since" bound
    ///     <see cref="GetRecentSessionsAsync"/> was called with; every other member is a
    ///     no-op stub (mirrors <c>SessionPersistenceServiceTickTests.RecordingSessionStore</c>'s
    ///     established shape for this interface).
    /// </summary>
    private sealed class RecordingDetectionArchive : IDetectionArchive
    {
        public DateTime? LastSince { get; private set; }

        public string? PersistenceConnectionString => null;

        public Task<long> AddSessionAsync(RequestScope scope, PersistedSession session, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> AddEchoAsync(Mostlylucid.BotDetection.Orchestration.Sessions.SessionEcho echo, CancellationToken ct = default) => Task.FromResult(0L);
        public Task UpsertSignatureAsync(RequestScope scope, PersistedSignature signature, CancellationToken ct = default) => Task.CompletedTask;
        public Task IncrementBucketAsync(DateTime bucketTime, bool isBot, double processingTimeMs, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRequestAsync(RequestScope scope, PersistedRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRequestBatchAsync(RequestScope scope, IReadOnlyList<PersistedRequest> requests, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<PersistedRequest>> GetUnatomizedRequestsAsync(int limit = 5000, CancellationToken ct = default) => Task.FromResult(new List<PersistedRequest>());
        public Task<List<PersistedRequest>> GetRecentRequestsAsync(int limit = 5000, DateTime? sinceUtc = null, CancellationToken ct = default) => Task.FromResult(new List<PersistedRequest>());
        public Task LinkRequestsToSessionAsync(long sessionId, IReadOnlyList<long> requestIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<PersistedSession>> GetSessionsAsync(string signature, int limit = 20, CancellationToken ct = default) => Task.FromResult(new List<PersistedSession>());

        public Task<List<PersistedSession>> GetRecentSessionsAsync(int limit = 50, bool? isBot = null, DateTime? since = null, CancellationToken ct = default)
        {
            LastSince = since;
            return Task.FromResult(new List<PersistedSession>());
        }

        public Task<PersistedSignature?> GetSignatureAsync(string signatureId, CancellationToken ct = default) => Task.FromResult<PersistedSignature?>(null);
        public Task<string> ResolveSignatureAsync(string requestedSignatureId, CancellationToken ct = default) => Task.FromResult(requestedSignatureId);
        public Task RecordSignatureMergeAsync(string oldSignatureId, string newSignatureId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<PersistedSignature>> GetTopSignaturesAsync(int limit = 20, bool? isBot = null, CancellationToken ct = default) => Task.FromResult(new List<PersistedSignature>());
        public Task<DashboardSessionSummary> GetSummaryAsync(CancellationToken ct = default) => Task.FromResult(new DashboardSessionSummary());
        public Task<List<AggregatedBucket>> GetTimeSeriesAsync(DateTime start, DateTime end, CancellationToken ct = default) => Task.FromResult(new List<AggregatedBucket>());
        public Task<List<CountrySessionStats>> GetCountryStatsAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult(new List<CountrySessionStats>());
        public Task<List<(PersistedSession Session, float Similarity)>> FindSimilarSessionsAsync(float[] queryVector, int topK = 10, float minSimilarity = 0.7f, CancellationToken ct = default) => Task.FromResult(new List<(PersistedSession, float)>());
        public Task<string> ResolveEntityAsync(string primarySignature, CancellationToken ct = default) => Task.FromResult($"entity:{primarySignature}");
        public Task<ResolvedEntity?> GetEntityForSignatureAsync(string primarySignature, CancellationToken ct = default) => Task.FromResult<ResolvedEntity?>(null);
        public Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default) => Task.FromResult<ResolvedEntity?>(null);
        public Task<List<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default) => Task.FromResult(new List<EntityEdge>());
        public Task MergeSignatureAsync(string entityId, string signature, double confidence, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateEntityAsync(ResolvedEntity entity, CancellationToken ct = default) => Task.CompletedTask;
        public Task PruneAsync(TimeSpan retention, CancellationToken ct = default) => Task.CompletedTask;
        public Task PruneBucketsAsync(TimeSpan retention, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<(string Signature, int SessionCount)>> GetOverflowingSignaturesAsync(int maxPerSignature, int limit = 500, CancellationToken ct = default) => Task.FromResult(new List<(string, int)>());
        public Task<CompactionResult> CompactSignatureSessionsAsync(string signature, int keepCount, CancellationToken ct = default) => Task.FromResult(new CompactionResult { Signature = signature });
        public Task<List<CompactionSignatureInfo>> GetSignaturePriorityInfoAsync(List<string> signatures, CancellationToken ct = default) => Task.FromResult(new List<CompactionSignatureInfo>());
        public Task<List<string>> GetActiveEntityIdsAsync(DateTime cutoff, int limit = 100, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingStore : IDashboardEventStore
    {
        public string? LastTopBotsAudienceFilter { get; private set; }
        public DateTime? LastTopBotsStartTime { get; private set; }
        public DateTime? LastTopBotsEndTime { get; private set; }
        public bool GetTopBotsWasCalled { get; private set; }
        public DateTime? LastThreatsStartTime { get; private set; }
        public DateTime? LastThreatsEndTime { get; private set; }
        public DateTime? LastDetectionsFilterStartTime { get; private set; }
        public DateTime? LastDetectionsFilterEndTime { get; private set; }

        public List<DashboardTopBotEntry> TopBotsResult { get; set; } = new() { new() { PrimarySignature = "sig-topbots" } };
        public List<DashboardDetectionEvent> DetectionsResult { get; set; } = new();

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
            => Task.FromResult(new DashboardDatasetBundle(null, null, null, null, null));

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            GetTopBotsWasCalled = true;
            LastTopBotsAudienceFilter = audienceFilter;
            LastTopBotsStartTime = startTime;
            LastTopBotsEndTime = endTime;
            return Task.FromResult(TopBotsResult);
        }

        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
        {
            LastThreatsStartTime = startTime;
            LastThreatsEndTime = endTime;
            return Task.FromResult(new List<ThreatEntry> { new() { Signature = "sig-threat", Path = "/wp-admin" } });
        }

        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
        {
            LastDetectionsFilterStartTime = filter?.StartTime;
            LastDetectionsFilterEndTime = filter?.EndTime;
            return Task.FromResult(DetectionsResult);
        }

        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
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
