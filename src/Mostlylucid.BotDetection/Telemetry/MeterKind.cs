namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     The OpenTelemetry-style instrument kind that produced the time-series.
///     Determines how <see cref="IMeterStream" /> aggregates samples into buckets:
///     counters become deltas-per-bucket, gauges become averages, histograms add P50/P99.
/// </summary>
public enum MeterKind
{
    Counter,
    Gauge,
    Histogram,
    UpDownCounter,
}