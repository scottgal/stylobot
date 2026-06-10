using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Triggers;

/// <summary>
///     Process-wide registry of <see cref="ArmedRuleAtom"/>s keyed by rule id.
///     The MeterTriggerService looks up or creates atoms per
///     trigger rule per tick; the <c>DefaultPolicyResolver</c> consults the
///     registry on each request to find the currently-armed rules to merge
///     into the effective list.
///
///     <para>
///         Subscribers wire up <see cref="OnTransition"/> to observe
///         UNARMED -> ARMED / ARMED -> UNARMED edges. The registry never
///         throws from a transition notification; subscriber exceptions are
///         caught so a faulty audit sink cannot stop the trigger loop.
///     </para>
///
///     <para>
///         <see cref="CurrentlyArmedAsync"/> takes an <see cref="IPolicyRuleStore"/>
///         so the snapshot can resolve atom ids back into rules without the
///         registry holding a hard reference to the store -- handy for hosts
///         that swap stores out (commercial Postgres pack vs FOSS YAML).
///     </para>
/// </summary>
public sealed class ArmedRuleRegistry
{
    private readonly ConcurrentDictionary<Guid, ArmedRuleAtom> _atoms = new();
    private readonly ILogger<ArmedRuleRegistry>? _logger;

    /// <summary>
    ///     Default constructor. Subscriber faults are silently swallowed --
    ///     this is the historical shape kept for tests / hosts that pre-date
    ///     the optional logger.
    /// </summary>
    public ArmedRuleRegistry()
    {
        _logger = null;
    }

    /// <summary>
    ///     Wave 5 constructor: takes an optional <see cref="ILogger"/> so a
    ///     faulting transition subscriber surfaces at Warning instead of being
    ///     dropped on the floor. The catch + isolate semantics are unchanged.
    ///     One bad subscriber MUST NOT take down the trigger loop, but the
    ///     registry can now name the rule id and exception type so the fault
    ///     is investigable.
    /// </summary>
    public ArmedRuleRegistry(ILogger<ArmedRuleRegistry>? logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Raised after a transition is recorded on any atom. The handler runs
    ///     on the calling thread (the trigger service's tick thread). Throwing
    ///     handlers are caught and, when an <see cref="ILogger"/> was passed
    ///     to the constructor, logged at Warning. The trigger loop must stay
    ///     alive even when a faulty subscriber faults, so the exception never
    ///     propagates past <see cref="RaiseTransition"/>.
    /// </summary>
    public event EventHandler<ArmedRuleTransition>? OnTransition;

    /// <summary>
    ///     Look up (or create) the atom for <paramref name="ruleId"/>. New
    ///     atoms start UNARMED with no observation history.
    /// </summary>
    public ArmedRuleAtom GetOrAdd(Guid ruleId) =>
        _atoms.GetOrAdd(ruleId, static _ => new ArmedRuleAtom());

    /// <summary>
    ///     Try to peek the atom for <paramref name="ruleId"/> without creating
    ///     one. Returns <c>null</c> when no atom has been registered yet.
    /// </summary>
    public ArmedRuleAtom? TryGet(Guid ruleId) =>
        _atoms.TryGetValue(ruleId, out var atom) ? atom : null;

    /// <summary>
    ///     Snapshot view of every rule id currently tracked, ARMED or not.
    ///     Used by the trigger service to GC atoms whose rule no longer exists
    ///     in the store.
    /// </summary>
    public IReadOnlyCollection<Guid> TrackedRuleIds()
    {
        // ConcurrentDictionary.Keys is a thread-safe snapshot already; project
        // through ToArray() so the caller sees a stable list.
        return _atoms.Keys.ToArray();
    }

    /// <summary>
    ///     Remove the atom for <paramref name="ruleId"/>. Returns the atom that
    ///     was removed (or <c>null</c> when none was present). The trigger
    ///     service calls this for rules that have been deleted from the store.
    /// </summary>
    public ArmedRuleAtom? Remove(Guid ruleId)
    {
        _atoms.TryRemove(ruleId, out var atom);
        return atom;
    }

    /// <summary>
    ///     Snapshot of the rules currently ARMED. Resolves atom ids back to
    ///     <see cref="PolicyRule"/>s via <paramref name="store"/>; rules that
    ///     are no longer in the store are silently skipped.
    /// </summary>
    public async Task<IReadOnlyCollection<PolicyRule>> CurrentlyArmedAsync(
        IPolicyRuleStore store,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var armed = new List<PolicyRule>();
        foreach (var kv in _atoms)
        {
            if (!kv.Value.IsArmed) continue;
            var rule = await store.GetByIdAsync(kv.Key, ct).ConfigureAwait(false);
            if (rule is not null) armed.Add(rule);
        }
        return armed;
    }

    /// <summary>
    ///     Snapshot of just the rule ids currently ARMED. Cheaper than
    ///     <see cref="CurrentlyArmedAsync"/> when the caller only needs the id
    ///     set (typical hot-path use case in the resolver).
    /// </summary>
    public IReadOnlyCollection<Guid> CurrentlyArmedIds()
    {
        var armed = new List<Guid>();
        foreach (var kv in _atoms)
            if (kv.Value.IsArmed)
                armed.Add(kv.Key);
        return armed;
    }

    /// <summary>
    ///     Raise <see cref="OnTransition"/> for a freshly-observed armed-state
    ///     transition. Called by MeterTriggerService when
    ///     <see cref="ArmedRuleAtom.Observe"/> returns <c>true</c>.
    /// </summary>
    public void RaiseTransition(Guid ruleId, bool nowArmed, DateTimeOffset at)
    {
        var handler = OnTransition;
        if (handler is null) return;

        // Fan out to each subscriber individually so one faulting handler
        // doesn't strand the others. The combined invocation list is the
        // historical event-handler shape; walking it explicitly preserves
        // hand-isolation semantics. The ILogger, when present, surfaces the
        // fault so a stuck audit sink is investigable instead of silent.
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<ArmedRuleTransition>)subscriber)(
                    this,
                    new ArmedRuleTransition(ruleId, nowArmed, at));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "ArmedRuleRegistry transition subscriber {Subscriber} threw {ExceptionType} for rule {RuleId} (nowArmed={NowArmed}); subscriber isolated.",
                    subscriber.Method.DeclaringType?.FullName + "." + subscriber.Method.Name,
                    ex.GetType().FullName,
                    ruleId,
                    nowArmed);
            }
        }
    }
}
