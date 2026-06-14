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

    /// <summary>
    ///     Task 5 -- Challenge partial renders the kind &lt;select&gt; with
    ///     the requested option pre-selected, and renders the site-key
    ///     sub-field ONLY for the two third-party kinds (turnstile, captcha).
    ///     proof-of-work and jschallenge are self-hosted and must not emit a
    ///     site-key input so the form payload stays clean.
    /// </summary>
    [Theory]
    [InlineData("turnstile",     true,  "site-key")]
    [InlineData("captcha",       true,  "site-key")]
    [InlineData("proof-of-work", false, null)]
    [InlineData("jschallenge",   false, null)]
    public async Task Challenge_renders_kind_and_optional_subfield(
        string kind, bool expectSubfield, string? subfieldName)
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync($"/_test/policy-stack-action-editor?kind=challenge&challengeKind={kind}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("data-edit-action-kind=\"challenge\"", html);
        Assert.Contains($"<option value=\"{kind}\" selected", html);

        if (expectSubfield)
            Assert.Contains($"name=\"action.challenge.{subfieldName}\"", html);
        else
            Assert.DoesNotContain("action.challenge.site-key", html);
    }

    /// <summary>
    ///     Task 5 -- the <c>?siteKey=</c> query param round-trips into the
    ///     site-key input's <c>value</c> attribute when the challenge kind
    ///     actually renders the input (turnstile here). Mirrors the
    ///     tag-name round-trip assertion above; same lockstep contract
    ///     between query-string defaults and the partial's seeded state.
    /// </summary>
    [Fact]
    public async Task Challenge_turnstile_round_trips_site_key_into_input_value()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync(
            "/_test/policy-stack-action-editor?kind=challenge&challengeKind=turnstile&siteKey=env:STYLOBOT_TURNSTILE_SITE_KEY");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("name=\"action.challenge.site-key\"", html);
        Assert.Contains("value=\"env:STYLOBOT_TURNSTILE_SITE_KEY\"", html);
    }

    /// <summary>
    ///     Task 6 -- the rate-limit partial is the load-bearing per-action
    ///     editor. Six fields (rate, unit, key, burst,
    ///     mitigation-timeout-seconds, over-limit-action) all round-trip
    ///     from the <c>?rate=&amp;unit=&amp;key=&amp;burst=&amp;mitigation=&amp;over=</c>
    ///     query into the partial's seeded state. This is the assertion
    ///     surface policy-stack-edit.js relies on when it re-seeds the
    ///     editor after a swap; if any of these stop round-tripping the
    ///     operator-facing edit loop silently drops values.
    /// </summary>
    [Fact]
    public async Task RateLimit_renders_every_field()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync(
            "/_test/policy-stack-action-editor?kind=ratelimit&rate=10&unit=minute&key=identity&burst=20&mitigation=120&over=block");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("data-edit-action-kind=\"ratelimit\"", html);
        Assert.Contains("name=\"action.ratelimit.rate\"", html);
        Assert.Contains("value=\"10\"", html);
        Assert.Contains("<option value=\"minute\" selected", html);
        Assert.Contains("<option value=\"identity\" selected", html);
        Assert.Contains("name=\"action.ratelimit.burst\"", html);
        Assert.Contains("value=\"20\"", html);
        Assert.Contains("name=\"action.ratelimit.mitigation-timeout-seconds\"", html);
        Assert.Contains("<option value=\"block\" selected", html);
    }

    /// <summary>
    ///     Task 6 -- with NO query params the partial falls back to the
    ///     spec-mandated defaults: rate=6, unit=minute, key=fingerprint,
    ///     over-limit-action=throttle-status, and the two optional
    ///     numeric inputs (burst / mitigation-timeout-seconds) render
    ///     with empty <c>value=""</c>. These defaults match
    ///     <c>PolicyEditPresenter.DefaultRateLimitKey</c> /
    ///     <c>DefaultOverLimitAction</c> so the editor doesn't drift from
    ///     the legacy-widening fallback the presenter uses when projecting
    ///     a stored <c>PolicyAction.RateLimit</c> with only RPM set.
    /// </summary>
    [Fact]
    public async Task RateLimit_with_no_query_params_renders_spec_defaults()
    {
        var client = await BuildClientAsync();

        var resp = await client.GetAsync("/_test/policy-stack-action-editor?kind=ratelimit");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("data-edit-action-kind=\"ratelimit\"", html);
        Assert.Contains("name=\"action.ratelimit.rate\"", html);
        Assert.Contains("value=\"6\"", html);
        Assert.Contains("<option value=\"minute\" selected", html);
        Assert.Contains("<option value=\"fingerprint\" selected", html);
        Assert.Contains("<option value=\"throttle-status\" selected", html);
        // Optional numeric inputs round-trip an empty string when the
        // query param is missing -- no silent default of "0" or a magic
        // sentinel. Empty means "unset" and the commercial JSONB sidecar
        // (Task 16) will persist that as null.
        Assert.Contains("name=\"action.ratelimit.burst\" value=\"\"", html);
        Assert.Contains("name=\"action.ratelimit.mitigation-timeout-seconds\" value=\"\"", html);
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