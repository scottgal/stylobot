using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Policies;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     E1 / Task 3 coverage. The three zero-field action editor partials
///     (Allow / Observe / Block) render via the same Razor pipeline the
///     middleware uses, and the action-editor switch-case dispatches on
///     <c>?kind=&lt;allow|observe|block&gt;</c>. Tasks 4-7 will widen this
///     test class (or sibling tests) with the parameterised partials
///     (tag / challenge / ratelimit / throttle); the zero-field variants are
///     the simplest case and lay down the test-host shape.
///
///     <para>
///     The test controller mirrors the middleware's <c>policystack/action-editor</c>
///     switch case 1:1 -- this keeps the test self-contained (no need to
///     boot the full <c>StyloBotDashboardMiddleware</c> with its 130+ DI
///     dependencies) while still exercising the actual partials end-to-end.
///     The route lives at <c>/_test/policy-stack-action-editor</c> to match
///     the convention established by <c>PolicyEditTests</c>.
///     </para>
/// </summary>
public sealed class PolicyActionEditorRenderTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = new();

    [Theory]
    [InlineData("allow")]
    [InlineData("observe")]
    [InlineData("block")]
    public async Task Zero_field_action_renders_no_input_elements(string kind)
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync($"/_test/policy-stack-action-editor?kind={kind}");
        resp.EnsureSuccessStatusCode();

        var html = await resp.Content.ReadAsStringAsync();

        // The data attribute is the contract policy-stack-edit.js uses to
        // route the swap; the JS reads `data-edit-action-kind` to detect
        // which partial it just received.
        Assert.Contains($"data-edit-action-kind=\"{kind}\"", html);

        // Zero-field partials must not contain any user-editable controls.
        // If a future change adds a stray <input> or <select> the test will
        // catch it; the slot is reserved for partials that explicitly
        // capture metadata (tag / challenge / ratelimit / throttle).
        Assert.DoesNotContain("<input", html);
        Assert.DoesNotContain("<select", html);
    }

    [Fact]
    public async Task Unknown_kind_returns_404()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync("/_test/policy-stack-action-editor?kind=banana");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Tag_renders_name_input_with_provided_default()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync("/_test/policy-stack-action-editor?kind=tag&name=stale-session");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("data-edit-action-kind=\"tag\"", html);
        Assert.Contains("name=\"action.tag.name\"", html);
        Assert.Contains("value=\"stale-session\"", html);
    }

    [Fact]
    public async Task Tag_with_empty_name_renders_empty_value_no_default_fallback()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync("/_test/policy-stack-action-editor?kind=tag&name=");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("data-edit-action-kind=\"tag\"", html);
        Assert.Contains("name=\"action.tag.name\"", html);
        // Empty input -> value="". No silent "untagged"/placeholder default
        // injected by either the model construction or the partial; an empty
        // string round-trips as an empty string so the operator-facing
        // required-validation kicks in client-side.
        Assert.Contains("value=\"\"", html);
    }

    private async Task<HttpClient> BuildClientAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(PolicyActionEditorRenderTests).Assembly)
            .AddApplicationPart(typeof(SbPolicyStackViewComponent).Assembly);

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
///     Test-only mirror of the middleware's <c>policystack/action-editor</c>
///     handler. Both this controller and
///     <c>StyloBotDashboardMiddleware.ServePolicyStackActionEditorAsync</c>
///     resolve the view path via <see cref="PolicyActionEditorViewPaths.ForKind"/>
///     AND build the per-kind <c>@model</c> via
///     <see cref="PolicyActionEditorViewPaths.BuildActionEditorModel"/>, so
///     when Tasks 5-7 add challenge / ratelimit / throttle the test
///     surface picks them up without drifting from the production
///     dispatch.
/// </summary>
[Route("/_test")]
public sealed class PolicyActionEditorTestController : Controller
{
    [HttpGet("policy-stack-action-editor")]
    public IActionResult ActionEditor(string? kind)
    {
        var k = (kind ?? string.Empty).ToLowerInvariant();
        var viewPath = PolicyActionEditorViewPaths.ForKind(k);
        if (viewPath is null) return NotFound($"unknown action kind: {k}");
        var model = PolicyActionEditorViewPaths.BuildActionEditorModel(k, Request.Query);
        // PartialView(viewPath, null) would fall through to the controller
        // model which is null too, so the partial without an @model
        // directive renders fine; the partials with @model directives
        // (tag today, challenge/ratelimit/throttle in Tasks 5-7) receive
        // the constructed slice.
        return PartialView(viewPath, model);
    }
}