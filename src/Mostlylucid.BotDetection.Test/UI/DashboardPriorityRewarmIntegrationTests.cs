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
///     On a cold content-cache miss, <c>StyloBotDashboardMiddleware.GetOrWarmingAsync</c>
///     now self-heals: it AWAITS <see cref="IDashboardContentCache.WarmAsync"/> inline and
///     returns real data to the SAME request, instead of firing an out-of-band
///     <see cref="DashboardMaterializerCoordinator.MarkDirtyAsync"/> and rendering a
///     "Warming up" placeholder for the current request while a background task composes
///     for the next one.
///
///     <para>
///         This replaced the fire-and-forget re-warm because it had no fallback on a
///         remote-mode host once the tick materializer stopped running there (db13f2cc):
///         with nothing ever calling <c>WarmAsync</c>, these four rows (Clusters/TopBots/
///         Sessions/Threats) stayed on the "Warming up" placeholder permanently rather than
///         eventually healing. Awaiting the warm inline removes that dependency on a tick
///         loop entirely -- it composes through the same <see cref="IDashboardPageComposer"/>
///         / <see cref="IDashboardEventStore"/> path (the gateway read-through in remote
///         mode) on demand.
///     </para>
///     <para>
///         <c>AddStyloBotDashboard</c> also registers a REAL <see cref="IScheduleCoordinator"/>
///         (<c>Mostlylucid.Common.Scheduling</c>'s wall-clock-aligned coordinator, firing
///         <see cref="TickCadence.Tick10s"/> on every multiple of 10s past the minute --
///         i.e. potentially under a second away from whenever the test happens to start).
///         <see cref="NeverTickingScheduleCoordinator"/> is registered ahead of
///         <c>AddStyloBotDashboard</c> (whose registration is <c>TryAddSingleton</c>) so the
///         materializer's tick subscription exists but its handler is never invoked --
///         isolating the assertion to ONLY the inline self-heal path, proving it does not
///         depend on the tick loop running at all.
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

    [Fact]
    public async Task Cold_miss_self_heals_inline_and_returns_real_data_on_the_same_request()
    {
        var composeCalls = new System.Collections.Concurrent.ConcurrentBag<string>();
        var manifests = new DefaultDashboardPageManifestSource();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, new BenignEventStore());

        long tick = 1;
        // Deliberately never pre-warmed: GetCurrentAsync structurally never composes on the
        // request thread (DashboardContentCache.GetAsync's cold-miss rule), so the FIRST
        // read here always misses. What matters is what GetOrWarmingAsync does next: with
        // the tick materializer never running (NeverTickingScheduleCoordinator below), the
        // ONLY thing that can compose this envelope is the inline WarmAsync self-heal.
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

        // The self-heal is awaited inline, so by the time the response is written the
        // compose has already happened for this request -- no background wait needed, and
        // no tick loop is running (NeverTickingScheduleCoordinator) to have done it instead.
        Assert.Contains("dashboard.topbots", composeCalls);

        var html = await response.Content.ReadAsStringAsync();
        // BenignEventStore returns real-but-empty datasets, which is a genuine "looked and
        // found nothing" result -- not a placeholder -- so this request's own render must
        // not show the Warming copy.
        Assert.DoesNotContain("Warming up", html);
    }

    [Fact]
    public async Task Warm_hit_does_not_recompose()
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

        // First request: cold miss, self-heals inline (same mechanism as the test above),
        // leaving the envelope warm for the second request.
        var firstResponse = await client.GetAsync("/dashboard/clusters?window=6h");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Contains("dashboard.topbots", composeCalls);

        // Second request: SAME window token, issued immediately after (same 5-minute bucket
        // for "6h" -- see HitsPerPeriodChartletBuilder.BucketSizeForWindow), so it resolves to
        // the SAME envelope the first request just composed. This is now a WARM hit.
        composeCalls.Clear();
        var secondResponse = await client.GetAsync("/dashboard/clusters?window=6h");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var secondHtml = await secondResponse.Content.ReadAsStringAsync();

        // A warm hit renders real (composed) data for at least the TopBots-backed rows --
        // no "Warming up" copy for a row whose envelope is now warm.
        Assert.DoesNotContain("Warming up", secondHtml);

        // No NEW compose call should follow a warm hit.
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
