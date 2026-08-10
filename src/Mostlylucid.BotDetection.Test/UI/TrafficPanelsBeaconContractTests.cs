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
///     The four Traffic side panels' Signal-Shingle beacon contract (RC2):
///     <list type="bullet">
///         <item>#traffic-panels carries data-sb-widget / data-sb-depends / data-sb-params,
///             so the content-ready beacon (BroadcastDirty) can target it at all — before
///             this, the four panels had no beacon contract and stayed "Warming up" forever
///             after a cold-miss first paint.</item>
///         <item>The boot prewarm (BootPrewarmEnabled) composes the pinned windows at host
///             start, so the first page load renders real data, not the warming shell.</item>
///         <item>The batch path renders "traffic-panels" from the warm page bundle through
///             the same TrafficPanelsProjector the SSR uses — an OOB swap can never
///             disagree with the first paint.</item>
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
    public async Task First_load_renders_data_and_panels_carry_the_beacon_contract()
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

        // ---- First page load: data, not warming, and the panels carry the contract. ----
        var response = await client.GetAsync("/dashboard/traffic");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-sb-widget=\"traffic-panels\"", html);
        Assert.Contains("data-sb-depends=\"countries,signature,threats\"", html);
        Assert.Contains("data-sb-params=\"window=", html);
        Assert.DoesNotContain("Warming up", html);
        Assert.Contains("GPTBot", html); // seeded bot surfaces in the panels (by source / top visitors / threats)

        // ---- Batch refresh: the content-ready ping's fetch renders the SAME panels. ----
        var batch = await client.GetAsync(
            "/dashboard/partials/update?widgets=traffic-panels&traffic-panels.window=24h");
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        var batchHtml = await batch.Content.ReadAsStringAsync();

        Assert.Contains("traffic-panels", batchHtml);
        Assert.Contains("hx-swap-oob", batchHtml);
        Assert.Contains("GPTBot", batchHtml);
        Assert.DoesNotContain("Warming up", batchHtml);
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
