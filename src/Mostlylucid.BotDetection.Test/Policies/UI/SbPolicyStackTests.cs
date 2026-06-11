using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.Policies.Telemetry;
using Mostlylucid.BotDetection.Test.Policies.Support;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Coverage for the SbPolicyStack view component + its presenter. The
///     real-render tests spin up a TestServer with a one-shot controller that
///     calls the view component via <c>ViewComponent</c> result -- that's
///     enough to drive the full Razor pipeline against the real partials.
///     The presenter-only tests verify the view-model shape directly without
///     the engine in the way.
/// </summary>
public sealed class SbPolicyStackTests : IAsyncDisposable
{
    private const string SeedPrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";
    private const string DomainAcme = "acme.com";
    private const string SubDocs = "docs.acme.com";
    private const string EpUpload = "GET /api/upload";

    private static readonly PolicyScope WildcardScope = PolicyScope.Wildcard();
    private static readonly PolicyScope DomainScope = PolicyScope.Domain(DomainAcme);
    private static readonly PolicyScope SubdomainScope = PolicyScope.Subdomain(DomainAcme, SubDocs);
    private static readonly PolicyScope EndpointScope = PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload);

    private readonly List<WebApplication> _apps = new();

    // -------- Real-render fan-out via TestServer --------

    [Fact]
    public async Task ViewComponent_renders_three_embed_shapes()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);

        var full = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);
        Assert.Contains("data-policy-stack-embed=\"full\"", full);
        Assert.Contains("data-tab=\"effective\"", full);
        Assert.Contains("data-tab=\"stack\"", full);

        var only = await GetHtmlAsync(client, WildcardScope, PolicyStackEmbed.EffectiveOnly);
        Assert.DoesNotContain("data-tab=\"stack\"", only);
        Assert.Contains("data-policy-stack-embed=\"effective-only\"", only);

        var badge = await GetHtmlAsync(client, DomainScope, PolicyStackEmbed.StatusBadge);
        Assert.Contains("rules effective here", badge);
        Assert.DoesNotContain("WHEN", badge);
        Assert.Contains("data-policy-stack-embed=\"status-badge\"", badge);
    }

    [Fact]
    public async Task RuleRow_renders_trigger_count_distribution_and_latency()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: true);

        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);

        // 18 matched / 18 total on the endpoint Block rule. (16 blocks won, 2 were overridden.)
        Assert.Contains("18/18", html);
        Assert.Contains("p50", html);
        Assert.Contains("p99", html);
        Assert.Contains("LIVE", html);
        Assert.Contains("verdict-error", html); // Block
    }

    [Fact]
    public async Task RuleRow_renders_predicate_chips_with_signal_tooltip()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);

        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);

        Assert.Contains("score.bot_probability", html);
        Assert.Contains("&gt;=", html); // HTML-encoded >=
        // Every chip carries a title= attribute even when the SignalCatalog
        // doesn't know the facet (seed-rule facets like "bot.type" are not
        // SignalKeys constants); the wire-up itself is what matters here.
        Assert.Matches(new Regex(@"<span class=""chip""[^>]*title="""), html);
    }

    [Fact]
    public async Task EffectiveTab_orders_most_specific_first_with_inherited_badge()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);

        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);

        var endpointIdx = html.IndexOf("ENDPOINT", StringComparison.Ordinal);
        var subdomainIdx = html.IndexOf("SUBDOMAIN", StringComparison.Ordinal);
        var domainIdx = html.IndexOf("DOMAIN", StringComparison.Ordinal);

        Assert.True(endpointIdx >= 0, "expected an ENDPOINT row");
        Assert.True(subdomainIdx > endpointIdx, "SUBDOMAIN must follow ENDPOINT");
        Assert.True(domainIdx > subdomainIdx, "DOMAIN must follow SUBDOMAIN");
        Assert.Contains("is-inherited", html);
    }

    [Fact]
    public async Task ScopeHash_is_stable_across_renders()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);

        var a = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);
        var b = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);

        var hashA = ExtractScopeHash(a);
        var hashB = ExtractScopeHash(b);

        Assert.False(string.IsNullOrEmpty(hashA), "hash A should be present");
        Assert.Equal(hashA, hashB);
    }

    // -------- StatusBadge fan-out guard (presenter-only) --------

    [Fact]
    public async Task StatusBadge_does_not_fan_out_aggregate_reads()
    {
        var resolver = await BuildResolverAsync();
        var tracker = new CallTrackingEffectivenessCache();
        var catalog = await BuildSignalCatalogAsync();
        var presenter = new PolicyStackPresenter(resolver, tracker, catalog, new PolicyConflictAnalyzer());

        var vm = await presenter.BuildAsync(
            scope: EndpointScope,
            embed: PolicyStackEmbed.StatusBadge,
            activeTab: "effective",
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false);

        Assert.Equal(0, tracker.GetManyCalls);
        Assert.True(vm.TotalEffectiveRules > 0,
            "StatusBadge must still surface the rule count without aggregate reads");
    }

    // -------- Presenter shape coverage (cheap, no TestServer) --------

    [Fact]
    public async Task Presenter_breadcrumb_walks_wildcard_to_endpoint_for_endpoint_scope()
    {
        var presenter = await BuildPresenterAsync();
        var vm = await presenter.BuildAsync(
            scope: EndpointScope,
            embed: PolicyStackEmbed.Full,
            activeTab: "effective",
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false);

        Assert.Equal(4, vm.BreadcrumbPath.Count);
        Assert.Null(vm.BreadcrumbPath[0].Host);
        Assert.IsType<HostScope.Domain>(vm.BreadcrumbPath[1].Host);
        Assert.IsType<HostScope.Subdomain>(vm.BreadcrumbPath[2].Host);
        Assert.IsType<HostScope.Endpoint>(vm.BreadcrumbPath[3].Host);
    }

    [Fact]
    public async Task Presenter_action_color_classes_follow_dashboard_palette()
    {
        var presenter = await BuildPresenterAsync();
        var vm = await presenter.BuildAsync(
            scope: EndpointScope,
            embed: PolicyStackEmbed.Full,
            activeTab: "effective",
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false);

        var endpointRow = vm.Rows.Single(r => r.SourcePill == "ENDPOINT");
        Assert.Equal("verdict-error", endpointRow.ActionColorClass); // Block

        var subdomainRow = vm.Rows.Single(r => r.SourcePill == "SUBDOMAIN");
        Assert.Equal("verdict-warning", subdomainRow.ActionColorClass); // Challenge

        var domainRow = vm.Rows.Single(r => r.SourcePill == "DOMAIN");
        Assert.Equal("verdict-success", domainRow.ActionColorClass); // Allow
    }

    [Fact]
    public async Task Presenter_empty_scope_returns_no_rows_and_no_throws()
    {
        // Empty rule store: the wildcard baseline seeds (allow-human +
        // block-confirmed-bot) embedded in Mostlylucid.BotDetection.dll would
        // otherwise inherit into any endpoint scope and break "no rows" here.
        var presenter = await BuildPresenterAsync(new EmptyPolicyRuleStore());
        var emptyScope = PolicyScope.Endpoint("unknown.example", "api.unknown.example", "GET /none");

        var vm = await presenter.BuildAsync(
            scope: emptyScope,
            embed: PolicyStackEmbed.Full,
            activeTab: "effective",
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false);

        Assert.Empty(vm.Rows);
        Assert.Equal(0, vm.TotalEffectiveRules);
        Assert.Null(vm.LatestEditAt);
    }

    [Fact]
    public async Task Presenter_distribution_line_renders_overridden_split()
    {
        var resolver = await BuildResolverAsync();
        var catalog = await BuildSignalCatalogAsync();
        var prewarm = new PrewarmedEffectivenessCache();
        var endpointRuleId = await GetEndpointRuleIdAsync(resolver);
        prewarm.Set(endpointRuleId, new PolicyDecisionAggregate(
            RuleId: endpointRuleId,
            Window: TimeSpan.FromHours(24),
            Matched: 18,
            TotalEvaluations: 20,
            WinDistribution: new Dictionary<string, int>
            {
                ["block"] = 16,
                ["allow"] = 2
            },
            P50LatencyMicros: 400,
            P99LatencyMicros: 2100,
            ComputedAt: DateTimeOffset.UtcNow));

        var presenter = new PolicyStackPresenter(resolver, prewarm, catalog, new PolicyConflictAnalyzer());
        var vm = await presenter.BuildAsync(
            scope: EndpointScope,
            embed: PolicyStackEmbed.Full,
            activeTab: "effective",
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false);

        var row = vm.Rows.Single(r => r.RuleId == endpointRuleId);
        Assert.Equal(18, row.TriggerCount);
        Assert.Equal(20, row.TotalEvaluations);
        Assert.Contains("block", row.DistributionLine);
        Assert.Contains("allow", row.DistributionLine);
        Assert.Equal("p50 0.4ms · p99 2.1ms", row.LatencyLine);
    }

    [Fact]
    public void ScopeHash_helper_is_deterministic()
    {
        var a = PolicyStackPresenter.ComputeScopeHash(EndpointScope);
        var b = PolicyStackPresenter.ComputeScopeHash(EndpointScope);
        var c = PolicyStackPresenter.ComputeScopeHash(WildcardScope);

        Assert.Equal(16, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // -------- Stack-tab coverage (B3) --------

    [Fact]
    public async Task Stack_tab_groups_by_scope_and_orders_ancestor_first()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full, activeTab: "stack");

        var domainIdx = html.IndexOf("DOMAIN", StringComparison.Ordinal);
        var subdomainIdx = html.IndexOf("SUBDOMAIN", StringComparison.Ordinal);
        var endpointIdx = html.IndexOf("ENDPOINT", StringComparison.Ordinal);

        Assert.True(domainIdx >= 0 && subdomainIdx > domainIdx && endpointIdx > subdomainIdx,
            "Stack tab orders DOMAIN, then SUBDOMAIN, then ENDPOINT (ancestor first)");
    }

    [Fact]
    public async Task Stack_tab_renders_conflict_callout_when_endpoint_overrides_domain_allow()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full, activeTab: "stack");

        Assert.Contains("sb-policy-stack-conflict", html);
        Assert.Contains("overrides", html);

        // The conflict is attributed to the ENDPOINT group (the override owner).
        var endpointGroupStart = html.IndexOf("data-scope-kind=\"endpoint\"", StringComparison.Ordinal);
        Assert.True(endpointGroupStart > 0, "expected an endpoint scope group");
        var conflictIdx = html.IndexOf("sb-policy-stack-conflict", endpointGroupStart, StringComparison.Ordinal);
        Assert.True(conflictIdx > endpointGroupStart,
            "conflict callout must render INSIDE the endpoint scope group");
    }

    [Fact]
    public async Task Stack_tab_does_not_run_analyzer_on_effective_tab_render()
    {
        var resolver = await BuildResolverAsync();
        var catalog = await BuildSignalCatalogAsync();
        var cache = new EmptyEffectivenessCache();
        var counting = new CountingConflictAnalyzer();
        var presenter = new PolicyStackPresenter(resolver, cache, catalog, counting);

        await presenter.BuildAsync(EndpointScope, PolicyStackEmbed.Full,
            activeTab: "effective",
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false);
        Assert.Equal(0, counting.Calls);

        await presenter.BuildAsync(EndpointScope, PolicyStackEmbed.Full,
            activeTab: "stack",
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false);
        Assert.Equal(1, counting.Calls);
    }

    [Fact]
    public async Task EffectiveOnly_embed_does_not_render_stack_groupings_even_with_active_tab_set()
    {
        // EffectiveOnly never shows the Stack tab; activeTab is ignored.
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.EffectiveOnly, activeTab: "stack");

        Assert.DoesNotContain("sb-policy-stack-scope-group", html);
        Assert.DoesNotContain("sb-policy-stack-conflict", html);
    }

    [Fact]
    public void ConflictAnalyzer_detects_block_overriding_allow_baseline()
    {
        var rules = SeedThreeRulesEndpointBlockSubdomainChallengeDomainAllow();
        var conflicts = new PolicyConflictAnalyzer().Analyse(rules);

        Assert.NotEmpty(conflicts);
        Assert.Contains(conflicts, c => c.Severity == "warning"
            && c.OwnerScope.Host is HostScope.Endpoint
            && c.OverriddenScope.Host is HostScope.Domain);
    }

    [Fact]
    public void ConflictAnalyzer_returns_empty_when_no_action_conflicts()
    {
        var rules = new List<EffectiveRule>
        {
            // Two Allow rules at different scopes -- no conflict.
            new EffectiveRule(
                MakeRule("is_human = true", new PolicyAction.Allow(), priority: 100,
                    scope: PolicyScope.Domain(DomainAcme)),
                PolicyScope.Domain(DomainAcme),
                IsInherited: false),
            new EffectiveRule(
                MakeRule("ua.family = chrome", new PolicyAction.Allow(), priority: 50,
                    scope: PolicyScope.Subdomain(DomainAcme, SubDocs)),
                PolicyScope.Subdomain(DomainAcme, SubDocs),
                IsInherited: false)
        };

        var conflicts = new PolicyConflictAnalyzer().Analyse(rules);
        Assert.Empty(conflicts);
    }

    // -------- B4: Filter + sort + aggregate strip --------

    [Theory]
    [InlineData("@modified",        true,  null, null, null, false, false, false, false, null)]
    [InlineData("@since:7d",        false, "7d", null, null, false, false, false, false, null)]
    [InlineData("@scope:endpoint",  false, null, "endpoint", null, false, false, false, false, null)]
    [InlineData("@action:block",    false, null, null, "block", false, false, false, false, null)]
    [InlineData("@observe",         false, null, null, null, true, false, false, false, null)]
    [InlineData("@hot",             false, null, null, null, false, true, false, false, null)]
    [InlineData("@slow",            false, null, null, null, false, false, true, false, null)]
    [InlineData("@no-hits",         false, null, null, null, false, false, false, true, null)]
    [InlineData("scraper",          false, null, null, null, false, false, false, false, "scraper")]
    [InlineData("@modified scraper", true, null, null, null, false, false, false, false, "scraper")]
    [InlineData("@unknown @modified", true, null, null, null, false, false, false, false, null)]
    public void Filter_parses_tokens(string input, bool modified, string? since, string? scope, string? action,
        bool observe, bool hot, bool slow, bool noHits, string? freeText)
    {
        var f = PolicyStackFilter.Parse(input);
        Assert.Equal(modified, f.OnlyModified);
        Assert.Equal(since is null ? null : TimeSpanParse(since), f.EditedSince);
        Assert.Equal(scope, f.OnlyScope);
        Assert.Equal(action, f.OnlyAction);
        Assert.Equal(observe, f.OnlyObserve);
        Assert.Equal(hot, f.HotOnly);
        Assert.Equal(slow, f.SlowOnly);
        Assert.Equal(noHits, f.NoHitsOnly);
        Assert.Equal(freeText, f.FreeText);
    }

    [Fact]
    public void Filter_empty_input_is_inactive()
    {
        Assert.False(PolicyStackFilter.Parse(null).IsActive);
        Assert.False(PolicyStackFilter.Parse("").IsActive);
        Assert.False(PolicyStackFilter.Parse("   ").IsActive);
        Assert.Same(PolicyStackFilter.Empty, PolicyStackFilter.Parse(null));
    }

    [Fact]
    public void Filter_canonical_round_trip()
    {
        var f1 = PolicyStackFilter.Parse("@modified @since:7d @action:block scraper");
        var canonical = f1.ToCanonicalString();
        var f2 = PolicyStackFilter.Parse(canonical);
        Assert.Equal(f1, f2);
    }

    [Fact]
    public void Filter_unknown_tokens_do_not_throw_and_are_silently_ignored()
    {
        // Forward-compatibility: future @-tokens must not break old URLs.
        var f = PolicyStackFilter.Parse("@something-new:42 @also-new @modified");
        Assert.True(f.OnlyModified);
        Assert.False(f.IsActive == false, "modified token must still activate the filter");
        Assert.Null(f.FreeText);
    }

    [Fact]
    public void Sort_parse_handles_known_keys_and_falls_back_to_default()
    {
        Assert.Equal(PolicyStackSort.Default, PolicyStackSort.Parse(null, null));
        Assert.Equal(PolicyStackSort.Default, PolicyStackSort.Parse("bogus", "asc"));

        var triggers = PolicyStackSort.Parse("triggers", "desc");
        Assert.Equal(PolicyStackSortKey.Triggers, triggers.Key);
        Assert.Equal(PolicyStackSortDir.Desc, triggers.Direction);
        Assert.Equal("triggers", triggers.KeyToken);
        Assert.Equal("desc", triggers.DirToken);

        var p99 = PolicyStackSort.Parse("p99", null);
        Assert.Equal(PolicyStackSortKey.P99Latency, p99.Key);
        Assert.Equal(PolicyStackSortDir.Asc, p99.Direction);
    }

    [Fact]
    public async Task Filter_at_no_hits_drops_rules_with_triggers()
    {
        // Endpoint rule has 18 triggers after prewarm; @no-hits MUST drop it.
        // The wildcard baseline seeds (allow-human + block-confirmed-bot) are
        // suppressed for this assertion -- the block-confirmed-bot rule is a
        // Block (verdict-error) with zero hits and would survive @no-hits,
        // defeating the "no verdict-error" check.
        var client = await BuildClientAsync(prewarmEndpointHits: true,
            ruleStoreOverride: new LegacySeedOnlyPolicyRuleStore());
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            filterExpression: "@no-hits");

        // The block-scraper rule (verdict-error) has triggers in the seeded cache;
        // it MUST be filtered out. The DOMAIN Allow rule still has 0 hits.
        Assert.DoesNotContain("verdict-error", html);
    }

    [Fact]
    public async Task Filter_empty_match_renders_filter_empty_state_message()
    {
        // No effective rules have wildcard source scope -- @scope:wildcard
        // must produce the dedicated empty-filter message, not the generic one.
        // The wildcard baseline seeds are suppressed so @scope:wildcard has
        // nothing to match against (the legacy three-rule corpus has no
        // wildcard-scope rules of its own).
        var client = await BuildClientAsync(prewarmEndpointHits: false,
            ruleStoreOverride: new LegacySeedOnlyPolicyRuleStore());
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            filterExpression: "@scope:wildcard");
        Assert.Contains("No rules match the current filter", html);
    }

    [Fact]
    public async Task Sort_by_triggers_descending_reorders_visible_rules()
    {
        // Pre-warm so the endpoint rule has triggers while domain / subdomain do not.
        var client = await BuildClientAsync(prewarmEndpointHits: true);

        var htmlAsc = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            sortKey: "triggers", sortDir: "asc");
        var htmlDesc = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            sortKey: "triggers", sortDir: "desc");

        var ascOrder = ExtractRuleIdOrder(htmlAsc);
        var descOrder = ExtractRuleIdOrder(htmlDesc);

        Assert.NotEmpty(ascOrder);
        Assert.NotEqual(ascOrder, descOrder);
    }

    [Fact]
    public async Task Aggregate_strip_shows_visible_counts()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);
        Assert.Contains("rules effective here", html);
        Assert.Contains("triggered in 24h", html);
    }

    [Fact]
    public async Task Aggregate_strip_no_hits_chip_applies_no_hits_filter()
    {
        // With no prewarm, every rule has 0 hits so the chip MUST render. We
        // assert the data attribute the chip exposes for B6 to bind to.
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);
        Assert.Contains("data-filter-apply=\"@no-hits\"", html);
    }

    [Fact]
    public async Task Effective_tab_renders_five_sortable_headers()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);
        Assert.Contains("data-sort-key=\"priority\"", html);
        Assert.Contains("data-sort-key=\"triggers\"", html);
        Assert.Contains("data-sort-key=\"p99\"", html);
        Assert.Contains("data-sort-key=\"blocked-pct\"", html);
        Assert.Contains("data-sort-key=\"edited\"", html);
    }

    [Fact]
    public async Task EffectiveOnly_embed_does_not_render_filter_bar_or_strip()
    {
        // EffectiveOnly is the tight-pane shape: no chrome at all. The filter
        // bar + strip live in _Full so they're absent here by construction.
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.EffectiveOnly);
        Assert.DoesNotContain("sb-policy-stack-filter-bar", html);
        Assert.DoesNotContain("sb-policy-stack-aggregate-strip", html);
    }

    [Fact]
    public async Task Filter_bar_round_trips_active_filter_in_input_value()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            filterExpression: "@modified @scope:endpoint");

        // The input MUST surface the canonical string so a refresh preserves
        // the filter; the canonical order is fixed by ToCanonicalString.
        Assert.Contains("value=\"@modified @scope:endpoint\"", html);
        Assert.Contains("data-active-filters=\"true\"", html);
    }

    [Fact]
    public async Task Sort_active_column_renders_indicator_arrow()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            sortKey: "triggers", sortDir: "asc");

        // Razor's default HTML encoder emits non-ASCII chars as numeric entities,
        // so "↑" arrives as "&#8593;". Either form means the indicator rendered.
        Assert.True(
            html.Contains("Triggers ↑", StringComparison.Ordinal)
            || html.Contains("Triggers &#x2191;", StringComparison.Ordinal)
            || html.Contains("Triggers &#8593;", StringComparison.Ordinal),
            "Expected Triggers column to render the ascending arrow indicator");
        Assert.Contains("is-active", html);
    }

    [Fact]
    public async Task Filter_chips_show_dismissable_tokens_for_active_filter()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            filterExpression: "@modified");

        // Chip carries the canonical token so B6 can compose a remove-action.
        Assert.Contains("data-filter-chip=\"@modified\"", html);
        Assert.Contains("Modified from default", html);
    }

    [Fact]
    public async Task Aggregate_strip_total_requests_sums_visible_evaluations()
    {
        // With prewarm: endpoint rule sees 18 matches across 18 evaluations.
        var client = await BuildClientAsync(prewarmEndpointHits: true);

        // Scoped to the endpoint rule via @scope:endpoint -> only one row.
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            filterExpression: "@scope:endpoint");

        // Strip must report 18 requests and 1 rule visible.
        Assert.Contains("<strong>18</strong> requests", html);
        Assert.Contains("<strong>1</strong> rules effective here", html);
        Assert.Contains("<strong>1</strong> triggered in 24h", html);
    }

    [Fact]
    public void Window_format_picks_canonical_label()
    {
        Assert.Equal("24h", PolicyStackWindowFormat.ForLabel(TimeSpan.FromHours(24)));
        Assert.Equal("24h", PolicyStackWindowFormat.ForLabel(TimeSpan.FromHours(1)));
        Assert.Equal("7d", PolicyStackWindowFormat.ForLabel(TimeSpan.FromDays(7)));
        Assert.Equal("30d", PolicyStackWindowFormat.ForLabel(TimeSpan.FromDays(30)));
    }

    // -------- B5: Explainer panel + decision trace --------

    [Fact]
    public async Task RuleRow_explain_button_has_htmx_explain_attributes()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);

        // The ⓘ button must carry the HTMX wiring the panel expects: a hx-get
        // pointing at /dashboard/policystack/explain with a scope + focusRule,
        // plus the swap target. Razor encodes & as &amp; inside attributes so
        // the regex tolerates either form.
        Assert.Matches(
            new Regex("hx-get=\"/dashboard/policystack/explain\\?scope=[^\"]+(&|&amp;)focusRule=[0-9a-fA-F\\-]+\""),
            html);
        Assert.Contains("hx-target=\"#sb-policy-stack-explainer\"", html);
        Assert.Contains("hx-swap=\"outerHTML\"", html);
    }

    [Fact]
    public async Task Explainer_panel_renders_picker_when_not_locked_and_no_fingerprint()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);

        // Full embed always paints the panel host so HTMX swaps land. With no
        // fingerprint the picker is the visible affordance.
        Assert.Contains("id=\"sb-policy-stack-explainer\"", html);
        Assert.Contains("sb-policy-stack-explainer-picker", html);
        Assert.Contains("data-locked=\"false\"", html);
        // Placeholder copy renders when there's nothing to replay.
        Assert.Contains("Pick a recent fingerprint", html);
    }

    [Fact]
    public async Task Explainer_panel_renders_locked_header_when_locked()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            explainerFingerprint: "fp-X", lockedFingerprint: true);

        Assert.Contains("data-locked=\"true\"", html);
        Assert.DoesNotContain("sb-policy-stack-explainer-picker", html);
        Assert.Contains("Why did this happen?", html);
    }

    [Fact]
    public async Task EffectiveOnly_embed_only_renders_explainer_when_locked()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);

        // Without locking, EffectiveOnly stays compact -- no panel host.
        var withoutLock = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.EffectiveOnly);
        Assert.DoesNotContain("id=\"sb-policy-stack-explainer\"", withoutLock);

        // With locking, the panel host appears (this is the fingerprint detail
        // page embed used by future call sites).
        var withLock = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.EffectiveOnly,
            explainerFingerprint: "fp-X", lockedFingerprint: true);
        Assert.Contains("id=\"sb-policy-stack-explainer\"", withLock);
        Assert.Contains("data-locked=\"true\"", withLock);
    }

    [Fact]
    public async Task Explainer_panel_renders_decision_trace_for_known_fingerprint()
    {
        // Seed a decision cycle on a real endpoint rule so the explainer has
        // something to replay. The decision log is wired through DI; we grab
        // the same singleton the presenter consumes.
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        await SeedDecisionsForFingerprintAsync(fingerprint: "fp-scrape-test");

        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            explainerFingerprint: "fp-scrape-test", lockedFingerprint: true);

        Assert.Contains("sb-policy-stack-decision-trace", html);
        Assert.Contains("WOULD APPLY", html);
    }

    [Fact]
    public async Task Explainer_panel_renders_empty_state_when_fingerprint_unknown()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            explainerFingerprint: "fp-never-seen");

        Assert.Contains("Pick a recent fingerprint", html);
        Assert.DoesNotContain("WOULD APPLY", html);
    }

    [Fact]
    public async Task Explainer_presenter_returns_placeholder_when_fingerprint_empty()
    {
        var resolver = await BuildResolverAsync();
        var catalog = await BuildSignalCatalogAsync();
        var decisionLog = new InMemoryPolicyDecisionLog();
        var explainer = new PolicyExplainerPresenter(resolver, decisionLog, catalog);

        var vm = await explainer.BuildAsync(EndpointScope, fingerprint: string.Empty, locked: false);

        Assert.Empty(vm.Rules);
        Assert.Null(vm.WinnerRuleId);
        Assert.Equal(string.Empty, vm.FingerprintId);
        Assert.False(vm.Locked);
    }

    [Fact]
    public async Task Explainer_presenter_marks_winner_rule_with_is_winner()
    {
        var resolver = await BuildResolverAsync();
        var catalog = await BuildSignalCatalogAsync();
        var decisionLog = new InMemoryPolicyDecisionLog();
        var explainer = new PolicyExplainerPresenter(resolver, decisionLog, catalog);

        var endpointRuleId = await GetEndpointRuleIdAsync(resolver);
        var observedAt = DateTimeOffset.UtcNow;
        const string fp = "fp-winner-test";

        // The cycle's winner is the endpoint Block rule. The other rules sat
        // in the cycle as zero-latency skipped rows (an earlier rule won).
        var effective = await resolver.EffectiveAsync(EndpointScope);
        foreach (var entry in effective)
        {
            var isWinner = entry.Rule.Id == endpointRuleId;
            await decisionLog.AppendAsync(new PolicyDecision(
                RuleId: entry.Rule.Id,
                WinnerRuleId: endpointRuleId,
                Matched: isWinner,
                RequestFingerprint: fp,
                Action: entry.Rule.Action,
                Mode: entry.Rule.Mode,
                EvalLatencyTicks: isWinner ? 5000 : 0,
                ObservedAt: observedAt));
        }

        var vm = await explainer.BuildAsync(EndpointScope, fingerprint: fp, locked: false);

        Assert.NotEmpty(vm.Rules);
        Assert.Equal(endpointRuleId, vm.WinnerRuleId);
        var winnerRow = vm.Rules.Single(r => r.RuleId == endpointRuleId);
        Assert.True(winnerRow.IsWinner);
        Assert.True(winnerRow.WasEvaluated);

        // Non-winners that the cycle marked as zero-latency skips render with
        // WasEvaluated=false + the canonical skip-reason string.
        var skipped = vm.Rules.Where(r => r.RuleId != endpointRuleId).ToList();
        Assert.NotEmpty(skipped);
        Assert.All(skipped, r =>
        {
            Assert.False(r.IsWinner);
            Assert.False(r.WasEvaluated);
            Assert.NotNull(r.SkipReason);
            Assert.Contains("earlier rule won", r.SkipReason!);
        });
    }

    // -------- B6: SignalR-driven live update beacon + HTMX OOB swap envelope --------

    [Fact]
    public async Task Full_embed_section_emits_ancestor_hashes_and_rows_url_data_attributes()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);

        // Four ancestors at Endpoint scope: Wildcard, Domain, Subdomain, Endpoint.
        // The data-* attribute is a comma-separated list of 16-hex hashes.
        Assert.Matches(
            new Regex("data-policy-stack-ancestors=\"[0-9a-f]{16},[0-9a-f]{16},[0-9a-f]{16},[0-9a-f]{16}\""),
            html);
        Assert.Matches(
            new Regex("data-policy-stack-rows-url=\"/dashboard/policystack/rows[^\"]*\""),
            html);
        Assert.Matches(new Regex("id=\"sb-policy-stack-rows-[0-9a-f]{16}\""), html);
    }

    [Fact]
    public async Task EffectiveOnly_embed_emits_envelope_div_with_scope_hash_id()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, WildcardScope, PolicyStackEmbed.EffectiveOnly);

        // EffectiveOnly only has Wildcard in its ancestor list (one entry).
        Assert.Matches(new Regex("data-policy-stack-ancestors=\"[0-9a-f]{16}\""), html);
        Assert.Matches(new Regex("data-policy-stack-rows-url=\"[^\"]*tab=effective\""), html);
        Assert.Matches(new Regex("id=\"sb-policy-stack-rows-[0-9a-f]{16}\""), html);
    }

    [Fact]
    public async Task StatusBadge_emits_ancestor_hashes_for_badge_tab()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, DomainScope, PolicyStackEmbed.StatusBadge);

        // Domain scope -> two ancestor hashes (Wildcard, Domain).
        Assert.Matches(new Regex("data-policy-stack-ancestors=\"[0-9a-f]{16},[0-9a-f]{16}\""), html);
        Assert.Matches(new Regex("data-policy-stack-rows-url=\"[^\"]*tab=badge\""), html);
    }

    [Fact]
    public void Ancestor_hashes_at_endpoint_scope_match_breadcrumb_path_hashes()
    {
        var hashes = Mostlylucid.BotDetection.UI.Policies.PolicyScopeKeys.AncestorHashes(EndpointScope);

        Assert.Equal(4, hashes.Count);
        // Re-derive each hash from the canonical scope key and confirm
        // membership; this is what the broadcaster + the client glue agree on.
        Assert.Contains(Mostlylucid.BotDetection.UI.Policies.PolicyScopeKeys.ScopeHash(PolicyScope.Wildcard()), hashes);
        Assert.Contains(Mostlylucid.BotDetection.UI.Policies.PolicyScopeKeys.ScopeHash(PolicyScope.Domain(DomainAcme)), hashes);
        Assert.Contains(Mostlylucid.BotDetection.UI.Policies.PolicyScopeKeys.ScopeHash(PolicyScope.Subdomain(DomainAcme, SubDocs)), hashes);
        Assert.Contains(Mostlylucid.BotDetection.UI.Policies.PolicyScopeKeys.ScopeHash(PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload)), hashes);
    }

    /// <summary>
    ///     Append a full decision cycle for <paramref name="fingerprint"/> --
    ///     one row per effective rule, with the endpoint Block rule as the
    ///     winner. The InMemory log is exposed directly (no drainer), so the
    ///     explainer sees the rows on its next read.
    /// </summary>
    private async Task SeedDecisionsForFingerprintAsync(string fingerprint)
    {
        // Grab the most recently built TestServer's services. Every test that
        // calls this seeds AFTER BuildClientAsync, so _apps.Last() is the
        // right host.
        var app = _apps[^1];
        var resolver = app.Services.GetRequiredService<IPolicyResolver>();
        var decisionLog = app.Services.GetRequiredService<IPolicyDecisionLog>();
        var endpointRuleId = await GetEndpointRuleIdAsync(resolver);
        var observedAt = DateTimeOffset.UtcNow;

        var effective = await resolver.EffectiveAsync(EndpointScope);
        foreach (var entry in effective)
        {
            var isWinner = entry.Rule.Id == endpointRuleId;
            await decisionLog.AppendAsync(new PolicyDecision(
                RuleId: entry.Rule.Id,
                WinnerRuleId: endpointRuleId,
                Matched: isWinner,
                RequestFingerprint: fingerprint,
                Action: entry.Rule.Action,
                Mode: entry.Rule.Mode,
                EvalLatencyTicks: isWinner ? 5000 : 0,
                ObservedAt: observedAt));
        }
    }

    private static TimeSpan TimeSpanParse(string value)
    {
        // Mirrors PolicyStackFilter's @since: parser; the test theory uses this
        // to assert the round-trip without exposing the private helper.
        var unit = value[^1];
        var n = int.Parse(value[..^1], CultureInfo.InvariantCulture);
        return unit switch
        {
            'h' or 'H' => TimeSpan.FromHours(n),
            'd' or 'D' => TimeSpan.FromDays(n),
            _ => throw new FormatException($"unknown since-unit '{unit}'")
        };
    }

    private static IReadOnlyList<string> ExtractRuleIdOrder(string html)
    {
        // data-rule-id appears once per _RuleRow. The match order in the
        // rendered HTML reflects the visible row order, which is what the
        // sort test asserts against.
        var ids = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(html, @"data-rule-id=""(?<id>[0-9a-fA-F\-]+)"""))
        {
            ids.Add(m.Groups["id"].Value);
        }
        return ids;
    }

    // -------- B3 helpers --------

    private static PolicyRule MakeRule(string predicate, PolicyAction action, int priority, PolicyScope scope) =>
        new(
            Id: Guid.NewGuid(),
            Scope: scope,
            Priority: priority,
            Predicate: PredicateParser.Parse(predicate),
            Action: action,
            Mode: PolicyMode.Live,
            Notes: string.Empty,
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());

    private static IReadOnlyList<EffectiveRule> SeedThreeRulesEndpointBlockSubdomainChallengeDomainAllow()
    {
        // Mirrors the seed YAML triple (most-specific-first, the order the
        // resolver hands them to us).
        var endpoint = MakeRule(
            "bot.type in (scraper, crawler) and score.bot_probability >= 0.7",
            new PolicyAction.Block(), priority: 10,
            scope: PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload));
        var subdomain = MakeRule(
            "geo.country in (CN, RU)",
            new PolicyAction.Challenge("turnstile"), priority: 50,
            scope: PolicyScope.Subdomain(DomainAcme, SubDocs));
        var domain = MakeRule(
            "is_human = true",
            new PolicyAction.Allow(), priority: 100,
            scope: PolicyScope.Domain(DomainAcme));

        return new[]
        {
            new EffectiveRule(endpoint, endpoint.Scope, IsInherited: false),
            new EffectiveRule(subdomain, subdomain.Scope, IsInherited: true),
            new EffectiveRule(domain, domain.Scope, IsInherited: true)
        };
    }

    /// <summary>Counts <see cref="PolicyConflictAnalyzer.Analyse"/> calls.</summary>
    private sealed class CountingConflictAnalyzer : PolicyConflictAnalyzer
    {
        public int Calls;
        public override IReadOnlyList<PolicyConflictViewModel> Analyse(IReadOnlyList<EffectiveRule> effectiveRules)
        {
            Interlocked.Increment(ref Calls);
            return base.Analyse(effectiveRules);
        }
    }

    // -------- C7: Drag-drop reorder + source-pill navigation --------

    [Fact]
    public async Task DragHandle_renders_only_when_canEdit_and_not_inherited()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);

        // Without canEdit: no handle anywhere.
        var htmlNoEdit = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            canEdit: false);
        Assert.DoesNotContain("sb-policy-stack-drag-handle", htmlNoEdit);
        Assert.DoesNotContain("data-policy-stack-drag-handle", htmlNoEdit);

        // With canEdit: the ENDPOINT row (non-inherited at this scope) MUST
        // render the handle; SUBDOMAIN/DOMAIN rows (inherited) MUST NOT.
        var htmlEdit = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            canEdit: true);
        Assert.Contains("sb-policy-stack-drag-handle", htmlEdit);
        Assert.Contains("data-policy-stack-drag-handle", htmlEdit);

        // There must be exactly one handle for the three effective rows
        // because only one of them (the endpoint rule) sits at the queried
        // scope. The other two are inherited and have no handle.
        var handleCount = new Regex("data-policy-stack-drag-handle")
            .Matches(htmlEdit).Count;
        Assert.Equal(1, handleCount);
    }

    [Fact]
    public async Task SourcePill_carries_href_when_inherited()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);

        // The SUBDOMAIN row at the endpoint scope is inherited; its source
        // pill must carry data-source-pill-href so the JS can navigate to
        // the owning scope. The DOMAIN row is also inherited.
        Assert.Contains("data-source-pill-href=\"/dashboard/policies?scope=subdomain", html);
        Assert.Contains("data-source-pill-href=\"/dashboard/policies?scope=domain", html);
    }

    [Fact]
    public async Task SourcePill_omits_href_when_not_inherited()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);

        // Wildcard scope from the seed YAML actually has no rules attached
        // directly; we render at the DOMAIN scope where its own rule lives
        // and assert that the DOMAIN pill there is NOT inherited and does
        // NOT carry the href. The wildcard/global rows at the same level
        // would have no href either.
        var html = await GetHtmlAsync(client, DomainScope, PolicyStackEmbed.Full);

        // The DOMAIN row at the domain scope is the rule's OWN scope. Find
        // the article tag for that row and confirm it lacks the href.
        var domainRowMatch = Regex.Match(html,
            "<article[^>]*data-source-pill=\"DOMAIN\"[\\s\\S]*?</article>");
        Assert.True(domainRowMatch.Success, "expected a DOMAIN row in the rendered HTML");
        Assert.DoesNotContain("data-source-pill-href", domainRowMatch.Value);
    }

    [Fact]
    public async Task EffectiveTab_carries_reorder_scope_data_attribute()
    {
        var client = await BuildClientAsync(prewarmEndpointHits: false);
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);

        // The reorder JS keys off data-policy-stack-reorder-scope on the <ul>;
        // PolicyScopeUrl.Encode for an endpoint scope produces the canonical
        // "endpoint|<domain>|<sub>|<template>" form.
        var encoded = Mostlylucid.BotDetection.UI.Services.PolicyScopeUrl.Encode(EndpointScope);
        Assert.Contains($"data-policy-stack-reorder-scope=\"{System.Net.WebUtility.HtmlEncode(encoded)}\"", html);

        // The attribute MUST live on the rule list (sb-policy-stack-rows),
        // not on a stray container, so the JS can scope drag events to the
        // correct list.
        Assert.Matches(
            new Regex("<ul[^>]*class=\"sb-policy-stack-rows\"[^>]*data-policy-stack-reorder-scope="),
            html);
    }

    // -------- F3: HideRuleList flag (policies-page double-render fix) --------

    [Fact]
    public async Task HideRuleList_true_suppresses_rule_list_but_keeps_aggregate_strip_filter_bar_explainer()
    {
        // /dashboard/policies renders the T4 template-grouped block ABOVE
        // SbPolicyStack and would print every rule twice if SbPolicyStack
        // also rendered its Effective tab. hideRuleList=true skips just
        // the rule-list section; the filter bar, aggregate strip, and
        // explainer panel still render so the tools surface stays usable.
        var client = await BuildClientAsync(prewarmEndpointHits: false);

        var hidden = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            hideRuleList: true);
        var visible = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full,
            hideRuleList: false);

        // Rule list (the row envelope + the per-row article) MUST be gone.
        Assert.DoesNotContain("sb-policy-stack-rows-envelope", hidden);
        Assert.DoesNotContain("data-rule-id=", hidden);

        // Sanity: the same render with the flag off DOES contain them so
        // we're not asserting against a missing partial.
        Assert.Contains("sb-policy-stack-rows-envelope", visible);
        Assert.Contains("data-rule-id=", visible);

        // Filter bar + aggregate strip + explainer panel STILL render --
        // the page wants those surfaces; only the rule list moves up to
        // the T4 template-grouped block above the component.
        Assert.Contains("sb-policy-stack-filter-bar", hidden);
        Assert.Contains("sb-policy-stack-aggregate-strip", hidden);
        Assert.Contains("id=\"sb-policy-stack-explainer\"", hidden);

        // The header / breadcrumb / tabs survive too -- the operator can
        // still flip Effective <-> Stack from the page chrome.
        Assert.Contains("data-policy-stack-embed=\"full\"", hidden);
        Assert.Contains("data-tab=\"effective\"", hidden);
        Assert.Contains("data-tab=\"stack\"", hidden);
    }

    [Fact]
    public async Task HideRuleList_defaults_to_false_for_all_other_call_sites()
    {
        // Other SbPolicyStack call sites (overview status badge, signature
        // detail, endpoint detail, etc.) MUST keep rendering rules. This
        // guards the "default false" promise on the new flag.
        var client = await BuildClientAsync(prewarmEndpointHits: false);

        // No hideRuleList query param -> presenter sees default false ->
        // rule list renders as before.
        var html = await GetHtmlAsync(client, EndpointScope, PolicyStackEmbed.Full);
        Assert.Contains("sb-policy-stack-rows-envelope", html);
        Assert.Contains("data-rule-id=", html);
    }

    [Fact]
    public void ReorderJs_script_is_emitted_alongside_realtime_script()
    {
        // Drive the SbLiveUpdates tag helper directly and confirm BOTH the
        // realtime beacon (B6) and the new reorder script (C7) appear in
        // the rendered output. The TagHelper carries the canonical asset
        // path constants; this guards against future refactors silently
        // dropping the reorder include.
        var httpAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        var tag = new Mostlylucid.BotDetection.UI.TagHelpers.SbLiveUpdatesTagHelper(
            httpAccessor, options: null);
        var context = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContext(
            tagName: "sb-live-updates",
            allAttributes: new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList(),
            items: new Dictionary<object, object>(),
            uniqueId: "test");
        var output = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperOutput(
            tagName: "sb-live-updates",
            attributes: new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList(),
            getChildContentAsync: (_, _) =>
                Task.FromResult<Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContent>(
                    new Microsoft.AspNetCore.Razor.TagHelpers.DefaultTagHelperContent()));

        tag.Process(context, output);

        using var sw = new StringWriter();
        output.Content.WriteTo(sw, System.Text.Encodings.Web.HtmlEncoder.Default);
        var html = sw.ToString();

        Assert.Contains("policy-stack-realtime.js", html);
        Assert.Contains("policy-stack-edit.js", html);
        Assert.Contains("policy-stack-reorder.js", html);
    }

    // -------- Render helpers --------

    private Task<HttpClient> BuildClientAsync(bool prewarmEndpointHits)
        => BuildClientAsync(prewarmEndpointHits, ruleStoreOverride: null);

    private async Task<HttpClient> BuildClientAsync(bool prewarmEndpointHits, IPolicyRuleStore? ruleStoreOverride)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // View components need the MVC + Razor pipelines. AddApplicationPart is
        // required because the test controller lives in this test assembly, and
        // the view component + Razor partials live in the UI assembly; default
        // ApplicationParts scanning misses both in the TestServer setup.
        builder.Services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(SbPolicyStackTests).Assembly)
            .AddApplicationPart(typeof(SbPolicyStackViewComponent).Assembly);

        // Mirror the production registrations from
        // StyloBotDashboardServiceExtensions.AddStyloBotDashboard so we hit the
        // real DI path.
        builder.Services.AddSingleton<ISignalCatalog>(_ =>
        {
            var asm = typeof(Mostlylucid.BotDetection.Models.SignalKeys).Assembly;
#pragma warning disable IL2026
            return SignalCatalog.LoadAsync(asm).GetAwaiter().GetResult();
#pragma warning restore IL2026
        });
        builder.Services.AddSingleton<IPolicyRuleStore>(_ =>
        {
            if (ruleStoreOverride is not null)
            {
                // Test-supplied stores still need their initial load before
                // the resolver pulls rules; the override path mirrors the
                // production behaviour (boot-time InitializeAsync).
                ruleStoreOverride.InitializeAsync().GetAwaiter().GetResult();
                return ruleStoreOverride;
            }
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

        // Pre-warm the effectiveness cache directly (without the hosted-service
        // shim) so the trigger-count test has something deterministic to assert.
        if (prewarmEndpointHits)
        {
            var resolver = app.Services.GetRequiredService<IPolicyResolver>();
            var cache = app.Services.GetRequiredService<IPolicyEffectivenessCache>();
            var endpointRuleId = await GetEndpointRuleIdAsync(resolver);
            var observedAt = DateTimeOffset.UtcNow;

            for (var i = 0; i < 16; i++)
            {
                await cache.OnDecisionAsync(new PolicyDecision(
                    RuleId: endpointRuleId,
                    WinnerRuleId: endpointRuleId,
                    Matched: true,
                    RequestFingerprint: $"fp-{i}",
                    Action: new PolicyAction.Block(),
                    Mode: PolicyMode.Live,
                    EvalLatencyTicks: 5000,
                    ObservedAt: observedAt));
            }
            // Two more matched but where some other rule actually "won" -- this
            // is what the overridden split surfaces in the row aggregate.
            for (var i = 0; i < 2; i++)
            {
                await cache.OnDecisionAsync(new PolicyDecision(
                    RuleId: endpointRuleId,
                    WinnerRuleId: Guid.NewGuid(),
                    Matched: true,
                    RequestFingerprint: $"fp-override-{i}",
                    Action: new PolicyAction.Allow(),
                    Mode: PolicyMode.Live,
                    EvalLatencyTicks: 5000,
                    ObservedAt: observedAt));
            }
        }

        return app.GetTestClient();
    }

    /// <summary>
    ///     The TestServer renders the view component via a controller-action shim
    ///     that <c>InvokeAsync</c>s it directly. Going through the controller
    ///     exercises the real view-engine pipeline (locator, partial fan-out,
    ///     model binding) without the cost of standing up the full dashboard.
    /// </summary>
    private static async Task<string> GetHtmlAsync(
        HttpClient client,
        PolicyScope scope,
        PolicyStackEmbed embed,
        string? activeTab = null,
        string? filterExpression = null,
        string? sortKey = null,
        string? sortDir = null,
        string? explainerFingerprint = null,
        bool lockedFingerprint = false,
        bool canEdit = false,
        bool hideRuleList = false)
    {
        var query = $"embed={embed}&scopeKind={ScopeKind(scope)}";
        switch (scope.Host)
        {
            case HostScope.Domain d:
                query += $"&domain={d.Name}";
                break;
            case HostScope.Subdomain s:
                query += $"&domain={s.DomainName}&sub={s.SubdomainName}";
                break;
            case HostScope.Endpoint e:
                query += $"&domain={e.DomainName}&sub={e.SubdomainName}&template={Uri.EscapeDataString(e.PathTemplate)}";
                break;
        }
        if (!string.IsNullOrEmpty(activeTab))
            query += $"&activeTab={Uri.EscapeDataString(activeTab)}";
        if (!string.IsNullOrEmpty(filterExpression))
            query += $"&filterExpression={Uri.EscapeDataString(filterExpression)}";
        if (!string.IsNullOrEmpty(sortKey))
            query += $"&sortKey={Uri.EscapeDataString(sortKey)}";
        if (!string.IsNullOrEmpty(sortDir))
            query += $"&sortDir={Uri.EscapeDataString(sortDir)}";
        if (explainerFingerprint is not null)
            query += $"&explainerFingerprint={Uri.EscapeDataString(explainerFingerprint)}";
        if (lockedFingerprint)
            query += "&lockedFingerprint=true";
        if (canEdit)
            query += "&canEdit=true";
        if (hideRuleList)
            query += "&hideRuleList=true";
        var resp = await client.GetAsync($"/_test/policy-stack?{query}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private static string ScopeKind(PolicyScope scope) => scope.Host switch
    {
        null => "wildcard",
        HostScope.Domain => "domain",
        HostScope.Subdomain => "subdomain",
        HostScope.Endpoint => "endpoint",
        _ => "wildcard"
    };

    private static string ExtractScopeHash(string html)
    {
        var match = Regex.Match(html, @"data-policy-stack-scope=""(?<h>[0-9a-fA-F]+)""");
        return match.Success ? match.Groups["h"].Value : string.Empty;
    }

    private static async Task<DefaultPolicyResolver> BuildResolverAsync()
    {
        var store = YamlPolicyRuleStore.FromEmbeddedResources(typeof(PolicyRule).Assembly, SeedPrefix);
        await store.InitializeAsync();
        return new DefaultPolicyResolver(store);
    }

    /// <summary>
    ///     Build a resolver around the supplied store. Used by the three
    ///     "empty store" tests so the wildcard baseline seed rules embedded
    ///     in <c>Mostlylucid.BotDetection.dll</c> don't leak in and flip
    ///     "no rules" assertions.
    /// </summary>
    private static async Task<DefaultPolicyResolver> BuildResolverAsync(IPolicyRuleStore store)
    {
        await store.InitializeAsync();
        return new DefaultPolicyResolver(store);
    }

    private static async Task<ISignalCatalog> BuildSignalCatalogAsync()
    {
        var asm = typeof(Mostlylucid.BotDetection.Models.SignalKeys).Assembly;
#pragma warning disable IL2026
        return await SignalCatalog.LoadAsync(asm);
#pragma warning restore IL2026
    }

    private static async Task<PolicyStackPresenter> BuildPresenterAsync()
    {
        var resolver = await BuildResolverAsync();
        var catalog = await BuildSignalCatalogAsync();
        // No-op cache stub for cheap presenter-only tests.
        var cache = new EmptyEffectivenessCache();
        return new PolicyStackPresenter(resolver, cache, catalog, new PolicyConflictAnalyzer());
    }

    /// <summary>
    ///     Presenter sitting on top of the supplied rule store. The empty-scope
    ///     test uses this with <see cref="EmptyPolicyRuleStore"/> so the wildcard
    ///     baseline seeds don't bleed into "no rows" assertions.
    /// </summary>
    private static async Task<PolicyStackPresenter> BuildPresenterAsync(IPolicyRuleStore store)
    {
        var resolver = await BuildResolverAsync(store);
        var catalog = await BuildSignalCatalogAsync();
        var cache = new EmptyEffectivenessCache();
        return new PolicyStackPresenter(resolver, cache, catalog, new PolicyConflictAnalyzer());
    }

    private static async Task<Guid> GetEndpointRuleIdAsync(IPolicyResolver resolver)
    {
        var effective = await resolver.EffectiveAsync(EndpointScope);
        return effective.First(r => r.SourceScope.Host is HostScope.Endpoint).Rule.Id;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            try { await app.DisposeAsync(); } catch { /* test cleanup */ }
        _apps.Clear();
    }

    // -------- Test doubles --------
    // EmptyPolicyRuleStore + LegacySeedOnlyPolicyRuleStore live in
    // Mostlylucid.BotDetection.Test.Policies.Support so other test classes
    // (PolicyResolverTests etc.) can share the same seed-leak primitives.

    /// <summary>Always returns empty aggregates -- presenter renders rows with 0 hits.</summary>
    private sealed class EmptyEffectivenessCache : IPolicyEffectivenessCache
    {
        public ValueTask OnDecisionAsync(PolicyDecision decision, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<PolicyDecisionAggregate> GetAsync(Guid ruleId, TimeSpan window, CancellationToken ct = default) =>
            new(new PolicyDecisionAggregate(ruleId, window, 0, 0,
                new Dictionary<string, int>(), 0, 0, DateTimeOffset.UtcNow));

        public ValueTask<IReadOnlyDictionary<Guid, PolicyDecisionAggregate>> GetManyAsync(
            IReadOnlyCollection<Guid> ruleIds, TimeSpan window, CancellationToken ct = default) =>
            new((IReadOnlyDictionary<Guid, PolicyDecisionAggregate>)new Dictionary<Guid, PolicyDecisionAggregate>());

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Counts GetMany calls so the StatusBadge fan-out guard test can assert zero.</summary>
    private sealed class CallTrackingEffectivenessCache : IPolicyEffectivenessCache
    {
        public int GetManyCalls;

        public ValueTask OnDecisionAsync(PolicyDecision decision, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<PolicyDecisionAggregate> GetAsync(Guid ruleId, TimeSpan window, CancellationToken ct = default) =>
            new(new PolicyDecisionAggregate(ruleId, window, 0, 0,
                new Dictionary<string, int>(), 0, 0, DateTimeOffset.UtcNow));

        public ValueTask<IReadOnlyDictionary<Guid, PolicyDecisionAggregate>> GetManyAsync(
            IReadOnlyCollection<Guid> ruleIds, TimeSpan window, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetManyCalls);
            return new((IReadOnlyDictionary<Guid, PolicyDecisionAggregate>)new Dictionary<Guid, PolicyDecisionAggregate>());
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Lets a test pre-seed aggregates without spinning up the ring buffer drainer.</summary>
    private sealed class PrewarmedEffectivenessCache : IPolicyEffectivenessCache
    {
        private readonly Dictionary<Guid, PolicyDecisionAggregate> _store = new();
        public void Set(Guid id, PolicyDecisionAggregate agg) => _store[id] = agg;

        public ValueTask OnDecisionAsync(PolicyDecision decision, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<PolicyDecisionAggregate> GetAsync(Guid ruleId, TimeSpan window, CancellationToken ct = default) =>
            new(_store.TryGetValue(ruleId, out var agg)
                ? agg
                : new PolicyDecisionAggregate(ruleId, window, 0, 0, new Dictionary<string, int>(), 0, 0, DateTimeOffset.UtcNow));
        public ValueTask<IReadOnlyDictionary<Guid, PolicyDecisionAggregate>> GetManyAsync(
            IReadOnlyCollection<Guid> ruleIds, TimeSpan window, CancellationToken ct = default)
        {
            var result = new Dictionary<Guid, PolicyDecisionAggregate>(ruleIds.Count);
            foreach (var id in ruleIds)
                if (_store.TryGetValue(id, out var agg)) result[id] = agg;
            return new((IReadOnlyDictionary<Guid, PolicyDecisionAggregate>)result);
        }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>
///     One-shot controller used by the render tests. Maps query-string params to
///     <see cref="PolicyScope"/> + <see cref="PolicyStackEmbed"/>, then invokes
///     the view component. Lives in this file so the test class can register
///     it inline without polluting production controllers.
/// </summary>
[Route("/_test/policy-stack")]
public sealed class PolicyStackTestController : Controller
{
    [HttpGet]
    public IActionResult Get(
        string embed = "Full",
        string scopeKind = "wildcard",
        string? domain = null,
        string? sub = null,
        string? template = null,
        string? activeTab = null,
        string? filterExpression = null,
        string? sortKey = null,
        string? sortDir = null,
        string? explainerFingerprint = null,
        bool lockedFingerprint = false,
        bool canEdit = false,
        bool hideRuleList = false)
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

        return ViewComponent("SbPolicyStack",
            new
            {
                scope, embed = parsedEmbed, activeTab,
                filterExpression, sortKey, sortDir,
                explainerFingerprint, lockedFingerprint,
                canEdit,
                hideRuleList
            });
    }
}
