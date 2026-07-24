using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     End-to-end proof that the dashboard's <c>?window=</c> period selector now reaches
///     Clusters/TopBots/Sessions/Threats/UserAgents -- previously these rows silently
///     ignored it while Summary/Countries/Endpoints/Visitors honoured it (see
///     <c>StyloBotDashboardMiddleware.BuildVisitorsPageWindow</c> and
///     <c>DashboardRowRawFetchers</c>). Mirrors the existing
///     <see cref="TrafficRowContentCacheIntegrationTests"/> /
///     <see cref="DashboardRowExtraContentCacheIntegrationTests"/> full middleware
///     round-trip pattern: real <see cref="StyloBotDashboardMiddleware"/>, real
///     Index.cshtml render, a window-sensitive fake store/archive standing in for SQLite.
///
///     <para>
///         Clusters is the only one of the five actually rendered by a reachable route
///         from the shell model (<c>/dashboard/clusters</c> -&gt; <c>_ClustersList.cshtml</c>
///         reads <c>Model.Clusters</c> directly) -- TopBots/Sessions/Threats/UserAgents
///         populate <c>DashboardShellModel</c> fields that no current view consumes (they
///         each have a SEPARATE self-fetch ViewComponent/HTMX endpoint instead, which is
///         the explicitly out-of-scope "compute on cold" layer a different ticket owns).
///         So Clusters is verified via rendered HTML (the windowed
///         <c>ClusterViewModel.WindowHitCount</c> activity number); the other four are
///         verified via the SAME request's recorded store/archive call parameters --
///         <c>ServeDashboardPageAsync</c> awaits every row's task unconditionally on EVERY
///         request regardless of which row is being viewed, so hitting <c>/dashboard/clusters</c>
///         also composes "dashboard.topbots"/"dashboard.sessions"/"dashboard.threats" and
///         the "useragents-raw" widget key on "dashboard.traffic" -- proving their fetch
///         genuinely varies with the request's window even though nothing currently renders it.
///     </para>
/// </summary>
public sealed class DashboardRowWindowThreadingIntegrationTests : IAsyncDisposable
{
    private WebApplication? _app;

    private const string MemberSignature = "sig-window-threading-test";
    private const int NarrowHitCount = 321;
    private const int WideHitCount = 87654;

    [Fact]
    public async Task All_five_rows_fetch_is_scoped_to_the_requests_selected_window()
    {
        var store = new WindowSensitiveEventStore(MemberSignature, NarrowHitCount, WideHitCount);
        var clusterReader = new SingleClusterReader(MemberSignature);
        var archive = new WindowSensitiveDetectionArchive();
        var sigCache = new SignatureAggregateCache(new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions());
        var manifests = new DefaultDashboardPageManifestSource();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(
            catalog, store, clusterReader: clusterReader, signatureCache: sigCache, detectionArchive: archive);

        long tick = 1;
        var cache = new Mostlylucid.BotDetection.Test.Helpers.AutoWarmingContentCache(
            new DashboardContentCache((m, w, ct) => composer.ComposeAsync(m, w, ct), () => tick,
                Options.Create(new DashboardMaterializerOptions())),
            () => tick);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(store);
        builder.Services.AddSingleton<IBotClusterReader>(clusterReader);
        builder.Services.AddSingleton<IDetectionArchive>(archive);
        builder.Services.AddSingleton(sigCache);
        builder.Services.AddSingleton<IDashboardContentCache>(cache);
        builder.Services.AddSingleton<IDashboardPageManifestSource>(manifests);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = true;
        });

        _app = builder.Build();
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        var client = _app.GetTestClient();

        // --- Narrow window (6h): request, then capture this request's recordings into
        // local copies BEFORE resetting for the wide request (the fakes are shared
        // singletons across both requests). ---
        store.Reset();
        archive.Reset();
        var narrowResponse = await client.GetAsync("/dashboard/clusters?window=6h");
        var narrowHtml = await narrowResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, narrowResponse.StatusCode);

        var narrowTopBots = store.TopBotsCalls.ToList();
        var narrowThreats = store.ThreatsCalls.ToList();
        var narrowDetections = store.DetectionsCalls.ToList();
        var narrowSinces = archive.SinceCalls.ToList();

        // --- Wide window (30d) ---
        store.Reset();
        archive.Reset();
        var wideResponse = await client.GetAsync("/dashboard/clusters?window=30d");
        var wideHtml = await wideResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, wideResponse.StatusCode);

        var wideTopBots = store.TopBotsCalls.ToList();
        var wideThreats = store.ThreatsCalls.ToList();
        var wideDetections = store.DetectionsCalls.ToList();
        var wideSinces = archive.SinceCalls.ToList();

        // Membership is unaffected by the window (operator ruling: Leiden clustering is
        // NOT re-run per window) -- both renders show the SAME cluster.
        Assert.Contains("WindowThreadingTestCluster", narrowHtml);
        Assert.Contains("WindowThreadingTestCluster", wideHtml);

        // Clusters: the per-cluster ACTIVITY metric (WindowHitCount) DOES vary with the
        // window -- rendered directly, since _ClustersList.cshtml is genuinely driven by
        // the shell model.
        Assert.Contains($">{NarrowHitCount}<", narrowHtml);
        Assert.DoesNotContain($">{WideHitCount}<", narrowHtml);
        Assert.Contains($">{WideHitCount}<", wideHtml);
        Assert.DoesNotContain($">{NarrowHitCount}<", wideHtml);

        // TopBots: at least one GetTopBotsAsync call this request (Clusters' own
        // windowed-activity lookup AND the TopBots row both call it -- either proves the
        // fix) spans <=7h for the narrow window and >100h for the wide window. NOT a
        // simple "last call" or "<=24h" check: SessionEnrichmentExtensions.
        // LoadUserAgentLookupAsync (an unrelated, pre-existing, intentionally-fixed-24h
        // helper the Sessions row also calls) ALSO calls GetTopBotsAsync every request --
        // <=7h / >100h cleanly excludes that fixed-24h call from either bucket.
        Assert.Contains(narrowTopBots, c => SpanHours(c) <= 7);
        Assert.Contains(wideTopBots, c => SpanHours(c) > 100);

        // Threats: GetThreatsAsync now receives the manifest's real window bounds instead
        // of always null/null (all-time).
        Assert.Contains(narrowThreats, c => c.Start is not null && SpanHours(c) <= 7);
        Assert.Contains(wideThreats, c => c.Start is not null && SpanHours(c) > 100);

        // UserAgents: routed through the composer's "useragents-raw" widget key on
        // "dashboard.traffic" (GetDetectionsAsync's DashboardFilter.StartTime/EndTime),
        // not the fixed-window DashboardAggregateCache snapshot.
        Assert.Contains(narrowDetections, c => c.Start is not null && SpanHours(c) <= 7);
        Assert.Contains(wideDetections, c => c.Start is not null && SpanHours(c) > 100);

        // Sessions: GetRecentSessionsAsync's "since" bound is the window's start, not the
        // fixed DetectionRetention default (30 days).
        Assert.Contains(narrowSinces, s => s is not null && DateTime.UtcNow - s.Value <= TimeSpan.FromHours(7));
        Assert.Contains(wideSinces, s => s is not null && DateTime.UtcNow - s.Value > TimeSpan.FromHours(100));

        return;

        static double SpanHours((DateTime? Start, DateTime? End) call)
        {
            var start = call.Start ?? DateTime.UtcNow.AddHours(-24);
            var end = call.End ?? DateTime.UtcNow;
            return (end - start).TotalHours;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private sealed class SingleClusterReader : IBotClusterReader
    {
        private readonly string _memberSignature;
        public SingleClusterReader(string memberSignature) => _memberSignature = memberSignature;

        public Task<IReadOnlyList<BotCluster>> GetClustersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BotCluster>>(new List<BotCluster>
            {
                new()
                {
                    ClusterId = "window-threading-test-cluster",
                    MemberSignatures = new List<string> { _memberSignature },
                    Type = BotClusterType.BotNetwork,
                    MemberCount = 1,
                    Label = "WindowThreadingTestCluster",
                }
            });

        public Task<BotClusterService.ClusterDiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken ct = default) =>
            Task.FromResult(new BotClusterService.ClusterDiagnosticsSnapshot { Algorithm = "leiden", Status = "ok" });
    }

    /// <summary>
    ///     Fake store whose windowed reads (GetTopBotsAsync/GetThreatsAsync/GetDetectionsAsync)
    ///     record the start/end they were called with, and whose GetTopBotsAsync hit count for
    ///     <see cref="MemberSignature"/> depends on the window's span (narrow vs wide) -- the
    ///     signal the Clusters row's rendered WindowHitCount proves reached the request.
    /// </summary>
    private sealed class WindowSensitiveEventStore : IDashboardEventStore
    {
        private readonly string _memberSignature;
        private readonly int _narrowHitCount;
        private readonly int _wideHitCount;

        public WindowSensitiveEventStore(string memberSignature, int narrowHitCount, int wideHitCount)
        {
            _memberSignature = memberSignature;
            _narrowHitCount = narrowHitCount;
            _wideHitCount = wideHitCount;
        }

        public List<(DateTime? Start, DateTime? End)> TopBotsCalls { get; } = new();
        public List<(DateTime? Start, DateTime? End)> ThreatsCalls { get; } = new();
        public List<(DateTime? Start, DateTime? End)> DetectionsCalls { get; } = new();

        public void Reset()
        {
            TopBotsCalls.Clear();
            ThreatsCalls.Clear();
            DetectionsCalls.Clear();
        }

        private int HitCountFor(DateTime? start, DateTime? end)
        {
            var span = (end ?? DateTime.UtcNow) - (start ?? DateTime.UtcNow.AddHours(-24));
            return span.TotalHours <= 24 ? _narrowHitCount : _wideHitCount;
        }

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
            => Task.FromResult(new DashboardDatasetBundle(
                Summary: new DashboardSummary
                {
                    Timestamp = DateTime.UtcNow, TotalRequests = 10, BotRequests = 5, HumanRequests = 5,
                    UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(),
                    UniqueSignatures = 1,
                },
                TimeBuckets: new List<DashboardTimeSeriesPoint>(),
                BotAggregate: new List<DashboardTopBotEntry>
                {
                    new()
                    {
                        PrimarySignature = _memberSignature, BotName = "WindowTestBot", HitCount = 1,
                        BotProbability = 0.9, BotType = "Scraper", LastSeen = DateTime.UtcNow,
                    },
                },
                Geo: new List<DashboardCountryStats> { new() { CountryCode = "US", TotalCount = 10, BotCount = 5 } },
                Endpoints: new List<DashboardEndpointStats>()));

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            TopBotsCalls.Add((startTime, endTime));
            return Task.FromResult(new List<DashboardTopBotEntry>
            {
                new() { PrimarySignature = _memberSignature, HitCount = HitCountFor(startTime, endTime) },
            });
        }

        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
        {
            ThreatsCalls.Add((startTime, endTime));
            return Task.FromResult(new List<ThreatEntry>());
        }

        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
        {
            DetectionsCalls.Add((filter?.StartTime, filter?.EndTime));
            return Task.FromResult(new List<DashboardDetectionEvent>());
        }

        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => Task.FromResult(new List<DashboardSignatureEvent>());
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

    /// <summary>
    ///     Minimal <see cref="IDetectionArchive"/> fake recording the "since" bound
    ///     <see cref="GetRecentSessionsAsync"/> was called with; every other member is a
    ///     no-op stub (mirrors <c>SessionPersistenceServiceTickTests.RecordingSessionStore</c>'s
    ///     established shape for this interface).
    /// </summary>
    private sealed class WindowSensitiveDetectionArchive : IDetectionArchive
    {
        public List<DateTime?> SinceCalls { get; } = new();
        public void Reset() => SinceCalls.Clear();

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
            SinceCalls.Add(since);
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
}
