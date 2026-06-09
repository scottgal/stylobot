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
///     Real-render coverage for the <c>SbHealthDot</c> view component. Mirrors
///     the <c>SbSparklineTests</c> harness: a TestServer-hosted controller
///     invokes the view component via <c>ViewComponent</c> result, so the full
///     Razor pipeline runs against the real Default.cshtml.
/// </summary>
public sealed class SbHealthDotTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Theory]
    [InlineData(HealthBand.Good,    "sb-health-good")]
    [InlineData(HealthBand.Caution, "sb-health-caution")]
    [InlineData(HealthBand.Warning, "sb-health-caution")]   // alias of Caution
    [InlineData(HealthBand.Danger,  "sb-health-danger")]
    [InlineData(HealthBand.Neutral, "sb-health-neutral")]
    public async Task Band_maps_to_canonical_class(HealthBand band, string expectedClass)
    {
        var html = await RenderAsync(new HealthDotViewModel(band, ""));
        Assert.Contains(expectedClass, html);
        Assert.Contains("role=\"img\"", html);
    }

    [Fact]
    public async Task Tooltip_appears_in_title_attribute()
    {
        var html = await RenderAsync(new HealthDotViewModel(HealthBand.Good, "all systems operational"));
        Assert.Contains("title=\"all systems operational\"", html);
    }

    [Fact]
    public async Task Empty_tooltip_falls_back_to_band_name_for_aria_label()
    {
        var html = await RenderAsync(new HealthDotViewModel(HealthBand.Danger, ""));
        Assert.Contains("aria-label=\"Danger\"", html);
    }

    // -------- Render helper --------

    private async Task<string> RenderAsync(HealthDotViewModel model)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbHealthDotTests).Assembly)
            .AddApplicationPart(typeof(SbHealthDotViewComponent).Assembly);

        var registry = new ModelRegistry();
        var id = registry.Add(model);
        builder.Services.AddSingleton(registry);

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);

        var client = app.GetTestClient();
        var resp = await client.GetAsync($"/_test/healthdot?id={id}");
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
        private readonly Dictionary<Guid, HealthDotViewModel> _store = new();
        public Guid Add(HealthDotViewModel model)
        {
            var id = Guid.NewGuid();
            _store[id] = model;
            return id;
        }
        public HealthDotViewModel Get(Guid id) => _store[id];
    }
}

[Route("/_test/healthdot")]
public sealed class SbHealthDotTestController : Controller
{
    private readonly SbHealthDotTests.ModelRegistry _registry;
    public SbHealthDotTestController(SbHealthDotTests.ModelRegistry registry) => _registry = registry;

    [HttpGet]
    public IActionResult Get(Guid id) => ViewComponent("SbHealthDot", new { model = _registry.Get(id) });
}
