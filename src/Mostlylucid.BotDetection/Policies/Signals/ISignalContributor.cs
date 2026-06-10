namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     Contributes additional signals into the per-request signals map at
///     resolver-call time. Snapshot-at-request-entry semantics: each
///     contributor is invoked ONCE per resolver call, so downstream signal
///     consumers see stable values for the lifetime of the request.
///
///     <para>
///         Phase F (meter signals into predicate evaluator) is the first
///         consumer; the <c>MeterSnapshotSignalContributor</c> in
///         <c>Mostlylucid.BotDetection.PrometheusPack</c> pumps the latest
///         meter snapshot under the <c>meter.{name}.{facet}</c> family so
///         composite predicates like
///         <c>score.bot_probability &gt; 0.9 AND meter.system.cpu_load.current &gt; 80</c>
///         evaluate naturally against the union of per-request and meter
///         signals.
///     </para>
/// </summary>
public interface ISignalContributor
{
    /// <summary>
    ///     Add signals to <paramref name="signals" />. Implementations must NOT
    ///     remove or overwrite existing keys -- per-request signals always win
    ///     over contributor-supplied ones. Use <c>TryAdd</c> semantics
    ///     (i.e. check <c>ContainsKey</c> before assigning).
    /// </summary>
    Task ContributeAsync(IDictionary<string, object?> signals, CancellationToken ct);
}
