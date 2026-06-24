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
        var wildcardRule = MakeRule(PolicyScope.Wildcard(), priority: 0);
        var domainRule = MakeRule(PolicyScope.Domain("example.com"), priority: 0);
        var endpointRule = MakeRule(
            PolicyScope.Endpoint("example.com", string.Empty, "/api/users/*"),
            priority: 0);

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
    ///     Build a vacuous PolicyRule -- only Scope + Id + Priority are
    ///     load-bearing for this test. Predicate is an empty AND (evaluates
    ///     true with no clauses) and Action is Allow.
    /// </summary>
    private static PolicyRule MakeRule(PolicyScope scope, int priority) =>
        new(
            Id: Guid.NewGuid(),
            Scope: scope,
            Priority: priority,
            Predicate: new Predicate.And(Array.Empty<Predicate>()),
            Action: new PolicyAction.Allow(),
            Mode: PolicyMode.Live,
            Notes: string.Empty,
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());
}
