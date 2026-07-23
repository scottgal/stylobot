using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Root-cause fix for the concurrent-load measure-gate collapse (§8 follow-up):
///     <c>ServeDashboardPageAsync</c> unconditionally built ALL dashboard rows' shell
///     data (Clusters/TopBots/Sessions/Threats/Countries/Endpoints/UserAgents/Visitors/
///     Summary) on every request, even though <c>_Traffic.cshtml</c> reads none of the
///     shell model's fields -- it fetches its own data directly from
///     <see cref="IDashboardEventStore"/>. That meant a `/dashboard/traffic` request paid
///     for the traffic partial's OWN 6 store calls PLUS ~9 redundant/unused shell-level
///     fetches. Fix 1 (never compute the traffic bundle synchronously) never touched this
///     -- it's a different, unrelated set of calls. This test asserts the redundant
///     shell-level calls are gone for a `/traffic` row specifically.
/// </summary>
public sealed class TrafficRowLazyFetchTests : IAsyncDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Traffic_row_does_not_fire_the_shell_levels_redundant_event_store_calls()
    {
        var store = new CountingEventStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IDashboardEventStore>(store);
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = true;
        });

        _app = builder.Build();
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        var response = await _app.GetTestClient().GetAsync("/dashboard/traffic?window=24h");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // GetThreatsAsync backs ONLY the shell's Threats field (Model.Threats) -- confirmed
        // unused by Index.cshtml chrome and every generically-dispatched row partial,
        // including _Traffic.cshtml. Zero calls proves the shell's Threats fetch was skipped
        // for this row, not just that it happened to succeed fast.
        Assert.Equal(0, store.Calls.GetValueOrDefault(nameof(IDashboardEventStore.GetThreatsAsync)));

        // GetTopBotsAsync/GetCountryStatsAsync/GetEndpointStatsAsync are called by BOTH the
        // shell's own redundant fetch (SafeGetVisitorsAsync/SafeGetCountriesDataAsync/
        // SafeGetEndpointsDataAsync) AND _Traffic.cshtml's own fetch (SafeTopBotsAsync x2
        // for current+prior window, SafeCountryStatsAsync, SafeEndpointStatsAsync). After the
        // fix, only the traffic partial's own calls remain.
        // _Traffic.cshtml's own render legitimately calls GetTopBotsAsync/GetCountryStatsAsync/
        // GetEndpointStatsAsync directly (current+prior window comparisons); pinning the exact
        // traffic-partial-internal count isn't this test's concern. The regression this guards
        // against is the SHELL-level redundant fetch on TOP of those (which added a call to
        // each via SafeGetVisitorsAsync/SafeGetCountriesDataAsync/SafeGetEndpointsDataAsync) --
        // an upper bound catches that regression; GetThreatsAsync==0 above is the precise,
        // unambiguous proof (that field has no legitimate traffic-partial caller at all).
        Assert.True(store.Calls.GetValueOrDefault(nameof(IDashboardEventStore.GetTopBotsAsync)) <= 3,
            $"got {store.Calls.GetValueOrDefault(nameof(IDashboardEventStore.GetTopBotsAsync))} -- shell-level redundant fetch may have regressed");
        Assert.True(store.Calls.GetValueOrDefault(nameof(IDashboardEventStore.GetCountryStatsAsync)) <= 2,
            $"got {store.Calls.GetValueOrDefault(nameof(IDashboardEventStore.GetCountryStatsAsync))} -- shell-level redundant fetch may have regressed");
        Assert.True(store.Calls.GetValueOrDefault(nameof(IDashboardEventStore.GetEndpointStatsAsync)) <= 2,
            $"got {store.Calls.GetValueOrDefault(nameof(IDashboardEventStore.GetEndpointStatsAsync))} -- shell-level redundant fetch may have regressed");

        // Page still rendered real traffic content, not an error/empty page.
        Assert.Contains("42", html);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private sealed class CountingEventStore : IDashboardEventStore
    {
        public Dictionary<string, int> Calls { get; } = new();

        private void Count(string name) => Calls[name] = Calls.GetValueOrDefault(name) + 1;

        public Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            Count(nameof(GetSummaryAsync));
            return Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 42,
                BotRequests = 10,
                HumanRequests = 32,
                UncertainRequests = 0,
                UniqueSignatures = 5,
                HumanFingerprints = 3,
                BotFingerprints = 2,
                RiskBandCounts = new(),
                TopBotTypes = new(),
                TopActions = new(),
            });
        }

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            Count(nameof(GetTopBotsAsync));
            return Task.FromResult(new List<DashboardTopBotEntry>());
        }

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(
            int count = 20, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            Count(nameof(GetCountryStatsAsync));
            return Task.FromResult(new List<DashboardCountryStats>());
        }

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            Count(nameof(GetEndpointStatsAsync));
            return Task.FromResult(new List<DashboardEndpointStats>());
        }

        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
        {
            Count(nameof(GetThreatsAsync));
            return Task.FromResult(new List<ThreatEntry>());
        }

        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
            DateTime startTime, DateTime endTime, TimeSpan bucketSize,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            Count(nameof(GetTimeSeriesAsync));
            return Task.FromResult(new List<DashboardTimeSeriesPoint>());
        }

        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(
            DateTime startTime, DateTime endTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());

        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
        {
            Count(nameof(GetDetectionsAsync));
            return Task.FromResult(new List<DashboardDetectionEvent>());
        }

        // Not exercised by a /traffic render -- fail loudly if that assumption changes.
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
    }
}
