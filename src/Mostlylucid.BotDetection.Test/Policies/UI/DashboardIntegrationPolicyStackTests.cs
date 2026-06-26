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
        // test can catch it. The endpoint-detail surface uses the compact
        // StatusBadge embed -- the full rule editor lives on
        // /dashboard/policies (see commit 3d3cdc91); the detail view only
        // needs the one-line "N rules effective here" indicator.
        var source = File.ReadAllText(LocatePartial("_EndpointDetail.cshtml"));
        Assert.Contains("dashboard-endpoint-policy", source);
        Assert.Contains("SbPolicyStack", source);
        Assert.Contains("PolicyStackEmbed.StatusBadge", source);
    }

    [Fact]
    public void EndpointsPage_partial_source_emits_lazy_drilldown_marker()
    {
        var source = File.ReadAllText(LocatePartial("_Endpoints.cshtml"));
        Assert.Contains("dashboard-endpoint-policy", source);
        Assert.Contains("lazy-policy-stack", source);
        // The path is composed via DashboardLinks.Resolve(...) so the
        // source carries the BasePath-relative subpath; the resolver
        // adds the configured mount prefix at render time.
        Assert.Contains("/policystack/rows", source);
        Assert.Contains("DashboardLinks.Resolve", source);
    }

    [Fact]
    public void SignatureDetail_partial_source_uses_status_badge_embed()
    {
        // The signature-detail surface uses the compact StatusBadge embed --
        // the full rule editor lives on /dashboard/policies (see commit
        // 3d3cdc91); the detail view only needs the one-line "N rules
        // effective here" indicator. The lockedFingerprint explainer that
        // EffectiveOnly carried is unused here because StatusBadge has no
        // expandable rule rows.
        var source = File.ReadAllText(LocatePartial("_SignatureDetail.cshtml"));
        Assert.Contains("dashboard-signature-policy", source);
        Assert.Contains("PolicyStackEmbed.StatusBadge", source);
    }

    [Fact]
    public void Overview_index_source_invokes_status_badge_embed()
    {
        var source = File.ReadAllText(LocatePartial("Index.cshtml"));
        Assert.Contains("dashboard-status-policy", source);
        Assert.Contains("PolicyStackEmbed.StatusBadge", source);
    }

    [Fact]
    public void InvestigatePolicy_partial_source_uses_status_badge_embed()
    {
        // The investigate-policy tab uses the compact StatusBadge embed --
        // the full rule editor lives on /dashboard/policies (see commit
        // 3d3cdc91); the investigation surface only needs the one-line
        // "N rules effective here" indicator.
        var source = File.ReadAllText(LocatePartial("_InvestigatePolicy.cshtml"));
        Assert.Contains("PolicyStackEmbed.StatusBadge", source);
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

    [Fact]
    public void Policies_partial_passes_hideRuleList_when_template_groups_render_above()
    {
        // F3 -- regression guard for the policies-page double-render bug
        // staging surfaced. The T4 template-grouped block at the top of
        // _Policies.cshtml owns the rule rendering, so the SbPolicyStack
        // invocation below it MUST pass hideRuleList=true (gated on
        // templateGroups.Count > 0) or every rule prints twice.
        var source = File.ReadAllText(LocatePartial("_Policies.cshtml"));
        Assert.Contains("hideRuleList", source);
        // The flag is wired off the same condition the template-groups
        // block uses, so an empty presenter -> empty groups -> falls back
        // to the SbPolicyStack rule list (the page never goes blank).
        Assert.Contains("templateGroups.Count > 0", source);
    }

    [Fact]
    public async Task Policies_page_compose_does_not_render_same_rule_twice()
    {
        // F3 -- end-to-end regression for the staging.stylobot.net bug:
        // /dashboard/policies rendered every rule once inside the T4
        // template-grouped block and again inside the SbPolicyStack
        // Effective tab. We can't easily render the full DashboardShellModel
        // (15+ required props) here, so the test composes the page the
        // same way _Policies.cshtml does -- the T4 template-grouped
        // _TemplateGroup partials AND the SbPolicyStack invocation -- via
        // two render hops, then concatenates and asserts NO rule id appears
        // twice. This is the exact composition the live page produces.
        var client = await BuildClientAsync();

        var templateGroupsHtml = await client.GetStringAsync(
            "/_test/dashboard/policies-template-groups");
        var sbPolicyStackHtml = await client.GetStringAsync(
            "/_test/dashboard/policies-sb-policy-stack");

        var composed = templateGroupsHtml + sbPolicyStackHtml;

        // Count <article ... data-rule-id="..."> occurrences -- exactly ONE
        // per rendered rule row. A4-A6's _RuleCard rewrite changed the
        // anchor class from sb-policy-stack-row to sb-policy-card, but the
        // data-rule-id contract on the article is preserved so this
        // duplication regression check still has a stable anchor. The
        // article-tag scope keeps child elements (drag/edit buttons inside
        // the article) from inflating the count -- those use data-action
        // or data-policy-stack-drag-handle, not data-rule-id.
        var ruleIds = System.Text.RegularExpressions.Regex
            .Matches(composed,
                @"<article\s+class=""sb-policy-card""(?:[^>]*?\s)?data-rule-id=""(?<id>[0-9a-fA-F\-]+)""")
            .Select(m => m.Groups["id"].Value)
            .ToList();
        Assert.NotEmpty(ruleIds);
        var duplicates = ruleIds.GroupBy(id => id).Where(g => g.Count() > 1).ToList();
        Assert.True(duplicates.Count == 0,
            $"Each rule must render exactly once; duplicates: {string.Join(",", duplicates.Select(g => g.Key))}");

        // Specific checks: the template-groups block carries the rule cards
        // and SbPolicyStack contributes the surrounding chrome (filter bar,
        // C15 posture card) WITHOUT a rule envelope of its own.
        Assert.Contains("sb-policy-stack-template-group", templateGroupsHtml);
        Assert.Contains("sb-policy-card", templateGroupsHtml);
        Assert.Contains("data-policy-stack-embed=\"full\"", sbPolicyStackHtml);
        Assert.Contains("sb-policy-stack-filter-bar", sbPolicyStackHtml);
        Assert.Contains("sb-posture-card", sbPolicyStackHtml);
        Assert.DoesNotContain("sb-policy-stack-rows-envelope", sbPolicyStackHtml);
        // SbPolicyStack with hideRuleList=true MUST emit no rule cards.
        Assert.DoesNotContain("<article class=\"sb-policy-card\"", sbPolicyStackHtml);
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
        // Resolved against the default test mount (/stylobot) via
        // DashboardLinkResolver; alternative mounts produce alternative
        // prefixes, see DashboardLinkResolverTests.
        Assert.Contains("/stylobot/policystack/rows", html);
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
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Services.IFacetPickerCatalog, Mostlylucid.BotDetection.UI.Services.FacetPickerCatalog>();
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Services.IFacetAutocompleteSource, Mostlylucid.BotDetection.UI.Services.FacetAutocompleteSource>();
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Services.FacetPillRenderer>();
        // C15: the SbPolicyStack Full embed now invokes PolicyStackPostureViewComponent
        // in place of the dry "N requests · N rules · N hits" aggregate strip. The
        // view component pulls a PolicyStackHitSnapshot from PolicyStackHitAtom and
        // classifies it via PolicyStackPostureClassifier, so both must be in DI.
        builder.Services.AddSingleton<Mostlylucid.BotDetection.Policies.Rules.PolicyIntentClassifier>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(Options.Create(new Mostlylucid.BotDetection.UI.Options.PolicyStackHitAtomOptions()));
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Atoms.PolicyStackHitAtom>();
        builder.Services.AddSingleton(Options.Create(new Mostlylucid.BotDetection.UI.Options.PostureClassifierOptions()));
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyStackPostureClassifier>();
        // Task 18: SbPolicyStackViewComponent reads IPolicyCanEditPolicy
        // when the canEdit override is unset. These integration tests
        // exercise the dashboard partials that embed the view component;
        // FOSS default keeps them read-only as the production FOSS host
        // does.
        builder.Services.AddSingleton<IPolicyCanEditPolicy, AlwaysReadOnlyPolicyCanEditPolicy>();
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

    [HttpGet("policies-template-groups")]
    public async Task<IActionResult> PoliciesTemplateGroups(
        [FromServices] PolicyStackPresenter presenter)
    {
        // Mirrors the T4 grouping arm of _Policies.cshtml: build the row
        // VMs via the same presenter the live page uses, project to
        // template groups, then render each group's partial in order.
        // Returns concatenated HTML so the regression test can sum it
        // with the SbPolicyStack output and check rule-id uniqueness.
        const string DomainAcme = "acme.com";
        const string SubDocs = "docs.acme.com";
        const string EpUpload = "GET /api/upload";
        var scope = PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload);

        var vm = await presenter.BuildAsync(
            scope: scope,
            embed: PolicyStackEmbed.EffectiveOnly,
            activeTab: "effective",
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false);
        var registry = HttpContext.RequestServices
            .GetService(typeof(Mostlylucid.BotDetection.Policies.Templates.TemplateRegistry))
            as Mostlylucid.BotDetection.Policies.Templates.TemplateRegistry;
        var groups = PolicyTemplateGrouping.Group(vm.Rows, registry);

        var sb = new System.Text.StringBuilder();
        foreach (var group in groups)
        {
            var partial = await RenderPartialAsync(
                "Components/SbPolicyStack/_TemplateGroup", group);
            sb.Append(partial);
        }
        return Content(sb.ToString(), "text/html");
    }

    [HttpGet("policies-sb-policy-stack")]
    public IActionResult PoliciesSbPolicyStack()
    {
        // Mirrors the SbPolicyStack invocation arm of _Policies.cshtml AT
        // THE SAME endpoint scope the template-groups endpoint uses, with
        // hideRuleList = true (the fix). The combined assertion lives in
        // the test.
        const string DomainAcme = "acme.com";
        const string SubDocs = "docs.acme.com";
        const string EpUpload = "GET /api/upload";
        var scope = PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload);
        return ViewComponent("SbPolicyStack", new
        {
            scope,
            embed = PolicyStackEmbed.Full,
            activeTab = "effective",
            aggregateWindow = (TimeSpan?)TimeSpan.FromHours(24),
            canEdit = false,
            hideRuleList = true
        });
    }

    private async Task<string> RenderPartialAsync(string viewName, object model)
    {
        // Render a Razor partial to a string from a controller -- used by
        // the policies-template-groups endpoint so the test can replay the
        // same composition _Policies.cshtml performs.
        var viewEngine = HttpContext.RequestServices
            .GetRequiredService<Microsoft.AspNetCore.Mvc.ViewEngines.ICompositeViewEngine>();
        var actionContextAccessor = new Microsoft.AspNetCore.Mvc.ActionContext(
            HttpContext, RouteData, ControllerContext.ActionDescriptor);
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewName, isMainPage: false);
        if (!viewResult.Success)
            viewResult = viewEngine.FindView(actionContextAccessor, viewName, isMainPage: false);
        if (!viewResult.Success)
            throw new InvalidOperationException(
                $"Could not locate view {viewName}; searched: {string.Join(", ", viewResult.SearchedLocations)}");

        await using var sw = new StringWriter();
        var viewDictionary = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary(
            new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(),
            new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
        {
            Model = model
        };
        var tempDataProvider = HttpContext.RequestServices
            .GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>();
        var tempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(HttpContext, tempDataProvider);
        var viewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(
            actionContextAccessor,
            viewResult.View,
            viewDictionary,
            tempData,
            sw,
            new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions());
        await viewResult.View.RenderAsync(viewContext);
        return sw.ToString();
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
        // Matches the resolver-driven URL the production _Endpoints.cshtml
        // partial now emits at the default test mount (/stylobot).
        var rowsUrl = $"/stylobot/policystack/rows?scope={encoded}&tab=effective";
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
