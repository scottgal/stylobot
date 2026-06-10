using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;

namespace Mostlylucid.BotDetection.PrometheusPack.Policies;

/// <summary>
///     Pumps the most-recent meter snapshot held by
///     <see cref="MeterSignalsAtom"/> into the per-request signals map under
///     the keys:
///     <list type="bullet">
///         <item><description><c>meter.{name}.current</c>      -- last observed value</description></item>
///         <item><description><c>meter.{name}.p50</c>          -- histogram only; absent for other kinds</description></item>
///         <item><description><c>meter.{name}.p99</c>          -- histogram only</description></item>
///         <item><description><c>meter.{name}.delta_1m</c>     -- sum of bucket values across the last 1 minute</description></item>
///         <item><description><c>meter.{name}.delta_5m</c>     -- sum of bucket values across the last 5 minutes</description></item>
///     </list>
///     <para>
///         Names with non-policy-safe characters are not normalised here --
///         the editor / catalog already shows what's queryable; if the user
///         writes a predicate against a key that doesn't exist it returns
///         the silent-miss false the evaluator gives every unknown facet.
///     </para>
///     <para>
///         The contributor is the Phase F bridge between the meter stream
///         (Phase E) and the predicate evaluator. The
///         <see cref="DefaultPolicyResolver"/> invokes it once per
///         <c>EffectiveWithContextAsync</c> call -- the snapshot-at-request-
///         entry semantics fall out of the atom's Tick10s rebuild, not from
///         per-request awaits.
///     </para>
///     <para>
///         <b>Wave 4 architectural-drift remediation:</b> the contributor no
///         longer awaits <see cref="IMeterStream.GetAsync"/> on the request
///         hot path. The previous shape issued two awaits per catalogued
///         meter on every policy resolution; with the default
///         <c>MaxTrackedMeters=128</c> that was ~256 awaits per request just
///         to populate the signal bag. The
///         <see cref="MeterSignalsAtom"/> now rebuilds the canonical
///         signals dictionary once every 10s and the contributor's per-request
///         cost is a single O(N) dictionary merge.
///     </para>
/// </summary>
public sealed class MeterSnapshotSignalContributor : ISignalContributor
{
    /// <summary>The bucket count used for both the 1m and 5m windows.</summary>
    /// <remarks>
    ///     Kept for binary-compat with call sites that referenced the constant
    ///     directly before the Wave 4 rebuild moved the bucketing into
    ///     <see cref="MeterSignalsAtom"/>.
    /// </remarks>
    internal const int WindowBuckets = MeterSignalsAtom.WindowBuckets;

    private readonly MeterSignalsAtom _atom;

    public MeterSnapshotSignalContributor(MeterSignalsAtom atom)
    {
        _atom = atom ?? throw new ArgumentNullException(nameof(atom));
    }

    /// <inheritdoc />
    public Task ContributeAsync(IDictionary<string, object?> signals, CancellationToken ct)
    {
        // Single lock-free read of the snapshot reference. The atom guarantees
        // the dictionary it returns is never mutated in place -- the next
        // tick publishes a NEW reference -- so we can iterate without holding
        // anything.
        var snapshot = _atom.CurrentSnapshot;
        if (snapshot.Count == 0) return Task.CompletedTask;

        foreach (var kv in snapshot)
        {
            // Existing keys in the per-request bag win; the meter atom must not
            // shadow upstream signals (e.g. a contributor that already wrote
            // meter.foo.current with a per-request override).
            if (!signals.ContainsKey(kv.Key))
                signals[kv.Key] = kv.Value;
        }
        return Task.CompletedTask;
    }
}
