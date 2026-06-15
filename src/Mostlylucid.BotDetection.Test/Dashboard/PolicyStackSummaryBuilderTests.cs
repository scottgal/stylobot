using System.Runtime.CompilerServices;
using FluentAssertions;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Test.Policies.Support;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

// "PolicyAction" is ambiguous inside the Mostlylucid.BotDetection.Policies tree
// (legacy enum at the parent namespace shadows the new record at .Rules) when
// the test references both PolicyRule construction AND PolicyMode side-by-side.
using RuleAction = Mostlylucid.BotDetection.Policies.Rules.PolicyAction;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Coverage for <see cref="PolicyStackSummaryBuilder"/> -- B8 of the Pack
///     Metrics + Cohesive Views plan. Pure unit tests against the builder
///     rather than the <c>WebApplicationFactory&lt;Program&gt;</c> dance: the
///     builder is pure logic over <see cref="IPolicyRuleStore"/> +
///     <see cref="IPolicyDecisionLog"/>, and a manual fake of each covers
///     every relevant path.
///     <para>
///         The builder counts rules at the <see cref="PolicyScope.Wildcard"/>
///         scope -- i.e. the global baseline that applies to every request --
///         not every scope in the corpus. Domain / subdomain / endpoint rules
///         are scope-specific tuning surfaced on the per-scope pages, not on
///         the global hub summary.
///     </para>
/// </summary>
public sealed class PolicyStackSummaryBuilderTests
{
    // ---------- 1. Null rule store -> BuildAsync returns null. ----------

    [Fact]
    public async Task BuildAsync_returns_null_when_rule_store_is_null()
    {
        var builder = new PolicyStackSummaryBuilder(ruleStore: null);

        var summary = await builder.BuildAsync(default);

        summary.Should().BeNull();
    }

    // ---------- 2. Empty store -> Warning band, all counts zero. ----------

    [Fact]
    public async Task Empty_store_returns_Warning_band_with_zero_counts()
    {
        var store = new EmptyPolicyRuleStore();

        var builder = new PolicyStackSummaryBuilder(ruleStore: store);
        var summary = await builder.BuildAsync(default);

        summary.Should().NotBeNull();
        summary!.TotalRules.Should().Be(0);
        summary.LiveRules.Should().Be(0);
        summary.ObserveRules.Should().Be(0);
        summary.DraftRules.Should().Be(0);
        summary.DisabledRules.Should().Be(0);
        summary.HealthBand.Should().Be(HealthBand.Warning);
    }

    // ---------- 3. Hand-built store with mixed modes -> per-mode counts match. ----------

    [Fact]
    public async Task Hand_built_store_counts_each_mode_correctly()
    {
        // 2 Live + 1 Observe + 1 Draft + 1 Disabled at the wildcard scope.
        // Picked so each enum value is represented at least once and the
        // total != any single mode count -- catches a miscategorise bug.
        var store = new FixedRulePolicyRuleStore(
            Wildcard(PolicyMode.Live),
            Wildcard(PolicyMode.Live),
            Wildcard(PolicyMode.Observe),
            Wildcard(PolicyMode.Draft),
            Wildcard(PolicyMode.Disabled));

        var builder = new PolicyStackSummaryBuilder(ruleStore: store);
        var summary = await builder.BuildAsync(default);

        summary.Should().NotBeNull();
        summary!.TotalRules.Should().Be(5);
        summary.LiveRules.Should().Be(2);
        summary.ObserveRules.Should().Be(1);
        summary.DraftRules.Should().Be(1);
        summary.DisabledRules.Should().Be(1);
    }

    // ---------- 4. Live > 0 -> Good band. ----------

    [Fact]
    public async Task At_least_one_live_rule_returns_Good_band()
    {
        var store = new FixedRulePolicyRuleStore(
            Wildcard(PolicyMode.Live),
            Wildcard(PolicyMode.Observe));

        var builder = new PolicyStackSummaryBuilder(ruleStore: store);
        var summary = await builder.BuildAsync(default);

        summary!.HealthBand.Should().Be(HealthBand.Good);
        summary.LiveRules.Should().Be(1);
    }

    // ---------- 5. Live == 0 but TotalRules > 0 -> Caution band. ----------

    [Fact]
    public async Task Rules_present_but_no_Live_returns_Caution_band()
    {
        var store = new FixedRulePolicyRuleStore(
            Wildcard(PolicyMode.Observe),
            Wildcard(PolicyMode.Draft));

        var builder = new PolicyStackSummaryBuilder(ruleStore: store);
        var summary = await builder.BuildAsync(default);

        summary!.HealthBand.Should().Be(HealthBand.Caution);
        summary.TotalRules.Should().Be(2);
        summary.LiveRules.Should().Be(0);
    }

    // ---------- 6. Null decision log -> DecisionsLast15m is null. ----------

    [Fact]
    public async Task DecisionsLast15m_is_null_when_decision_log_is_null()
    {
        var store = new FixedRulePolicyRuleStore(Wildcard(PolicyMode.Live));

        var builder = new PolicyStackSummaryBuilder(ruleStore: store, decisionLog: null);
        var summary = await builder.BuildAsync(default);

        summary!.DecisionsLast15m.Should().BeNull();
    }

    // ---------- 7. Decision log present -> count reflects rows inside window only. ----------

    [Fact]
    public async Task DecisionsLast15m_counts_only_rows_inside_the_15m_window()
    {
        var store = new FixedRulePolicyRuleStore(Wildcard(PolicyMode.Live));

        // Fake log returns 5 rows when called with a 15m window; rows outside
        // the window are filtered by the fake itself so the assertion isn't
        // sensitive to wall-clock drift in CI.
        var log = new FakePolicyDecisionLog(rowsInWindow: 5, rowsOutsideWindow: 2);

        var builder = new PolicyStackSummaryBuilder(ruleStore: store, decisionLog: log);
        var summary = await builder.BuildAsync(default);

        summary!.DecisionsLast15m.Should().Be(5d);
        log.LastRequestedWindow.Should().Be(PolicyStackSummaryBuilder.ShortWindow);
    }

    // ---------- 8. TimeProvider is honoured. ----------

    [Fact]
    public async Task ComputedAtUtc_matches_supplied_TimeProvider()
    {
        var store = new FixedRulePolicyRuleStore(Wildcard(PolicyMode.Live));
        // FOSS test project doesn't reference Microsoft.Extensions.TimeProvider.Testing,
        // so a hand-rolled fixed-instant TimeProvider keeps the dependency
        // surface unchanged. Same shape we'd get from FakeTimeProvider's
        // single-arg ctor.
        var fakeTime = new FixedTimeProvider(
            new DateTimeOffset(2026, 6, 9, 16, 32, 9, TimeSpan.Zero));

        var builder = new PolicyStackSummaryBuilder(
            ruleStore: store,
            timeProvider: fakeTime);
        var summary = await builder.BuildAsync(default);

        summary!.ComputedAtUtc.Should().Be(new DateTimeOffset(2026, 6, 9, 16, 32, 9, TimeSpan.Zero));
    }

    // ============================================================
    // Test helpers
    // ============================================================

    /// <summary>
    ///     Build a wildcard-scoped rule with the supplied mode. Other fields
    ///     are set to vacuous-but-valid values; the summary builder only
    ///     reads <see cref="PolicyRule.Mode"/> so the rest is decoration.
    /// </summary>
    private static PolicyRule Wildcard(PolicyMode mode)
    {
        // Vacuous AND -- evaluates true with no clauses. Cheaper than fabricating
        // a real Term predicate and keeps the test independent of SignalKeys.
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

    /// <summary>
    ///     Test double for <see cref="IPolicyDecisionLog"/> that yields a
    ///     fixed number of rows from <see cref="StreamWindowAsync"/>. The
    ///     "outsideWindow" rows are tracked but not yielded -- the builder
    ///     only consumes <see cref="StreamWindowAsync"/>, which is documented
    ///     to filter by the supplied window, so the fake mirrors that
    ///     contract instead of returning every row and forcing the builder
    ///     to filter.
    /// </summary>
    private sealed class FakePolicyDecisionLog : IPolicyDecisionLog
    {
        private readonly int _rowsInWindow;
        public TimeSpan LastRequestedWindow { get; private set; }

        public FakePolicyDecisionLog(int rowsInWindow, int rowsOutsideWindow)
        {
            _rowsInWindow = rowsInWindow;
            // rowsOutsideWindow is accepted so the test reads naturally
            // ("5 in window, 2 outside") but never affects the fake's yield --
            // StreamWindowAsync's contract is "rows inside the window only".
            _ = rowsOutsideWindow;
        }

        public ValueTask AppendAsync(PolicyDecision decision, CancellationToken ct = default) => ValueTask.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PolicyDecisionAggregate> AggregateAsync(Guid ruleId, TimeSpan window, CancellationToken ct = default)
            => throw new NotSupportedException("Summary builder does not call AggregateAsync.");

        public Task<IReadOnlyList<PolicyDecision>> GetByFingerprintAsync(string fingerprint, int max = 100, CancellationToken ct = default)
            => throw new NotSupportedException("Summary builder does not call GetByFingerprintAsync.");

        public Task<IReadOnlyList<string>> GetRecentFingerprintsAsync(int limit, CancellationToken ct = default)
            => throw new NotSupportedException("Summary builder does not call GetRecentFingerprintsAsync.");

        public async IAsyncEnumerable<PolicyDecision> StreamWindowAsync(
            TimeSpan window,
            int maxRows = 100_000,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequestedWindow = window;
            for (var i = 0; i < _rowsInWindow; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new PolicyDecision(
                    RuleId: Guid.NewGuid(),
                    WinnerRuleId: Guid.NewGuid(),
                    Matched: true,
                    RequestFingerprint: null,
                    Action: new RuleAction.Allow(),
                    Mode: PolicyMode.Live,
                    EvalLatencyTicks: 0,
                    ObservedAt: DateTimeOffset.UtcNow);
            }
            await Task.CompletedTask;
        }
    }
}
