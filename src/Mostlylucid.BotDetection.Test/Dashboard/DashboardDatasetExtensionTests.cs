using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Phase 2 seam (IDashboardDatasetExtension): pack/commercial widgets join the
///     tick-warmed bundle instead of self-fetching. Asserts the composer resolves an
///     extension-kind widget into the page result, the catalog maps the key, and a
///     throwing extension fails closed (null slice, no page break).
/// </summary>
public sealed class DashboardDatasetExtensionTests
{
    private const string Kind = "compliance-status";

    [DashboardWidget("compliance-card", Kind)]
    private sealed class ComplianceWidget { }

    private sealed record ComplianceModel(int Score);

    private static DashboardPageWindow Window() => new(null, null, "all", null, null, 500, 60);

    private static DashboardPageManifest Manifest() =>
        new("dashboard.traffic", new[] { "compliance-card" });

    [Fact]
    public void Catalog_maps_the_extension_kind()
    {
        var catalog = DashboardWidgetCatalog.BuildFrom(new[] { typeof(ComplianceWidget) });
        Assert.Equal(Kind, catalog.ExtensionKindFor("compliance-card"));
        Assert.Null(catalog.NeedsFor("compliance-card")); // not a FOSS DatasetKind
    }

    [Fact]
    public async Task Composer_resolves_the_extension_into_the_page_result()
    {
        var catalog = DashboardWidgetCatalog.BuildFrom(new[] { typeof(ComplianceWidget) });
        var ext = new FakeExtension(Kind, _ => new ComplianceModel(97));
        var composer = new DefaultDashboardPageComposer(catalog, new ThrowingStore(), new[] { ext });

        var page = await composer.ComposeAsync(Manifest(), Window(), default);

        var model = page.GetExtension<ComplianceModel>(Kind);
        Assert.NotNull(model);
        Assert.Equal(97, model!.Score);
    }

    [Fact]
    public async Task Throwing_extension_fails_closed_to_a_null_slice()
    {
        var catalog = DashboardWidgetCatalog.BuildFrom(new[] { typeof(ComplianceWidget) });
        var ext = new FakeExtension(Kind, _ => throw new InvalidOperationException("boom"));
        var composer = new DefaultDashboardPageComposer(catalog, new ThrowingStore(), new[] { ext });

        var page = await composer.ComposeAsync(Manifest(), Window(), default);

        Assert.Null(page.GetExtension<ComplianceModel>(Kind)); // no page break, just null
    }

    [Fact]
    public async Task No_extensions_registered_leaves_the_slice_null()
    {
        var catalog = DashboardWidgetCatalog.BuildFrom(new[] { typeof(ComplianceWidget) });
        var composer = new DefaultDashboardPageComposer(catalog, new ThrowingStore()); // no extensions

        var page = await composer.ComposeAsync(Manifest(), Window(), default);

        Assert.Null(page.GetExtension<ComplianceModel>(Kind));
    }

    private sealed class FakeExtension(string kind, Func<DashboardDatasetContext, object?> resolve)
        : IDashboardDatasetExtension
    {
        public string DatasetKind => kind;
        public Task<object?> ResolveAsync(DashboardDatasetContext context, CancellationToken ct)
            => Task.FromResult(resolve(context));
    }

    // Store whose ComposeBatchAsync is the interface default (FanOut). The manifest has no
    // FOSS DatasetKind, so datasets is empty and no Get* method is reached — all throw.
    private sealed class ThrowingStore : IDashboardEventStore
    {
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
    }
}
