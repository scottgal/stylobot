using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Real-render coverage for the <c>SbConfigBaseline</c> view component (the config-baseline
///     layer of /dashboard/policies). Views compile at runtime here, so this TestServer render is
///     what actually validates the Razor. It also pins the product-critical gate: the config rows
///     are VIEW = all tiers, EDIT = commercial-only. FOSS (<see cref="AlwaysReadOnlyPolicyCanEditPolicy"/>)
///     must render the rows but no edit affordance; a gate that grants edit must surface it.
/// </summary>
public sealed class SbConfigBaselineRenderTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task Renders_the_config_baseline_rows_including_the_silent_scraper_throttle()
    {
        var html = await RenderAsync(new AlwaysReadOnlyPolicyCanEditPolicy());

        Assert.Contains("data-config-baseline", html);
        Assert.Contains("Scraper", html);
        Assert.Contains("throttle-aggressive", html);   // the previously-invisible config default
        Assert.Contains("BotDetection:BotTypeActionPolicies:Scraper", html); // the override target key
    }

    [Fact]
    public async Task FOSS_default_gate_renders_no_edit_affordance()
    {
        var html = await RenderAsync(new AlwaysReadOnlyPolicyCanEditPolicy());

        // VIEW is all-tiers, EDIT is commercial-only: the FOSS read-only gate must suppress the
        // config-row edit control in one swing.
        Assert.DoesNotContain("data-config-edit", html);
    }

    [Fact]
    public async Task Gate_that_grants_edit_renders_the_config_row_edit_control()
    {
        var html = await RenderAsync(new AlwaysCanEditPolicy());

        Assert.Contains("data-config-edit", html);
    }

    [Fact]
    public async Task Showcase_demo_configuration_renders_disabled_config_edit_affordance_without_write_wiring()
    {
        var html = await RenderAsync(new AlwaysReadOnlyPolicyCanEditPolicy(), showcaseDemo: true);

        Assert.Contains("Demo mode: editing is read-only", html);
        Assert.Contains("aria-label=\"Edit Scraper action policy unavailable in demo mode\"", html);
        Assert.Contains("disabled", html);
        Assert.DoesNotContain("data-config-edit", html);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            try { await app.DisposeAsync(); } catch { /* test cleanup */ }
        _apps.Clear();
    }

    // -------- Render helper --------

    private async Task<string> RenderAsync(IPolicyCanEditPolicy canEditPolicy, bool showcaseDemo = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (showcaseDemo)
            builder.Configuration[PolicyEditAffordanceResolver.ShowcaseDemoConfigKey] = "true";

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbConfigBaselineRenderTests).Assembly)
            .AddApplicationPart(typeof(SbConfigBaselineViewComponent).Assembly);

        // Real default options carry Scraper -> throttle-aggressive; that is the case under test.
        builder.Services.Configure<BotDetectionOptions>(o =>
        {
            o.BotTypeActionPolicies = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Scraper"] = "throttle-aggressive",
                ["MaliciousBot"] = "block-hard",
            };
            o.DefaultActionPolicyName = "throttle-stealth";
        });

        // Stub registry: resolves the two names we render to their kinds; the composer's real
        // ActionType->colour mapping is unit-tested separately in EffectivePolicyComposerTests.
        var registry = new Mock<IActionPolicyRegistry>();
        registry.Setup(r => r.GetPolicy("throttle-aggressive")).Returns(StubPolicy("throttle-aggressive", ActionType.Throttle));
        registry.Setup(r => r.GetPolicy("block-hard")).Returns(StubPolicy("block-hard", ActionType.Block));
        registry.Setup(r => r.GetPolicy("throttle-stealth")).Returns(StubPolicy("throttle-stealth", ActionType.Throttle));
        builder.Services.AddSingleton(registry.Object);

        builder.Services.AddSingleton<IEffectivePolicyConfigOverlay, PassthroughEffectivePolicyConfigOverlay>();
        builder.Services.AddSingleton<EffectivePolicyComposer>();
        // The view component consumes the IConfigBaselineProvider seam; the local composer is it.
        builder.Services.AddSingleton<IConfigBaselineProvider>(sp => sp.GetRequiredService<EffectivePolicyComposer>());
        builder.Services.AddSingleton(canEditPolicy);

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);

        var resp = await app.GetTestClient().GetAsync("/_test/config-baseline");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private static IActionPolicy StubPolicy(string name, ActionType type)
    {
        var p = new Mock<IActionPolicy>();
        p.SetupGet(x => x.Name).Returns(name);
        p.SetupGet(x => x.ActionType).Returns(type);
        return p.Object;
    }

    private sealed class AlwaysCanEditPolicy : IPolicyCanEditPolicy
    {
        public bool CanEdit(ClaimsPrincipal? user) => true;
    }
}

[Route("/_test/config-baseline")]
public sealed class SbConfigBaselineTestController : Controller
{
    [HttpGet]
    public IActionResult Get() => ViewComponent("SbConfigBaseline");
}
