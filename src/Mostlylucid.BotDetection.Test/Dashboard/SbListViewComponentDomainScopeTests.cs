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

    // --- Top Bots VC (the uncovered 4th list widget) -----------------------

    [Fact]
    public async Task TopBots_fallback_threads_selected_domains_into_store()
    {
        var store = new RecordingStore();
        var vc = new SbTopBotsViewComponent(store, DefaultOptions());
        SetHttpContext(vc, ContextWithScope(new FakeScope(["example.com"])));

        // No composed BotAggregate on HttpContext.Items -> self-fetch GetTopBotsAsync fallback.
        await vc.InvokeAsync();

        Assert.Equal(new[] { "example.com" }, store.LastTopBotsDomains);
    }

    [Fact]
    public async Task TopBots_fallback_with_null_scope_passes_null_domains()
    {
        var store = new RecordingStore();
        var vc = new SbTopBotsViewComponent(store, DefaultOptions());
        SetHttpContext(vc, ContextWithScope(new NullDashboardDomainScope()));

        await vc.InvokeAsync();

        Assert.Null(store.LastTopBotsDomains);
    }

    /// <summary>
    ///     The prod invariant: for the same window a domain-scoped render's header counters
    ///     (All / Bots / Humans / Internal) can never EXCEED the all-domain render's -- a
    ///     scope is a subset. Before the fix the scoped self-fetch (a) dropped the domain
    ///     filter (returning ALL-domain rows) and (b) read all-audience while the all-domain
    ///     view read a narrower composed slice, so scoped out-counted unscoped in every bucket.
    /// </summary>
    [Fact]
    public async Task TopBots_scoped_counts_never_exceed_unscoped_counts()
    {
        // Full (all-domain) population: 3 bots + 2 humans + 1 internal.
        var full = new List<DashboardTopBotEntry>
        {
            Bot("b1"), Bot("b2"), Bot("b3"),
            Human("h1"), Human("h2"),
            Internal("i1"),
        };
        // example.com subset: 2 bots + 1 human + 1 internal (a strict subset of `full`).
        var subset = new List<DashboardTopBotEntry>
        {
            Bot("b1"), Bot("b2"),
            Human("h1"),
            Internal("i1"),
        };

        List<DashboardTopBotEntry> Provider(IReadOnlyList<string>? domains) =>
            domains is { Count: > 0 } ? subset : full;

        var unscopedStore = new RecordingStore { TopBotsProvider = Provider };
        var unscopedVc = new SbTopBotsViewComponent(unscopedStore, DefaultOptions());
        SetHttpContext(unscopedVc, ContextWithScope(new NullDashboardDomainScope()));
        var unscoped = await InvokeCounts(unscopedVc);

        var scopedStore = new RecordingStore { TopBotsProvider = Provider };
        var scopedVc = new SbTopBotsViewComponent(scopedStore, DefaultOptions());
        SetHttpContext(scopedVc, ContextWithScope(new FakeScope(["example.com"])));
        var scoped = await InvokeCounts(scopedVc);

        // Scope threaded through to the store read.
        Assert.Equal(new[] { "example.com" }, scopedStore.LastTopBotsDomains);
        Assert.Null(unscopedStore.LastTopBotsDomains);

        // The invariant, per bucket: scoped <= unscoped.
        Assert.True(scoped.All <= unscoped.All, $"All: scoped {scoped.All} > unscoped {unscoped.All}");
        Assert.True(scoped.Bots <= unscoped.Bots, $"Bots: scoped {scoped.Bots} > unscoped {unscoped.Bots}");
        Assert.True(scoped.Humans <= unscoped.Humans, $"Humans: scoped {scoped.Humans} > unscoped {unscoped.Humans}");
        Assert.True(scoped.Internal <= unscoped.Internal, $"Internal: scoped {scoped.Internal} > unscoped {unscoped.Internal}");

        // And they reflect the actual all-audience distribution per side (All = public count,
        // i.e. bots + humans; Internal counted separately). Full = 3 bots + 2 humans + 1
        // internal; example.com subset = 2 bots + 1 human + 1 internal.
        Assert.Equal(5, unscoped.All);
        Assert.Equal(3, unscoped.Bots);
        Assert.Equal(2, unscoped.Humans);
        Assert.Equal(1, unscoped.Internal);
        Assert.Equal(3, scoped.All);
        Assert.Equal(2, scoped.Bots);
        Assert.Equal(1, scoped.Humans);
        Assert.Equal(1, scoped.Internal);
    }

    private static async Task<TopBotsCounts> InvokeCounts(SbTopBotsViewComponent vc)
    {
        var result = await vc.InvokeAsync();
        var model = (TopBotsListModel)((ViewViewComponentResult)result).ViewData.Model!;
        return model.Counts!;
    }

    // Non-groupable rows (null BotName) so CollapseGroupableIdentities preserves each entry
    // instead of merging same-name identities -- keeps the count arithmetic explicit.
    private static DashboardTopBotEntry Bot(string sig) => new()
    {
        PrimarySignature = sig, BotType = "SearchEngine", IsKnownBot = true,
        BotProbability = 0.95, HitCount = 1, LastSeen = DateTime.UtcNow,
    };

    private static DashboardTopBotEntry Human(string sig) => new()
    {
        PrimarySignature = sig, BotType = null, IsKnownBot = false,
        BotProbability = 0.1, HitCount = 1, LastSeen = DateTime.UtcNow,
    };

    private static DashboardTopBotEntry Internal(string sig) => new()
    {
        PrimarySignature = sig, BotType = "Internal", IsKnownBot = false,
        BotProbability = 0.0, HitCount = 1, LastSeen = DateTime.UtcNow,
    };

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

        /// <summary>
        ///     Optional seed for <see cref="GetTopBotsAsync"/>: given the domains argument,
        ///     returns the rows that scope should yield. Null (default) keeps the historical
        ///     empty-list behaviour every other test in this file relies on.
        /// </summary>
        public Func<IReadOnlyList<string>?, List<DashboardTopBotEntry>>? TopBotsProvider { get; init; }

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
            return Task.FromResult(TopBotsProvider?.Invoke(domains) ?? new List<DashboardTopBotEntry>());
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
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());
    }
}
