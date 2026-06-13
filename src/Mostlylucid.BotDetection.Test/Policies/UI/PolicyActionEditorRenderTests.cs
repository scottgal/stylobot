using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
///     switch case. The middleware's switch lives in
///     <c>StyloBotDashboardMiddleware.cs</c> around line 595; this controller
///     keeps the dispatch logic identical so the partials are exercised by
///     the same view-path strings the production middleware uses.
/// </summary>
[Route("/_test")]
public sealed class PolicyActionEditorTestController : Controller
{
    [HttpGet("policy-stack-action-editor")]
    public IActionResult ActionEditor(string? kind)
    {
        var k = (kind ?? string.Empty).ToLowerInvariant();
        var viewPath = k switch
        {
            "allow"   => "/Views/Shared/Components/SbPolicyStack/_EditAction_Allow.cshtml",
            "observe" => "/Views/Shared/Components/SbPolicyStack/_EditAction_Observe.cshtml",
            "block"   => "/Views/Shared/Components/SbPolicyStack/_EditAction_Block.cshtml",
            _ => null
        };
        if (viewPath is null) return NotFound($"unknown action kind: {k}");
        return PartialView(viewPath);
    }
}