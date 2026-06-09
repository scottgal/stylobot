namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Configuration for <see cref="LocalMeterStream" />. Every magic number
///     lives here so deployments can scope the listener (the
///     <see cref="MeterNamePrefixFilter" /> is particularly important to tests
///     so ambient ASP.NET / runtime meters do not crowd the catalog).
/// </summary>
/// <remarks>
///     The storage shape is an LFU sliding cache of per-meter summary atoms.
///     Each atom keeps the last <see cref="RecentBucketCount" /> aggregated
///     buckets of width <see cref="BucketWidth" /> plus -- for histograms only --
///     a bounded percentile reservoir. Raw per-sample rings are NOT kept;
///     summaries are what flow as <see cref="MeterSignal" /> emissions and what
///     <see cref="LocalMeterStream.GetAsync" /> rebuilds time-series from.
/// </remarks>
public sealed class LocalMeterStreamOptions
{
    /// <summary>
    ///     LFU capacity for distinct meters tracked in memory. When the
    ///     dictionary exceeds this size the eviction sweep removes the
    ///     least-frequently-hit atoms (with the sliding-decay applied to
    ///     <c>HitCount</c>) until the count is back under cap.
    /// </summary>
    public int MaxTrackedMeters { get; set; } = 1024;

    /// <summary>
    ///     Number of per-meter recent-bucket entries kept by each summary atom.
    ///     With the default <see cref="BucketWidth" /> of 5s, the default 24
    ///     covers a 2-minute rolling window.
    /// </summary>
    public int RecentBucketCount { get; set; } = 24;

    /// <summary>
    ///     Width of each summary bucket. Samples that land inside the same
    ///     bucket are aggregated per-kind (counter delta, gauge mean, etc.).
    /// </summary>
    public TimeSpan BucketWidth { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Per-histogram reservoir capacity. Bounded circular buffer of recent
    ///     recorded values used to estimate P50 / P99 on read. Only allocated
    ///     for histogram kinds; null on every other atom.
    /// </summary>
    public int PercentileReservoirSize { get; set; } = 256;

    /// <summary>
    ///     How often the background sweep decays every atom's
    ///     <c>HitCount</c> by <see cref="HitCountDecayFactor" />. Same sweep
    ///     performs LFU eviction when the dictionary exceeds
    ///     <see cref="MaxTrackedMeters" />.
    /// </summary>
    public TimeSpan HitCountDecayInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Multiplier applied to every atom's <c>HitCount</c> each decay tick.
    ///     The default 0.5 halves the score every interval so a previously-hot
    ///     but now-idle meter loses to a freshly-active one within a few cycles.
    /// </summary>
    public double HitCountDecayFactor { get; set; } = 0.5;

    /// <summary>
    ///     When empty, the listener subscribes to every meter the process
    ///     publishes. When non-empty, only instruments whose name
    ///     <see cref="string.StartsWith(string,System.StringComparison)" /> this
    ///     prefix (case-insensitive) are subscribed. Tests set this to a
    ///     unique per-test prefix so they get isolated catalogs.
    /// </summary>
    public string MeterNamePrefixFilter { get; set; } = "";

    /// <summary>
    ///     Minimum wall-clock gap between successive <see cref="MeterSignal" />
    ///     emissions for the same meter. Counter-heavy meters can update
    ///     thousands of times per second; this coalesces emissions so
    ///     downstream sinks (the blackboard in Phase F, the trigger service in
    ///     Phase G) do not get fire-hosed.
    /// </summary>
    public TimeSpan MinSignalEmitInterval { get; set; } = TimeSpan.FromSeconds(1);
}