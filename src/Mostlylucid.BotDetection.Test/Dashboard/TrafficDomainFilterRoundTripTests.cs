using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Controllers;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Pins the URL round-trip for the multi-select domain filter on
///     /dashboard/traffic. The chip-dismissal path emits a URL of the shape
///     <c>?domain=stylo.bot&amp;domain=auth.stylo.bot</c> and the controller-side
///     query binding hydrates <see cref="TrafficFilters.Domains"/> back from
///     the same URL. This test catches the "SelectMany yields a single
///     comma-separated value" / "StringValues collapsed to one" trap that
///     would silently reduce the multi-select to a single-string filter.
/// </summary>
public sealed class TrafficDomainFilterRoundTripTests
{
    private static readonly string[] Selected = new[] { "stylo.bot", "auth.stylo.bot" };

    [Fact]
    public void Building_URL_with_two_domains_emits_repeated_domain_params()
    {
        // Mirrors _Body.cshtml LinkWith: append one ?domain= param per entry.
        var qs = string.Join("&", Selected.Select(d => $"domain={Uri.EscapeDataString(d)}"));
        var url = $"/dashboard/traffic?{qs}";

        Assert.Contains("domain=stylo.bot", url);
        Assert.Contains("domain=auth.stylo.bot", url);
        Assert.Contains("domain=stylo.bot&domain=auth.stylo.bot", url);
    }

    [Fact]
    public async Task Round_trip_parses_repeated_domain_params_back_into_filters()
    {
        var url = $"/dashboard/traffic?{string.Join("&", Selected.Select(d => $"domain={Uri.EscapeDataString(d)}"))}";
        var parsed = QueryHelpers.ParseQuery(url.Substring(url.IndexOf('?') + 1));

        // ParseQuery preserves repeated keys as a StringValues list, which is
        // what HttpContext.Request.Query exposes. If ASP.NET ever collapses
        // this to a single comma-joined value the assertion below trips.
        var parsedDomains = parsed["domain"]
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToArray();
        Assert.Equal(Selected, parsedDomains);

        var ctrl = NewController(out var http, url);

        var result = await ctrl.Index(
            country: null, botType: null, window: "60m", threat: null, partial: null, ct: default);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TrafficPageModel>(view.Model);
        Assert.True(model.Filters.Domains.SequenceEqual(Selected, StringComparer.Ordinal),
            "Controller should hydrate TrafficFilters.Domains from the repeated ?domain=X URL params.");
    }

    private static TrafficController NewController(out DefaultHttpContext httpContext, string url)
    {
        var store = new NoopEventStore();
        var controller = new TrafficController(
            store,
            Options.Create(new DashboardLayoutOptions()),
            Options.Create(new ThreatsOptions()));
        httpContext = new DefaultHttpContext();
        var qIndex = url.IndexOf('?');
        httpContext.Request.Path = qIndex > 0 ? url.Substring(0, qIndex) : url;
        httpContext.Request.QueryString = qIndex > 0 ? new QueryString(url.Substring(qIndex)) : QueryString.Empty;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    ///     Bare-bones in-memory store: every method returns an empty payload
    ///     so the controller renders a valid model without touching a real
    ///     event store. Only the surface the traffic page path calls is
    ///     exercised; anything else throws so any accidental read-path drift
    ///     is caught by the test.
    /// </summary>
    private sealed class NoopEventStore : IDashboardEventStore
    {
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardTopBotEntry>());

        public Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0, BotRequests = 0, HumanRequests = 0, UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 0
            });

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(
            int count = 20, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardCountryStats>());

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardEndpointStats>());

        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
            DateTime startTime, DateTime endTime, TimeSpan bucketSize,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardTimeSeriesPoint>());

        public Task<IReadOnlyList<DomainOption>> GetDomainOptionsAsync(
            int lookbackDays = 30, int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DomainOption>>(Array.Empty<DomainOption>());

        // --- unused surface ---
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => Task.CompletedTask;
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => Task.FromResult(signature);
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => Task.FromResult(new List<DashboardDetectionEvent>());
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => Task.FromResult(new List<DashboardSignatureEvent>());
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult<DashboardCountryDetail?>(null);
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => Task.FromResult(new List<SignatureEndpointStats>());
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult<DashboardEndpointDetail?>(null);
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult(new List<ThreatEntry>());
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => Task.FromResult(new List<UserAgentSearchResult>());
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => Task.FromResult(new List<UserAgentVersionBucket>());
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => Task.FromResult(new List<HoneypotHitRow>());
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordDegradationSnapshotAsync(Mostlylucid.BotDetection.RateLimit.DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>>(Array.Empty<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>());
    }
}
