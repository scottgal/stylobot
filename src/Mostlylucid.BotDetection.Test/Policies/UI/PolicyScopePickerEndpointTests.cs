using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Task 9 coverage. The middleware's
///     <c>GET /dashboard/policystack/scope-picker</c> fragment endpoint
///     renders the existing <c>SbScopePicker</c> view component for inline
///     use inside <c>_EditRow.cshtml</c> and the Apply Template flow.
///     The plan originally called for a brand new
///     <c>SbPolicyScopePicker</c> component but <c>SbScopePicker</c>
///     already covers every composite-scope axis the plan listed (Host kind
///     including endpoint+path-template, Method, Geo, Identity), so this
///     test exercises the existing component through the new endpoint
///     instead of re-implementing the picker.
///
///     <para>
///         The test controller mirrors the middleware's switch-case 1:1
///         (decode <c>?scope=</c> via <see cref="PolicyScopeUrl.Decode"/>,
///         honour <c>?fieldName=</c>, return 400 on
///         <c>?mode=multi</c>) -- this keeps the test self-contained without
///         booting the full middleware while still exercising the
///         production view component and view-locator pipeline.
///     </para>
/// </summary>
public sealed class PolicyScopePickerEndpointTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task Inline_with_endpoint_scope_round_trips_domain_subdomain_and_path()
    {
        var client = await BuildClientAsync();

        var encoded = PolicyScopeUrl.Encode(
            PolicyScope.Endpoint("acme.com", "api", "POST /login"));
        var resp = await client.GetAsync(
            $"/_test/policy-stack-scope-picker?mode=inline&scope={Uri.EscapeDataString(encoded)}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // The four-axis fieldset rendered; this is the unique marker for
        // the existing SbScopePicker component.
        Assert.Contains("sb-scope-picker", html);
        Assert.Contains("data-axis=\"host\"", html);
        Assert.Contains("data-axis=\"method\"", html);
        Assert.Contains("data-axis=\"geo\"", html);
        Assert.Contains("data-axis=\"identity\"", html);

        // The endpoint host kind seeded the JSON payload (the Alpine root
        // reads this on init to hydrate the controls). Asserting on
        // data-scope-picker-seed keeps the test stable against future
        // markup tweaks to the visible controls.
        Assert.Contains("data-scope-picker-seed", html);
        Assert.Contains("acme.com", html);
        Assert.Contains("api", html);
        Assert.Contains("POST /login", html);

        // Hidden input carries the default field name when none is
        // supplied; the wrapping form binds the JSON projection under
        // this key.
        Assert.Contains("name=\"scope\"", html);
        Assert.Contains("data-scope-picker-hidden", html);
    }

    [Fact]
    public async Task Inline_with_no_scope_query_renders_wildcard_picker()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync("/_test/policy-stack-scope-picker?mode=inline");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("sb-scope-picker", html);
        // The picker's hint text only renders when the picker is alive,
        // so it's a stable "rendered the wildcard form" assertion.
        Assert.Contains("wildcard scope", html);
    }

    [Fact]
    public async Task Inline_with_custom_field_name_round_trips_into_hidden_input()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync(
            "/_test/policy-stack-scope-picker?mode=inline&fieldName=applied-to%5B2%5D");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // Apply Template (Task 10) will embed multiple pickers in one form
        // and needs a distinct hidden-input name per row. The endpoint
        // already honours that contract for the inline mode so Task 10
        // can re-use it row-by-row if needed.
        Assert.Contains("name=\"applied-to[2]\"", html);
    }

    [Fact]
    public async Task Mode_multi_returns_400_today_so_callers_fail_loud()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync("/_test/policy-stack-scope-picker?mode=multi");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("multi", body);
    }

    [Fact]
    public async Task Default_mode_is_inline_when_query_omits_mode()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync("/_test/policy-stack-scope-picker");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("sb-scope-picker", html);
    }

    // ---- Test rig ----------------------------------------------------

    private async Task<HttpClient> BuildClientAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(PolicyScopePickerEndpointTests).Assembly)
            .AddApplicationPart(typeof(SbScopePickerViewComponent).Assembly);

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            try { await app.DisposeAsync(); } catch { /* test cleanup */ }
        _apps.Clear();
    }
}

/// <summary>
///     Test-only mirror of the middleware's
///     <c>policystack/scope-picker</c> switch case. Same dispatch (mode
///     check, scope decode, fieldName fallback, view component invoke) so
///     adding new query parameters to the middleware route requires the
///     mirroring change here; the two stay in lockstep.
/// </summary>
[Route("/_test")]
public sealed class PolicyScopePickerTestController : Controller
{
    [HttpGet("policy-stack-scope-picker")]
    public IActionResult ScopePicker(
        [FromQuery] string? mode = null,
        [FromQuery] string? scope = null,
        [FromQuery] string? fieldName = null)
    {
        var m = (mode ?? "inline").ToLowerInvariant();
        if (m.Length == 0) m = "inline";
        if (m != "inline")
            return BadRequest($"unsupported scope-picker mode: {m}");

        var initial = string.IsNullOrEmpty(scope) ? null : PolicyScopeUrl.Decode(scope);
        var field = string.IsNullOrEmpty(fieldName) ? "scope" : fieldName;
        var vm = new ScopePickerViewModel(FieldName: field, Initial: initial);
        return ViewComponent("SbScopePicker", new { model = vm });
    }
}