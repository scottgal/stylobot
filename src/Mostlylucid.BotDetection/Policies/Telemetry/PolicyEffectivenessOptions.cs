namespace Mostlylucid.BotDetection.Policies.Telemetry;

/// <summary>
///     Tuning knobs for <see cref="PolicyEffectivenessCache"/>. Every magic
///     number on the cache's hot path lives here so deployments can size the
///     ring buffer, channel, and drainer cadence to their gateway's traffic
///     shape without rebuilding.
/// </summary>
public sealed class PolicyEffectivenessOptions
{
    /// <summary>
    ///     The set of windows the dashboard's compact-row signals will request.
    ///     The ring buffer's age is implicitly bounded by the largest tracked
    ///     window: anything older drops off as it ages past the cutoff. Callers
    ///     may request narrower windows (the buffer is a sliding cutoff over
    ///     the same physical slots).
    /// </summary>
    public IReadOnlyList<TimeSpan> WindowsToTrack { get; init; }
        = new[] { TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30) };

    /// <summary>Per-rule ring-buffer capacity. Beyond this, oldest entries drop.</summary>
    public int PerRuleRingCapacity { get; init; } = 4096;

    /// <summary>Maximum number of distinct rule ids the cache will hold; LFU-evict at capacity.</summary>
    public int MaxRules { get; init; } = 1024;

    /// <summary>Bounded channel capacity for the writer side of the façade.</summary>
    public int ChannelCapacity { get; init; } = 50_000;

    /// <summary>Drainer cadence (cache → durable log).</summary>
    public TimeSpan DrainerInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Drainer batch size per cycle.</summary>
    public int DrainerBatchSize { get; init; } = 500;

    /// <summary>
    ///     Upper bound on the LFU access counter so a long-lived hot rule cannot
    ///     permanently dominate eviction. Counters saturate at this value and
    ///     are slowly aged down inside the drainer loop.
    /// </summary>
    public long LfuCounterCap { get; init; } = 1_000_000;
}
