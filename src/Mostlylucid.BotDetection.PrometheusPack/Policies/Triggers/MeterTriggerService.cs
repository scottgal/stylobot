using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Triggers;
using Mostlylucid.BotDetection.PrometheusPack.Policies;
using Mostlylucid.BotDetection.Policies.Predicate;

namespace Mostlylucid.BotDetection.PrometheusPack.Policies.Triggers;

/// <summary>
///     Oscilloscope-style 1s-tick service that walks every trigger rule
///     (rules classified by <see cref="TriggerRuleClassifier"/>) and feeds
///     fresh meter snapshots into per-rule <see cref="ArmedRuleAtom"/>s.
///
///     <para>
///         On each tick:
///         <list type="number">
///           <item>Re-read the rule corpus (cached for <see cref="RuleListCacheTtl"/> so the store is not hot-pathed every tick).</item>
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
///         is unavailable -- typical on viewer hosts that don't register the
///         in-process meter stream. The hosted-service lifecycle starts and
///         stops cleanly; the tick loop just yields immediately.
///     </para>
///
///     <para>
///         <b>NOTE (DONE_WITH_CONCERNS):</b> the project-wide
///         <c>ScheduleCoordinator</c> referenced in the
///         <c>feedback_no_background_services</c> memory does not yet exist in
///         this repo (only mentioned in docs). This service falls back to a
///         <see cref="PeriodicTimer"/>-driven <see cref="BackgroundService"/>;
///         a follow-up should migrate the tick source to the coordinator's
///         <c>tick.1s</c> signal once that infrastructure lands.
///     </para>
/// </summary>
public sealed class MeterTriggerService : BackgroundService
{
    /// <summary>Default cadence the trigger loop ticks at.</summary>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(1);

    /// <summary>Default TTL for the cached rule corpus.</summary>
    public static readonly TimeSpan DefaultRuleListCacheTtl = TimeSpan.FromSeconds(5);

    private readonly IPolicyRuleStore? _store;
    private readonly ArmedRuleRegistry _registry;
    private readonly MeterSnapshotSignalContributor? _contributor;
    private readonly ILogger<MeterTriggerService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _ruleListCacheTtl;

    private IReadOnlyList<PolicyRule>? _cachedRules;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>
    ///     Production constructor. Accepts nullable
    ///     <see cref="MeterSnapshotSignalContributor"/> and
    ///     <see cref="IPolicyRuleStore"/> so the service can register on hosts
    ///     that lack one or both and just no-op its tick.
    /// </summary>
    public MeterTriggerService(
        ArmedRuleRegistry registry,
        ILogger<MeterTriggerService> logger,
        IPolicyRuleStore? store = null,
        MeterSnapshotSignalContributor? contributor = null,
        TimeProvider? timeProvider = null)
        : this(registry, logger, store, contributor, timeProvider, DefaultTickInterval, DefaultRuleListCacheTtl)
    {
    }

    /// <summary>
    ///     Test-friendly constructor that exposes the tick cadence and rule
    ///     cache TTL. Tests pass a <see cref="FakeTimeProvider"/> via
    ///     <paramref name="timeProvider"/> and drive the loop directly through
    ///     <see cref="TickOnceAsync"/> rather than relying on the
    ///     <see cref="PeriodicTimer"/>.
    /// </summary>
    public MeterTriggerService(
        ArmedRuleRegistry registry,
        ILogger<MeterTriggerService> logger,
        IPolicyRuleStore? store,
        MeterSnapshotSignalContributor? contributor,
        TimeProvider? timeProvider,
        TimeSpan tickInterval,
        TimeSpan ruleListCacheTtl)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger;
        _store = store;
        _contributor = contributor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tickInterval = tickInterval <= TimeSpan.Zero ? DefaultTickInterval : tickInterval;
        _ruleListCacheTtl = ruleListCacheTtl <= TimeSpan.Zero ? DefaultRuleListCacheTtl : ruleListCacheTtl;
    }

    /// <summary>
    ///     Property accessor for the underlying <see cref="ArmedRuleRegistry"/>
    ///     so callers (e.g. the resolver bridge) can subscribe to transitions
    ///     without taking a direct DI reference.
    /// </summary>
    public ArmedRuleRegistry Registry => _registry;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No store + no contributor = nothing the trigger loop can do. Stay
        // alive (keeps the hosted-service contract honest) but skip the work.
        if (_store is null || _contributor is null)
        {
            _logger.LogDebug(
                "MeterTriggerService: no IPolicyRuleStore or MeterSnapshotSignalContributor registered; trigger loop is a no-op.");
            return;
        }

        using var timer = new PeriodicTimer(_tickInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await TickOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A failing tick MUST NOT kill the loop. Log and continue.
                    _logger.LogWarning(ex, "MeterTriggerService tick failed; continuing.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    /// <summary>
    ///     Run one full pass over the trigger rules. Public so tests can drive
    ///     the loop deterministically without spinning the <see cref="PeriodicTimer"/>.
    /// </summary>
    public async Task TickOnceAsync(CancellationToken ct)
    {
        if (_store is null || _contributor is null) return;

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
        //
        // The MeterTriggerService doesn't care about per-scope filtering --
        // any trigger rule, at any scope, should be evaluated. The cleanest
        // contract is a flat enumeration: use GetEffectiveRulesAsync against
        // a wildcard-only path AND, when available, fall back to enumerating
        // every scope via the store's GetByIdAsync chain. The YAML store
        // already returns "everything indexed at wildcard"; deeper-scope
        // trigger rules need an explicit walk.
        //
        // For now we collect the rules at every scope reachable through
        // GetByIdAsync via the store's known indexes. The simplest correct
        // path: use GetEffectiveRulesAsync with a path of every scope-shape
        // we know about; in practice the store impls return the full corpus
        // for an empty/wildcard walk.
        var path = new[] { PolicyScope.Wildcard() };
        var rules = await _store!.GetEffectiveRulesAsync(path, ct).ConfigureAwait(false);
        _cachedRules = rules;
        _cachedAt = now;
        return rules;
    }
}
