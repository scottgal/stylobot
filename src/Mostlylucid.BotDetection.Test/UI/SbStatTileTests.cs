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
///     Real-render coverage for the <c>SbStatTile</c> view component. Same
///     TestServer harness as <c>SbSparklineTests</c> -- a stub controller runs
///     the component through the real Razor pipeline and we assert on the
///     produced HTML. The embedded sparkline is rendered through
///     <c>Component.InvokeAsync</c>, so the &lt;polyline&gt; assertion verifies
///     composition without re-implementing the SVG path math.
/// </summary>
public sealed class SbStatTileTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task Renders_minimum_shape_with_title_and_value()
    {
        var html = await RenderAsync(new StatTileViewModel("Endpoints", "38"));
        Assert.Contains("data-stat-tile", html);
        Assert.Contains("Endpoints", html);
        Assert.Contains(">38<", html);
    }

    [Fact]
    public async Task Sparkline_embeds_when_supplied()
    {
        var spark = new SparklineViewModel(new[] { 1.0, 2.0, 3.0 });
        var html = await RenderAsync(new StatTileViewModel("Endpoints", "38", Sparkline: spark));
        Assert.Contains("sb-sparkline", html);
        Assert.Contains("<polyline", html);
    }

    [Fact]
    public async Task Drill_link_renders_when_href_present()
    {
        var html = await RenderAsync(new StatTileViewModel(
            "Endpoints",
            "38",
            DrillHref: "/dashboard/aspnet-pack/routes",
            DrillLabel: "View routes →"));
        Assert.Contains("href=\"/dashboard/aspnet-pack/routes\"", html);
        // Razor HTML-encodes the arrow; decode before asserting so the test reads
        // as a human-meaningful round-trip rather than a contest with the encoder.
        Assert.Contains("View routes →", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task No_drill_link_when_href_missing()
    {
        var html = await RenderAsync(new StatTileViewModel("Endpoints", "38"));
        Assert.DoesNotContain("sb-stat-tile-drill", html);
    }

    [Fact]
    public async Task Health_band_drives_root_class_and_data_attribute()
    {
        var html = await RenderAsync(new StatTileViewModel("Endpoints", "38", HealthBand: HealthBand.Danger));
        Assert.Contains("sb-stat-tile-danger", html);
        Assert.Contains("data-health=\"Danger\"", html);
    }

    [Fact]
    public async Task Delta_renders_when_supplied()
    {
        var html = await RenderAsync(new StatTileViewModel("Endpoints", "38", Delta: "+12 vs 24h ago"));
        // Razor HTML-encodes '+' as &#x2B;. Decode the response so the assertion
        // reads as a human-meaningful round-trip.
        Assert.Contains("+12 vs 24h ago", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task Icon_class_renders_when_supplied()
    {
        var html = await RenderAsync(new StatTileViewModel("Endpoints", "38", Icon: "icon-route"));
        Assert.Contains("class=\"icon-route\"", html);
    }

    [Fact]
    public async Task Unit_renders_when_supplied()
    {
        var html = await RenderAsync(new StatTileViewModel("Latency", "2.1", Unit: "ms"));
        Assert.Contains(">ms<", html);
    }

    // -------- Render helper --------

    private async Task<string> RenderAsync(StatTileViewModel model)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbStatTileTests).Assembly)
            .AddApplicationPart(typeof(SbStatTileViewComponent).Assembly);

        var registry = new ModelRegistry();
        var id = registry.Add(model);
        builder.Services.AddSingleton(registry);

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);

        var client = app.GetTestClient();
        var resp = await client.GetAsync($"/_test/stattile?id={id}");
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
        private readonly Dictionary<Guid, StatTileViewModel> _store = new();
        public Guid Add(StatTileViewModel model)
        {
            var id = Guid.NewGuid();
            _store[id] = model;
            return id;
        }
        public StatTileViewModel Get(Guid id) => _store[id];
    }
}

[Route("/_test/stattile")]
public sealed class SbStatTileTestController : Controller
{
    private readonly SbStatTileTests.ModelRegistry _registry;
    public SbStatTileTestController(SbStatTileTests.ModelRegistry registry) => _registry = registry;

    [HttpGet]
    public IActionResult Get(Guid id) => ViewComponent("SbStatTile", new { model = _registry.Get(id) });
}
