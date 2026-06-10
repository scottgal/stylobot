using System.Runtime.CompilerServices;
using FluentAssertions;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

using RuleAction = Mostlylucid.BotDetection.Policies.Rules.PolicyAction;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Coverage for <see cref="PolicyStackHealthSummaryProvider"/> -- the C1
///     adapter that shapes <see cref="PolicyStackSummary"/> into an
///     <see cref="StatTileViewModel"/> for the dashboard overview row.
///     <para>
///         Wires through the real <see cref="PolicyStackSummaryBuilder"/>
///         with an in-memory rule store so the test covers the integration
///         shape that ships in production (vs. fabricating a parallel summary
///         stub the production code doesn't use).
///     </para>
/// </summary>
public sealed class PolicyStackHealthSummaryProviderTests
{
    // ---------- 1. Builder null -> provider returns null (surface absent). ----------

    [Fact]
    public async Task BuildTileAsync_returns_null_when_builder_is_null()
    {
        var provider = new PolicyStackHealthSummaryProvider(builder: null);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().BeNull();
    }

    // ---------- 2. Builder returns null (no rule store) -> provider also returns null. ----------

    [Fact]
    public async Task BuildTileAsync_returns_null_when_summary_builder_returns_null()
    {
        // PolicyStackSummaryBuilder constructed without a rule store returns
        // null from BuildAsync -- the C1 provider mirrors that signal as a
        // null tile so the row builder skips it.
        var summaryBuilder = new PolicyStackSummaryBuilder(ruleStore: null);
        var provider = new PolicyStackHealthSummaryProvider(summaryBuilder);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().BeNull();
    }

    // ---------- 3. Live rules count -> tile Value, Unit "live". ----------

    [Fact]
    public async Task BuildTileAsync_returns_tile_with_LiveRules_count()
    {
        var store = new InMemoryPolicyRuleStore(
            Enumerable.Range(0, 17).Select(_ => Wildcard(PolicyMode.Live)).ToArray());

        var summaryBuilder = new PolicyStackSummaryBuilder(ruleStore: store);
        var provider = new PolicyStackHealthSummaryProvider(summaryBuilder);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().NotBeNull();
        tile!.Title.Should().Be("Policy Stack");
        tile.Value.Should().Be("17");
        tile.Unit.Should().Be("live");
        tile.HealthBand.Should().Be(HealthBand.Good);
        tile.DrillHref.Should().Be(PolicyStackHealthSummaryProvider.DrillPath);
        tile.DrillLabel.Should().Be("View rules");
    }

    // ---------- 4. Delta carries observe + draft counts. ----------

    [Fact]
    public async Task BuildTileAsync_delta_carries_observe_and_draft_counts()
    {
        var rules = new List<PolicyRule>();
        rules.Add(Wildcard(PolicyMode.Live)); // at least 1 live so band stays Good
        for (var i = 0; i < 5; i++) rules.Add(Wildcard(PolicyMode.Observe));
        for (var i = 0; i < 2; i++) rules.Add(Wildcard(PolicyMode.Draft));

        var store = new InMemoryPolicyRuleStore(rules.ToArray());
        var summaryBuilder = new PolicyStackSummaryBuilder(ruleStore: store);
        var provider = new PolicyStackHealthSummaryProvider(summaryBuilder);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().NotBeNull();
        tile!.Delta.Should().NotBeNull();
        tile.Delta!.Should().Contain("5");
        tile.Delta.Should().Contain("2");
        tile.Delta.Should().Contain("observe");
        tile.Delta.Should().Contain("draft");
    }

    // ============================================================
    // Helpers (copied from PolicyStackSummaryBuilderTests so the tile
    // tests stay self-contained -- the summary builder's tests already
    // cover the rule-counting surface, this file covers the C1 mapping
    // on top.)
    // ============================================================

    private static PolicyRule Wildcard(PolicyMode mode)
    {
        var predicate = new Predicate.And(Array.Empty<Predicate>());
        return new PolicyRule(
            Id: Guid.NewGuid(),
            Scope: PolicyScope.Wildcard(),
            Priority: 0,
            Predicate: predicate,
            Action: new RuleAction.Allow(),
            Mode: mode,
            Notes: string.Empty,
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());
    }

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

#pragma warning disable CS0067
        public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;
#pragma warning restore CS0067
    }
}
