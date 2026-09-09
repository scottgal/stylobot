using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.Common.Scheduling;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     A dashboard document must contain EXACTLY ONE <c>&lt;main&gt;</c> landmark.
///     <para>
///         Why this exists (overview- 2026-09-09, 8 deterministic commercial reds):
///         five Playwright tests use <c>Locator("main")</c> in strict mode and went
///         ambiguous — the rendered dashboard had two. The inner one is
///         <c>Index.cshtml</c>'s own <c>&lt;main&gt;</c>, nested inside an outer landmark
///         that FOSS itself supplies on the standalone path
///         (<c>WrapStandaloneDashboardPage</c>, StyloBotDashboardMiddleware.cs:1485) and
///         that a host <c>_Layout</c> supplies on the hosted path. Nesting a landmark
///         inside a landmark is invalid HTML, and this codebase already states the rule:
///         <c>_SignatureDetail.cshtml</c> renders a plain <c>&lt;div&gt;</c> because
///         "nesting &lt;main&gt; inside Index.cshtml's own &lt;main&gt; is invalid HTML".
///         The fix is to demote the INNER element, keeping the outer landmark as the one
///         real one.
///     </para>
/// </summary>
public sealed class DashboardSingleMainLandmarkTests : IAsyncDisposable
{
    private static readonly Regex MainTag = new(@"<main\b", RegexOptions.IgnoreCase);

    private WebApplication? _app;

    private sealed class NeverTickingScheduleCoordinator : IScheduleCoordinator
    {
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
        public IDisposable Subscribe(TickCadence cadence, string subscriberName, CostHint costHint,
            Func<DateTimeOffset, CancellationToken, Task> handler) => new NoopDisposable();
        public IReadOnlyList<TickSubscriberMetadata> Snapshot() => Array.Empty<TickSubscriberMetadata>();
    }

    private sealed class EmptyEventStore : IDashboardEventStore
    {
        public Task<DashboardSummary> GetSummaryAsync(DateTime? s = null, DateTime? e = null, string? a = null, IReadOnlyList<string>? d = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow, TotalRequests = 0, BotRequests = 0, HumanRequests = 0,
                UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(), UniqueSignatures = 0,
            });
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int c = 10, DateTime? s = null, DateTime? e = null, string? a = null, IReadOnlyList<string>? d = null)
            => Task.FromResult(new List<DashboardTopBotEntry>());
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int c = 20, DateTime? s = null, DateTime? e = null, string? a = null, IReadOnlyList<string>? d = null)
            => Task.FromResult(new List<DashboardCountryStats>());
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int c = 50, DateTime? s = null, DateTime? e = null, string? a = null, IReadOnlyList<string>? d = null)
            => Task.FromResult(new List<DashboardEndpointStats>());
        public Task<List<ThreatEntry>> GetThreatsAsync(int c = 20, DateTime? s = null, DateTime? e = null, IReadOnlyList<string>? d = null)
            => Task.FromResult(new List<ThreatEntry>());
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? f = null, CancellationToken ct = default)
            => Task.FromResult(new List<DashboardDetectionEvent>());
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime s, DateTime e, TimeSpan b, string? a = null, IReadOnlyList<string>? d = null)
            => Task.FromResult(new List<DashboardTimeSeriesPoint>());
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime s, DateTime e, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());
        public Task AddDetectionAsync(DashboardDetectionEvent d) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent s) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string s, string n, string? d, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int l = 100, int o = 0, bool? b = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string c, DateTime? s = null, DateTime? e = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string s, int t = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string m, string p, DateTime? s = null, DateTime? e = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string q, int l = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string f, int h = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int c = 50, DateTime? s = null, DateTime? e = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter f, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime s, DateTime e, string? f = null, string? c = null, string? bt = null, string? th = null, IReadOnlyList<string>? d = null) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Standalone_dashboard_page_renders_exactly_one_main_landmark()
    {
        var store = new EmptyEventStore();
        var manifests = new DefaultDashboardPageManifestSource();
        var composer = new DefaultDashboardPageComposer(DashboardWidgetCatalog.BuildFromLoadedAssemblies(), store);
        long tick = 1;
        var cache = new DashboardContentCache(
            compose: (m, w, ct) => composer.ComposeAsync(m, w, ct),
            currentTick: () => tick,
            options: Options.Create(new DashboardMaterializerOptions()));

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
        builder.Services.AddStyloBotWidgets();

        _app = builder.Build();
        _app.UseMiddleware<SbWidgetBatchMiddleware>();
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        var html = await _app.GetTestClient().GetStringAsync("/dashboard/traffic");

        Assert.Equal(HttpStatusCode.OK, HttpStatusCode.OK); // request succeeded (GetStringAsync throws otherwise)
        // The standalone shell supplies the one landmark; the dashboard body must not
        // nest a second. Two <main> elements make Locator("main") ambiguous (strict mode).
        Assert.Equal(1, MainTag.Matches(html).Count);
        // And it is the SHELL's landmark, not a leftover inner one.
        Assert.Contains("<main class=\"flex-1 p-4 max-w-7xl w-full mx-auto\">", html);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
