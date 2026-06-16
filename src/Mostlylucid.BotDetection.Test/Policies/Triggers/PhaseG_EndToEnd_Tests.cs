using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Triggers;
using Mostlylucid.BotDetection.PrometheusPack.Policies;
using Mostlylucid.BotDetection.PrometheusPack.Policies.Triggers;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using PredicateNode = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Triggers;

/// <summary>
///     G6: end-to-end loop. Drive a meter from below->above->below threshold,
///     tick the trigger service, and confirm the armed-rule registry +
///     resolver bridge land the rule in the effective list while armed and
///     drop it after recovery.
/// </summary>
public sealed class PhaseG_EndToEnd_Tests
{
    [Fact]
    public async Task End_to_end_loop_arms_recovers_and_appears_in_effective_rules()
    {
        // --- Arrange ----------------------------------------------------------
        // Trigger options pinned to short windows so the test is deterministic
        // and quick. The classifier still treats them as a trigger rule.
        var sustain = TimeSpan.FromMilliseconds(100);
        var recover = TimeSpan.FromMilliseconds(200);
        var trigger = new RuleTriggerOptions(sustain, recover);

        var ruleId = Guid.NewGuid();
        var rule = new PolicyRule(
            ruleId,
            PolicyScope.Wildcard(),
            Priority: 100,
            Predicate: new PredicateNode.Term("meter.test.foo.current", PredicateOp.Gt, 50m),
            Action: new PolicyAction.Block(),
            Mode: PolicyMode.Live,
            Notes: "G6 end-to-end test",
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid(),
            AutoPromoteAt: null,
            Trigger: trigger);

        var store = new SingleRulePolicyRuleStore(rule);
        var meterStream = new MutableFakeMeterStream();
        meterStream.SetMeter("test.foo", current: 0d);

        // Wave 4: contributor + catalog source read from MeterSignalsAtom.
        // Tests pump the atom rebuild manually so the meter snapshot tracks
        // the meterStream's current state at each TickOnceAsync call.
        var atom = new MeterSignalsAtom(meterStream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();
        var contributor = new MeterSnapshotSignalContributor(atom);
        var registry = new ArmedRuleRegistry();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

        var service = new MeterTriggerService(
            registry,
            NullLogger<MeterTriggerService>.Instance,
            store,
            contributor,
            clock,
            tickInterval: TimeSpan.FromMilliseconds(50),
            ruleListCacheTtl: TimeSpan.FromMilliseconds(10));

        var resolver = new DefaultPolicyResolver(
            store,
            contributors: new[] { (Mostlylucid.BotDetection.Policies.Signals.ISignalContributor)contributor },
            armedRegistry: registry);

        var transitions = new List<ArmedRuleTransition>();
        registry.OnTransition += (_, t) => transitions.Add(t);

        // --- Act: phase 1, below threshold ------------------------------------
        // Tick: meter is 0, predicate false, no transition.
        await service.TickOnceAsync(CancellationToken.None);
        Assert.False(registry.GetOrAdd(ruleId).IsArmed);

        // --- Act: phase 2, meter pops above threshold -------------------------
        meterStream.SetMeter("test.foo", current: 100d);
        await atom.RebuildNowAsync();

        // First over-threshold tick: starts the sustain streak, no transition.
        await service.TickOnceAsync(CancellationToken.None);
        Assert.False(registry.GetOrAdd(ruleId).IsArmed);

        // Advance the clock past the sustain window and tick again.
        clock.Advance(sustain + TimeSpan.FromMilliseconds(10));
        await service.TickOnceAsync(CancellationToken.None);

        // --- Assert: ARMED transition landed ----------------------------------
        Assert.True(registry.GetOrAdd(ruleId).IsArmed);
        Assert.Single(transitions);
        Assert.Equal(ruleId, transitions[0].RuleId);
        Assert.True(transitions[0].NowArmed);

        // --- Assert: armed rule appears in effective list (empty per-request) -
        var effective = await resolver.EffectiveWithContextAsync(
            PolicyScope.Wildcard(),
            requestSignals: new Dictionary<string, object?>());

        Assert.Single(effective);
        Assert.Equal(ruleId, effective[0].Rule.Id);
        Assert.True(effective[0].IsArmed);

        // --- Act: phase 3, meter drops back below threshold -------------------
        meterStream.SetMeter("test.foo", current: 10d);
        await atom.RebuildNowAsync();

        // First below-threshold tick: starts the recover streak, still armed.
        await service.TickOnceAsync(CancellationToken.None);
        Assert.True(registry.GetOrAdd(ruleId).IsArmed);

        // Advance past the recover window and tick.
        clock.Advance(recover + TimeSpan.FromMilliseconds(10));
        await service.TickOnceAsync(CancellationToken.None);

        // --- Assert: UNARMED transition landed --------------------------------
        Assert.False(registry.GetOrAdd(ruleId).IsArmed);
        Assert.Equal(2, transitions.Count);
        Assert.False(transitions[1].NowArmed);

        // --- Assert: armed rule no longer in effective list -------------------
        var effectiveAfter = await resolver.EffectiveWithContextAsync(
            PolicyScope.Wildcard(),
            requestSignals: new Dictionary<string, object?>());
        Assert.Empty(effectiveAfter);
    }

    [Fact]
    public async Task Resolver_armed_rules_sort_above_regular_rules()
    {
        var triggerRuleId = Guid.NewGuid();
        var triggerRule = new PolicyRule(
            triggerRuleId,
            PolicyScope.Wildcard(),
            Priority: 100,
            Predicate: new PredicateNode.Term("meter.test.bar.current", PredicateOp.Gt, 50m),
            Action: new PolicyAction.Block(),
            Mode: PolicyMode.Live,
            Notes: "trigger rule",
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid(),
            AutoPromoteAt: null,
            Trigger: new RuleTriggerOptions(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)));

        var regularRuleId = Guid.NewGuid();
        // An always-true regular rule attached at the same scope.
        var regularRule = new PolicyRule(
            regularRuleId,
            PolicyScope.Wildcard(),
            Priority: 50,
            Predicate: new PredicateNode.And(Array.Empty<PredicateNode>()), // vacuously true
            Action: new PolicyAction.Allow(),
            Mode: PolicyMode.Live,
            Notes: "regular rule",
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());

        var store = new MultiRulePolicyRuleStore(triggerRule, regularRule);
        var meterStream = new MutableFakeMeterStream();
        meterStream.SetMeter("test.bar", current: 100d);
        var atom = new MeterSignalsAtom(meterStream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();
        var contributor = new MeterSnapshotSignalContributor(atom);
        var registry = new ArmedRuleRegistry();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var service = new MeterTriggerService(
            registry,
            NullLogger<MeterTriggerService>.Instance,
            store,
            contributor,
            clock,
            tickInterval: TimeSpan.FromMilliseconds(25),
            ruleListCacheTtl: TimeSpan.FromMilliseconds(10));

        // Drive past sustain.
        await service.TickOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(80));
        await service.TickOnceAsync(CancellationToken.None);
        Assert.True(registry.GetOrAdd(triggerRuleId).IsArmed);

        var resolver = new DefaultPolicyResolver(
            store,
            contributors: new[] { (Mostlylucid.BotDetection.Policies.Signals.ISignalContributor)contributor },
            armedRegistry: registry);

        var effective = await resolver.EffectiveWithContextAsync(
            PolicyScope.Wildcard(),
            requestSignals: new Dictionary<string, object?>());

        Assert.Equal(2, effective.Count);
        // Armed rule sorts above regular even though same wildcard scope.
        Assert.True(effective[0].IsArmed);
        Assert.Equal(triggerRuleId, effective[0].Rule.Id);
        Assert.False(effective[1].IsArmed);
        Assert.Equal(regularRuleId, effective[1].Rule.Id);
    }

    /// <summary>
    ///     Wave 6: <see cref="MeterTriggerService"/> previously walked the rule
    ///     corpus via <c>GetEffectiveRulesAsync([Wildcard])</c>, which silently
    ///     dropped every trigger rule attached to a non-wildcard scope (a
    ///     <see cref="PolicyScope.Domain"/> rule never appeared in the
    ///     wildcard-only walk and so its meter predicate never armed). The fix
    ///     added <see cref="IPolicyRuleStore.GetAllRulesAsync"/> + had
    ///     <c>LoadRulesAsync</c> use it. This test pins the new behaviour: a
    ///     trigger rule on a Domain scope, with a meter-only predicate, must
    ///     be picked up by the trigger sweep and armed.
    /// </summary>
    [Fact]
    public async Task MeterTriggerService_picks_up_non_wildcard_trigger_rules()
    {
        var sustain = TimeSpan.FromMilliseconds(50);
        var recover = TimeSpan.FromMilliseconds(100);

        var ruleId = Guid.NewGuid();
        // Scope: a non-wildcard host. Pre-Wave-6, this never showed up in the
        // trigger sweep -- the wildcard-only walk skipped it entirely.
        var rule = new PolicyRule(
            ruleId,
            PolicyScope.Domain("x.com"),
            Priority: 100,
            Predicate: new PredicateNode.Term("meter.test.baz.current", PredicateOp.Gt, 10m),
            Action: new PolicyAction.Block(),
            Mode: PolicyMode.Live,
            Notes: "non-wildcard trigger rule",
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid(),
            AutoPromoteAt: null,
            Trigger: new RuleTriggerOptions(sustain, recover));

        var store = new SingleRulePolicyRuleStore(rule);
        var meterStream = new MutableFakeMeterStream();
        // Above the predicate threshold.
        meterStream.SetMeter("test.baz", current: 100d);

        var atom = new MeterSignalsAtom(meterStream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();
        var contributor = new MeterSnapshotSignalContributor(atom);
        var registry = new ArmedRuleRegistry();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

        var service = new MeterTriggerService(
            registry,
            NullLogger<MeterTriggerService>.Instance,
            store,
            contributor,
            clock,
            tickInterval: TimeSpan.FromMilliseconds(25),
            ruleListCacheTtl: TimeSpan.FromMilliseconds(10));

        // First tick: starts the sustain streak.
        await service.TickOnceAsync(CancellationToken.None);
        Assert.False(registry.GetOrAdd(ruleId).IsArmed);

        // Advance the clock past sustain and tick again -- this is the moment
        // the rule arms IF the trigger sweep saw it at all. Before the
        // GetAllRulesAsync fix, the rule was never enumerated and the atom
        // stayed unarmed forever.
        clock.Advance(sustain + TimeSpan.FromMilliseconds(10));
        await service.TickOnceAsync(CancellationToken.None);

        Assert.True(registry.GetOrAdd(ruleId).IsArmed);
    }

    // -------- Test doubles -----------------------------------------------------

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class SingleRulePolicyRuleStore : IPolicyRuleStore
    {
        private readonly PolicyRule _rule;

        public SingleRulePolicyRuleStore(PolicyRule rule) => _rule = rule;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(
                scope == _rule.Scope ? new[] { _rule } : Array.Empty<PolicyRule>());

        public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(IReadOnlyList<PolicyScope> scopePath, CancellationToken ct = default)
        {
            foreach (var s in scopePath)
                if (s == _rule.Scope) return Task.FromResult<IReadOnlyList<PolicyRule>>(new[] { _rule });
            return Task.FromResult<IReadOnlyList<PolicyRule>>(Array.Empty<PolicyRule>());
        }

        public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PolicyRule?>(id == _rule.Id ? _rule : null);

        public Task<IReadOnlyList<PolicyRule>> GetAllRulesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(new[] { _rule });

#pragma warning disable CS0067
        public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;
#pragma warning restore CS0067
    }

    private sealed class MultiRulePolicyRuleStore : IPolicyRuleStore
    {
        private readonly PolicyRule[] _rules;

        public MultiRulePolicyRuleStore(params PolicyRule[] rules) => _rules = rules;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(_rules.Where(r => r.Scope == scope).ToArray());

        public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(IReadOnlyList<PolicyScope> scopePath, CancellationToken ct = default)
        {
            var seen = new HashSet<Guid>();
            var list = new List<PolicyRule>();
            foreach (var s in scopePath)
            foreach (var r in _rules)
                if (r.Scope == s && seen.Add(r.Id))
                    list.Add(r);
            return Task.FromResult<IReadOnlyList<PolicyRule>>(list);
        }

        public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_rules.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<PolicyRule>> GetAllRulesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(_rules.ToArray());

#pragma warning disable CS0067
        public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;
#pragma warning restore CS0067
    }

    /// <summary>
    ///     Minimal <see cref="IMeterStream"/> double whose meters can be
    ///     mutated mid-test. Mirrors the <c>FakeMeterStream</c> pattern used
    ///     by <c>MeterSnapshotSignalContributorTests</c>; kept private here
    ///     because the registry test needs different mutability semantics
    ///     (re-assign current on a known meter).
    /// </summary>
    private sealed class MutableFakeMeterStream : IMeterStream
    {
        private readonly Dictionary<string, MeterCatalogEntry> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _currents = new(StringComparer.Ordinal);

        public void SetMeter(string name, double current)
        {
            _entries[name] = new MeterCatalogEntry(name, name, MeterKind.Gauge, null, null);
            _currents[name] = current;
        }

        public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(_entries.Values.ToArray());

        public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
        {
            if (!_currents.TryGetValue(meterName, out var current))
                return Task.FromResult<MeterTimeSeries?>(null);
            // single-bucket series: the contributor reads .current only for the
            // facets we care about in these tests.
            var now = DateTimeOffset.UtcNow;
            var ts = new MeterTimeSeries(
                meterName,
                MeterKind.Gauge,
                new[] { now },
                new[] { current },
                current,
                null,
                null);
            return Task.FromResult<MeterTimeSeries?>(ts);
        }
    }
}
