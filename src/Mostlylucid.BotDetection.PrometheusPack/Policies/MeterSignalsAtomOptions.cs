namespace Mostlylucid.BotDetection.PrometheusPack.Policies;

/// <summary>
///     Configuration for <see cref="MeterSignalsAtom"/>. Wave 5 of the
///     architectural-drift remediation: pulls the previously
///     <c>internal const int WindowBuckets = 6</c> off the atom so deployments
///     can tune the meter-snapshot window granularity per their predicate
///     vocabulary.
/// </summary>
/// <remarks>
///     The atom rebuilds its snapshot every
///     <see cref="MeterSignalsAtom.RebuildCadence"/> (Tick10s). Each rebuild
///     asks every catalogued meter for a 1-minute series and a 5-minute
///     series, both with <see cref="WindowBuckets"/> buckets, and folds them
///     into the <c>meter.{name}.delta_1m</c> / <c>meter.{name}.delta_5m</c>
///     facets. A higher bucket count gives the underlying time-series shape
///     more resolution at the cost of more per-rebuild work; six is the
///     original constant that the Wave 4 hot-path migration preserved
///     numerically.
/// </remarks>
public sealed class MeterSignalsAtomOptions
{
    /// <summary>
    ///     Bucket count for both the 1-minute and 5-minute time-series
    ///     extraction. Default: <c>6</c>. Numerically equivalent to the
    ///     <c>internal const int WindowBuckets = 6</c> retired in Wave 5.
    /// </summary>
    public int WindowBuckets { get; set; } = 6;
}
