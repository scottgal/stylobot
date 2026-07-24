using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Task 4 (window-threading branch): the "Your Signature" section title on
///     <c>BotDetectionHeader/Default.cshtml</c> now links to
///     <c>{basePath}/signature/{signatureId}</c> when a signature was resolved for the
///     request, and degrades to plain (non-link) text when it wasn't (e.g. an excluded
///     path where detection never ran). Renders the real Default.cshtml through the full
///     Razor pipeline (mirrors <see cref="SbHealthDotTests"/>'s harness), directly via
///     <c>Controller.View(viewPath, model)</c> rather than through
///     <c>BotDetectionHeaderViewComponent.Invoke()</c> -- Task 4 only changed the view
///     template, not the ViewComponent's <c>DetectionDataExtractor</c> plumbing (the
///     signature id was already on <see cref="DetectionDisplayModel.Signatures"/>), so
///     testing the view directly against a hand-built model is the precise unit under test.
/// </summary>
public sealed class BotDetectionHeaderYourSignatureLinkTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task Signature_available_renders_a_link_to_signature_detail()
    {
        var model = new DetectionDisplayModel
        {
            Signatures = new MultiFactorSignatureDisplay { PrimarySignature = "abc123deadbeef" },
        };

        var html = await RenderAsync(model, basePath: "/dashboard");

        Assert.Contains("href=\"/dashboard/signature/abc123deadbeef\"", html);
        Assert.Contains("Your Signature</a>", html);
    }

    [Fact]
    public async Task Signature_available_url_escapes_the_signature_id()
    {
        // Signatures are hex-only in production, but the escaping itself should still be
        // correct and not silently drop/mangle unexpected characters.
        var model = new DetectionDisplayModel
        {
            Signatures = new MultiFactorSignatureDisplay { PrimarySignature = "abc 123/xyz" },
        };

        var html = await RenderAsync(model, basePath: "/dashboard");

        Assert.Contains("href=\"/dashboard/signature/abc%20123%2Fxyz\"", html);
    }

    [Fact]
    public async Task No_signature_degrades_to_plain_text_no_link()
    {
        // Signatures left null -- e.g. a request to an excluded path where detection
        // never ran, so no signature was ever resolved for this render. Other pre-existing
        // "/signature/" references in the template (the live ticker items, the "View Full
        // Dashboard" footer link -- both Alpine-bound, driven by client-side state, not this
        // Task 4 change) are expected and unrelated, so the assertion is scoped to the
        // specific "Your Signature" section-title element rather than the whole page.
        var model = new DetectionDisplayModel { Signatures = null };

        var html = await RenderAsync(model, basePath: "/dashboard");

        Assert.Contains("<div class=\"section-title\">Your Signature</div>", html);
        Assert.DoesNotContain("class=\"section-title\" href=", html);
    }

    // -------- Render helper --------

    private async Task<string> RenderAsync(DetectionDisplayModel model, string basePath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(BotDetectionHeaderYourSignatureLinkTests).Assembly)
            .AddApplicationPart(typeof(DetectionDisplayModel).Assembly);

        var registry = new ModelRegistry();
        var id = registry.Add(model);
        builder.Services.AddSingleton(registry);

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);

        var client = app.GetTestClient();
        var resp = await client.GetAsync($"/_test/botdetectionheader?id={id}&basePath={Uri.EscapeDataString(basePath)}");
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
        private readonly Dictionary<Guid, DetectionDisplayModel> _store = new();
        public Guid Add(DetectionDisplayModel model)
        {
            var id = Guid.NewGuid();
            _store[id] = model;
            return id;
        }
        public DetectionDisplayModel Get(Guid id) => _store[id];
    }
}

[Route("/_test/botdetectionheader")]
public sealed class BotDetectionHeaderTestController : Controller
{
    private readonly BotDetectionHeaderYourSignatureLinkTests.ModelRegistry _registry;
    public BotDetectionHeaderTestController(BotDetectionHeaderYourSignatureLinkTests.ModelRegistry registry) => _registry = registry;

    [HttpGet]
    public IActionResult Get(Guid id, string basePath)
    {
        ViewData["BasePath"] = basePath;
        return View("~/Views/Shared/Components/BotDetectionHeader/Default.cshtml", _registry.Get(id));
    }
}
