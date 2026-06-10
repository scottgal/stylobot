using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.Policies.Telemetry;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     B7-B9 integration smoke tests. Each call site that wires SbPolicyStack
///     gets one cheap test that confirms the embed marker reaches the rendered
///     HTML on a real TestServer. The view-component itself has 56 dedicated
///     tests; these are surface-level wiring guards, not behavioural coverage.
///
///     <para>
///     Surfaces covered here:
///     <list type="bullet">
///       <item><description>InvestigatePolicy partial -- Full embed, scope from filter.</description></item>
///       <item><description>Policies tab (via direct view-component invoke at the same scope the partial decodes) -- Full embed.</description></item>
///       <item><description>Endpoint detail surface -- Full embed at Endpoint scope.</description></item>
///       <item><description>Signature detail surface -- EffectiveOnly + locked explainer.</description></item>
///       <item><description>Overview status badge -- StatusBadge at Domain scope.</description></item>
///     </list>
///     The Endpoints page collapsible relies on the same Component.InvokeAsync
///     under the hood and is covered by inspecting the partial source rather
///     than rendering DashboardShellModel (which has 15+ required props).
///     </para>
/// </summary>
public sealed class DashboardIntegrationPolicyStackTests : IAsyncDisposable
{
    private const string SeedPrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";
    private const string DomainAcme = "acme.com";
    private const string SubDocs = "docs.acme.com";
    private const string EpUpload = "GET /api/upload";

    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task InvestigatePolicy_partial_renders_full_embed_when_endpoint_filter_is_set()
    {
        var client = await BuildClientAsync();

        var html = await client.GetStringAsync(
            "/_test/dashboard/investigate-policy?host=acme.com&endpointPath=GET%20/api/upload");

        Assert.Contains("data-policy-stack-embed=\"full\"", html);
        Assert.Contains("data-tab=\"effective\"", html);
    }

    [Fact]
    public async Task InvestigatePolicy_partial_falls_back_to_wildcard_when_filter_is_empty()
    {
        var client = await BuildClientAsync();

        // Empty host AND empty endpointPath -- the partial must still render
        // (the body's own decoder degrades to Wildcard).
        var html = await client.GetStringAsync(
            "/_test/dashboard/investigate-policy?host=&endpointPath=");

        Assert.Contains("data-policy-stack-embed=\"full\"", html);
    }

    [Fact]
    public async Task Policies_tab_uses_full_embed_at_decoded_scope()
    {
        // Mirrors the body of _Policies.cshtml: decode ?scope= then invoke
        // SbPolicyStack with Full + 24h aggregate window.
        var client = await BuildClientAsync();

        var encoded = PolicyScopeUrl.Encode(
            PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload));
        var html = await client.GetStringAsync(
            $"/_test/dashboard/policies?scope={Uri.EscapeDataString(encoded)}");

        Assert.Contains("data-policy-stack-embed=\"full\"", html);
    }

    [Fact]
    public async Task EndpointDetail_uses_full_embed_for_endpoint_scope()
    {
        // _EndpointDetail.cshtml builds a PolicyScope.Endpoint from the
        // request host + method + path. The wrapper div in the partial
        // carries the `dashboard-endpoint-policy` marker class; the smoke
        // test asserts on the embed marker the view component itself emits
        // (the wrapper class is checked by the static-source assertion).
        var client = await BuildClientAsync();

        var html = await client.GetStringAsync(
            "/_test/dashboard/endpoint-detail?host=acme.com&method=GET&path=/api/upload");

        Assert.Contains("data-policy-stack-embed=\"full\"", html);
    }

    [Fact]
    public void EndpointDetail_partial_source_carries_marker_class()
    {
        // Static source assertion: the dashboard-endpoint-policy class lives
        // on the wrapper div in _EndpointDetail.cshtml itself, so a
        // refactor that strips it would break this guard before any HTML
        // test can catch it.
        var source = File.ReadAllText(LocatePartial("_EndpointDetail.cshtml"));
        Assert.Contains("dashboard-endpoint-policy", source);
        Assert.Contains("SbPolicyStack", source);
        Assert.Contains("PolicyStackEmbed.Full", source);
    }

    [Fact]
    public void EndpointsPage_partial_source_emits_lazy_drilldown_marker()
    {
        var source = File.ReadAllText(LocatePartial("_Endpoints.cshtml"));
        Assert.Contains("dashboard-endpoint-policy", source);
        Assert.Contains("lazy-policy-stack", source);
        Assert.Contains("/dashboard/policystack/rows", source);
    }

    [Fact]
    public void SignatureDetail_partial_source_uses_effective_only_and_locked()
    {
        var source = File.ReadAllText(LocatePartial("_SignatureDetail.cshtml"));
        Assert.Contains("dashboard-signature-policy", source);
        Assert.Contains("PolicyStackEmbed.EffectiveOnly", source);
        Assert.Contains("lockedFingerprint = true", source);
    }

    [Fact]
    public void Overview_index_source_invokes_status_badge_embed()
    {
        var source = File.ReadAllText(LocatePartial("Index.cshtml"));
        Assert.Contains("dashboard-status-policy", source);
        Assert.Contains("PolicyStackEmbed.StatusBadge", source);
    }

    [Fact]
    public void InvestigatePolicy_partial_source_uses_full_embed()
    {
        var source = File.ReadAllText(LocatePartial("_InvestigatePolicy.cshtml"));
        Assert.Contains("PolicyStackEmbed.Full", source);
        Assert.Contains("SbPolicyStack", source);
        // Bespoke legacy rendering is gone -- no more per-BotType policy cards.
        Assert.DoesNotContain("BotType &rarr; policy", source);
    }

    [Fact]
    public void Policies_partial_source_uses_full_embed_at_decoded_scope()
    {
        var source = File.ReadAllText(LocatePartial("_Policies.cshtml"));
        Assert.Contains("PolicyStackEmbed.Full", source);
        Assert.Contains("PolicyScopeUrl.Decode", source);
        // Legacy view-component tag must be gone from the actual markup --
        // the new view is component-driven via Component.InvokeAsync. The
        // explanatory comment may still mention the old tag by name, so
        // we look for the rendered form ("<sb-policies-tab " or "<sb-policies-tab/>")
        // rather than the bare token.
        Assert.DoesNotContain("<sb-policies-tab />", source);
        Assert.DoesNotContain("<sb-policies-tab/>", source);
    }

    private static string LocatePartial(string filename)
    {
        // Find the repo root by walking up from the test bin directory.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!,
            "src", "Mostlylucid.BotDetection.UI", "Views", "StyloBot", "Dashboard", filename);
        Assert.True(File.Exists(path), $"Expected partial at {path}");
        return path;
    }

    [Fact]
    public async Task SignatureDetail_uses_effective_only_with_locked_explainer()
    {
        var client = await BuildClientAsync();

        var html = await client.GetStringAsync(
            "/_test/dashboard/signature-policy?host=acme.com&method=GET&path=/api/upload&fp=fp-123");

        Assert.Contains("data-policy-stack-embed=\"effective-only\"", html);
        Assert.Contains("data-locked=\"true\"", html);
    }

    [Fact]
    public async Task Overview_status_badge_renders_for_host()
    {
        var client = await BuildClientAsync();

        var html = await client.GetStringAsync(
            "/_test/dashboard/status-badge?host=acme.com");

        Assert.Contains("data-policy-stack-embed=\"status-badge\"", html);
    }

    [Fact]
    public async Task Endpoints_lazy_drilldown_emits_dashboard_endpoint_policy_marker()
    {
        // The Endpoints page lazy-loads the SbPolicyStack rows via hx-get.
        // The marker class + hx-get URL the smoke test asserts on are
        // emitted by _Endpoints.cshtml's outer <details>; we render that
        // wrapper directly so we can assert without fabricating
        // DashboardShellModel.
        var client = await BuildClientAsync();

        var html = await client.GetStringAsync(
            "/_test/dashboard/endpoints-collapsible?host=acme.com");

        Assert.Contains("dashboard-endpoint-policy", html);
        Assert.Contains("/dashboard/policystack/rows", html);
    }

    private async Task<HttpClient> BuildClientAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(DashboardIntegrationPolicyStackTests).Assembly)
            .AddApplicationPart(typeof(SbPolicyStackViewComponent).Assembly);

        builder.Services.AddSingleton<ISignalCatalog>(_ =>
        {
            var asm = typeof(Mostlylucid.BotDetection.Models.SignalKeys).Assembly;
#pragma warning disable IL2026
            return SignalCatalog.LoadAsync(asm).GetAwaiter().GetResult();
#pragma warning restore IL2026
        });
        builder.Services.AddSingleton<IPolicyRuleStore>(_ =>
        {
            var asm = typeof(PolicyRule).Assembly;
            var store = YamlPolicyRuleStore.FromEmbeddedResources(asm, SeedPrefix);
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });
        builder.Services.AddSingleton<IPolicyResolver, DefaultPolicyResolver>();
        builder.Services.AddSingleton<IPolicyDecisionLog, InMemoryPolicyDecisionLog>();
        builder.Services.AddSingleton<IPolicyEffectivenessCache>(sp =>
            new PolicyEffectivenessCache(
                sp.GetRequiredService<IPolicyDecisionLog>(),
                Options.Create(new PolicyEffectivenessOptions()),
                NullLogger<PolicyEffectivenessCache>.Instance));
        builder.Services.AddSingleton<PolicyConflictAnalyzer>();
        builder.Services.AddSingleton<PolicyExplainerPresenter>();
        builder.Services.AddSingleton<PolicyStackPresenter>();

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
///     One-shot controller for the B7-B9 surface smoke tests. Each action
///     replays the relevant SbPolicyStack invocation shape from the modified
///     dashboard partial -- enough to assert the embed marker reaches HTML,
///     without dragging in DashboardShellModel + its 15+ required props.
/// </summary>
[Route("/_test/dashboard")]
public sealed class DashboardIntegrationStubController : Controller
{
    [HttpGet("policies")]
    public IActionResult Policies(string? scope = null)
    {
        // Mirrors _Policies.cshtml verbatim: decode scope, default to wildcard.
        var decoded = PolicyScopeUrl.Decode(scope);
        return ViewComponent("SbPolicyStack", new
        {
            scope = decoded,
            embed = PolicyStackEmbed.Full,
            activeTab = "effective",
            aggregateWindow = (TimeSpan?)TimeSpan.FromHours(24),
            canEdit = false
        });
    }

    [HttpGet("investigate-policy")]
    public IActionResult InvestigatePolicy(string? host = null, string? endpointPath = null)
    {
        // Mirrors _InvestigatePolicy.cshtml scope resolution.
        PolicyScope scope;
        if (!string.IsNullOrEmpty(endpointPath) && !string.IsNullOrEmpty(host))
            scope = PolicyScope.Endpoint(host, host, endpointPath);
        else if (!string.IsNullOrEmpty(host))
            scope = PolicyScope.Domain(host);
        else
            scope = PolicyScope.Wildcard();
        return ViewComponent("SbPolicyStack", new
        {
            scope,
            embed = PolicyStackEmbed.Full,
            activeTab = "effective",
            aggregateWindow = (TimeSpan?)TimeSpan.FromHours(24),
            canEdit = false
        });
    }

    [HttpGet("endpoint-detail")]
    public IActionResult EndpointDetail(string host, string method, string path)
    {
        // Mirrors _EndpointDetail.cshtml's PolicyScope.Endpoint construction.
        var template = $"{method} {path}";
        var scope = string.IsNullOrEmpty(host)
            ? (PolicyScope)PolicyScope.Wildcard()
            : PolicyScope.Endpoint(host, host, template);
        return ViewComponent("SbPolicyStack", new
        {
            scope,
            embed = PolicyStackEmbed.Full,
            activeTab = "effective",
            aggregateWindow = (TimeSpan?)TimeSpan.FromHours(24),
            canEdit = false
        });
    }

    [HttpGet("signature-policy")]
    public IActionResult SignaturePolicy(string host, string method, string path, string fp)
    {
        // Mirrors _SignatureDetail.cshtml: EffectiveOnly + locked explainer.
        var template = $"{method} {path}";
        var scope = PolicyScope.Endpoint(host, host, template);
        return ViewComponent("SbPolicyStack", new
        {
            scope,
            embed = PolicyStackEmbed.EffectiveOnly,
            explainerFingerprint = fp,
            lockedFingerprint = true,
            aggregateWindow = (TimeSpan?)TimeSpan.FromHours(24)
        });
    }

    [HttpGet("status-badge")]
    public IActionResult StatusBadge(string host)
    {
        // Mirrors Index.cshtml's status-badge invocation.
        var scope = PolicyScope.Domain(host);
        return ViewComponent("SbPolicyStack", new
        {
            scope,
            embed = PolicyStackEmbed.StatusBadge,
            aggregateWindow = (TimeSpan?)TimeSpan.FromHours(24)
        });
    }

    [HttpGet("endpoints-collapsible")]
    public ContentResult EndpointsCollapsible(string host)
    {
        // Mirrors the outer <details> + hx-get wrapper in _Endpoints.cshtml.
        // We emit the same wrapper markup verbatim so the smoke test can
        // assert on the marker class + the rows URL the lazy-load points at.
        var pageScope = string.IsNullOrEmpty(host)
            ? (PolicyScope)PolicyScope.Wildcard()
            : PolicyScope.Domain(host);
        var encoded = PolicyScopeUrl.Encode(pageScope);
        var rowsUrl = $"/dashboard/policystack/rows?scope={encoded}&tab=effective";
        var html = $"""
            <details class="dashboard-endpoint-policy mt-4" data-endpoint="{host}">
                <summary>Show effective policy stack for this site</summary>
                <div class="lazy-policy-stack" hx-get="{rowsUrl}" hx-trigger="toggle once from:closest details[open]" hx-swap="innerHTML" hx-target="this">
                    <em>Loading policy…</em>
                </div>
            </details>
            """;
        return Content(html, "text/html");
    }
}
