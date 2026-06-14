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
///     Real-render coverage for the <c>SbMeterTable</c> view component. Same
///     TestServer harness as <c>SbStatTileTests</c> / <c>SbTrendCardTests</c>:
///     a stub controller runs the component through the real Razor pipeline
///     and we assert on the produced HTML. Composition with SbSparkline +
///     SbHealthDot is verified by their rendered class markers, not by
///     re-implementing their geometry / colour logic here.
/// </summary>
public sealed class SbMeterTableTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task Empty_rows_render_empty_message_and_no_table()
    {
        var html = await RenderAsync(new MeterTableViewModel(
            "All meters",
            Array.Empty<MeterTableRowViewModel>(),
            EmptyMessage: "Pack is idle"));
        Assert.Contains("Pack is idle", html);
        Assert.DoesNotContain("<table", html);
        Assert.Contains("sb-meter-table__empty", html);
    }

    [Fact]
    public async Task Each_row_renders_with_its_name()
    {
        var rows = new[]
        {
            new MeterTableRowViewModel("stylobot_aspnet_observations_received_total", "Counter", "1234"),
            new MeterTableRowViewModel("stylobot_aspnet_log_records_total",           "Counter", "8765"),
            new MeterTableRowViewModel("stylobot_aspnet_logger_queue_depth",          "Gauge",   "12")
        };
        var html = await RenderAsync(new MeterTableViewModel("Meters", rows));
        Assert.Contains("stylobot_aspnet_observations_received_total", html);
        Assert.Contains("stylobot_aspnet_log_records_total", html);
        Assert.Contains("stylobot_aspnet_logger_queue_depth", html);
    }

    [Fact]
    public async Task Kind_pill_class_matches_kind()
    {
        var rows = new[]
        {
            new MeterTableRowViewModel("counter_meter", "Counter", "1"),
            new MeterTableRowViewModel("gauge_meter", "Gauge", "1"),
            new MeterTableRowViewModel("histogram_meter", "Histogram", "1"),
            new MeterTableRowViewModel("updown_meter", "UpDownCounter", "1")
        };
        var html = await RenderAsync(new MeterTableViewModel("Meters", rows));
        Assert.Contains("sb-meter-table__pill--counter", html);
        Assert.Contains("sb-meter-table__pill--gauge", html);
        Assert.Contains("sb-meter-table__pill--histogram", html);
        Assert.Contains("sb-meter-table__pill--updowncounter", html);
    }

    [Fact]
    public async Task Healthband_applies_row_variant_class()
    {
        var rows = new[]
        {
            new MeterTableRowViewModel(
                "log_drop_total", "Counter", "42",
                HealthBand: HealthBand.Danger)
        };
        var html = await RenderAsync(new MeterTableViewModel("Meters", rows));
        Assert.Contains("sb-meter-table__row--danger", html);
        // SbHealthDot is composed in only when band != Neutral.
        Assert.Contains("sb-health-dot", html);
    }

    [Fact]
    public async Task Sparkline_column_renders_when_supplied()
    {
        var spark = new SparklineViewModel(new[] { 0.0, 5.0, 10.0 });
        var rows = new[]
        {
            new MeterTableRowViewModel(
                "observations_total", "Counter", "1234",
                Sparkline: spark)
        };
        var html = await RenderAsync(new MeterTableViewModel("Meters", rows));
        Assert.Contains("sb-meter-table__spark", html);
        Assert.Contains("<polyline", html);    // SbSparkline rendered through composition
    }

    [Fact]
    public async Task Drill_link_renders_only_when_drill_href_set()
    {
        var rows = new[]
        {
            new MeterTableRowViewModel(
                "with_link", "Counter", "1",
                DrillHref: "/dashboard/insights/x"),
            new MeterTableRowViewModel("without_link", "Counter", "2")
        };
        var html = await RenderAsync(new MeterTableViewModel("Meters", rows));
        // The with-link row has exactly one drill anchor; the without-link row
        // should not emit one. Counting class occurrences keeps the test honest
        // about "only one link rendered".
        var anchorOccurrences = CountOccurrences(html, "class=\"sb-meter-table__drill\"");
        Assert.Equal(1, anchorOccurrences);
        Assert.Contains("href=\"/dashboard/insights/x\"", html);
    }

    [Fact]
    public async Task Description_renders_as_visible_subtitle()
    {
        // Per commit 33aa8337 (YAML meter catalog with human labels +
        // descriptions): the description is now shown as a visible
        // <p class="sb-meter-table__name-desc"> subtitle below the meter
        // name so the operator sees it without hovering. The previous
        // title= attribute would have hidden it behind a hover state.
        var rows = new[]
        {
            new MeterTableRowViewModel(
                "observations_total", "Counter", "1234",
                Description: "my description")
        };
        var html = await RenderAsync(new MeterTableViewModel("Meters", rows));
        Assert.Contains("sb-meter-table__name-desc", html);
        Assert.Contains("my description", html);
    }

    [Fact]
    public async Task Caption_renders_when_supplied()
    {
        var html = await RenderAsync(new MeterTableViewModel(
            "Meters",
            new[] { new MeterTableRowViewModel("any", "Counter", "1") },
            Caption: "Last 5 minutes, top 50 meters by request rate"));
        Assert.Contains("Last 5 minutes, top 50 meters by request rate", html);
        Assert.Contains("sb-meter-table__caption", html);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    // -------- Render helper --------

    private async Task<string> RenderAsync(MeterTableViewModel model)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbMeterTableTests).Assembly)
            .AddApplicationPart(typeof(SbMeterTableViewComponent).Assembly);

        var registry = new ModelRegistry();
        var id = registry.Add(model);
        builder.Services.AddSingleton(registry);

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);

        var client = app.GetTestClient();
        var resp = await client.GetAsync($"/_test/metertable?id={id}");
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
        private readonly Dictionary<Guid, MeterTableViewModel> _store = new();
        public Guid Add(MeterTableViewModel model)
        {
            var id = Guid.NewGuid();
            _store[id] = model;
            return id;
        }
        public MeterTableViewModel Get(Guid id) => _store[id];
    }
}

[Route("/_test/metertable")]
public sealed class SbMeterTableTestController : Controller
{
    private readonly SbMeterTableTests.ModelRegistry _registry;
    public SbMeterTableTestController(SbMeterTableTests.ModelRegistry registry) => _registry = registry;

    [HttpGet]
    public IActionResult Get(Guid id) => ViewComponent("SbMeterTable", new { model = _registry.Get(id) });
}
