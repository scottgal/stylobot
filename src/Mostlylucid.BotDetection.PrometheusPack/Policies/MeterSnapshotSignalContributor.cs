using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;

namespace Mostlylucid.BotDetection.PrometheusPack.Policies;

/// <summary>
///     Pumps the most-recent meter snapshot from <see cref="IMeterStream" />
///     into the per-request signals map under the keys:
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
///         (which Phase E shipped) and the predicate evaluator. The
///         <see cref="DefaultPolicyResolver" /> invokes it once per
///         <c>EffectiveWithContextAsync</c> call -- the snapshot-at-request-entry
///         semantics fall out of that single invocation, not from request-scoped
///         DI.
///     </para>
/// </summary>
public sealed class MeterSnapshotSignalContributor : ISignalContributor
{
    private readonly IMeterStream _stream;

    /// <summary>The bucket count used for both the 1m and 5m windows.</summary>
    /// <remarks>
    ///     Six buckets keeps the contributor allocation modest (two arrays of
    ///     six doubles per meter) while still producing a meaningful delta sum.
    ///     The exact bucket count is not surfaced in the signal value itself --
    ///     the <c>delta_*</c> facets are sums, not means.
    /// </remarks>
    internal const int WindowBuckets = 6;

    public MeterSnapshotSignalContributor(IMeterStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <inheritdoc />
    public async Task ContributeAsync(IDictionary<string, object?> signals, CancellationToken ct)
    {
        var catalog = await _stream.ListAsync(ct).ConfigureAwait(false);
        if (catalog.Count == 0) return;

        foreach (var entry in catalog)
        {
            // 1m window -- current + delta_1m + (p50/p99 for histograms).
            var oneMinute = await _stream
                .GetAsync(entry.Name, TimeSpan.FromMinutes(1), WindowBuckets, ct)
                .ConfigureAwait(false);
            if (oneMinute is not null)
            {
                TryAdd(signals, $"meter.{entry.Name}.current",  oneMinute.Current);
                TryAdd(signals, $"meter.{entry.Name}.delta_1m", SumValues(oneMinute));
                if (entry.Kind == MeterKind.Histogram)
                {
                    TryAdd(signals, $"meter.{entry.Name}.p50", oneMinute.P50);
                    TryAdd(signals, $"meter.{entry.Name}.p99", oneMinute.P99);
                }
            }

            // 5m window -- delta_5m only; current/p50/p99 are point-in-time
            // measurements and the 1m series already captures them.
            var fiveMinute = await _stream
                .GetAsync(entry.Name, TimeSpan.FromMinutes(5), WindowBuckets, ct)
                .ConfigureAwait(false);
            if (fiveMinute is not null)
                TryAdd(signals, $"meter.{entry.Name}.delta_5m", SumValues(fiveMinute));
        }
    }

    private static void TryAdd(IDictionary<string, object?> signals, string key, object? value)
    {
        if (!signals.ContainsKey(key))
            signals[key] = value;
    }

    private static double SumValues(MeterTimeSeries ts)
    {
        double sum = 0;
        for (var i = 0; i < ts.Values.Count; i++)
            sum += ts.Values[i];
        return sum;
    }
}