using FluentAssertions;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     A1 contract: <see cref="EffectiveStackResolver"/> returns an
///     <see cref="EffectiveStackView"/> with the queried-scope-owned rules in
///     <c>Owned</c> and the ancestor-or-equal rules in <c>Effective</c>. A2
///     owns the sort, A3 owns the annotation booleans -- this file only pins
///     the ancestor-walk shape.
/// </summary>
public class EffectiveStackResolverTests
{
    [Fact]
    public void EffectiveRules_at_endpoint_includes_inherited_subdomain_domain_global()
    {
        // Three rules at three scopes: wildcard, domain=example.com,
        // endpoint=/api/users/* under example.com. The plan reads
        // "global / domain / endpoint" -- PolicyScope has no Global()
        // factory; Wildcard() is the equivalent zero-slot scope.
        //
        // Each rule carries a DIFFERENT predicate so the A3 shadow pass
        // produces no annotations -- this test's job is to pin the
        // ancestor-walk shape, not the annotation behaviour (which has its
        // own dedicated tests below).
        var wildcardRule = MakeRule(
            PolicyScope.Wildcard(),
            priority: 0,
            predicate: MakePredicate("bot.type", "=", "scraper"));
        var domainRule = MakeRule(
            PolicyScope.Domain("example.com"),
            priority: 0,
            predicate: MakePredicate("bot.type", "=", "ai-crawler"));
        var endpointRule = MakeRule(
            PolicyScope.Endpoint("example.com", string.Empty, "/api/users/*"),
            priority: 0,
            predicate: MakePredicate("bot.type", "=", "feed-fetcher"));

        var rules = new[] { wildcardRule, domainRule, endpointRule };

        var resolver = new EffectiveStackResolver();
        var view = resolver.ResolveAt(
            PolicyScope.Endpoint("example.com", string.Empty, "/api/users/*"),
            rules);

        view.Owned
            .Should()
            .ContainSingle(r => r.RuleId == endpointRule.Id);

        view.Effective
            .Select(p => p.RuleId)
            .Should()
            .BeEquivalentTo(
                new[] { endpointRule.Id, domainRule.Id, wildcardRule.Id });

        view.Annotations.Should().BeEmpty();
    }

    [Fact]
    public void EffectiveRules_sorted_most_specific_first_then_priority_ascending()
    {
        var domainRule = MakeRule(scope: PolicyScope.Domain("example.com"), priority: 1);
        var endpointHighPri = MakeRule(
            scope: PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
            priority: 1);
        var endpointLowPri = MakeRule(
            scope: PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
            priority: 0);
        var wildcardRule = MakeRule(scope: PolicyScope.Wildcard(), priority: 0);

        var rules = new[] { domainRule, endpointHighPri, endpointLowPri, wildcardRule };

        var view = new EffectiveStackResolver()
            .ResolveAt(PolicyScope.Endpoint("example.com", string.Empty, "/api/*"), rules);

        // Specificity descending: endpoint > domain > wildcard.
        // Within same specificity: priority ascending (0 wins over 1).
        view.Effective.Select(r => r.RuleId).Should().Equal(
            endpointLowPri.Id, endpointHighPri.Id, domainRule.Id, wildcardRule.Id);
    }

    [Fact]
    public void EffectiveRules_inherited_flag_true_for_broader_scopes()
    {
        var endpointRule = MakeRule(
            scope: PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
            priority: 0);
        var wildcardRule = MakeRule(scope: PolicyScope.Wildcard(), priority: 0);

        var view = new EffectiveStackResolver()
            .ResolveAt(
                PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
                new[] { endpointRule, wildcardRule });

        view.Effective.First(r => r.RuleId == endpointRule.Id).IsInherited.Should().BeFalse();
        view.Effective.First(r => r.RuleId == wildcardRule.Id).IsInherited.Should().BeTrue();
    }

    /// <summary>
    ///     Same-scope shadowing: two endpoint-scoped rules with identical
    ///     predicates differ only by priority. The lower-priority rule wins;
    ///     the higher-priority rule (sorted second in the win-order list)
    ///     never fires at this scope -> shadowed.
    /// </summary>
    [Fact]
    public void Owned_rule_marked_shadowed_when_higher_priority_rule_at_same_scope_has_same_predicate()
    {
        var pred = MakePredicate("bot.type", "=", "scraper");
        var winner = MakeRule(
            scope: PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
            priority: 0,
            predicate: pred);
        var loser = MakeRule(
            scope: PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
            priority: 1,
            predicate: pred);

        var view = new EffectiveStackResolver()
            .ResolveAt(
                PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
                new[] { winner, loser });

        var loserProj = view.Effective.Single(p => p.RuleId == loser.Id);
        loserProj.IsShadowed.Should().BeTrue();
        view.Annotations.Should().ContainSingle(a => a.Kind == "shadowed" && a.RuleId == loser.Id);

        // The winner is the first card in the win-order list and stays
        // un-shadowed; only the loser flips.
        view.Effective.Single(p => p.RuleId == winner.Id).IsShadowed.Should().BeFalse();
    }

    /// <summary>
    ///     Inheritance shadowing: a domain-scoped rule and an endpoint-scoped
    ///     rule share a predicate. At the endpoint query, the endpoint card
    ///     wins (more specific); the inherited domain rule never fires there
    ///     -> shadowed. The Owned list is also re-synced so the shadowed flag
    ///     is observable through whichever list the consumer reads.
    /// </summary>
    [Fact]
    public void Inherited_rule_marked_shadowed_when_owned_rule_at_more_specific_scope_has_same_predicate()
    {
        var pred = MakePredicate("bot.type", "=", "scraper");
        var domainRule = MakeRule(
            scope: PolicyScope.Domain("example.com"),
            priority: 0,
            predicate: pred);
        var endpointRule = MakeRule(
            scope: PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
            priority: 0,
            predicate: pred);

        var view = new EffectiveStackResolver()
            .ResolveAt(
                PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
                new[] { domainRule, endpointRule });

        var domainProj = view.Effective.Single(p => p.RuleId == domainRule.Id);
        domainProj.IsShadowed.Should().BeTrue();
        view.Annotations.Should().Contain(a => a.Kind == "shadowed" && a.RuleId == domainRule.Id);

        // Endpoint rule is the winner -- stays un-shadowed and produces no
        // annotation of its own.
        view.Effective.Single(p => p.RuleId == endpointRule.Id).IsShadowed.Should().BeFalse();
        view.Annotations.Should().NotContain(a => a.RuleId == endpointRule.Id);
    }

    /// <summary>
    ///     The plan's third test asks for "unreachable when zero hits AND
    ///     shadowed". The "zero hits" half of that gate is not observable on
    ///     <see cref="PolicyRule"/> today -- no hit-count field exists on the
    ///     record, and the A10 row builder is the surface that will fold in
    ///     telemetry. This test pins the A3 contract honestly: even when a
    ///     shadowed projection is present, <see cref="EffectiveRuleProjection.IsUnreachable"/>
    ///     stays <c>false</c> because A3 cannot fairly evaluate it. The test
    ///     guards against a future change that quietly flips IsUnreachable
    ///     true from the shadowed signal alone (which would be wrong: a
    ///     shadowed rule whose predicate still matches traffic at a sibling
    ///     scope is NOT unreachable).
    /// </summary>
    [Fact]
    public void Shadowed_rule_does_not_imply_unreachable_without_zero_hits_signal()
    {
        var pred = MakePredicate("bot.type", "=", "scraper");
        var winner = MakeRule(
            scope: PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
            priority: 0,
            predicate: pred);
        var loser = MakeRule(
            scope: PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
            priority: 1,
            predicate: pred);

        var view = new EffectiveStackResolver()
            .ResolveAt(
                PolicyScope.Endpoint("example.com", string.Empty, "/api/*"),
                new[] { winner, loser });

        var loserProj = view.Effective.Single(p => p.RuleId == loser.Id);
        loserProj.IsShadowed.Should().BeTrue();
        loserProj.IsUnreachable.Should().BeFalse();
        view.Annotations.Should().NotContain(a => a.Kind == "unreachable");
    }

    /// <summary>
    ///     Build a vacuous PolicyRule -- only Scope + Id + Priority are
    ///     load-bearing for the A1/A2 tests. Predicate is an empty AND
    ///     (evaluates true with no clauses) and Action is Allow. The
    ///     <paramref name="predicate"/> overload is used by A3's annotation
    ///     tests, which need predicates that hash-equal across rules.
    /// </summary>
    private static PolicyRule MakeRule(PolicyScope scope, int priority) =>
        MakeRule(scope, priority, new Predicate.And(Array.Empty<Predicate>()));

    private static PolicyRule MakeRule(PolicyScope scope, int priority, Predicate predicate) =>
        new(
            Id: Guid.NewGuid(),
            Scope: scope,
            Priority: priority,
            Predicate: predicate,
            Action: new PolicyAction.Allow(),
            Mode: PolicyMode.Live,
            Notes: string.Empty,
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());

    /// <summary>
    ///     Build a leaf <see cref="Predicate.Term"/>. Two terms with the same
    ///     facet / op / value hash-equal under
    ///     <see cref="PredicateHasher.Hash"/>, which is what the A3 shadow
    ///     pass keys on.
    /// </summary>
    private static Predicate MakePredicate(string facet, string op, string value) =>
        new Predicate.Term(facet, op switch
        {
            "=" => PredicateOp.Eq,
            "!=" => PredicateOp.Neq,
            "in" => PredicateOp.In,
            _ => PredicateOp.Eq,
        }, value);
}
