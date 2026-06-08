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

    private static readonly PolicyScope WildcardScope = new PolicyScope.Wildcard();
    private static readonly PolicyScope DomainScope = new PolicyScope.Domain(DomainAcme);
    private static readonly PolicyScope SubdomainScope = new PolicyScope.Subdomain(DomainAcme, SubDocs);
    private static readonly PolicyScope EndpointScope = new PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload);

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
        Assert.IsType<PolicyScope.Wildcard>(vm.BreadcrumbPath[0]);
        Assert.IsType<PolicyScope.Domain>(vm.BreadcrumbPath[1]);
        Assert.IsType<PolicyScope.Subdomain>(vm.BreadcrumbPath[2]);
        Assert.IsType<PolicyScope.Endpoint>(vm.BreadcrumbPath[3]);
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
        var presenter = await BuildPresenterAsync();
        var emptyScope = new PolicyScope.Endpoint("unknown.example", "api.unknown.example", "GET /none");

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
            && c.OwnerScope is PolicyScope.Endpoint
            && c.OverriddenScope is PolicyScope.Domain);
    }

    [Fact]
    public void ConflictAnalyzer_returns_empty_when_no_action_conflicts()
    {
        var rules = new List<EffectiveRule>
        {
            // Two Allow rules at different scopes -- no conflict.
            new EffectiveRule(
                MakeRule("is_human = true", new PolicyAction.Allow(), priority: 100,
                    scope: new PolicyScope.Domain(DomainAcme)),
                new PolicyScope.Domain(DomainAcme),
                IsInherited: false),
            new EffectiveRule(
                MakeRule("ua.family = chrome", new PolicyAction.Allow(), priority: 50,
                    scope: new PolicyScope.Subdomain(DomainAcme, SubDocs)),
                new PolicyScope.Subdomain(DomainAcme, SubDocs),
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
        var client = await BuildClientAsync(prewarmEndpointHits: true);
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
        var client = await BuildClientAsync(prewarmEndpointHits: false);
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
            scope: new PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload));
        var subdomain = MakeRule(
            "geo.country in (CN, RU)",
            new PolicyAction.Challenge("turnstile"), priority: 50,
            scope: new PolicyScope.Subdomain(DomainAcme, SubDocs));
        var domain = MakeRule(
            "is_human = true",
            new PolicyAction.Allow(), priority: 100,
            scope: new PolicyScope.Domain(DomainAcme));

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

    // -------- Render helpers --------

    private async Task<HttpClient> BuildClientAsync(bool prewarmEndpointHits)
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
        string? sortDir = null)
    {
        var query = $"embed={embed}&scopeKind={ScopeKind(scope)}";
        switch (scope)
        {
            case PolicyScope.Domain d:
                query += $"&domain={d.DomainName}";
                break;
            case PolicyScope.Subdomain s:
                query += $"&domain={s.DomainName}&sub={s.SubdomainName}";
                break;
            case PolicyScope.Endpoint e:
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
        var resp = await client.GetAsync($"/_test/policy-stack?{query}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private static string ScopeKind(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "wildcard",
        PolicyScope.Domain => "domain",
        PolicyScope.Subdomain => "subdomain",
        PolicyScope.Endpoint => "endpoint",
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

    private static async Task<Guid> GetEndpointRuleIdAsync(IPolicyResolver resolver)
    {
        var effective = await resolver.EffectiveAsync(EndpointScope);
        return effective.First(r => r.SourceScope is PolicyScope.Endpoint).Rule.Id;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            try { await app.DisposeAsync(); } catch { /* test cleanup */ }
        _apps.Clear();
    }

    // -------- Test doubles --------

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
        string? sortDir = null)
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

        return ViewComponent("SbPolicyStack",
            new { scope, embed = parsedEmbed, activeTab, filterExpression, sortKey, sortDir });
    }
}
