using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Direct coverage for <see cref="PolicyEditPresenter"/>'s
///     <see cref="PolicyEditRowViewModel"/> shaping. Each
///     <see cref="PolicyAction"/> variant must produce its own typed slice on
///     the view model so the per-action partials introduced in subsequent
///     traffic-shaping editor tasks have a single place to dispatch off of.
/// </summary>
public sealed class PolicyEditPresenterTests
{
    [Fact]
    public async Task RateLimit_round_trips_full_field_set()
    {
        var rule = new PolicyRule(
            Id: Guid.NewGuid(),
            Scope: PolicyScope.Wildcard(),
            Priority: 100,
            Predicate: PredicateParser.Parse("bot.type = scraper"),
            Action: new PolicyAction.RateLimit(RequestsPerMinute: 6),
            Mode: PolicyMode.Draft,
            Notes: string.Empty,
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());

        var store = new InMemoryPolicyRuleStore(rule);
        var presenter = new PolicyEditPresenter(store);

        var vm = await presenter.BuildForExistingRuleAsync(rule.Id, CancellationToken.None);

        Assert.NotNull(vm);
        Assert.Equal("ratelimit", vm!.ActionKind);
        Assert.NotNull(vm.RateLimit);
        Assert.Equal(6, vm.RateLimit!.Rate);
        Assert.Equal("minute", vm.RateLimit.Unit);
        Assert.Equal("fingerprint", vm.RateLimit.Key);              // default per spec
        Assert.Null(vm.RateLimit.Burst);                            // null = unset
        Assert.Null(vm.RateLimit.MitigationTimeoutSeconds);
        Assert.Equal("throttle-status", vm.RateLimit.OverLimitAction);  // default per spec
        Assert.Null(vm.Throttle);
        Assert.Null(vm.Challenge);
        Assert.Null(vm.Tag);
    }

    [Theory]
    [InlineData("allow", typeof(PolicyAction.Allow))]
    [InlineData("observe", typeof(PolicyAction.Observe))]
    [InlineData("block", typeof(PolicyAction.Block))]
    [InlineData("tag", typeof(PolicyAction.Tag))]
    [InlineData("challenge", typeof(PolicyAction.Challenge))]
    [InlineData("ratelimit", typeof(PolicyAction.RateLimit))]
    [InlineData("throttle", typeof(PolicyAction.Throttle))]
    public async Task Every_action_kind_round_trips_to_a_known_kind_string(string expected, Type _)
    {
        var rule = TestRules.WithAction(expected);
        var presenter = new PolicyEditPresenter(new InMemoryPolicyRuleStore(rule));
        var vm = await presenter.BuildForExistingRuleAsync(rule.Id, CancellationToken.None);
        Assert.Equal(expected, vm!.ActionKind);
    }

    /// <summary>
    ///     Small helper that fabricates a <see cref="PolicyRule"/> with the
    ///     requested action kind and otherwise-default fields. Used by the
    ///     theory above to keep each case a one-liner.
    /// </summary>
    private static class TestRules
    {
        public static PolicyRule WithAction(string kind)
        {
            PolicyAction action = kind switch
            {
                "allow" => new PolicyAction.Allow(),
                "observe" => new PolicyAction.Observe(),
                "block" => new PolicyAction.Block(),
                "tag" => new PolicyAction.Tag("test-tag"),
                "challenge" => new PolicyAction.Challenge("turnstile"),
                "ratelimit" => new PolicyAction.RateLimit(RequestsPerMinute: 60),
                "throttle" => new PolicyAction.Throttle(requestsPerSecond: 50),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown action kind")
            };

            return new PolicyRule(
                Id: Guid.NewGuid(),
                Scope: PolicyScope.Wildcard(),
                Priority: 0,
                Predicate: PredicateParser.Parse("bot.type = scraper"),
                Action: action,
                Mode: PolicyMode.Draft,
                Notes: string.Empty,
                Source: "test",
                CreatedAt: DateTimeOffset.UtcNow,
                RevisionId: Guid.NewGuid());
        }
    }

    /// <summary>
    ///     Minimal <see cref="IPolicyRuleStore"/> double; presenter only
    ///     calls <see cref="GetByIdAsync"/> so the rest are stubbed.
    /// </summary>
    private sealed class InMemoryPolicyRuleStore : IPolicyRuleStore
    {
        private readonly IReadOnlyList<PolicyRule> _rules;

        public InMemoryPolicyRuleStore(params PolicyRule[] rules)
        {
            _rules = rules;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
        {
            IReadOnlyList<PolicyRule> matched = _rules.Where(r => r.Scope == scope).ToArray();
            return Task.FromResult(matched);
        }

        public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(
            IReadOnlyList<PolicyScope> scopePath,
            CancellationToken ct = default)
        {
            var result = new List<PolicyRule>(_rules.Count);
            foreach (var scope in scopePath)
            {
                foreach (var rule in _rules)
                {
                    if (rule.Scope == scope) result.Add(rule);
                }
            }
            return Task.FromResult<IReadOnlyList<PolicyRule>>(result);
        }

        public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PolicyRule?>(_rules.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<PolicyRule>> GetAllRulesAsync(CancellationToken ct = default)
            => Task.FromResult(_rules);

#pragma warning disable CS0067 // Event required by interface, never raised here.
        public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;
#pragma warning restore CS0067
    }
}