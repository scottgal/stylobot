using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Domain-scope seam (foss/domain-scope-seam): the dashboard-wide domain selector is
///     supplied by the <see cref="IDashboardDomainScope"/> DI seam. The three list
///     view-components' SELF-FETCH FALLBACK (no composed pageResult on HttpContext.Items,
///     so they read the store directly) must thread the seam's selected domains into the
///     store's <c>domains</c> filter. The FOSS default (<see cref="NullDashboardDomainScope"/>)
///     returns null, so the default path is byte-for-byte the pre-seam behavior (domains == null).
/// </summary>
public sealed class SbListViewComponentDomainScopeTests
{
    private static IOptions<StyloBotDashboardOptions> DefaultOptions() =>
        Options.Create(new StyloBotDashboardOptions { BasePath = "/stylobot" });

    private static void SetHttpContext(ViewComponent vc, HttpContext httpContext)
    {
        var viewContext = new ViewContext { HttpContext = httpContext };
        vc.ViewComponentContext = new ViewComponentContext { ViewContext = viewContext };
    }

    private static DefaultHttpContext ContextWithScope(IDashboardDomainScope? scope)
    {
        var services = new ServiceCollection();
        if (scope is not null)
            services.AddSingleton(scope);
        // vc.View(...) eagerly resolves ICompositeViewEngine from RequestServices once
        // a real provider is assigned; a stub is enough since the result is never rendered.
        services.AddSingleton(new Mock<ICompositeViewEngine>().Object);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    // --- Endpoints VC -------------------------------------------------------

    [Fact]
    public async Task Endpoints_fallback_threads_selected_domains_into_store()
    {
        var store = new RecordingStore();
        var vc = new SbEndpointsListViewComponent(new DashboardAggregateCache(), store, DefaultOptions());
        SetHttpContext(vc, ContextWithScope(new FakeScope(["example.com"])));

        // No composed pageResult, no audience/range -> legacy self-fetch fallback.
        await vc.InvokeAsync();

        Assert.Equal(new[] { "example.com" }, store.LastEndpointDomains);
    }

    [Fact]
    public async Task Endpoints_fallback_with_null_scope_passes_null_domains()
    {
        var store = new RecordingStore();
        var vc = new SbEndpointsListViewComponent(new DashboardAggregateCache(), store, DefaultOptions());
        // NullDashboardDomainScope is the FOSS default -> returns null.
        SetHttpContext(vc, ContextWithScope(new NullDashboardDomainScope()));

        await vc.InvokeAsync();

        Assert.Null(store.LastEndpointDomains);
    }

    [Fact]
    public async Task Endpoints_fallback_with_no_scope_registered_passes_null_domains()
    {
        var store = new RecordingStore();
        var vc = new SbEndpointsListViewComponent(new DashboardAggregateCache(), store, DefaultOptions());
        SetHttpContext(vc, ContextWithScope(scope: null));

        await vc.InvokeAsync();

        Assert.Null(store.LastEndpointDomains);
    }

    // --- Countries VC -------------------------------------------------------

    [Fact]
    public async Task Countries_fallback_threads_selected_domains_into_store()
    {
        var store = new RecordingStore();
        var vc = new SbCountriesListViewComponent(new DashboardAggregateCache(), store, DefaultOptions());
        SetHttpContext(vc, ContextWithScope(new FakeScope(["example.com"])));

        await vc.InvokeAsync();

        Assert.Equal(new[] { "example.com" }, store.LastCountryDomains);
    }

    [Fact]
    public async Task Countries_fallback_with_null_scope_passes_null_domains()
    {
        var store = new RecordingStore();
        var vc = new SbCountriesListViewComponent(new DashboardAggregateCache(), store, DefaultOptions());
        SetHttpContext(vc, ContextWithScope(new NullDashboardDomainScope()));

        await vc.InvokeAsync();

        Assert.Null(store.LastCountryDomains);
    }

    // --- Visitors VC --------------------------------------------------------

    [Fact]
    public async Task Visitors_fallback_threads_selected_domains_into_store()
    {
        var store = new RecordingStore();
        var vc = new SbVisitorListViewComponent(store, DefaultOptions());
        SetHttpContext(vc, ContextWithScope(new FakeScope(["example.com"])));

        // No composed BotAggregate -> self-fetch GetTopBotsAsync fallback.
        await vc.InvokeAsync();

        Assert.Equal(new[] { "example.com" }, store.LastTopBotsDomains);
        // Segment counts read is scoped identically so counts match the rows.
        Assert.Equal(new[] { "example.com" }, store.LastSegmentDomains);
    }

    [Fact]
    public async Task Visitors_fallback_with_null_scope_passes_null_domains()
    {
        var store = new RecordingStore();
        var vc = new SbVisitorListViewComponent(store, DefaultOptions());
        SetHttpContext(vc, ContextWithScope(new NullDashboardDomainScope()));

        await vc.InvokeAsync();

        Assert.Null(store.LastTopBotsDomains);
        Assert.Null(store.LastSegmentDomains);
    }

    private sealed class FakeScope(IReadOnlyList<string> domains) : IDashboardDomainScope
    {
        public IReadOnlyList<string>? GetSelectedDomains(HttpContext context) => domains;
    }

    /// <summary>Captures the <c>domains</c> argument each list-store method receives.</summary>
    private sealed class RecordingStore : IDashboardEventStore
    {
        public IReadOnlyList<string>? LastEndpointDomains { get; private set; }
        public IReadOnlyList<string>? LastCountryDomains { get; private set; }
        public IReadOnlyList<string>? LastTopBotsDomains { get; private set; }
        public IReadOnlyList<string>? LastSegmentDomains { get; private set; }

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            LastEndpointDomains = domains;
            return Task.FromResult(new List<DashboardEndpointStats>());
        }

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            LastCountryDomains = domains;
            return Task.FromResult(new List<DashboardCountryStats>());
        }

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            LastTopBotsDomains = domains;
            return Task.FromResult(new List<DashboardTopBotEntry>());
        }

        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null)
        {
            LastSegmentDomains = domains;
            return Task.FromResult(new FilterCounts());
        }

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());
    }
}
