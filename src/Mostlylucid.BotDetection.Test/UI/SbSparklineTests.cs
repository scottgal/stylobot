using System.Globalization;
using System.Text.RegularExpressions;
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
///     Real-render coverage for the <c>SbSparkline</c> view component. Mirrors the
///     <c>SbPolicyStackTests</c> harness: a TestServer-hosted controller invokes the
///     view component via <c>ViewComponent</c> result, so the full Razor pipeline
///     (locator + partial) runs against the real Default.cshtml.
/// </summary>
public sealed class SbSparklineTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task Empty_values_renders_empty_svg()
    {
        var html = await RenderAsync(new SparklineViewModel(Array.Empty<double>()));
        Assert.Contains("<svg", html);
        Assert.DoesNotContain("<polyline", html);
        Assert.Contains("viewBox=\"0 0 60 16\"", html);
    }

    [Fact]
    public async Task Single_value_renders_horizontal_line_centered()
    {
        var html = await RenderAsync(new SparklineViewModel(new[] { 4.2 }, Width: 60, Height: 16));
        Assert.Contains("<polyline", html);
        Assert.Contains("0,8 60,8", html);    // y = h/2 = 8
    }

    [Fact]
    public async Task Many_values_scales_x_across_width()
    {
        var html = await RenderAsync(new SparklineViewModel(new[] { 0.0, 5.0, 10.0 }, Width: 60, Height: 16));
        Assert.Contains("<polyline", html);
        Assert.Contains("0,16", html);      // first point at min (bottom)
        Assert.Contains("60,0", html);      // last point at max (top)
    }

    [Fact]
    public async Task Flat_series_renders_horizontal_midline()
    {
        var html = await RenderAsync(new SparklineViewModel(new[] { 3.0, 3.0, 3.0, 3.0 }));
        Assert.Matches(new Regex("<polyline points=\"[^\"]*\""), html);
        Assert.Contains(",8", html);        // all y = h/2 = 8 (range == 0 branch)
    }

    [Fact]
    public async Task Custom_color_class_appears_on_svg()
    {
        var html = await RenderAsync(new SparklineViewModel(new[] { 1.0, 2.0 }, ColorClass: "spark-danger"));
        Assert.Contains("class=\"sb-sparkline spark-danger\"", html);
    }

    [Fact]
    public async Task Custom_dimensions_propagate_to_viewBox()
    {
        var html = await RenderAsync(new SparklineViewModel(new[] { 1.0, 2.0 }, Width: 120, Height: 24));
        Assert.Contains("viewBox=\"0 0 120 24\"", html);
        Assert.Contains("width=\"120\"", html);
        Assert.Contains("height=\"24\"", html);
    }

    [Fact]
    public async Task Render_uses_invariant_culture_decimal_separator()
    {
        // Set a culture that formats decimals with a comma. If the partial ever
        // forgets InvariantCulture, the comma will collide with the comma that
        // separates point pairs inside `points="..."` and make the SVG invalid.
        var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var html = await RenderAsync(new SparklineViewModel(new[] { 0.0, 1.5, 3.0 }, Width: 60, Height: 16));

            // Pull the points="..." attribute out so we can sanity-check just the
            // coordinate string. Pairs are "x,y x,y x,y" — exactly one comma per pair,
            // so a well-formed series of 3 points has exactly 3 commas in total.
            var match = Regex.Match(html, "points=\"(?<p>[^\"]+)\"");
            Assert.True(match.Success, "expected a polyline points attribute");
            var pointsAttr = match.Groups["p"].Value;
            Assert.Equal(3, pointsAttr.Count(c => c == ','));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = prev;
        }
    }

    // -------- Render helper --------

    /// <summary>
    ///     Spin up a TestServer with a controller-action shim that invokes the
    ///     view component. Identical pattern to <c>SbPolicyStackTests</c> — going
    ///     through a real controller exercises the actual view engine (locator,
    ///     partial pickup, model binding) without the dashboard middleware.
    /// </summary>
    private async Task<string> RenderAsync(SparklineViewModel model)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // The controller lives in this assembly; the view component + partial in
        // the UI assembly. Both ApplicationParts must be registered explicitly —
        // default scanning misses them inside a TestServer setup.
        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbSparklineTests).Assembly)
            .AddApplicationPart(typeof(SbSparklineViewComponent).Assembly);

        // Stash the model in a shared singleton so the controller can fish it
        // back out by id. The view component takes a record so we can't shove it
        // through query string cleanly.
        var registry = new ModelRegistry();
        var id = registry.Add(model);
        builder.Services.AddSingleton(registry);

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);

        var client = app.GetTestClient();
        var resp = await client.GetAsync($"/_test/sparkline?id={id}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            try { await app.DisposeAsync(); } catch { /* test cleanup */ }
        _apps.Clear();
    }

    /// <summary>
    ///     In-process pass-through for the per-test view model. The controller
    ///     can't bind a record from query string, so the test pre-registers the
    ///     model under a guid and hands the guid to the controller.
    /// </summary>
    public sealed class ModelRegistry
    {
        private readonly Dictionary<Guid, SparklineViewModel> _store = new();
        public Guid Add(SparklineViewModel model)
        {
            var id = Guid.NewGuid();
            _store[id] = model;
            return id;
        }
        public SparklineViewModel Get(Guid id) => _store[id];
    }
}

/// <summary>
///     One-shot controller that hands the registered model to the SbSparkline
///     view component. Lives next to the test class so it's discovered by the
///     ApplicationParts registration above but doesn't pollute production routes.
/// </summary>
[Route("/_test/sparkline")]
public sealed class SbSparklineTestController : Controller
{
    private readonly SbSparklineTests.ModelRegistry _registry;
    public SbSparklineTestController(SbSparklineTests.ModelRegistry registry) => _registry = registry;

    [HttpGet]
    public IActionResult Get(Guid id) => ViewComponent("SbSparkline", new { model = _registry.Get(id) });
}
