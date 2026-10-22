using System.Net;
using Microsoft.AspNetCore.Builder;
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
///     Rendered-DOM contract for the lean endpoint table (the Traffic landing
///     page's primary surface). These hit the real
///     <c>/dashboard/partials/endpoints</c> route through a TestServer, so they
///     assert what the browser receives rather than re-reading the Razor source.
///     <para>
///         Pinned behaviour: five columns, a binary bot/not-bot split rendered as
///         "Human / unknown" (never the signature count), canonical endpoint links
///         (including the root path's escaped %2F segment), scope preservation on
///         sort/page links, the full-columns opt-in, and the honesty flag for
///         lifetime-only endpoint stores.
///     </para>
/// </summary>
public sealed class LeanEndpointsRenderedTests : IAsyncDisposable
{
    private WebApplication? _app;

    private async Task<WebApplication> BuildAppAsync(bool windowedEndpoints = false, bool supportsInternalEndpoints = true, bool emptyEndpoints = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(new SeededEndpointStore(windowedEndpoints, supportsInternalEndpoints, emptyEndpoints));
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = true;
        });

        var app = builder.Build();
        app.UseMiddleware<StyloBotDashboardMiddleware>();
        await app.StartAsync();
        _app = app;
        return app;
    }

    private static string RowFor(string html, string path)
    {
        var at = html.IndexOf(path, StringComparison.Ordinal);
        Assert.True(at > 0, $"row {path} was not rendered");
        var end = html.IndexOf("</tr>", at, StringComparison.Ordinal);
        Assert.True(end > at);
        return html[at..end];
    }

    [Fact]
    public async Task Renders_five_columns_and_the_binary_split_as_human_unknown()
    {
        var app = await BuildAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/dashboard/partials/endpoints");

        Assert.Equal(5, Count(html, "<th scope=\"col\""));

        var root = RowFor(html, "sb-ep-path\">/</span>");
        // Seed: 100 requests, 40 bots => 60 not-bot. 7 signatures must NOT be used.
        Assert.Contains(">100<", root);
        Assert.Contains("sb-ep-human\">60<", root);
        Assert.Contains(">40<", root);
        Assert.DoesNotContain("sb-ep-human\">7<", root);
        Assert.DoesNotContain(">Human<", html);

        // The filter bar and the per-row share bar must stay distinct classes: a
        // collision collapsed the filter bar to a 3.5rem pill and pushed the search
        // input over the table on the real host.
        Assert.Contains("class=\"sb-ep-bar\"", html);
        Assert.Contains("class=\"sb-ep-sharebar\"", html);
        Assert.DoesNotContain("class=\"sb-ep-bar\" aria-hidden", html);
    }

    [Fact]
    public async Task Root_endpoint_link_uses_the_query_route_that_actually_resolves()
    {
        var app = await BuildAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/dashboard/partials/endpoints");

        // Root cannot be a path segment (the page route keeps the remainder verbatim and
        // never unescapes %2F), so the root row links through the query-string route.
        // Assert the generated href as parsed HTML, not as an encoding-specific literal.
        var rootHref = RootHref(html);
        Assert.NotNull(rootHref);
        Assert.Contains("method=GET", rootHref);
        Assert.Contains("path=%2F", rootHref);
        Assert.DoesNotContain("/dashboard/endpoint/GET/\"", html);
        // Non-root rows keep the segment route. The href is on the anchor BEFORE the
        // path span, so assert on the document (RowFor starts at the span).
        Assert.Contains("/dashboard/endpoint/GET/pricing", html);
    }

    [Fact]
    public async Task Generated_root_link_resolves_to_the_endpoint_detail_with_data()
    {
        var app = await BuildAppAsync();
        var client = app.GetTestClient();
        var html = await client.GetStringAsync("/dashboard/partials/endpoints");

        // Take the href the root row actually renders, then follow it.
        var href = RootHref(html);
        Assert.True(href is not null, "the root row must render a real detail link; rendered partial: " + Truncate(html));
        var response = await client.GetAsync(href);
        var detailHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("GET", detailHtml);
        Assert.DoesNotContain("Endpoint detail not found", detailHtml);
    }

    [Fact]
    public async Task Sort_and_page_links_preserve_explicit_window_and_domain_scope()
    {
        var app = await BuildAppAsync();
        var html = await app.GetTestClient()
            .GetStringAsync("/dashboard/partials/endpoints?window=7d&domain=example.com");

        Assert.Contains("window=7d", html);
        Assert.Contains("domain=example.com", html);
        Assert.Contains("columns=lean", html);

        // The interaction is a real button carrying hx-get, so it is keyboard
        // reachable; the header also announces its sort direction.
        Assert.Contains("class=\"sb-ep-sort\"", html);
        Assert.Contains("aria-sort=", html);
    }

    [Fact]
    public async Task Full_column_inventory_remains_available_as_an_opt_in()
    {
        var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var lean = await client.GetStringAsync("/dashboard/partials/endpoints");
        Assert.DoesNotContain("Latency", lean);
        Assert.Contains("sb-ep-toggle", lean);

        var full = await client.GetStringAsync("/dashboard/partials/endpoints?columns=full");
        Assert.Contains("Latency", full);
        Assert.Contains("Threat", full);
        Assert.DoesNotContain("sb-ep-toggle", full);
    }

    [Fact]
    public async Task Internal_window_read_is_retained_and_survives_sort()
    {
        // Lifetime-only store + internal audience + an explicit range: the rows are a
        // RETAINED window, not lifetime. Sorting must carry the same actual inputs
        // (audience/window) so the next swap derives the same truth.
        var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var html = await client.GetStringAsync("/dashboard/partials/endpoints?audience=all_incl_internal&window=7d");
        Assert.True(html.Contains("internal, retained window"),
            "expected retained-window label; rendered: " + Truncate(html));
        Assert.DoesNotContain("lifetime totals", html);
        Assert.Contains("audience=all_incl_internal", html);
        Assert.Contains("window=7d", html);

        var sorted = await client.GetStringAsync(
            "/dashboard/partials/endpoints?audience=all_incl_internal&window=7d&sort=path&dir=asc");
        Assert.Contains("internal, retained window", sorted);
        Assert.DoesNotContain("lifetime totals", sorted);
    }

    [Fact]
    public async Task Page_level_internal_toggle_keeps_its_scope_on_table_links()
    {
        // The page-level toggle is ?internal=show WITHOUT an audience parameter. The read
        // must resolve all_incl_internal, the label must be retained-window, and the
        // generated sort/page links must keep internal=show (or the next swap reads the
        // default audience and relabels).
        var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var html = await client.GetStringAsync("/dashboard/partials/endpoints?internal=show&window=7d");
        Assert.True(html.Contains("internal, retained window"),
            "expected retained-window label for internal=show; rendered: " + Truncate(html));
        Assert.DoesNotContain("lifetime totals", html);
        Assert.Contains("internal=show", html);
        Assert.Contains("window=7d", html);

        // Follow the generated sort URL, not a hand-built one.
        var sortUrl = ExtractFirst(html, "hx-get=\"([^\"]*sort=path[^\"]*)\"");
        Assert.True(sortUrl is not null, "expected a generated sort link; rendered: " + Truncate(html));
        var sorted = await client.GetStringAsync(sortUrl);
        Assert.Contains("internal, retained window", sorted);
        Assert.Contains("internal=show", sorted);
        Assert.Contains("window=7d", sorted);

        // Detail navigation must keep the same actual scope.
        var detailUrl = RootHref(html);
        Assert.True(detailUrl is not null, "expected the endpoint detail link");
        Assert.Contains("internal=show", detailUrl);
        Assert.Contains("window=7d", detailUrl);
        var detail = await client.GetAsync(detailUrl);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task Internal_request_on_a_host_without_internal_endpoint_data_says_external_only()
    {
        // PG after option A: the rollup no longer carries Internal, so an internal request
        // returns external rows. The table must say so, not imply internal/retained.
        var app = await BuildAppAsync(windowedEndpoints: true, supportsInternalEndpoints: false);
        var client = app.GetTestClient();

        var html = await client.GetStringAsync("/dashboard/partials/endpoints?internal=show&window=7d");

        Assert.Contains("external only", html);
        Assert.Contains("Internal self-probe is not available at endpoint level", html);
        Assert.DoesNotContain("internal, retained window", html);
        Assert.DoesNotContain("lifetime totals", html);
    }

    [Fact]
    public async Task External_only_without_a_window_does_not_claim_a_range()
    {
        // No ?window= at all: the label must not say "in this range".
        var app = await BuildAppAsync(windowedEndpoints: true, supportsInternalEndpoints: false);
        var html = await app.GetTestClient().GetStringAsync("/dashboard/partials/endpoints?internal=show");

        Assert.Contains("external traffic only", html);
        Assert.Contains("Internal self-probe is not available at endpoint level", html);
        Assert.DoesNotContain("in this range (external only)", html);
    }

    [Fact]
    public async Task Empty_external_only_state_does_not_claim_a_range()
    {
        var app = await BuildAppAsync(windowedEndpoints: true, supportsInternalEndpoints: false, emptyEndpoints: true);
        var html = await app.GetTestClient().GetStringAsync("/dashboard/partials/endpoints?internal=show");

        Assert.Contains("No endpoint breakdown recorded (external traffic only).", html);
        Assert.Contains("external traffic only", html);
        Assert.DoesNotContain("No endpoint breakdown recorded in this range", html);
    }

    [Fact]
    public async Task Full_view_shows_the_external_only_badge_for_an_unsupported_internal_read()
    {
        var app = await BuildAppAsync(windowedEndpoints: true, supportsInternalEndpoints: false);
        var html = await app.GetTestClient()
            .GetStringAsync("/dashboard/partials/endpoints?columns=full&internal=show&window=7d");

        Assert.Contains("external only", html);
        Assert.DoesNotContain("internal, retained window", html);
        Assert.DoesNotContain("lifetime totals", html);
    }

    [Fact]
    public async Task Presentation_tokens_cannot_forge_the_scope_label()
    {
        // A lifetime-only store with no internal audience: the producer says Lifetime.
        // A forged ?retained=1 / ?lifetime=1 must NOT change what the rows are called.
        var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var forgedRetained = await client.GetStringAsync("/dashboard/partials/endpoints?retained=1");
        Assert.Contains("lifetime totals", forgedRetained);
        Assert.DoesNotContain("internal, retained window", forgedRetained);

        var forgedWindowed = await client.GetStringAsync("/dashboard/partials/endpoints?lifetime=1");
        Assert.Contains("lifetime totals", forgedWindowed);
    }

    [Fact]
    public async Task Lifetime_scope_token_labels_the_table_and_defaults_to_range()
    {
        // The first paint sets this from IDashboardEventStore.SupportsWindowedEndpointStats
        // in the view component; a partial swap carries the same decision as ?lifetime=1.
        var lifetime = await BuildAppAsync(windowedEndpoints: false);
        var lifetimeHtml = await lifetime.GetTestClient().GetStringAsync("/dashboard/partials/endpoints?lifetime=1");
        Assert.Contains("lifetime totals", lifetimeHtml);

        await DisposeAsync();

        var windowed = await BuildAppAsync(windowedEndpoints: true);
        var windowedHtml = await windowed.GetTestClient().GetStringAsync("/dashboard/partials/endpoints");
        Assert.Contains("in this range", windowedHtml);
        Assert.DoesNotContain("lifetime totals", windowedHtml);
    }

    [Fact]
    public async Task Rows_expose_a_disclosure_and_a_real_detail_link()
    {
        var app = await BuildAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/dashboard/partials/endpoints");

        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(html, "data-sb-ep-toggle=\"sb-ep-d-\\d+\""),
            "disclosure toggle missing; rendered partial: " + Truncate(html));
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Contains("aria-controls=", html);
        Assert.Contains("<tr class=\"sb-ep-detail\"", html);
        Assert.Contains("Open full endpoint detail", html);

        // Observed upstream status, not an assumption: this row has 80 of 100
        // requests observed at the app and 20 with no origin status on record.
        // The disclosure body is a sibling detail row, so assert on the document.
        Assert.Contains("80 of 100 requests", html);
        Assert.Contains("20 have no origin status", html);
        Assert.DoesNotContain("answered by StyloBot", html);
    }

    private static string? ExtractFirst(string html, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, pattern);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    /// <summary>Root row href as the browser would see it (HTML-decoded).</summary>
    private static string? RootHref(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "<a class=\"sb-ep-open\" href=\"([^\"]*endpoint\\?method=GET[^\"]*)\"");
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string Truncate(string html) =>
        html.Length <= 3000 ? html : html[..3000] + "…";

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    private sealed class SeededEndpointStore(bool windowedEndpoints, bool supportsInternalEndpoints, bool emptyEndpoints) : IDashboardEventStore
    {
        public bool SupportsWindowedEndpointStats => windowedEndpoints;
        public bool SupportsInternalEndpointStats => supportsInternalEndpoints;

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            if (emptyEndpoints) return Task.FromResult(new List<DashboardEndpointStats>());
            var rows = new List<DashboardEndpointStats>
            {
                new()
                {
                    Method = "GET", Path = "/", TotalCount = 100, BotCount = 40,
                    UniqueSignatures = 7,
                    UpstreamStatus2xx = 80, UpstreamNoneCount = 5,
                    Status2xx = 80, Status4xx = 15, Status5xx = 5
                },
                new() { Method = "GET", Path = "/pricing", TotalCount = 25, BotCount = 0, UpstreamStatus2xx = 25, Status2xx = 25 },
                new() { Method = "POST", Path = "/api/orders", TotalCount = 40, BotCount = 40, UpstreamStatus2xx = 30, Status2xx = 30 },
            };
            return Task.FromResult(rows);
        }

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null)
            => Task.FromResult<DashboardEndpointDetail?>(new DashboardEndpointDetail
            {
                Method = method,
                Path = path,
                TotalCount = 100,
                BotCount = 40,
                BotRate = 0.4,
                TopActions = new Dictionary<string, int>(),
                TopCountries = new Dictionary<string, int>(),
                RiskBands = new Dictionary<string, int>(),
                TopBots = new List<DashboardTopBotEntry>(),
                RecentDetections = new List<SignatureDetectionRow>(),
            });
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
    }
}
