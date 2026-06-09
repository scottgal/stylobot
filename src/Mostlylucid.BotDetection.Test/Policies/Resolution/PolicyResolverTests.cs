using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Test.Policies.Support;

namespace Mostlylucid.BotDetection.Test.Policies.Resolution;

public class PolicyResolverTests
{
    private const string DomainAcme = "acme.com";
    private const string SubDocs = "docs.acme.com";
    private const string EpUpload = "GET /api/upload";

    // These tests were written against the legacy three-rule seed corpus
    // (domain Allow + subdomain Challenge + endpoint Block on acme.com).
    // Commit 54b41133 added two wildcard baseline YAMLs that ship as
    // embedded resources in Mostlylucid.BotDetection.dll; they leak into
    // every scope path and broke the exact-count asserts here. Filter
    // them out with the same LegacySeedOnlyPolicyRuleStore double the
    // SbPolicyStackTests render tests use -- production behaviour is
    // unchanged, the seeds stay on disk.
    private static async Task<DefaultPolicyResolver> BuildResolverAsync()
    {
        var store = new LegacySeedOnlyPolicyRuleStore();
        await store.InitializeAsync();
        return new DefaultPolicyResolver(store);
    }

    [Fact]
    public async Task Effective_rules_are_most_specific_first_then_priority()
    {
        var resolver = await BuildResolverAsync();
        var effective = await resolver.EffectiveAsync(
            new PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload));

        Assert.Equal(3, effective.Count);

        // Endpoint first, not inherited.
        Assert.Equal(new PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload), effective[0].SourceScope);
        Assert.False(effective[0].IsInherited);

        // Subdomain next, inherited.
        Assert.Equal(new PolicyScope.Subdomain(DomainAcme, SubDocs), effective[1].SourceScope);
        Assert.True(effective[1].IsInherited);

        // Domain last, inherited.
        Assert.Equal(new PolicyScope.Domain(DomainAcme), effective[2].SourceScope);
        Assert.True(effective[2].IsInherited);
    }

    [Fact]
    public async Task Effective_at_subdomain_excludes_endpoint_specific_rules()
    {
        var resolver = await BuildResolverAsync();
        var effective = await resolver.EffectiveAsync(new PolicyScope.Subdomain(DomainAcme, SubDocs));

        Assert.DoesNotContain(effective, e => e.Rule.Action is PolicyAction.Block);
        Assert.Equal(2, effective.Count);   // subdomain + domain

        Assert.Equal(new PolicyScope.Subdomain(DomainAcme, SubDocs), effective[0].SourceScope);
        Assert.False(effective[0].IsInherited);

        Assert.Equal(new PolicyScope.Domain(DomainAcme), effective[1].SourceScope);
        Assert.True(effective[1].IsInherited);
    }

    [Fact]
    public async Task Effective_at_domain_returns_only_domain_rule()
    {
        var resolver = await BuildResolverAsync();
        var effective = await resolver.EffectiveAsync(new PolicyScope.Domain(DomainAcme));

        Assert.Single(effective);
        Assert.Equal(new PolicyScope.Domain(DomainAcme), effective[0].SourceScope);
        Assert.False(effective[0].IsInherited);
    }

    [Fact]
    public async Task Effective_with_context_filters_unmatched_predicates()
    {
        var resolver = await BuildResolverAsync();

        // Scraper at the upload endpoint -> endpoint Block rule matches; the
        // domain Allow rule (is_human = true) does not, because is_human is
        // false on this request.
        var scraperSignals = new Dictionary<string, object?>
        {
            ["bot.type"] = "scraper",
            ["score.bot_probability"] = 0.92m,
            ["geo.country"] = "US",
            ["is_human"] = false
        };
        var matched = await resolver.EffectiveWithContextAsync(
            new PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload),
            scraperSignals);

        Assert.Contains(matched, e => e.Rule.Action is PolicyAction.Block);
        Assert.DoesNotContain(matched, e => e.Rule.Action is PolicyAction.Allow);
    }

    [Fact]
    public async Task Effective_with_context_returns_empty_when_no_predicates_match()
    {
        var resolver = await BuildResolverAsync();

        // Empty signal bag -> every rule's term hits an unknown facet -> false.
        var matched = await resolver.EffectiveWithContextAsync(
            new PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload),
            new Dictionary<string, object?>());

        Assert.Empty(matched);
    }

    [Fact]
    public void PredicateEvaluator_returns_false_for_unknown_facet()
    {
        var p = PredicateParser.Parse("does.not.exist = true");
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData("score.bot_probability >= 0.7", 0.9, true)]
    [InlineData("score.bot_probability >= 0.7", 0.5, false)]
    [InlineData("score.bot_probability between 0.4 and 0.6", 0.5, true)]
    [InlineData("score.bot_probability between 0.4 and 0.6", 0.7, false)]
    [InlineData("score.bot_probability > 0.5", 0.6, true)]
    [InlineData("score.bot_probability < 0.5", 0.4, true)]
    [InlineData("score.bot_probability <= 0.5", 0.5, true)]
    [InlineData("score.bot_probability != 0.5", 0.6, true)]
    [InlineData("score.bot_probability != 0.5", 0.5, false)]
    public void Numeric_operators_handle_decimal_facet_values(string expr, double facetValue, bool expected)
    {
        var p = PredicateParser.Parse(expr);
        var signals = new Dictionary<string, object?>
        {
            ["score.bot_probability"] = (decimal)facetValue
        };
        Assert.Equal(expected, PredicateEvaluator.Evaluate(p, signals));
    }

    [Fact]
    public void In_operator_matches_string_facet_against_list()
    {
        var p = PredicateParser.Parse("bot.type in (scraper, crawler)");
        Assert.True(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?> { ["bot.type"] = "scraper" }));
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?> { ["bot.type"] = "human" }));
    }

    [Fact]
    public void NotIn_operator_inverts_in_logic()
    {
        var p = PredicateParser.Parse("bot.type not in (human, friendly_bot)");
        Assert.True(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?> { ["bot.type"] = "scraper" }));
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?> { ["bot.type"] = "human" }));
    }

    [Fact]
    public void AnyIn_operator_treats_facet_as_enumerable()
    {
        var p = PredicateParser.Parse("tags any in (premium, suspicious)");
        Assert.True(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["tags"] = new[] { "anonymous", "premium" }
        }));
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["tags"] = new[] { "anonymous", "verified" }
        }));
    }

    [Fact]
    public void AllIn_operator_requires_every_facet_entry_in_list()
    {
        var p = PredicateParser.Parse("tags all in (premium, verified, anonymous)");
        Assert.True(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["tags"] = new[] { "premium", "verified" }
        }));
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["tags"] = new[] { "premium", "unknown" }
        }));
    }

    [Fact]
    public void Matches_operator_runs_regex()
    {
        var p = PredicateParser.Parse("ua.string matches \"^Mozilla\"");
        Assert.True(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["ua.string"] = "Mozilla/5.0 ..."
        }));
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["ua.string"] = "curl/8.0.0"
        }));
    }

    [Fact]
    public void Contains_operator_does_substring_match()
    {
        var p = PredicateParser.Parse("ua.string contains \"GoogleBot\"");
        Assert.True(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["ua.string"] = "compatible; GoogleBot/2.1"
        }));
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["ua.string"] = "compatible; Bingbot/2.0"
        }));
    }

    [Fact]
    public void And_predicate_requires_all_children_true()
    {
        var p = PredicateParser.Parse("bot.type in (scraper) and score.bot_probability >= 0.7");
        Assert.True(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["bot.type"] = "scraper",
            ["score.bot_probability"] = 0.9m
        }));
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["bot.type"] = "scraper",
            ["score.bot_probability"] = 0.3m
        }));
    }

    [Fact]
    public void Or_predicate_requires_any_child_true()
    {
        var p = PredicateParser.Parse("bot.type = scraper or bot.type = crawler");
        Assert.True(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["bot.type"] = "crawler"
        }));
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["bot.type"] = "human"
        }));
    }

    [Fact]
    public void Boolean_equality_supports_typed_and_string_values()
    {
        var p = PredicateParser.Parse("is_human = true");
        Assert.True(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["is_human"] = true
        }));
        Assert.False(PredicateEvaluator.Evaluate(p, new Dictionary<string, object?>
        {
            ["is_human"] = false
        }));
    }
}
