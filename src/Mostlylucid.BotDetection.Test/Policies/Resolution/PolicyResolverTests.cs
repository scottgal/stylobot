using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Signals;
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
    private static async Task<DefaultPolicyResolver> BuildResolverAsync(
        IEnumerable<ISignalContributor>? contributors = null)
    {
        var store = new LegacySeedOnlyPolicyRuleStore();
        await store.InitializeAsync();
        return new DefaultPolicyResolver(store, contributors);
    }

    [Fact]
    public async Task Effective_rules_are_most_specific_first_then_priority()
    {
        var resolver = await BuildResolverAsync();
        var effective = await resolver.EffectiveAsync(
            PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload));

        Assert.Equal(3, effective.Count);

        // Endpoint first, not inherited.
        Assert.Equal(PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload), effective[0].SourceScope);
        Assert.False(effective[0].IsInherited);

        // Subdomain next, inherited.
        Assert.Equal(PolicyScope.Subdomain(DomainAcme, SubDocs), effective[1].SourceScope);
        Assert.True(effective[1].IsInherited);

        // Domain last, inherited.
        Assert.Equal(PolicyScope.Domain(DomainAcme), effective[2].SourceScope);
        Assert.True(effective[2].IsInherited);
    }

    [Fact]
    public async Task Effective_at_subdomain_excludes_endpoint_specific_rules()
    {
        var resolver = await BuildResolverAsync();
        var effective = await resolver.EffectiveAsync(PolicyScope.Subdomain(DomainAcme, SubDocs));

        Assert.DoesNotContain(effective, e => e.Rule.Action is PolicyAction.Block);
        Assert.Equal(2, effective.Count);   // subdomain + domain

        Assert.Equal(PolicyScope.Subdomain(DomainAcme, SubDocs), effective[0].SourceScope);
        Assert.False(effective[0].IsInherited);

        Assert.Equal(PolicyScope.Domain(DomainAcme), effective[1].SourceScope);
        Assert.True(effective[1].IsInherited);
    }

    [Fact]
    public async Task Effective_at_domain_returns_only_domain_rule()
    {
        var resolver = await BuildResolverAsync();
        var effective = await resolver.EffectiveAsync(PolicyScope.Domain(DomainAcme));

        Assert.Single(effective);
        Assert.Equal(PolicyScope.Domain(DomainAcme), effective[0].SourceScope);
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
            PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload),
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
            PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload),
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

    // ---- Phase F: ISignalContributor integration --------------------------

    [Fact]
    public async Task Phase_F_composite_predicate_resolves_per_request_signals_and_contributor_signals()
    {
        // Composite predicate -- per-request half + contributor half -- so the
        // resolver only matches if BOTH halves resolve through the merged map.
        var store = new SingleRulePolicyRuleStore(
            "score.bot_probability > 0.9 and meter.system.cpu_load.current > 80");
        var contributor = new DelegateContributor(signals =>
        {
            signals["meter.system.cpu_load.current"] = 95.0;
            return Task.CompletedTask;
        });
        var resolver = new DefaultPolicyResolver(store, new[] { contributor });

        var matched = await resolver.EffectiveWithContextAsync(
            PolicyScope.Wildcard(),
            new Dictionary<string, object?>
            {
                ["score.bot_probability"] = 0.95m
            });

        Assert.Single(matched);
    }

    [Fact]
    public async Task Phase_F_request_signals_win_over_contributor_signals()
    {
        // Contributor pumps a high value; per-request bag overrides with a low
        // value; predicate should evaluate against the request value.
        var store = new SingleRulePolicyRuleStore("meter.foo.current > 50");
        var contributor = new DelegateContributor(signals =>
        {
            // Contributor follows TryAdd semantics -- already present, so no-op.
            if (!signals.ContainsKey("meter.foo.current"))
                signals["meter.foo.current"] = 100.0;
            return Task.CompletedTask;
        });
        var resolver = new DefaultPolicyResolver(store, new[] { contributor });

        var matched = await resolver.EffectiveWithContextAsync(
            PolicyScope.Wildcard(),
            new Dictionary<string, object?>
            {
                ["meter.foo.current"] = 5.0
            });

        Assert.Empty(matched);
    }

    [Fact]
    public async Task Phase_F_throwing_contributor_does_not_break_resolver()
    {
        // Predicate uses only per-request signals; contributor throws and
        // must be swallowed so the resolver still returns the matching rule.
        var store = new SingleRulePolicyRuleStore("score.bot_probability > 0.9");
        var contributor = new DelegateContributor(_ =>
            throw new InvalidOperationException("boom"));
        var resolver = new DefaultPolicyResolver(store, new[] { contributor });

        var matched = await resolver.EffectiveWithContextAsync(
            PolicyScope.Wildcard(),
            new Dictionary<string, object?>
            {
                ["score.bot_probability"] = 0.95m
            });

        Assert.Single(matched);
    }

    [Fact]
    public async Task Phase_F_OCE_from_contributor_propagates()
    {
        var store = new SingleRulePolicyRuleStore("score.bot_probability > 0.9");
        var contributor = new DelegateContributor(_ =>
            throw new OperationCanceledException());
        var resolver = new DefaultPolicyResolver(store, new[] { contributor });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.EffectiveWithContextAsync(
                PolicyScope.Wildcard(),
                new Dictionary<string, object?> { ["score.bot_probability"] = 0.95m }));
    }

    [Fact]
    public async Task Phase_F_null_contributors_is_identical_to_pre_phase_F_behaviour()
    {
        // Build resolver via the null-contributors path; assert the legacy
        // EffectiveWithContextAsync surface returns the same rules as the
        // current LegacySeedOnlyPolicyRuleStore baseline.
        var store = new LegacySeedOnlyPolicyRuleStore();
        await store.InitializeAsync();
        var resolverNoContrib = new DefaultPolicyResolver(store, contributors: null);

        var scraperSignals = new Dictionary<string, object?>
        {
            ["bot.type"] = "scraper",
            ["score.bot_probability"] = 0.92m,
            ["geo.country"] = "US",
            ["is_human"] = false
        };
        var matched = await resolverNoContrib.EffectiveWithContextAsync(
            PolicyScope.Endpoint(DomainAcme, SubDocs, EpUpload),
            scraperSignals);

        Assert.Contains(matched, e => e.Rule.Action is PolicyAction.Block);
        Assert.DoesNotContain(matched, e => e.Rule.Action is PolicyAction.Allow);
    }

    // ---- Support: tiny single-rule store + delegate-backed contributor ----

    private sealed class SingleRulePolicyRuleStore : IPolicyRuleStore
    {
        private readonly IReadOnlyList<PolicyRule> _rules;

        public SingleRulePolicyRuleStore(string predicateText)
        {
            var rule = new PolicyRule(
                Id: Guid.NewGuid(),
                Scope: PolicyScope.Wildcard(),
                Priority: 100,
                Predicate: PredicateParser.Parse(predicateText),
                Action: new PolicyAction.Block(),
                Mode: PolicyMode.Live,
                Notes: string.Empty,
                Source: "test",
                CreatedAt: DateTimeOffset.UtcNow,
                RevisionId: Guid.NewGuid());
            _rules = new[] { rule };
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
            => Task.FromResult(scope.IsWildcard ? _rules : (IReadOnlyList<PolicyRule>)Array.Empty<PolicyRule>());

        public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(IReadOnlyList<PolicyScope> scopePath, CancellationToken ct = default)
            => Task.FromResult(_rules);

        public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PolicyRule?>(_rules[0].Id == id ? _rules[0] : null);

        public Task<IReadOnlyList<PolicyRule>> GetAllRulesAsync(CancellationToken ct = default)
            => Task.FromResult(_rules);

#pragma warning disable CS0067
        public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;
#pragma warning restore CS0067
    }

    private sealed class DelegateContributor : ISignalContributor
    {
        private readonly Func<IDictionary<string, object?>, Task> _impl;

        public DelegateContributor(Func<IDictionary<string, object?>, Task> impl)
        {
            _impl = impl;
        }

        public Task ContributeAsync(IDictionary<string, object?> signals, CancellationToken ct)
            => _impl(signals);
    }
}
