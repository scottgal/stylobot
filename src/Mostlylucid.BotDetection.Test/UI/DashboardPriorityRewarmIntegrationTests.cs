using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.Common.Scheduling;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Task 3 (window-threading branch): on a cold content-cache miss,
///     <c>StyloBotDashboardMiddleware.GetOrWarmingAsync</c> now fires an out-of-band
///     priority re-warm via <see cref="DashboardMaterializerCoordinator.MarkDirtyAsync"/>
///     instead of silently waiting for the next scheduled Tick10s sweep.
///
///     <para>
///         <see cref="DashboardMaterializerCoordinator"/> is a <c>sealed</c> class with no
///         virtual members, so it cannot be mocked directly (Moq requires an interface or a
///         non-sealed class with overridable members). Its own dependencies
///         (<see cref="IDashboardContentCache"/>, the manifest source) ARE interfaces, so
///         this test wires a REAL coordinator to a REAL <see cref="DashboardContentCache"/>
///         whose compose delegate is wrapped to record every call -- the same "observe the
///         real effect" strategy <see cref="DashboardRowExtraContentCacheIntegrationTests"/>
///         and <see cref="DashboardRowWindowThreadingIntegrationTests"/> already use for this
///         subsystem. A compose call reaching the pageKey's manifest, off the request thread,
///         after a cold-miss request completed, IS the observable proof that
///         <c>MarkDirtyAsync</c> ran for that key -- a stronger assertion than a mock
///         invocation count would be, since it proves the coordinator actually found the
///         envelope <c>GetCurrentAsync</c> marked live and re-warmed it end to end.
///     </para>
///     <para>
///         <c>AddStyloBotDashboard</c> also registers a REAL <see cref="IScheduleCoordinator"/>
///         (<c>Mostlylucid.Common.Scheduling</c>'s wall-clock-aligned coordinator, firing
///         <see cref="TickCadence.Tick10s"/> on every multiple of 10s past the minute --
///         i.e. potentially under a second away from whenever the test happens to start).
///         Left alone, the coordinator's own ordinary tick loop would independently warm
///         the same live envelope this test is trying to isolate, making the assertion pass
///         for the WRONG reason depending on test wall-clock timing (confirmed while writing
///         this test: it intermittently passed even with the priority re-warm call deleted).
///         <see cref="NeverTickingScheduleCoordinator"/> is registered ahead of
///         <c>AddStyloBotDashboard</c> (whose registration is <c>TryAddSingleton</c>) so the
///         materializer's tick subscription exists but its handler is never invoked --
///         isolating the assertion to ONLY the fire-and-forget <c>MarkDirtyAsync</c> path.
///     </para>
/// </summary>
public sealed class DashboardPriorityRewarmIntegrationTests : IAsyncDisposable
{
    private WebApplication? _app;

    /// <summary>Accepts subscriptions but never invokes them -- see the class doc comment.</summary>
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
    public async Task Cold_miss_fires_a_priority_rewarm_that_composes_the_pagekey()
    {
        var composeCalls = new System.Collections.Concurrent.ConcurrentBag<string>();
        var manifests = new DefaultDashboardPageManifestSource();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, new BenignEventStore());

        long tick = 1;
        // Deliberately NEVER pre-warmed: GetCurrentAsync structurally returns
        // DashboardPageResult.Warming on the first read (never composes on the request
        // thread) but ALSO marks the envelope live -- which is exactly what lets the
        // fire-and-forget MarkDirtyAsync find it and re-warm out of band.
        var cache = new DashboardContentCache(
            compose: async (m, w, ct) =>
            {
                composeCalls.Add(m.PageKey);
                return await composer.ComposeAsync(m, w, ct);
            },
            currentTick: () => tick,
            options: Options.Create(new DashboardMaterializerOptions { Enabled = true }));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(new BenignEventStore());
        builder.Services.AddSingleton<IDashboardContentCache>(cache);
        builder.Services.AddSingleton<IDashboardPageManifestSource>(manifests);
        builder.Services.AddSingleton<IScheduleCoordinator>(new NeverTickingScheduleCoordinator());
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

        // No compose call has happened yet -- a genuinely cold cache.
        Assert.Empty(composeCalls);

        var response = await client.GetAsync("/dashboard/clusters?window=6h");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // The request itself never composed synchronously (structural rule) -- it rendered
        // the Warming placeholder for every row-extra manifest.
        Assert.Contains("Warming up", html);

        // The priority re-warm is fire-and-forget -- give the background Task.Run a moment
        // to complete, then assert it actually reached the composer for at least one of the
        // four row-extra pageKeys (dashboard.clusters / dashboard.topbots / dashboard.sessions
        // / dashboard.threats -- ServeDashboardPageAsync requests all four on every render).
        var composed = await WaitUntilAsync(() => composeCalls.Count > 0, TimeSpan.FromSeconds(3));

        Assert.True(composed, "Expected the fire-and-forget priority re-warm to reach the composer within 3s of a cold-miss request.");
        Assert.Contains("dashboard.topbots", composeCalls);
    }

    [Fact]
    public async Task Warm_hit_does_not_trigger_a_priority_rewarm()
    {
        var composeCalls = new System.Collections.Concurrent.ConcurrentBag<string>();
        var manifests = new DefaultDashboardPageManifestSource();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, new BenignEventStore());

        long tick = 1;
        var cache = new DashboardContentCache(
            compose: async (m, w, ct) =>
            {
                composeCalls.Add(m.PageKey);
                return await composer.ComposeAsync(m, w, ct);
            },
            currentTick: () => tick,
            options: Options.Create(new DashboardMaterializerOptions { Enabled = true }));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(new BenignEventStore());
        builder.Services.AddSingleton<IDashboardContentCache>(cache);
        builder.Services.AddSingleton<IDashboardPageManifestSource>(manifests);
        builder.Services.AddSingleton<IScheduleCoordinator>(new NeverTickingScheduleCoordinator());
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

        // First request: cold miss, triggers the SAME background priority re-warm exercised
        // above. Wait for it to land so the cache genuinely becomes warm for this envelope.
        var firstResponse = await client.GetAsync("/dashboard/clusters?window=6h");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var warmed = await WaitUntilAsync(() => composeCalls.Contains("dashboard.topbots"), TimeSpan.FromSeconds(3));
        Assert.True(warmed, "Setup precondition failed: the priority re-warm never composed dashboard.topbots.");

        // Second request: SAME window token, issued immediately after (same 5-minute bucket
        // for "6h" -- see HitsPerPeriodChartletBuilder.BucketSizeForWindow), so it resolves to
        // the SAME envelope the first request's re-warm just composed. This is now a WARM hit.
        composeCalls.Clear();
        var secondResponse = await client.GetAsync("/dashboard/clusters?window=6h");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var secondHtml = await secondResponse.Content.ReadAsStringAsync();

        // A warm hit renders real (composed) data for at least the TopBots-backed rows --
        // no "Warming up" copy for a row whose envelope is now warm.
        Assert.DoesNotContain("Warming up", secondHtml);

        // No NEW compose call should follow a warm hit -- give any errant fire-and-forget
        // task the same grace window as the cold-miss test before asserting silence.
        await Task.Delay(500);
        Assert.Empty(composeCalls);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    /// <summary>Benign store: every read returns empty/zeroed data, nothing throws.</summary>
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
}
