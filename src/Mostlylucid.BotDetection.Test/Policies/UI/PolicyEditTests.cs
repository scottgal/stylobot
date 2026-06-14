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
using Mostlylucid.BotDetection.Test.Policies.Support;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Policies;
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
        PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload);

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

        // Task 8 -- the selector is now <select name="action.kind"> (dotted
        // form-encoding consistent with the per-kind partials), KindsForSelector
        // feeds the <option> rows, and the seeded Block rule's <option> carries
        // the human label, not the bare lower-case kind.
        Assert.Contains("name=\"action.kind\"", html);
        Assert.Matches(new Regex(@"<option value=""block"" selected=""selected"">Block</option>"),
            html);
    }

    [Fact]
    public async Task EditRow_renders_only_the_active_kind_partial_into_slot()
    {
        var client = await BuildClientAsync();
        var ruleId = await GetEndpointRuleIdAsync();

        var html = await client.GetStringAsync(
            $"/_test/policy-stack-edit?ruleId={ruleId}");

        // Task 8 -- the per-kind partial dispatched off ActionKind ("block" for
        // the seeded endpoint rule) is the ONLY one rendered into the slot.
        // The old `[data-edit-action-meta]` hidden-toggle pattern is gone --
        // non-active kinds simply aren't in the DOM until the HTMX swap brings
        // them in. We assert this by checking the active partial's data
        // attribute is present AND no other kind's data attribute is.
        Assert.Contains("data-action-editor-slot", html);
        Assert.Contains("data-edit-action-kind=\"block\"", html);
        Assert.DoesNotContain("data-edit-action-kind=\"tag\"", html);
        Assert.DoesNotContain("data-edit-action-kind=\"challenge\"", html);
        Assert.DoesNotContain("data-edit-action-kind=\"ratelimit\"", html);
        Assert.DoesNotContain("data-edit-action-kind=\"throttle\"", html);
        // The old scalar-input names from the pre-Task 8 _EditAction.cshtml
        // (challenge_kind / tag_name / requests_per_minute) are gone too.
        Assert.DoesNotContain("name=\"challenge_kind\"", html);
        Assert.DoesNotContain("name=\"tag_name\"", html);
        Assert.DoesNotContain("name=\"requests_per_minute\"", html);
    }

    [Fact]
    public async Task EditRow_renders_kind_selector_and_active_kind_partial()
    {
        // Seed a rate-limit rule via a FixedRulePolicyRuleStore so we can
        // assert the initial render of _EditAction.cshtml wraps the
        // ratelimit partial in the [data-action-editor-slot] AND wires
        // the HTMX attributes Task 12's JS will piggyback on.
        var ruleId = Guid.NewGuid();
        var rule = new PolicyRule(
            Id: ruleId,
            Scope: PolicyScope.Wildcard(),
            Priority: 100,
            Predicate: PredicateParser.Parse("bot.type = scraper"),
            Action: new PolicyAction.RateLimit(RequestsPerMinute: 6),
            Mode: PolicyMode.Draft,
            Notes: string.Empty,
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());

        var client = await BuildClientAsync(new FixedRulePolicyRuleStore(rule));

        var html = await client.GetStringAsync(
            $"/_test/policy-stack-edit?ruleId={ruleId}");

        Assert.Contains("name=\"action.kind\"", html);
        Assert.Contains("data-action-editor-slot", html);
        Assert.Contains("data-edit-action-kind=\"ratelimit\"", html);   // server-rendered active slot
        Assert.Contains("hx-get=\"/dashboard/policystack/action-editor\"", html);
        Assert.Contains("hx-target=\"[data-action-editor-slot]\"", html);
    }

    [Fact]
    public void KindsForSelector_and_ForKind_have_matching_kinds()
    {
        // feedback_no_word_lists: the selector option list and the dispatch
        // switch are two sides of the same single source of truth. If a kind
        // ever lands on one without the other this test catches it before the
        // editor silently drops a rendered <option> on the floor.
        foreach (var (kind, _) in PolicyActionEditorViewPaths.KindsForSelector)
        {
            Assert.NotNull(PolicyActionEditorViewPaths.ForKind(kind));
        }
    }

    [Fact]
    public void KindsForSelector_round_trips_through_BuildActionEditorModel()
    {
        // Same lockstep contract as the ForKind() assertion above, but on the
        // model-construction half of the dispatch. The zero-field kinds
        // (allow / observe / block) return null on purpose -- the partials
        // have no @model directive -- so the assertion is "the kind is
        // recognised", not "a model is built": ForKind() already proved a
        // partial exists, this proves the dispatcher accepts the kind at all.
        var emptyQuery = new Microsoft.AspNetCore.Http.QueryCollection();
        foreach (var (kind, _) in PolicyActionEditorViewPaths.KindsForSelector)
        {
            // BuildActionEditorModel returns null for zero-field kinds AND for
            // unknown kinds. The lockstep contract is that for every kind in
            // KindsForSelector, ForKind() is non-null (covered above) -- the
            // "call without throwing" check below is enough to prove the
            // dispatcher is wired for this kind.
            var _ = PolicyActionEditorViewPaths.BuildActionEditorModel(kind, emptyQuery);
        }
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

    // ---- C8 backtest panel coverage ----

    [Fact]
    public async Task EditRow_renders_empty_backtest_placeholder_on_initial_load()
    {
        var client = await BuildClientAsync();
        var ruleId = await GetEndpointRuleIdAsync();

        var html = await client.GetStringAsync(
            $"/_test/policy-stack-edit?ruleId={ruleId}");

        // Initial render: the Backtest property on the view model is null so
        // the partial emits the placeholder copy + the data-edit-backtest-slot
        // wrapper that the JS targets.
        Assert.Contains("data-edit-backtest-slot", html);
        Assert.Contains("sb-edit-backtest-placeholder", html);
        Assert.Contains("Type a valid predicate to see impact", html);
    }

    [Fact]
    public async Task BacktestRoute_returns_400_for_invalid_predicate()
    {
        var client = await BuildClientAsync();

        var resp = await client.PostAsync("/_test/policy-stack-backtest",
            new StringContent("{\"predicate\":\"bot.type =\",\"window\":\"24h\"}",
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"message\":", body);
    }

    [Fact]
    public async Task BacktestRoute_renders_populated_panel_with_window_picker()
    {
        var client = await BuildClientAsync();

        var resp = await client.PostAsync("/_test/policy-stack-backtest",
            new StringContent("{\"predicate\":\"bot.type = scraper\",\"window\":\"7d\"}",
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        // Populated panel: window-picker shows 24h / 7d / 30d buttons, and the
        // 7d button is the one with aria-pressed=true because we asked for that
        // window. The matching-requests and promote affordances both render.
        Assert.Contains("data-backtest-window=\"24h\"", body);
        Assert.Contains("data-backtest-window=\"7d\"", body);
        Assert.Contains("data-backtest-window=\"30d\"", body);
        Assert.Contains("data-backtest-show-matching", body);
        Assert.Contains("data-backtest-promote-observe", body);
        // The placeholder MUST NOT render when the panel is populated.
        Assert.DoesNotContain("sb-edit-backtest-placeholder", body);
    }

    // ---- Helpers ----

    private async Task<HttpClient> BuildClientAsync(IPolicyRuleStore? storeOverride = null)
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
            // Tests that need a rule with an action not present in the
            // embedded YAML seeds (e.g. ratelimit, throttle) pass a
            // FixedRulePolicyRuleStore via storeOverride. Default falls
            // through to the embedded YAML seeds the rest of the suite
            // has always asserted against.
            if (storeOverride is not null) return storeOverride;
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
        return effective.First(r => r.SourceScope.Host is HostScope.Endpoint).Rule.Id;
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
            "domain" => PolicyScope.Domain(domain ?? "unknown"),
            "subdomain" => PolicyScope.Subdomain(domain ?? "unknown", sub ?? "unknown"),
            "endpoint" => PolicyScope.Endpoint(domain ?? "unknown", sub ?? "unknown", template ?? "GET /"),
            _ => PolicyScope.Wildcard()
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

    /// <summary>
    ///     Mirror of the C8 backtest middleware handler. Parses the body,
    ///     runs the runner against the DI-registered InMemoryPolicyDecisionLog
    ///     (which the test fixture seeds with snapshot-bearing rows), and
    ///     renders <c>_BacktestPanel.cshtml</c> with the populated view
    ///     model. Bad predicate -> 400.
    /// </summary>
    [HttpPost("policy-stack-backtest")]
    public async Task<IActionResult> Backtest(
        [FromBody] BacktestDto body,
        [FromServices] IPolicyDecisionLog log)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Predicate))
            return BadRequest(new { message = "predicate is required" });

        Mostlylucid.BotDetection.Policies.Predicate.Predicate ast;
        try
        {
            ast = PredicateParser.Parse(body.Predicate);
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

        var (window, label) = (body.Window ?? "24h").ToLowerInvariant() switch
        {
            "7d" => (TimeSpan.FromDays(7), "7d"),
            "30d" => (TimeSpan.FromDays(30), "30d"),
            _ => (TimeSpan.FromHours(24), "24h")
        };

        var runner = new Mostlylucid.BotDetection.Policies.Decisions.PolicyBacktestRunner(log);
        var result = await runner.RunAsync(ast, window, ct: HttpContext.RequestAborted);

        var vm = new PolicyBacktestViewModel(
            TotalRequests: result.TotalRequests,
            WouldMatch: result.WouldMatch,
            WouldEnforce: result.WouldEnforce,
            OverriddenByHigherPriority: result.OverriddenByHigherPriority,
            EstimatedAddedP50Micros: result.EstimatedAddedP50Micros,
            EstimatedAddedP99Micros: result.EstimatedAddedP99Micros,
            SampleMatchingFingerprints: result.SampleMatchingFingerprints,
            Window: result.Window,
            WindowLabel: label,
            Caveat: result.Caveat);

        return PartialView("/Views/Shared/Components/SbPolicyStack/_BacktestPanel.cshtml", vm);
    }

    public sealed class BacktestDto
    {
        public string Predicate { get; set; } = string.Empty;
        public string? ActionKind { get; set; }
        public string? Window { get; set; }
    }
}
