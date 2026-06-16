using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     T4 Part A real-Razor render tests for the SbScopePicker view
///     component. Drives the same TestServer pattern as
///     <c>SbPolicyStackTests</c>: a one-shot controller invokes the view
///     component via <c>ViewComponent(...)</c> so the real view-engine
///     pipeline (locator, partials, model binding) runs against the
///     production partials.
/// </summary>
public sealed class SbScopePickerRenderTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task Wildcard_initial_renders_all_axes_with_none_selected()
    {
        var client = await BuildClientAsync();
        var html = await GetHtmlAsync(client);

        // Four axis rows are present, each with its own label.
        Assert.Contains("data-axis=\"host\"", html);
        Assert.Contains("data-axis=\"method\"", html);
        Assert.Contains("data-axis=\"geo\"", html);
        Assert.Contains("data-axis=\"identity\"", html);

        // Hidden field is present with the default field name.
        Assert.Contains("name=\"scope\"", html);
        Assert.Contains("data-scope-picker-hidden", html);

        // None-everywhere wildcard hint visible.
        Assert.Contains("wildcard scope", html);
    }

    [Fact]
    public async Task Custom_field_name_round_trips_through_hidden_input()
    {
        var client = await BuildClientAsync();
        var html = await GetHtmlAsync(client, fieldName: "applied-to[0]");
        Assert.Contains("name=\"applied-to[0]\"", html);
    }

    [Fact]
    public async Task Initial_subdomain_seed_pre_populates_host_axis_in_seed_json()
    {
        var client = await BuildClientAsync();
        var html = await GetHtmlAsync(client, initialKind: "subdomain");

        // Seed JSON sits on a data attribute so it survives DOMRoundtrip.
        Assert.Contains("data-scope-picker-seed", html);
        Assert.Contains("subdomain", html);
        Assert.Contains("acme.com", html);
    }

    [Fact]
    public async Task Method_axis_renders_default_verb_options()
    {
        var client = await BuildClientAsync();
        var html = await GetHtmlAsync(client);
        foreach (var verb in ScopePickerViewModel.DefaultMethods)
        {
            Assert.Contains($"value=\"{verb}\"", html);
        }
    }

    // ---- Test rig ----------------------------------------------------

    private async Task<HttpClient> BuildClientAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbScopePickerRenderTests).Assembly)
            .AddApplicationPart(typeof(SbScopePickerViewComponent).Assembly);

        // _Multi.cshtml takes IDashboardLinkResolver via @inject for the
        // "+ Add scope" hx-get URL; without the registration Razor fails
        // page activation before rendering anything.
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Services.IDashboardLinkResolver>(
            new Mostlylucid.BotDetection.UI.Services.DashboardLinkResolver(
                new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions { BasePath = "/stylobot" }));

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestClient();
    }

    private static async Task<string> GetHtmlAsync(
        HttpClient client,
        string? initialKind = null,
        string? fieldName = null)
    {
        var qs = new List<string>();
        if (initialKind is not null) qs.Add($"initialKind={initialKind}");
        if (fieldName is not null) qs.Add($"fieldName={Uri.EscapeDataString(fieldName)}");
        var url = "/_test/scope-picker" + (qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty);
        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps) await app.DisposeAsync();
    }
}

[ApiController]
[Route("_test")]
public sealed class TestScopePickerController : Controller
{
    [HttpGet("scope-picker")]
    public IActionResult Render(
        [FromQuery] string? initialKind = null,
        [FromQuery] string? fieldName = null)
    {
        PolicyScope? initial = initialKind switch
        {
            "subdomain" => PolicyScope.Subdomain("acme.com", "docs.acme.com"),
            "domain" => PolicyScope.Domain("acme.com"),
            "endpoint" => PolicyScope.Endpoint("acme.com", "docs.acme.com", "GET /upload"),
            _ => null
        };
        var vm = new ScopePickerViewModel(
            FieldName: fieldName ?? "scope",
            Initial: initial);
        return ViewComponent("SbScopePicker", new { model = vm });
    }
}
