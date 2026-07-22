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
/// Reproduces the staging "visitors landing renders all-zero / all-absent" regression
/// against the real registry/row path (StyloBotDashboardMiddleware -> Index.cshtml ->
/// _Visitors.cshtml -> Visitors/Index.cshtml), for a LOCAL (direct-store) host — the
/// actual topology of the marketing site's commercial Postgres-backed dashboard.
///
/// Every other per-widget fetch in ServeDashboardPageAsync (summary, countries,
/// endpoints) is wrapped in a Safe* try/catch that degrades to an empty/zero default
/// on failure. The visitor-list fetch (GetTopBotsAsync, feeding the SbVisitorList
/// component) was NOT. When the store's top-bots read fails while summary/countries
/// still succeed, the unguarded visitorTask exception propagates out of the
/// Task.WhenAll and aborts ServeDashboardPageAsync entirely -- so the WHOLE page
/// (including the hardcoded representative signature shapes, which have no data
/// dependency) never renders. That reads to an operator as "everything is zero".
/// </summary>
public sealed class VisitorsListFetchFailureIntegrationTests : IAsyncDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Visitors_route_still_renders_when_visitor_list_fetch_fails()
    {
        var store = new TopBotsThrowingEventStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // Register the fake store BEFORE AddStyloBotDashboard so its
        // TryAddSingleton<IDashboardEventStore, SqliteDashboardEventStore>() no-ops.
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

        var response = await _app.GetTestClient().GetAsync("/dashboard/visitors");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Summary/countries came from the store directly -- must be real, not zeroed.
        Assert.Contains("77", html);
        // Hardcoded representative shapes have no data dependency; their absence means
        // the canonical Visitors/Index.cshtml partial never rendered at all.
        Assert.Contains("Representative bot", html);
        Assert.Contains("countries-map-visitors", html);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private sealed class TopBotsThrowingEventStore : IDashboardEventStore
    {
        public Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 77,
                BotRequests = 20,
                HumanRequests = 57,
                UncertainRequests = 0,
                UniqueSignatures = 9,
                HumanFingerprints = 5,
                BotFingerprints = 4,
                RiskBandCounts = new(),
                TopBotTypes = new(),
                TopActions = new(),
            });

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => throw new InvalidOperationException("simulated transient store failure on top-bots read");

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(
            int count = 20, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardCountryStats>
            {
                new() { CountryCode = "GB", TotalCount = 77, BotCount = 20 }
            });

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardEndpointStats>());

        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
            => Task.FromResult(new List<ThreatEntry>());

        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(
            DateTime startTime, DateTime endTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());

        // ComputeUserAgentsFallbackAsync reads through this in the same parallel batch.
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult(new List<DashboardDetectionEvent>());

        // Not exercised by this route -- fail loudly if that assumption changes.
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
    }
}
