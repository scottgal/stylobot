using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Real-render coverage for the <c>SbTrendCard</c> view component. Mirrors
///     the <c>SbStatTileTests</c> harness: a TestServer-hosted controller
///     invokes the view component via <c>ViewComponent</c> result so the full
///     Razor pipeline runs against the real Default.cshtml. The embedded
///     SbSparkline + SbHealthDot are rendered through <c>Component.InvokeAsync</c>,
///     so composition is verified by asserting on their rendered markers without
///     re-implementing their geometry / colour logic here.
/// </summary>
public sealed class SbTrendCardTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task Renders_title_and_headline_value()
    {
        var html = await RenderAsync(new TrendCardViewModel("Observations", "1,234"));
        Assert.Contains("data-trend-card", html);
        Assert.Contains("Observations", html);
        // Razor HTML-encodes the comma-separated value into a span; assert on
        // the bracketed value so we catch literal placement, not class names.
        Assert.Contains(">1,234<", html);
    }

    [Fact]
    public async Task Healthband_variant_applies_card_class_and_health_dot()
    {
        var html = await RenderAsync(new TrendCardViewModel(
            "Observations", "1,234", HealthBand: HealthBand.Warning));
        Assert.Contains("sb-trend-card--caution", html);  // Warning aliases Caution
        // SbHealthDot is composed in only when band != Neutral.
        Assert.Contains("sb-health-dot", html);
    }

    [Fact]
    public async Task Subtitle_unit_delta_and_drill_link_render_when_set()
    {
        var html = await RenderAsync(new TrendCardViewModel(
            "Observations",
            "1,234",
            Subtitle: "stylobot_aspnet observations / 1m",
            Unit: "/min",
            Delta: "+12 vs 5m",
            DrillHref: "/dashboard/insights/aspnet",
            DrillLabel: "Open insights"));
        Assert.Contains("stylobot_aspnet observations / 1m", html);
        Assert.Contains(">/min<", html);
        // Razor encodes '+' as &#x2B; in the delta; decode before asserting so the
        // intent ("did the delta text round-trip") is clear.
        Assert.Contains("+12 vs 5m", WebUtility.HtmlDecode(html));
        Assert.Contains("href=\"/dashboard/insights/aspnet\"", html);
        Assert.Contains("Open insights", html);
    }

    [Fact]
    public async Task No_footer_when_drill_href_missing()
    {
        var html = await RenderAsync(new TrendCardViewModel("Observations", "1,234"));
        Assert.DoesNotContain("sb-trend-card__footer", html);
        Assert.DoesNotContain("sb-trend-card__drill", html);
    }

    [Fact]
    public async Task Trend_sparkline_section_renders_when_supplied()
    {
        var trend = new SparklineViewModel(new[] { 0.0, 5.0, 10.0 });
        var html = await RenderAsync(new TrendCardViewModel(
            "Observations", "1,234", TrendSparkline: trend));
        Assert.Contains("sb-trend-card__trend", html);
        Assert.Contains("<polyline", html);    // SbSparkline rendered through composition
    }

    [Fact]
    public async Task Axis_labels_render_under_trend_sparkline()
    {
        var trend = new SparklineViewModel(new[] { 0.0, 5.0, 10.0 });
        var html = await RenderAsync(new TrendCardViewModel(
            "Observations",
            "1,234",
            TrendSparkline: trend,
            AxisLabels: new[] { "-30m", "-15m", "now" }));
        Assert.Contains("sb-trend-card__axis", html);
        // HTML-encoding turns '-' into itself; minus signs come through verbatim.
        Assert.Contains("-30m", html);
        Assert.Contains("-15m", html);
        Assert.Contains("now", html);
    }

    [Fact]
    public async Task Headline_sparkline_renders_inline_when_supplied()
    {
        var inline = new SparklineViewModel(new[] { 1.0, 2.0, 3.0 });
        var html = await RenderAsync(new TrendCardViewModel(
            "Observations", "1,234", HeadlineSparkline: inline));
        Assert.Contains("sb-trend-card__spark-inline", html);
        Assert.Contains("<polyline", html);
    }

    // -------- Render helper --------

    private async Task<string> RenderAsync(TrendCardViewModel model)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbTrendCardTests).Assembly)
            .AddApplicationPart(typeof(SbTrendCardViewComponent).Assembly);

        var registry = new ModelRegistry();
        var id = registry.Add(model);
        builder.Services.AddSingleton(registry);

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);

        var client = app.GetTestClient();
        var resp = await client.GetAsync($"/_test/trendcard?id={id}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            try { await app.DisposeAsync(); } catch { /* test cleanup */ }
        _apps.Clear();
    }

    public sealed class ModelRegistry
    {
        private readonly Dictionary<Guid, TrendCardViewModel> _store = new();
        public Guid Add(TrendCardViewModel model)
        {
            var id = Guid.NewGuid();
            _store[id] = model;
            return id;
        }
        public TrendCardViewModel Get(Guid id) => _store[id];
    }
}

[Route("/_test/trendcard")]
public sealed class SbTrendCardTestController : Controller
{
    private readonly SbTrendCardTests.ModelRegistry _registry;
    public SbTrendCardTestController(SbTrendCardTests.ModelRegistry registry) => _registry = registry;

    [HttpGet]
    public IActionResult Get(Guid id) => ViewComponent("SbTrendCard", new { model = _registry.Get(id) });
}
