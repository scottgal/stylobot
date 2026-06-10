using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Triggers;
using Mostlylucid.BotDetection.PrometheusPack.Policies;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.PrometheusPack.Policies.Triggers;

/// <summary>
///     Oscilloscope-style trigger evaluator. Subscribes to
///     <see cref="TickCadence.Tick1s" /> on the project's
///     <see cref="IScheduleCoordinator" /> and, on each tick, walks every
///     trigger rule (classified by <see cref="TriggerRuleClassifier" />) feeding
///     a fresh meter snapshot into per-rule <see cref="ArmedRuleAtom" />s.
///
///     <para>
///         On each tick:
///         <list type="number">
///           <item>Re-read the rule corpus (cached for <see cref="DefaultRuleListCacheTtl"/> so the store is not hot-pathed every tick).</item>
///           <item>Filter to trigger rules.</item>
///           <item>Build a meter signals dict via <see cref="MeterSnapshotSignalContributor"/>.</item>
///           <item>Evaluate each rule's predicate against the dict.</item>
///           <item>Feed the result into the rule's atom via <see cref="ArmedRuleAtom.Observe"/>.</item>
///           <item>Raise <see cref="ArmedRuleRegistry.OnTransition"/> when the atom transitions.</item>
///           <item>GC atoms whose rule is no longer in the store.</item>
///         </list>
///     </para>
///
///     <para>
///         The service is a graceful no-op when <see cref="MeterSnapshotSignalContributor"/>
///         or <see cref="IPolicyRuleStore"/> is unavailable -- typical on viewer
///         hosts that don't register the in-process meter stream. The subscription
///         handler short-circuits in that case; the coordinator keeps firing
///         ticks at other subscribers untouched.
///     </para>
///
///     <para>
///         <b>Wave 2 architectural-drift remediation:</b> this service no longer
///         inherits <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
///         and no longer owns a <see cref="PeriodicTimer"/>. The tick source is
///         <see cref="IScheduleCoordinator"/>'s single, drift-free wall-clock
///         cadence. See <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
public sealed class MeterTriggerService : IDisposable
{
    /// <summary>
    ///     Cadence the trigger loop ticks at. The coordinator's
    ///     <see cref="TickCadence.Tick1s"/> is the canonical 1Hz oscilloscope
    ///     tick; kept here as a constant for documentation parity with the
    ///     pre-migration class.
    /// </summary>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(1);

    /// <summary>Default TTL for the cached rule corpus.</summary>
    public static readonly TimeSpan DefaultRuleListCacheTtl = TimeSpan.FromSeconds(5);

    private readonly IPolicyRuleStore? _store;
    private readonly ArmedRuleRegistry _registry;
    private readonly MeterSnapshotSignalContributor? _contributor;
    private readonly ILogger<MeterTriggerService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ruleListCacheTtl;

    private readonly IDisposable _subscription;
    private int _disposed;

    private IReadOnlyList<PolicyRule>? _cachedRules;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>
    ///     Production constructor. The trigger service is meaningless without
    ///     ticks, so <paramref name="coordinator"/> is non-nullable. The store
    ///     and contributor stay nullable so the service can register on hosts
    ///     that lack one or both and just no-op its tick handler.
    /// </summary>
    public MeterTriggerService(
        IScheduleCoordinator coordinator,
        ArmedRuleRegistry registry,
        ILogger<MeterTriggerService> logger,
        IPolicyRuleStore? store = null,
        MeterSnapshotSignalContributor? contributor = null,
        TimeProvider? timeProvider = null)
        : this(coordinator, registry, logger, store, contributor, timeProvider, DefaultRuleListCacheTtl)
    {
    }

    /// <summary>
    ///     Test-friendly constructor that exposes the rule-cache TTL. The tick
    ///     cadence is fixed (Tick1s on the coordinator) but tests usually pass
    ///     <see cref="NullScheduleCoordinator.Instance"/> and drive the loop
    ///     directly through <see cref="TickOnceAsync(CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    ///     Backwards-compatible signature note: the pre-Wave-2 test rig passed
    ///     a <c>tickInterval</c> argument to a now-removed
    ///     <see cref="PeriodicTimer"/>. We keep the TTL parameter so existing
    ///     tests don't have to change beyond inserting the coordinator and
    ///     dropping the cadence.
    /// </remarks>
    public MeterTriggerService(
        IScheduleCoordinator coordinator,
        ArmedRuleRegistry registry,
        ILogger<MeterTriggerService> logger,
        IPolicyRuleStore? store,
        MeterSnapshotSignalContributor? contributor,
        TimeProvider? timeProvider,
        TimeSpan ruleListCacheTtl)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger;
        _store = store;
        _contributor = contributor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ruleListCacheTtl = ruleListCacheTtl <= TimeSpan.Zero ? DefaultRuleListCacheTtl : ruleListCacheTtl;

        _subscription = coordinator.Subscribe(
            TickCadence.Tick1s,
            "MeterTriggerService",
            CostHint.Medium,
            OnTickAsync);
    }

    // ----- Test-shim constructors --------------------------------------------
    // These mirror the pre-Wave-2 signatures so existing tests that constructed
    // `MeterTriggerService` directly (e.g. PhaseG_EndToEnd_Tests) keep compiling
    // unchanged. They route to NullScheduleCoordinator so the subscription is a
    // no-op and the test drives ticks via TickOnceAsync.

    /// <summary>
    ///     Test-shim constructor matching the pre-Wave-2 signature. Routes the
    ///     subscription to <see cref="NullScheduleCoordinator.Instance"/> so the
    ///     coordinator never fires and the test drives ticks via
    ///     <see cref="TickOnceAsync(CancellationToken)"/>. The
    ///     <paramref name="tickInterval"/> argument is ignored -- it predates
    ///     <see cref="ScheduleCoordinator"/> and is kept only so existing
    ///     test rigs don't have to be rewritten.
    /// </summary>
    public MeterTriggerService(
        ArmedRuleRegistry registry,
        ILogger<MeterTriggerService> logger,
        IPolicyRuleStore? store,
        MeterSnapshotSignalContributor? contributor,
        TimeProvider? timeProvider,
        TimeSpan tickInterval,
        TimeSpan ruleListCacheTtl)
        : this(NullScheduleCoordinator.Instance, registry, logger, store, contributor, timeProvider, ruleListCacheTtl)
    {
        _ = tickInterval; // pre-migration; the coordinator owns cadence now.
    }

    /// <summary>
    ///     Property accessor for the underlying <see cref="ArmedRuleRegistry"/>
    ///     so callers (e.g. the resolver bridge) can subscribe to transitions
    ///     without taking a direct DI reference.
    /// </summary>
    public ArmedRuleRegistry Registry => _registry;

    /// <summary>
    ///     The coordinator's tick handler. Called by
    ///     <see cref="IScheduleCoordinator.Subscribe"/>'s single-flight gate at
    ///     <see cref="TickCadence.Tick1s"/>; a slow tick skips the next instead
    ///     of overlapping.
    /// </summary>
    private Task OnTickAsync(DateTimeOffset _, CancellationToken ct)
    {
        if (_disposed != 0) return Task.CompletedTask;
        return TickOnceAsync(ct);
    }

    /// <summary>
    ///     Run one full pass over the trigger rules. Public so tests can drive
    ///     the loop deterministically without going through the coordinator.
    /// </summary>
    public async Task TickOnceAsync(CancellationToken ct)
    {
        if (_store is null || _contributor is null)
        {
            _logger.LogDebug(
                "MeterTriggerService: no IPolicyRuleStore or MeterSnapshotSignalContributor registered; trigger loop is a no-op.");
            return;
        }

        var now = _timeProvider.GetUtcNow();

        // 1. Refresh the rule corpus (cached for _ruleListCacheTtl).
        var rules = await LoadRulesAsync(now, ct).ConfigureAwait(false);

        // 2. Build the meter signals dict ONCE for this tick. Every trigger
        //    rule sees the same snapshot, which matches the
        //    snapshot-at-request-entry semantics for per-request resolution.
        var meterSignals = new Dictionary<string, object?>(capacity: 32);
        await _contributor.ContributeAsync(meterSignals, ct).ConfigureAwait(false);

        // 3. Walk the trigger rules, drive their atoms, and collect transitions.
        var triggerRuleIds = new HashSet<Guid>();
        foreach (var rule in rules)
        {
            if (!TriggerRuleClassifier.IsTriggerRule(rule)) continue;
            triggerRuleIds.Add(rule.Id);

            var predicateTrue = PredicateEvaluator.Evaluate(rule.Predicate, meterSignals);

            var atom = _registry.GetOrAdd(rule.Id);
            var trigger = rule.Trigger!; // IsTriggerRule guarantees non-null
            var transitioned = atom.Observe(
                predicateTrue,
                now,
                trigger.EffectiveSustainFor,
                trigger.EffectiveRecoverAfter);

            if (transitioned)
            {
                _logger.LogInformation(
                    "Policy trigger transition: rule={RuleId} armed={NowArmed} at={At}",
                    rule.Id,
                    atom.IsArmed,
                    now);
                _registry.RaiseTransition(rule.Id, atom.IsArmed, now);
            }
        }

        // 4. GC orphans: any atom whose rule id is no longer a trigger rule.
        foreach (var trackedId in _registry.TrackedRuleIds())
        {
            if (!triggerRuleIds.Contains(trackedId))
                _registry.Remove(trackedId);
        }
    }

    private async Task<IReadOnlyList<PolicyRule>> LoadRulesAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_cachedRules is not null && now - _cachedAt < _ruleListCacheTtl)
            return _cachedRules;

        // GetEffectiveRulesAsync(wildcard-walk) returns the full corpus the
        // resolver would see for a wildcard scope -- which is the union of
        // every rule attached at the Wildcard scope. To get the WHOLE corpus
        // we pass an empty path and rely on the store's semantics, OR we walk
        // every scope known to the store. Simpler: pass [Wildcard] and rely
        // on the store impls to include every rule indexed at wildcard. For
        // FOSS YamlPolicyRuleStore that is exactly the wildcard-attached
        // rules; rules attached at deeper scopes are still candidates because
        // EffectiveAsync walks scopes from specific->broad and concatenates.
        var path = new[] { PolicyScope.Wildcard() };
        var rules = await _store!.GetEffectiveRulesAsync(path, ct).ConfigureAwait(false);
        _cachedRules = rules;
        _cachedAt = now;
        return rules;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* swallow -- coordinator already torn down */ }
    }
}
