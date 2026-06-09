namespace Mostlylucid.BotDetection.PrometheusPack.Telemetry.Prometheus;

/// <summary>
///     The Prometheus / OpenMetrics text-format type the gateway exposition declared
///     for a metric family. Mapped to <see cref="MeterKind" /> by
///     <see cref="RemoteMeterStream" />:
///     <list type="bullet">
///         <item><c>Counter</c> -> <see cref="MeterKind.Counter" /></item>
///         <item><c>Gauge</c> -> <see cref="MeterKind.Gauge" /></item>
///         <item><c>Histogram</c> -> <see cref="MeterKind.Histogram" /></item>
///         <item><c>Summary</c> -> <see cref="MeterKind.Histogram" /> (close enough for dashboard purposes)</item>
///         <item><c>Untyped</c> -> <see cref="MeterKind.Gauge" /></item>
///     </list>
/// </summary>
public enum PrometheusMetricKind
{
    Counter,
    Gauge,
    Histogram,
    Summary,
    Untyped,
}

/// <summary>
///     A single sample line from a Prometheus / OpenMetrics text exposition.
///     A metric family (e.g. <c>http_requests_total</c>) can produce many samples,
///     one per unique label combination.
/// </summary>
/// <param name="Name">
///     The full sample name as written in the exposition. For histograms / summaries
///     this includes the <c>_bucket</c> / <c>_sum</c> / <c>_count</c> suffix.
/// </param>
/// <param name="Labels">
///     Unescaped label key -> value pairs. Empty if the sample had no labels.
/// </param>
/// <param name="Value">The numeric sample value.</param>
/// <param name="TimestampMs">
///     Explicit sample timestamp in milliseconds-since-epoch when the exposition
///     wrote one; null otherwise (the parser does not synthesise a timestamp).
/// </param>
public sealed record PrometheusMetricSample(
    string Name,
    IReadOnlyDictionary<string, string> Labels,
    double Value,
    long? TimestampMs);

/// <summary>
///     A single metric family parsed from a Prometheus / OpenMetrics text exposition.
///     One family per unique family name; <see cref="Samples" /> holds every line
///     observed for that name (including <c>_bucket</c> / <c>_sum</c> / <c>_count</c>
///     for histograms and summaries).
/// </summary>
/// <param name="Name">
///     The family name as it appeared in the <c># HELP</c> / <c># TYPE</c> directives
///     (e.g. <c>http_requests_total</c>). For histograms / summaries this is the
///     base name without <c>_bucket</c> / <c>_sum</c> / <c>_count</c> suffixes.
/// </param>
/// <param name="Kind">
///     The declared TYPE. <see cref="PrometheusMetricKind.Untyped" /> when no
///     <c># TYPE</c> directive preceded the samples.
/// </param>
/// <param name="Unit">The declared <c># UNIT</c>, or null when absent.</param>
/// <param name="Help">The declared <c># HELP</c> text, or null when absent.</param>
/// <param name="Samples">
///     Every parsed sample observed for this family. For counters / gauges there is
///     typically one per label combination; histograms / summaries emit several.
/// </param>
public sealed record PrometheusMetric(
    string Name,
    PrometheusMetricKind Kind,
    string? Unit,
    string? Help,
    IReadOnlyList<PrometheusMetricSample> Samples);
