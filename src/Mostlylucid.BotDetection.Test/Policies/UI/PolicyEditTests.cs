using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Predicate;
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
///     C6 -- expression editor coverage. Covers:
///     <list type="bullet">
///         <item><description>Pencil + Add Rule affordances only render when canEdit is true.</description></item>
///         <item><description>The edit row emits BOTH chip pane AND textarea visible simultaneously
///         (the load-bearing "no advanced/simple flip" constraint).</description></item>
///         <item><description>Action-meta inputs toggle correctly based on the seeded action kind.</description></item>
///         <item><description>The PolicyEditPresenter populates an existing rule's text into the
///         textarea and a new rule with empty defaults.</description></item>
///         <item><description>The parse route emits a canonical lowercase-discriminator AST for
///         valid input and returns 400 with position for bad input.</description></item>
///     </list>
/// </summary>
public sealed class PolicyEditTests : IAsyncDisposable
{
    private const string SeedPrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";
    private const string DomainAcme = "acme.com";
    private const string SubDocs = "docs.acme.com";
    private const string EpUpload = "GET /api/upload";

    private static readonly PolicyScope EndpointScope =
        new PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload);

    private readonly List<WebApplication> _apps = new();

    // ---- Presenter coverage (cheap, no TestServer) ----

    [Fact]
    public async Task PolicyEditPresenter_existing_rule_round_trips_predicate_text()
    {
        var presenter = await BuildPresenterAsync();
        var ruleId = await GetEndpointRuleIdAsync();

        var vm = await presenter.BuildForExistingRuleAsync(ruleId);

        Assert.NotNull(vm);
        Assert.Equal(ruleId, vm!.RuleId);
        Assert.Equal("PUT", vm.HttpMethod);
        Assert.Equal($"/api/v1/policies/{ruleId}", vm.SubmitUrl);
        Assert.Contains("scope=", vm.CancelUrl);
        // The endpoint Block rule's seed predicate is "bot.type in (scraper, ...)
        // and score.bot_probability >= 0.7" -- the formatter preserves both.
        Assert.Contains("bot.type", vm.PredicateText);
        Assert.Equal("block", vm.ActionKind);
    }

    [Fact]
    public async Task PolicyEditPresenter_returns_null_for_unknown_rule_id()
    {
        var presenter = await BuildPresenterAsync();
        var vm = await presenter.BuildForExistingRuleAsync(Guid.NewGuid());
        Assert.Null(vm);
    }

    [Fact]
    public async Task PolicyEditPresenter_new_rule_has_empty_defaults_at_scope()
    {
        var presenter = await BuildPresenterAsync();
        var vm = presenter.BuildForNewRule(EndpointScope);

        Assert.Null(vm.RuleId);
        Assert.Equal(EndpointScope, vm.Scope);
        Assert.Equal("POST", vm.HttpMethod);
        Assert.Equal("/api/v1/policies", vm.SubmitUrl);
        Assert.Empty(vm.PredicateText);
        Assert.Equal("observe", vm.ActionKind);
        Assert.Equal(PolicyMode.Draft, vm.Mode);
    }

    // ---- Row-VM CanEdit threading ----

    [Fact]
    public async Task RuleRow_pencil_button_only_visible_when_canEdit_is_true()
    {
        var client = await BuildClientAsync();

        var htmlNoEdit = await GetHtmlAsync(client, EndpointScope, canEdit: false);
        Assert.DoesNotContain("sb-policy-stack-row-edit", htmlNoEdit);
        Assert.DoesNotContain("data-action=\"edit\"", htmlNoEdit);

        var htmlEdit = await GetHtmlAsync(client, EndpointScope, canEdit: true);
        Assert.Contains("sb-policy-stack-row-edit", htmlEdit);
        Assert.Contains("data-action=\"edit\"", htmlEdit);
        Assert.Contains("hx-get=\"/dashboard/policystack/edit?ruleId=", htmlEdit);
    }

    [Fact]
    public async Task AddRule_button_only_visible_when_canEdit_is_true()
    {
        var client = await BuildClientAsync();

        var htmlNoEdit = await GetHtmlAsync(client, EndpointScope, canEdit: false);
        Assert.DoesNotContain("sb-policy-stack-add-rule", htmlNoEdit);
        Assert.DoesNotContain("data-action=\"add-rule\"", htmlNoEdit);

        var htmlEdit = await GetHtmlAsync(client, EndpointScope, canEdit: true);
        Assert.Contains("sb-policy-stack-add-rule", htmlEdit);
        Assert.Contains("data-action=\"add-rule\"", htmlEdit);
        Assert.Contains("hx-get=\"/dashboard/policystack/edit/new?scope=", htmlEdit);
    }

    // ---- _EditRow.cshtml render coverage ----

    [Fact]
    public async Task EditRow_renders_chip_pane_and_textarea_simultaneously()
    {
        var client = await BuildClientAsync();
        var ruleId = await GetEndpointRuleIdAsync();

        var html = await client.GetStringAsync(
            $"/_test/policy-stack-edit?ruleId={ruleId}");

        // Both panes MUST be present at the same time in the SAME container --
        // the "no advanced/simple flip" constraint is this assertion.
        Assert.Contains("data-edit-chip-pane", html);
        Assert.Contains("data-edit-expression", html);
        // The chip pane must come BEFORE the textarea in the document because
        // the sb-edit-panes wrapper places them as side-by-side panes.
        var chipIdx = html.IndexOf("data-edit-chip-pane", StringComparison.Ordinal);
        var exprIdx = html.IndexOf("data-edit-expression", StringComparison.Ordinal);
        Assert.True(chipIdx > 0 && exprIdx > chipIdx,
            "chip pane must render alongside (and before) the textarea pane");

        // The sb-edit-panes wrapper is what enforces the "both visible at once"
        // layout in the partial.
        Assert.Contains("sb-edit-panes", html);
    }

    [Fact]
    public async Task EditRow_renders_existing_predicate_text_into_textarea()
    {
        var client = await BuildClientAsync();
        var ruleId = await GetEndpointRuleIdAsync();

        var html = await client.GetStringAsync(
            $"/_test/policy-stack-edit?ruleId={ruleId}");

        // The endpoint Block rule's predicate involves bot.type + score.bot_probability.
        // Razor encodes `.` literally and `>=` as &gt;= -- assert on the parts
        // that are stable across encoders.
        Assert.Contains("bot.type", html);
        Assert.Contains("score.bot_probability", html);
        // The textarea contains the canonical-form text from PredicateFormatter.
        Assert.Matches(new Regex(@"<textarea[^>]*data-edit-expression[^>]*>[^<]*bot\.type[^<]*</textarea>"),
            html);
    }

    [Fact]
    public async Task EditRow_seeded_action_kind_is_selected_in_dropdown()
    {
        var client = await BuildClientAsync();
        var ruleId = await GetEndpointRuleIdAsync();

        var html = await client.GetStringAsync(
            $"/_test/policy-stack-edit?ruleId={ruleId}");

        // The endpoint seed rule is a Block. The <select name="action_kind">
        // must mark <option value="block"> as selected.
        Assert.Matches(new Regex(@"<option value=""block"" selected=""selected"">block</option>"),
            html);
    }

    [Fact]
    public async Task EditRow_action_meta_inputs_hidden_for_non_matching_kinds()
    {
        var client = await BuildClientAsync();
        var ruleId = await GetEndpointRuleIdAsync();

        var html = await client.GetStringAsync(
            $"/_test/policy-stack-edit?ruleId={ruleId}");

        // The Block action means challenge_kind, tag_name, requests_per_minute
        // all start hidden. Razor's bool attribute treatment writes `hidden="hidden"`.
        Assert.Matches(
            new Regex(@"name=""challenge_kind""[^>]*hidden=""hidden"""),
            html);
        Assert.Matches(
            new Regex(@"name=""tag_name""[^>]*hidden=""hidden"""),
            html);
        Assert.Matches(
            new Regex(@"name=""requests_per_minute""[^>]*hidden=""hidden"""),
            html);
    }

    [Fact]
    public async Task NewRuleRoute_renders_pre_populated_scope_with_empty_defaults()
    {
        var client = await BuildClientAsync();
        var encoded = PolicyScopeUrl.Encode(EndpointScope);

        var html = await client.GetStringAsync(
            $"/_test/policy-stack-edit-new?scope={Uri.EscapeDataString(encoded)}");

        // New row: data-rule-id="new", POST, /api/v1/policies (no id), and the
        // scope encoded in data-edit-scope.
        Assert.Contains("data-rule-id=\"new\"", html);
        Assert.Contains("data-edit-http-method=\"POST\"", html);
        Assert.Contains("data-edit-submit-url=\"/api/v1/policies\"", html);
        // PolicyScopeUrl.Encode for an endpoint scope produces something like
        // "endpoint|acme.com|docs.acme.com|GET%20%2Fapi%2Fupload".
        Assert.Contains("endpoint", html);
    }

    // ---- Parse route coverage ----

    [Fact]
    public async Task ParseRoute_returns_canonical_lowercase_ast_for_valid_input()
    {
        var client = await BuildClientAsync();

        var resp = await client.PostAsync("/_test/policy-stack-parse",
            new StringContent("bot.type = scraper and score.bot_probability >= 0.7", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        // The wire format the JS chip renderer consumes uses lowercase "kind"
        // discriminators ("and"/"or"/"term") -- NOT the C# record's "$kind"
        // PascalCase form. This guard is the load-bearing assertion for the
        // editor's bidirectional sync.
        Assert.Contains("\"kind\":\"and\"", body);
        Assert.Contains("\"kind\":\"term\"", body);
        Assert.DoesNotContain("\"$kind\"", body);
        Assert.DoesNotContain("\"And\"", body); // PascalCase MUST NOT leak
        Assert.Contains("\"facet\":\"bot.type\"", body);
        Assert.Contains("\"op\":\"gte\"", body); // >= maps to canonical "gte"
    }

    [Fact]
    public async Task ParseRoute_returns_400_with_position_for_bad_input()
    {
        var client = await BuildClientAsync();

        var resp = await client.PostAsync("/_test/policy-stack-parse",
            new StringContent("bot.type =", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        // The parser exception carries a character offset for the inline
        // error indicator. JSON keys: { "message": "...", "position": N }.
        Assert.Contains("\"message\":", body);
        Assert.Contains("\"position\":", body);
    }

    // ---- Helpers ----

    private async Task<HttpClient> BuildClientAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(PolicyEditTests).Assembly)
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
        builder.Services.AddSingleton<PolicyEditPresenter>();
        builder.Services.AddSingleton<RazorViewRenderer>();

        var app = builder.Build();
        app.UseRouting();
        app.MapControllers();
        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestClient();
    }

    private static async Task<string> GetHtmlAsync(
        HttpClient client,
        PolicyScope scope,
        bool canEdit)
    {
        var query = $"embed=Full&scopeKind=endpoint&domain={DomainAcme}&sub={SubDocs}&template={Uri.EscapeDataString(EpUpload)}";
        if (canEdit) query += "&canEdit=true";
        var resp = await client.GetAsync($"/_test/policy-stack-canedit?{query}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private static async Task<PolicyEditPresenter> BuildPresenterAsync()
    {
        var asm = typeof(PolicyRule).Assembly;
        var store = YamlPolicyRuleStore.FromEmbeddedResources(asm, SeedPrefix);
        await store.InitializeAsync();
        return new PolicyEditPresenter(store);
    }

    private static async Task<Guid> GetEndpointRuleIdAsync()
    {
        var asm = typeof(PolicyRule).Assembly;
        var store = YamlPolicyRuleStore.FromEmbeddedResources(asm, SeedPrefix);
        await store.InitializeAsync();
        var resolver = new DefaultPolicyResolver(store);
        var effective = await resolver.EffectiveAsync(EndpointScope);
        return effective.First(r => r.SourceScope is PolicyScope.Endpoint).Rule.Id;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            try { await app.DisposeAsync(); } catch { /* test cleanup */ }
        _apps.Clear();
    }
}

/// <summary>
///     Helper controller for the C6 tests. Provides three endpoints:
///     <c>/_test/policy-stack-canedit</c> renders the view component with the
///     canEdit flag flipped via query string so the pencil / add-rule visibility
///     guards can assert against both states; <c>/_test/policy-stack-edit</c>
///     renders the <c>_EditRow</c> partial for an existing rule;
///     <c>/_test/policy-stack-edit-new</c> renders the same partial for a new
///     rule at the given scope. <c>/_test/policy-stack-parse</c> mirrors the
///     middleware's parse handler so the parse coverage doesn't need to bring
///     up the full Dashboard middleware stack.
/// </summary>
[Route("/_test")]
public sealed class PolicyEditTestController : Controller
{
    private readonly PolicyEditPresenter _editPresenter;
    private readonly PolicyStackPresenter _stackPresenter;
    private readonly IRazorViewEngine _viewEngine;

    public PolicyEditTestController(
        PolicyEditPresenter editPresenter,
        PolicyStackPresenter stackPresenter,
        IRazorViewEngine viewEngine)
    {
        _editPresenter = editPresenter;
        _stackPresenter = stackPresenter;
        _viewEngine = viewEngine;
    }

    [HttpGet("policy-stack-canedit")]
    public IActionResult CanEdit(
        string embed = "Full",
        string scopeKind = "wildcard",
        string? domain = null,
        string? sub = null,
        string? template = null,
        bool canEdit = false)
    {
        PolicyScope scope = scopeKind switch
        {
            "domain" => new PolicyScope.Domain(domain ?? "unknown"),
            "subdomain" => new PolicyScope.Subdomain(domain ?? "unknown", sub ?? "unknown"),
            "endpoint" => new PolicyScope.Endpoint(domain ?? "unknown", sub ?? "unknown", template ?? "GET /"),
            _ => new PolicyScope.Wildcard()
        };
        var parsedEmbed = Enum.TryParse<PolicyStackEmbed>(embed, ignoreCase: true, out var e)
            ? e
            : PolicyStackEmbed.Full;

        return ViewComponent("SbPolicyStack", new
        {
            scope,
            embed = parsedEmbed,
            activeTab = (string?)null,
            canEdit
        });
    }

    [HttpGet("policy-stack-edit")]
    public async Task<IActionResult> EditExisting(Guid ruleId)
    {
        var vm = await _editPresenter.BuildForExistingRuleAsync(ruleId);
        if (vm is null) return NotFound();
        return PartialView("/Views/Shared/Components/SbPolicyStack/_EditRow.cshtml", vm);
    }

    [HttpGet("policy-stack-edit-new")]
    public IActionResult EditNew(string scope)
    {
        var decoded = PolicyScopeUrl.Decode(scope);
        var vm = _editPresenter.BuildForNewRule(decoded);
        return PartialView("/Views/Shared/Components/SbPolicyStack/_EditRow.cshtml", vm);
    }

    [HttpPost("policy-stack-parse")]
    public async Task<IActionResult> Parse()
    {
        string text;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            text = await reader.ReadToEndAsync(HttpContext.RequestAborted);
        }

        try
        {
            var ast = PredicateParser.Parse(text);
            var canonical = PolicyStackParseSerialiser.SerialiseAst(ast);
            return Content($"{{\"ast\":{canonical}}}", "application/json; charset=utf-8");
        }
        catch (PredicateParseException ex)
        {
            return new ContentResult
            {
                StatusCode = 400,
                ContentType = "application/json; charset=utf-8",
                Content = $"{{\"message\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)},\"position\":{ex.Position}}}"
            };
        }
    }
}
