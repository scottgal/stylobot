using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.Dashboard;
using Mostlylucid.Common.Scheduling;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Boot-time materializer pass (DashboardMaterializerOptions.BootPrewarmEnabled) and the
///     content-ready beacon's surface kinds (PageSurfaceKindsFor).
///     <para>
///         Boot prewarm: the tick loop's first fire waits for the next wall-clock Tick10s
///         boundary, so without this pass the first request can cold-miss (Warming shell)
///         for up to 10s after boot. StartAsync fires one MaterializeTickAsync immediately
///         and bound-awaits it, so the pinned dashboard.traffic windows are composed before
///         the first request lands.
///     </para>
///     <para>
///         Beacon kinds: the materializer's warm bumps + queues the page's SURFACE KINDS
///         (summary/countries/...) instead of the raw page key, because the client
///         (sb-live-updates.js) matches a widget's data-sb-depends against the beacon's
///         dirtyKinds — a raw "dashboard.traffic" never intersects any depends, so the
///         content-ready ping was unmatchable ("the SignalR content-ready ping never
///         happens").
///     </para>
/// </summary>
public sealed class DashboardBootPrewarmIntegrationTests
{
    private sealed class NeverTickingScheduleCoordinator : IScheduleCoordinator
    {
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }

        public IDisposable Subscribe(TickCadence cadence, string subscriberName, CostHint costHint, Func<DateTimeOffset, CancellationToken, Task> handler)
            => new NoopDisposable();

        public IReadOnlyList<TickSubscriberMetadata> Snapshot() => Array.Empty<TickSubscriberMetadata>();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task Boot_prewarm_composes_the_pinned_traffic_windows_before_any_request()
    {
        var composeCalls = new ConcurrentBag<string>();
        var manifests = new DefaultDashboardPageManifestSource();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, new BenignEventStore());
        long tick = 1;
        var options = Options.Create(new DashboardMaterializerOptions
        {
            Enabled = true,
            BootPrewarmEnabled = true,
        });
        var cache = new DashboardContentCache(
            compose: async (m, w, ct) =>
            {
                composeCalls.Add(m.PageKey);
                return await composer.ComposeAsync(m, w, ct);
            },
            currentTick: () => tick,
            options: options);
        var cursor = new DashboardChangeCursor();
        var coordinator = new DashboardMaterializerCoordinator(
            cache, cursor, manifests, options,
            schedule: new NeverTickingScheduleCoordinator(),
            hubContext: new RecordingHubContext(new RecordingHub()));

        await coordinator.StartAsync(CancellationToken.None);

        // The 4 pinned PrewarmWindows (6h/24h/7d/30d) were composed before anything was
        // requested -- the first request now resolves to a warm bundle instead of Warming.
        Assert.True(coordinator.HasWarmedSuccessfully,
            "boot pass must latch the materializer health latch");
        Assert.True(composeCalls.Count(k => k == "dashboard.traffic") >= 4,
            $"expected >=4 pinned traffic composes at boot, got {composeCalls.Count(k => k == "dashboard.traffic")}");

        // The warm also bumped the page's SURFACE KINDS (not the raw page key), so the
        // content-ready beacon's dirtyKinds intersect widget data-sb-depends.
        Assert.Equal(cursor.CurrentTick, cursor.TickFor("summary"));
        Assert.Equal(cursor.CurrentTick, cursor.TickFor("countries"));
        Assert.Equal(cursor.CurrentTick, cursor.TickFor("threats"));
        Assert.Equal(0, cursor.TickFor("dashboard.traffic"));
    }

    [Fact]
    public async Task Boot_prewarm_disabled_does_not_compose_at_startup()
    {
        var composeCalls = new ConcurrentBag<string>();
        var manifests = new DefaultDashboardPageManifestSource();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, new BenignEventStore());
        long tick = 1;
        var options = Options.Create(new DashboardMaterializerOptions { Enabled = true });
        var cache = new DashboardContentCache(
            compose: async (m, w, ct) =>
            {
                composeCalls.Add(m.PageKey);
                return await composer.ComposeAsync(m, w, ct);
            },
            currentTick: () => tick,
            options: options);
        var cursor = new DashboardChangeCursor();
        var coordinator = new DashboardMaterializerCoordinator(
            cache, cursor, manifests, options,
            schedule: new NeverTickingScheduleCoordinator());

        await coordinator.StartAsync(CancellationToken.None);

        // Default (off): no behavior change for hosts that don't opt in.
        Assert.Empty(composeCalls);
        Assert.False(coordinator.HasWarmedSuccessfully);
    }

    [Fact]
    public async Task Content_ready_beacon_carries_surface_kinds_not_the_page_key()
    {
        var hub = new RecordingHub();
        var hubContext = new RecordingHubContext(hub);
        var manifests = new DefaultDashboardPageManifestSource();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, new BenignEventStore());
        long tick = 1;
        var options = Options.Create(new DashboardMaterializerOptions
        {
            Enabled = true,
            BootPrewarmEnabled = true,
            MaterializerBroadcastIntervalMs = 20,
        });
        var cache = new DashboardContentCache(
            compose: (m, w, ct) => composer.ComposeAsync(m, w, ct),
            currentTick: () => tick,
            options: options);
        var cursor = new DashboardChangeCursor();
        // Wire the constrainer's cursor the way BroadcastConstrainerCursorWire does at
        // host start -- without it the flush window skips the BroadcastDirty beacon.
        SignalRBroadcastConstrainer.SetCursor(cursor);
        var coordinator = new DashboardMaterializerCoordinator(
            cache, cursor, manifests, options,
            schedule: new NeverTickingScheduleCoordinator(),
            hubContext: hubContext);

        await coordinator.StartAsync(CancellationToken.None);

        // Drain the constrainer's flush window: the BroadcastDirty beacon must carry the
        // dashboard.traffic SURFACE KINDS so the client's data-sb-depends matches.
        var received = await WaitUntilAsync(
            () => hub.DirtyBeacons.Count > 0, TimeSpan.FromSeconds(5));
        Assert.True(received, "expected the constrainer to flush a BroadcastDirty beacon");
        var kinds = hub.DirtyBeacons.SelectMany(b => b.DirtyKinds).ToHashSet();
        Assert.Contains("summary", kinds);
        Assert.Contains("countries", kinds);
        Assert.Contains("threats", kinds);
        Assert.DoesNotContain("dashboard.traffic", kinds);
    }

    // Reuses the benign store shape from DashboardPriorityRewarmIntegrationTests: the
    // composer path only needs the store's ComposeBatchAsync + direct reads, all empty.
    private sealed class BenignEventStore : IDashboardEventStore
    {
        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
            => Task.FromResult(new DashboardDatasetBundle(null, null, null, null, null));

        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow, TotalRequests = 0, BotRequests = 0, HumanRequests = 0,
                UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(), UniqueSignatures = 0,
            });

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardTopBotEntry>());

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardCountryStats>());

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardEndpointStats>());

        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<ThreatEntry>());

        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult(new List<DashboardDetectionEvent>());

        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());

        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
    }

    private sealed class RecordingHub : IStyloBotDashboardHub
    {
        public List<DashboardDirtyBeacon> DirtyBeacons { get; } = new();

        public Task BroadcastDirty(DashboardDirtyBeacon beacon)
        {
            lock (DirtyBeacons) DirtyBeacons.Add(beacon);
            return Task.CompletedTask;
        }

        public Task BroadcastInvalidation(string signal) => Task.CompletedTask;
        public Task BroadcastAttackArc(string countryCode, string riskBand) => Task.CompletedTask;
        public Task PolicyChanged(string scopeKey) => Task.CompletedTask;
        public Task FingerprintDirty(string fingerprintId, string slot) => Task.CompletedTask;
    }

    private sealed class RecordingHubContext : IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>
    {
        private readonly RecordingClients _clients;
        public RecordingHubContext(RecordingHub hub) => _clients = new RecordingClients(hub);
        public IHubClients<IStyloBotDashboardHub> Clients => _clients;
        public IGroupManager Groups => new NoopGroupManager();
    }

    private sealed class RecordingClients : IHubClients<IStyloBotDashboardHub>
    {
        private readonly IStyloBotDashboardHub _hub;
        public RecordingClients(IStyloBotDashboardHub hub) => _hub = hub;
        public IStyloBotDashboardHub All => _hub;
        public IStyloBotDashboardHub AllExcept(IReadOnlyList<string> excludedConnectionIds) => _hub;
        public IStyloBotDashboardHub Client(string connectionId) => _hub;
        public IStyloBotDashboardHub Clients(IReadOnlyList<string> connectionIds) => _hub;
        public IStyloBotDashboardHub Group(string groupName) => _hub;
        public IStyloBotDashboardHub GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _hub;
        public IStyloBotDashboardHub Groups(IReadOnlyList<string> groupNames) => _hub;
        public IStyloBotDashboardHub User(string userId) => _hub;
        public IStyloBotDashboardHub Users(IReadOnlyList<string> userIds) => _hub;
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
