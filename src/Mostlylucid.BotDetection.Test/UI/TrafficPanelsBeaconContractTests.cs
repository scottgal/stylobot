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
using Mostlylucid.BotDetection.UI.Services.Dashboard;
using Mostlylucid.Common.Scheduling;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     The four Traffic side panels' SSR-only render contract (dash- 2026-08-16, after the
///     operator's rip-out a62024fd):
///     <list type="bullet">
///         <item>First paint is the SSR-complete page with REAL data (page 200 + the
///             widgets' data present when the store has it).</item>
///         <item>No beacon widget attrs on the panels: data-sb-widget / data-sb-depends stay
///             DELETED until the update machinery returns as the gated re-activation. The
///             container keeps id="traffic-panels" + data-sb-params (render-state only).</item>
///         <item>No "Warming up" strip anywhere — the spinner is dead; a cold miss renders
///             the honest empty state.</item>
///         <item>The /dashboard/partials/update batch endpoint survives server-side
///             (dormant structure for the re-activation), but no test pins a client batch
///             fetch — the client never calls it today.</item>
///     </list>
/// </summary>
public sealed class TrafficPanelsBeaconContractTests : IAsyncDisposable
{
    private WebApplication? _app;

    private sealed class NeverTickingScheduleCoordinator : IScheduleCoordinator
    {
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }

        public IDisposable Subscribe(TickCadence cadence, string subscriberName, CostHint costHint, Func<DateTimeOffset, CancellationToken, Task> handler)
            => new NoopDisposable();

        public IReadOnlyList<TickSubscriberMetadata> Snapshot() => Array.Empty<TickSubscriberMetadata>();
    }

    [Fact]
    public async Task First_load_renders_real_data_with_no_beacon_contract()
    {
        var store = new SeededEventStore();
        var manifests = new DefaultDashboardPageManifestSource();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        long tick = 1;
        var cache = new DashboardContentCache(
            compose: (m, w, ct) => composer.ComposeAsync(m, w, ct),
            currentTick: () => tick,
            options: Options.Create(new DashboardMaterializerOptions { Enabled = true, BootPrewarmEnabled = true }));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(store);
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
        // Boot prewarm ON for this host (the materializer's StartAsync pass composes the
        // pinned windows before the first request can land).
        builder.Services.Configure<DashboardMaterializerOptions>(o => o.BootPrewarmEnabled = true);
        builder.Services.AddStyloBotWidgets();

        _app = builder.Build();
        _app.UseMiddleware<SbWidgetBatchMiddleware>();
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        var client = _app.GetTestClient();

        // ---- First page load: SSR-complete first paint with real data, no beacon. ----
        var response = await client.GetAsync("/dashboard/traffic");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // SSR-only contract: no beacon widget attrs on the TRAFFIC PANELS container —
        // the client does NOTHING for these widgets. The container keeps its render-state
        // id + params only. (Other widgets elsewhere on the page may still carry their own
        // attrs; the rip-out a62024fd scoped the traffic panels.)
        Assert.Contains("id=\"traffic-panels\"", html);
        Assert.DoesNotContain("data-sb-widget=\"traffic-panels\"", html);
        Assert.DoesNotContain("data-sb-depends=\"countries,signature,threats\"", html);
        Assert.Contains("data-sb-params=\"window=", html);
        Assert.DoesNotContain("Warming up", html);
        Assert.Contains("GPTBot", html); // seeded bot surfaces in the panels (by source / top visitors / threats)

        // No client batch fetch is pinned: the /dashboard/partials/update endpoint
        // survives server-side but the client never calls it today (dash- 2026-08-16).
    }

    /// <summary>Seed store: one bot + one country so the composed bundle carries real data.</summary>
    private sealed class SeededEventStore : IDashboardEventStore
    {
        private static readonly List<DashboardTopBotEntry> Bots =
        [
            new()
            {
                PrimarySignature = "sig-1",
                BotName = "GPTBot",
                BotType = "AI",
                RiskBand = "High",
                BotProbability = 0.95,
                Confidence = 0.9,
                HitCount = 42,
                CountryCode = "GB",
                FirstSeen = DateTime.UtcNow.AddHours(-2),
                LastSeen = DateTime.UtcNow,
            },
        ];

        private static readonly List<DashboardCountryStats> Countries =
        [
            new() { CountryCode = "GB", TotalCount = 50, BotCount = 25, BotRate = 0.5 },
        ];

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
            => Task.FromResult(new DashboardDatasetBundle(
                new DashboardSummary
                {
                    Timestamp = DateTime.UtcNow, TotalRequests = 100, BotRequests = 50, HumanRequests = 50,
                    UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(), UniqueSignatures = 1,
                },
                new List<DashboardTimeSeriesPoint>(),
                Bots,
                Countries,
                new List<DashboardEndpointStats>
                {
                    new() { Path = "/api/health", Method = "GET", TotalCount = 10, BotCount = 2, BotRate = 0.2 },
                }));

        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow, TotalRequests = 100, BotRequests = 50, HumanRequests = 50,
                UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(), UniqueSignatures = 1,
            });

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(Bots);

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(Countries);

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardEndpointStats>
            {
                new() { Path = "/api/health", Method = "GET", TotalCount = 10, BotCount = 2, BotRate = 0.2 },
            });

        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<ThreatEntry>());

        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult(new List<DashboardDetectionEvent>());

        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardTimeSeriesPoint>());

        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());

        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
