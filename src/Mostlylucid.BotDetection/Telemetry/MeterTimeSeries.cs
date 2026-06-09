namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     A windowed time-series for a single instrument, sliced into evenly-spaced buckets.
///     Produced by <see cref="IMeterStream.GetAsync" /> and consumed by SbTrendCard,
///     SbMeterTable, and the /dashboard/insights page.
/// </summary>
/// <param name="Name">The instrument name this series belongs to.</param>
/// <param name="Kind">The instrument kind (determines how Values were aggregated).</param>
/// <param name="Buckets">Bucket-start timestamps, ascending; the last bucket always ends at "now".</param>
/// <param name="Values">
///     One value per bucket. Counters expose deltas-per-bucket; gauges and up-down expose the
///     average sample within the bucket; histograms expose the average. Empty buckets are 0.
/// </param>
/// <param name="Current">The most recent sample value observed in the buffer.</param>
/// <param name="P50">Median across all samples in the window for histograms; null otherwise.</param>
/// <param name="P99">99th percentile across all samples in the window for histograms; null otherwise.</param>
public sealed record MeterTimeSeries(
    string Name,
    MeterKind Kind,
    IReadOnlyList<DateTimeOffset> Buckets,
    IReadOnlyList<double> Values,
    double Current,
    double? P50,
    double? P99);