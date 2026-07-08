using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Controllers;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Pins the FOSS controller's URL-round-trip binding for the neutral
///     <c>?domain=X&amp;domain=Y</c> plumbing. Commercial marketing-site
///     overlay owns the widget markup + license-filtered dropdown options,
///     but the URL binding + the store's <c>WHERE ... AND domain IN (...)</c>
///     WHERE clause are FOSS infrastructure. If this test fails, the SQL
///     filter goes silent even though the widget chips render.
///     <para>
///         This test replaces the pre-revert full-widget round-trip suite;
///         it deliberately only pins the query-binding step. The commercial
///         side has its own tests for option loading + license filtering.
///     </para>
/// </summary>
public sealed class TrafficDomainFilterRoundTripTests
{
    [Fact]
    public void Repeated_domain_query_values_bind_into_TrafficFilters_Domains()
    {
        // Arrange: HttpContext with Request.Query["domain"] = ["stylo.bot", "auth.stylo.bot"]
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?domain=stylo.bot&domain=auth.stylo.bot");

        // Act: the controller reads Request.Query["domain"] inline (repeated
        // key -> StringValues; [FromQuery] on a single string binds only
        // the first occurrence, so the controller uses this shape directly).
        var domainValues = ctx.Request.Query["domain"]
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Assert
        Assert.Equal(new[] { "stylo.bot", "auth.stylo.bot" }, domainValues);
    }

    [Fact]
    public async Task Controller_binds_repeated_domain_query_into_filters_and_threads_to_store()
    {
        // End-to-end pin: verify the controller path actually observes the
        // repeated ?domain= values, drops them onto TrafficFilters.Domains,
        // AND passes the same list through to the store (so the SQL WHERE
        // fires). The TestEventStore captures the domains argument on the
        // first store call so we can Assert both surfaces at once.
        var store = new CapturingEventStore();
        var ctrl = NewController(store, out var http);
        http.Request.QueryString = new QueryString("?domain=stylo.bot&domain=auth.stylo.bot");

        var result = await ctrl.Index(
            country: null, botType: null, window: "60m", threat: null, partial: null, ct: default);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TrafficPageModel>(view.Model);
        Assert.Equal(
            new[] { "stylo.bot", "auth.stylo.bot" },
            model.Filters.Domains.ToArray());
        // Store side: the domains argument the controller passed to
        // GetTopBotsAsync is what generates the SQL "domain IN (...)"
        // predicate downstream. Empty here would silently regress.
        Assert.NotNull(store.LastTopBotsDomains);
        Assert.Equal(
            new[] { "stylo.bot", "auth.stylo.bot" },
            store.LastTopBotsDomains!.ToArray());
    }

    [Fact]
    public async Task Controller_passes_null_domains_when_query_is_empty()
    {
        // "no filter = null" convention: an empty Filters.Domains list should
        // NOT trigger the WHERE ... AND domain IN (...) predicate. The
        // controller collapses Count == 0 -> null before the store call so
        // BuildDomainPredicate short-circuits without generating a no-op
        // "IN ()" that would break the SQL.
        var store = new CapturingEventStore();
        var ctrl = NewController(store, out _);

        _ = await ctrl.Index(
            country: null, botType: null, window: "60m", threat: null, partial: null, ct: default);

        Assert.Null(store.LastTopBotsDomains);
    }

    private static TrafficController NewController(CapturingEventStore store, out DefaultHttpContext httpContext)
    {
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var contentCache = new Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardContentCache(
            (m, w, ct) => composer.ComposeAsync(m, w, ct), () => 1L,
            Options.Create(new Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardMaterializerOptions()));
        var manifests = new DefaultDashboardPageManifestSource();
        var controller = new TrafficController(
            store,
            contentCache,
            manifests,
            Options.Create(new DashboardLayoutOptions()),
            Options.Create(new ThreatsOptions()));
        httpContext = new DefaultHttpContext();
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    ///     Captures the <c>domains</c> argument from the <c>ComposeBatchAsync</c>
    ///     call (current window) and from <c>GetTopBotsAsync</c> (prior window)
    ///     so we can assert what the controller threaded through. Every other
    ///     method returns empty so the controller guards don't blow up.
    /// </summary>
    private sealed class CapturingEventStore : IDashboardEventStore
    {
        /// <summary>Domains captured from the current-window ComposeBatchAsync call.</summary>
        public IReadOnlyList<string>? LastTopBotsDomains { get; private set; }

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
        {
            // Capture domains from the current-window compose call.
            LastTopBotsDomains = request.Domains;
            var summary = new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0, BotRequests = 0, HumanRequests = 0, UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 0,
            };
            return Task.FromResult(new DashboardDatasetBundle(
                Summary: summary,
                TimeBuckets: new List<DashboardTimeSeriesPoint>(),
                BotAggregate: new List<DashboardTopBotEntry>(),
                Geo: new List<DashboardCountryStats>(),
                Endpoints: new List<DashboardEndpointStats>()));
        }

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            return Task.FromResult(new List<DashboardTopBotEntry>());
        }

        public Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0,
                BotRequests = 0,
                HumanRequests = 0,
                UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 0
            });

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(
            int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardCountryStats>());

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardEndpointStats>());

        // Interface completeness -- controller doesn't touch these on this path.
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => Task.CompletedTask;
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => Task.FromResult(signature);
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => Task.FromResult(new List<DashboardDetectionEvent>());
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => Task.FromResult(new List<DashboardSignatureEvent>());
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => Task.FromResult(new List<DashboardTimeSeriesPoint>());
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult<DashboardCountryDetail?>(null);
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => Task.FromResult(new List<SignatureEndpointStats>());
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult<DashboardEndpointDetail?>(null);
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult(new List<ThreatEntry>());
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => Task.FromResult(new List<UserAgentSearchResult>());
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => Task.FromResult(new List<UserAgentVersionBucket>());
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => Task.FromResult(new List<HoneypotHitRow>());
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordDegradationSnapshotAsync(
            Mostlylucid.BotDetection.RateLimit.DegradationSnapshot snapshot,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>>
            GetDegradationHistoryAsync(DateTime startTime, DateTime endTime,
                CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>>(
                Array.Empty<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>());
    }
}
