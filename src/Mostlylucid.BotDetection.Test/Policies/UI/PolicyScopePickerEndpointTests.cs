using System.Net;
using System.Text.RegularExpressions;
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
///     Tasks 9 + 10 coverage. The middleware's
///     <c>GET /dashboard/policystack/scope-picker</c> fragment endpoint
///     renders the existing <c>SbScopePicker</c> view component for use
///     inside <c>_EditRow.cshtml</c> and the Apply Template flow.
///     The plan originally called for a brand new
///     <c>SbPolicyScopePicker</c> component but <c>SbScopePicker</c>
///     already covers every composite-scope axis the plan listed (Host kind
///     including endpoint+path-template, Method, Geo, Identity), so these
///     tests exercise the existing component through the new endpoint
///     instead of re-implementing the picker.
///
///     <para>
///         The test controller mirrors the middleware's switch-case 1:1
///         (decode <c>?scope=</c> via <see cref="PolicyScopeUrl.Decode"/>,
///         honour <c>?fieldName=</c>, dispatch <c>?mode=multi</c> to the
///         multi wrapper, dispatch <c>?mode=inline-row</c> to the row
///         partial) -- this keeps the test self-contained without booting
///         the full middleware while still exercising the production view
///         component and view-locator pipeline.
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

        // Apply Template (Task 10) embeds multiple pickers in one form
        // and needs a distinct hidden-input name per row. The inline
        // endpoint already honours that contract row-by-row; the multi
        // endpoint builds the indexed names itself.
        Assert.Contains("name=\"applied-to[2]\"", html);
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

    [Fact]
    public async Task Unsupported_mode_returns_400_so_callers_fail_loud()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync("/_test/policy-stack-scope-picker?mode=triple-fancy");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("triple-fancy", body);
    }

    // ---- Task 10 -- multi mode ---------------------------------------

    [Fact]
    public async Task Multi_with_two_scopes_renders_two_rows_plus_add_button()
    {
        var client = await BuildClientAsync();

        var encoded1 = Uri.EscapeDataString(
            PolicyScopeUrl.Encode(PolicyScope.Endpoint("acme.com", "api", "POST /login")));
        var encoded2 = Uri.EscapeDataString(
            PolicyScopeUrl.Encode(PolicyScope.Endpoint("acme.com", "api", "POST /oauth/token")));

        var resp = await client.GetAsync(
            $"/_test/policy-stack-scope-picker?mode=multi&multi={encoded1}&multi={encoded2}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // The wrapper fieldset is rendered with the multi marker.
        Assert.Contains("data-scope-picker-mode=\"multi\"", html);

        // Two row wrappers are emitted -- one per supplied scope.
        Assert.Equal(2, Regex.Matches(html, "data-scope-row=").Count);

        // The "+ Add scope" button is present so the operator can extend
        // the row set.
        Assert.Contains("data-add-scope-row", html);

        // Each row gets its own indexed FieldName so model-binders pick
        // them up as the same collection slot the row index implies.
        Assert.Contains("applied-to[0]", html);
        Assert.Contains("applied-to[1]", html);

        // The second scope's path-template propagated through to the
        // second picker (i.e. each row is independently seeded).
        Assert.Contains("POST /oauth/token", html);
    }

    [Fact]
    public async Task Multi_with_no_scopes_renders_one_empty_row_so_operator_has_a_slot()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync("/_test/policy-stack-scope-picker?mode=multi");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("data-scope-picker-mode=\"multi\"", html);

        // Exactly one row wrapper -- the wildcard slot the operator
        // populates.
        Assert.Single(Regex.Matches(html, "data-scope-row="));
        Assert.Contains("data-add-scope-row", html);
        Assert.Contains("applied-to[0]", html);
    }

    [Fact]
    public async Task Multi_with_custom_prefix_drives_indexed_field_names()
    {
        var client = await BuildClientAsync();

        var encoded = Uri.EscapeDataString(
            PolicyScopeUrl.Encode(PolicyScope.Endpoint("acme.com", "api", "POST /login")));

        var resp = await client.GetAsync(
            $"/_test/policy-stack-scope-picker?mode=multi&multi={encoded}&fieldNamePrefix=apply");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // The wrapper's data-scope-picker-prefix attribute and each
        // picker's hidden-input name both pick up the custom prefix.
        Assert.Contains("data-scope-picker-prefix=\"apply\"", html);
        Assert.Contains("apply[0]", html);
    }

    // ---- Task 10 -- inline-row mode ----------------------------------

    [Fact]
    public async Task Inline_row_returns_just_a_row_wrapper_for_htmx_append()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync(
            "/_test/policy-stack-scope-picker?mode=inline-row&fieldNamePrefix=applied-to");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // The row wrapper is present, but the multi fieldset is NOT --
        // the response slots into [data-multi-scope-rows] inside an
        // existing wrapper, so wrapping it again would nest fieldsets.
        Assert.Contains("data-scope-row=", html);
        Assert.Contains("data-remove-scope-row", html);
        Assert.DoesNotContain("data-scope-picker-mode=\"multi\"", html);

        // The picker carries the prefix-derived FieldName. rowIndex
        // defaults to "new" so the appended row binds under
        // applied-to[new] until the client-side wiring rewrites
        // indexes; the picker view component just echoes the name back.
        Assert.Contains("applied-to[new]", html);

        // The picker itself rendered (four axes present).
        Assert.Contains("sb-scope-picker", html);
        Assert.Contains("data-axis=\"host\"", html);
    }

    [Fact]
    public async Task Inline_row_honours_explicit_rowIndex_when_supplied()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync(
            "/_test/policy-stack-scope-picker?mode=inline-row&fieldNamePrefix=applied-to&rowIndex=3");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // Explicit rowIndex flows through into the hidden-input name so
        // a caller that already knows the next index can request the
        // row pre-bound to that slot.
        Assert.Contains("applied-to[3]", html);
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
///     check, scope decode, fieldName fallback, view component invoke
///     for inline; multi wrapper for multi; row partial for inline-row)
///     so adding new query parameters to the middleware route requires
///     the mirroring change here; the two stay in lockstep.
/// </summary>
[Route("/_test")]
public sealed class PolicyScopePickerTestController : Controller
{
    [HttpGet("policy-stack-scope-picker")]
    public IActionResult ScopePicker(
        [FromQuery] string? mode = null,
        [FromQuery] string? scope = null,
        [FromQuery] string? fieldName = null,
        [FromQuery] string? fieldNamePrefix = null,
        [FromQuery] string? rowIndex = null)
    {
        var m = (mode ?? "inline").ToLowerInvariant();
        if (m.Length == 0) m = "inline";

        switch (m)
        {
            case "inline":
            {
                var initial = string.IsNullOrEmpty(scope) ? null : PolicyScopeUrl.Decode(scope);
                var field = string.IsNullOrEmpty(fieldName) ? "scope" : fieldName;
                var vm = new ScopePickerViewModel(FieldName: field, Initial: initial);
                return ViewComponent("SbScopePicker", new { model = vm });
            }

            case "multi":
            {
                var scopes = new List<PolicyScope?>();
                foreach (var encoded in Request.Query["multi"])
                {
                    if (string.IsNullOrEmpty(encoded)) continue;
                    try { scopes.Add(PolicyScopeUrl.Decode(encoded)); }
                    catch { /* skip junk */ }
                }
                if (scopes.Count == 0) scopes.Add(null);

                var prefix = string.IsNullOrEmpty(fieldNamePrefix) ? "applied-to" : fieldNamePrefix;
                var vm = new ScopePickerMultiViewModel(scopes, prefix);
                return PartialView("/Views/Shared/Components/SbScopePicker/_Multi.cshtml", vm);
            }

            case "inline-row":
            {
                var prefix = string.IsNullOrEmpty(fieldNamePrefix) ? "applied-to" : fieldNamePrefix;
                var label = string.IsNullOrEmpty(rowIndex) ? "new" : rowIndex;
                var vm = new ScopePickerViewModel(FieldName: $"{prefix}[{label}]", Initial: null);
                return PartialView("/Views/Shared/Components/SbScopePicker/_Row.cshtml", vm);
            }

            default:
                return BadRequest($"unsupported scope-picker mode: {m}");
        }
    }
}