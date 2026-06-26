using System.Net;
using System.Net.Http;
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
///     C-UX3 -- predicate composer activation. Covers:
///     <list type="bullet">
///         <item><description>The picker rendering no longer emits
///         <c>disabled</c> attributes on facet/op/value inputs.</description></item>
///         <item><description>The <c>+ AND</c>/<c>+ OR</c>/× buttons are
///         enabled.</description></item>
///         <item><description>The Razor view ships the row template the JS
///         clones for new rows.</description></item>
///         <item><description>The parse route still round-trips a single-term
///         predicate the picker would build.</description></item>
///         <item><description>The parse route accepts the AND-then-OR shape
///         the picker serialises when an OR row is added.</description></item>
///     </list>
/// </summary>
public sealed class PolicyEditComposerTests : IAsyncDisposable
{
    private const string SeedPrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";
    private readonly List<WebApplication> _apps = new();

    [Fact]
    public async Task ExpandSurface_picker_inputs_are_no_longer_disabled()
    {
        // Use a fixture rule whose predicate IS in the curated picker catalog
        // (ua.bot_type) so the picker block renders live rows -- not the
        // empty-state placeholder. Without this the live row assertions
        // below have nothing to check against.
        var client = await BuildClientAsync(BuildCatalogProjectableRule(out var ruleId));

        var html = await client.GetStringAsync($"/_test/policy-stack-expand?ruleId={ruleId}");

        // Assert that the live picker block (everything before the
        // <template> element) carries no `disabled` attribute on any of
        // its inputs. We slice off the template subtree because templates
        // may legitimately ship attribute names we'd otherwise pattern-
        // match against.
        var templateIdx = html.IndexOf("sb-facet-picker-row-template", StringComparison.Ordinal);
        Assert.True(templateIdx > 0, "row template must be present so the JS can clone new rows");
        var liveBlock = html.Substring(0, templateIdx);

        // There must be at least one live .sb-facet-picker-row in the
        // live block -- otherwise we'd be asserting against the empty-
        // state placeholder, which is the wrong surface.
        Assert.Contains("class=\"sb-facet-picker-row\"", liveBlock);

        // No `disabled` attributes in the live picker block.
        var pickerStart = liveBlock.IndexOf("data-facet-picker", StringComparison.Ordinal);
        var pickerOnly = liveBlock.Substring(pickerStart);
        Assert.DoesNotContain("disabled", pickerOnly);
    }

    [Fact]
    public async Task ExpandSurface_add_and_add_or_remove_buttons_are_enabled()
    {
        var client = await BuildClientAsync(BuildCatalogProjectableRule(out var ruleId));

        var html = await client.GetStringAsync($"/_test/policy-stack-expand?ruleId={ruleId}");

        // + AND / + OR buttons no longer carry `disabled`.
        var andBtn = Regex.Match(html, "<button[^>]*data-add-row=\"and\"[^>]*>");
        var orBtn = Regex.Match(html, "<button[^>]*data-add-row=\"or\"[^>]*>");
        Assert.True(andBtn.Success);
        Assert.True(orBtn.Success);
        Assert.DoesNotContain("disabled", andBtn.Value);
        Assert.DoesNotContain("disabled", orBtn.Value);

        // The × remove button on the live row must NOT carry `disabled`.
        // We anchor on the live row by slicing off everything from the
        // <template> onwards (where catalog-defaulted markup also lives).
        var templateIdx = html.IndexOf("sb-facet-picker-row-template", StringComparison.Ordinal);
        var liveBlock = html.Substring(0, templateIdx);
        var removeBtnInPickerArea = Regex.Match(
            liveBlock,
            "<button[^>]*sb-facet-picker-remove[^>]*>");
        Assert.True(removeBtnInPickerArea.Success,
            "expected an enabled remove button on at least one live picker row");
        Assert.DoesNotContain("disabled", removeBtnInPickerArea.Value);
    }

    [Fact]
    public async Task ExpandSurface_sustain_wrapper_controls_are_present()
    {
        var client = await BuildClientAsync(BuildCatalogProjectableRule(out var ruleId));

        var html = await client.GetStringAsync($"/_test/policy-stack-expand?ruleId={ruleId}");

        // The predicate-level Sustain wrapper is a checkbox + duration field
        // that, when ticked, wraps the predicate in "<inner> for <duration>"
        // -- the parser already accepts that shape (ParseSustainUnit).
        Assert.Contains("data-predicate-sustain-toggle", html);
        Assert.Contains("data-predicate-sustain-duration", html);
    }

    [Fact]
    public async Task ExpandSurface_renders_row_template_with_catalog_defaults()
    {
        var client = await BuildClientAsync(BuildCatalogProjectableRule(out var ruleId));

        var html = await client.GetStringAsync($"/_test/policy-stack-expand?ruleId={ruleId}");

        // The template element exists, contains a row, and the row's facet
        // <select> is pre-populated from the catalog (first entry today is
        // "ua.bot_type" from picker-catalog.yaml).
        Assert.Contains("<template", html);
        Assert.Contains("sb-facet-picker-row-template", html);
        Assert.Matches(new Regex(
            @"<template[^>]*sb-facet-picker-row-template[^>]*>[\s\S]*?<select[^>]*picker_facet_0[^>]*>[\s\S]*?ua\.bot_type",
            RegexOptions.Singleline), html);
    }

    [Fact]
    public async Task ParseRoute_round_trips_a_single_term_the_picker_would_build()
    {
        var client = await BuildClientAsync();

        // Approximates what the JS serialiser produces for one AND-row:
        //   facet=ua.bot_type op=eq value=Scraper
        // -> textarea text:  "ua.bot_type = Scraper"
        var resp = await client.PostAsync("/_test/policy-stack-parse",
            new StringContent("ua.bot_type = Scraper", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"kind\":\"term\"", body);
        Assert.Contains("\"facet\":\"ua.bot_type\"", body);
        Assert.Contains("\"op\":\"eq\"", body);
    }

    [Fact]
    public async Task ParseRoute_accepts_AND_then_OR_shape_picker_serialises()
    {
        var client = await BuildClientAsync();

        // Approximates a two-row AND with one OR-row added on top:
        //   (ua.bot_type = Scraper and score.bot_probability >= 0.7) or geo.country = CN
        // The picker JS emits exactly this shape when ands.length > 0
        // AND ors.length > 0.
        var resp = await client.PostAsync("/_test/policy-stack-parse",
            new StringContent("(ua.bot_type = Scraper and score.bot_probability >= 0.7) or geo.country = CN",
                Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        // OR at the top level wraps an AND-of-Terms branch + a Term branch.
        Assert.Contains("\"kind\":\"or\"", body);
        Assert.Contains("\"kind\":\"and\"", body);
        Assert.Contains("\"facet\":\"geo.country\"", body);
    }

    [Fact]
    public async Task ParseRoute_accepts_Sustain_wrapped_predicate()
    {
        var client = await BuildClientAsync();

        // The Sustain-wrapper checkbox produces "<inner> for <duration>".
        // The parser surfaces it as a (currently-non-emitted-via-canonical-
        // serialiser) Sustain node, so we just assert it parses cleanly
        // (no 400). The chip renderer reads what the canonical serialiser
        // emits -- Sustain falls through to the textarea, which is the
        // documented graceful-degrade for this v1.
        var resp = await client.PostAsync("/_test/policy-stack-parse",
            new StringContent("ua.bot_type = Scraper for 30s", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- Helpers ----

    private async Task<HttpClient> BuildClientAsync(IPolicyRuleStore? storeOverride = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(PolicyEditComposerTests).Assembly)
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
        builder.Services.AddSingleton<IFacetPickerCatalog, FacetPickerCatalog>();
        builder.Services.AddSingleton<IFacetAutocompleteSource, FacetAutocompleteSource>();
        builder.Services.AddSingleton<FacetPillRenderer>();
        builder.Services.AddSingleton<Mostlylucid.BotDetection.Policies.Rules.PolicyIntentClassifier>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(Options.Create(new Mostlylucid.BotDetection.UI.Options.PolicyStackHitAtomOptions()));
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Atoms.PolicyStackHitAtom>();
        builder.Services.AddSingleton(Options.Create(new Mostlylucid.BotDetection.UI.Options.PostureClassifierOptions()));
        builder.Services.AddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyStackPostureClassifier>();
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

    /// <summary>
    ///     Build a one-rule fixture store whose predicate uses catalog
    ///     facets (<c>ua.bot_type</c>) so the picker actually renders live
    ///     rows -- not the empty-state placeholder. The seed YAML rules
    ///     mostly use uncurated facets (<c>bot.type</c> / <c>score.bot_probability</c>),
    ///     which deliberately drop out of <see cref="PolicyEditPresenter.BuildPickerRows"/>
    ///     and leave the picker block empty.
    /// </summary>
    private static IPolicyRuleStore BuildCatalogProjectableRule(out Guid ruleId)
    {
        ruleId = Guid.NewGuid();
        var rule = new PolicyRule(
            Id: ruleId,
            Scope: PolicyScope.Wildcard(),
            Priority: 100,
            Predicate: PredicateParser.Parse("ua.bot_type = Scraper"),
            Action: new PolicyAction.Block(),
            Mode: PolicyMode.Draft,
            Notes: string.Empty,
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());
        return new FixedRulePolicyRuleStore(rule);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            try { await app.DisposeAsync(); } catch { /* test cleanup */ }
        _apps.Clear();
    }
}

/// <summary>
///     Helper controller for the composer activation test. Adds
///     <c>/_test/policy-stack-expand</c> which renders
///     <c>_RuleCardExpand.cshtml</c> directly via the presenter so the
///     assertions can target the picker markup specifically (the sibling
///     <c>PolicyEditTestController</c> only renders the inner
///     <c>_EditRow</c>).
/// </summary>
[Route("/_test")]
public sealed class PolicyEditComposerTestController : Controller
{
    private readonly PolicyEditPresenter _editPresenter;

    public PolicyEditComposerTestController(PolicyEditPresenter editPresenter)
    {
        _editPresenter = editPresenter;
    }

    [HttpGet("policy-stack-expand")]
    public async Task<IActionResult> Expand(Guid ruleId)
    {
        var vm = await _editPresenter.BuildExpandForExistingRuleAsync(ruleId);
        if (vm is null) return NotFound();
        return PartialView("/Views/Shared/Components/SbPolicyStack/_RuleCardExpand.cshtml", vm);
    }
}
