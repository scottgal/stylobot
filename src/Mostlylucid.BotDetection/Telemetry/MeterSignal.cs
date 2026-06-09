namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Materialised summary of a single meter at a point in time, emitted by
///     <see cref="LocalMeterStream" /> to every registered <see cref="IMeterSignalSink" />
///     when the underlying <see cref="MeterCatalogEntry" /> has materially updated.
/// </summary>
/// <remarks>
///     This is the signal carrier that Phase F wires onto the request blackboard
///     (so per-request predicates can read live meter values) and that Phase G
///     feeds into the oscilloscope-style trigger service. In Phase E (this commit)
///     the sink is just an extension point; no consumers are wired yet.
/// </remarks>
/// <param name="Name">The instrument name the signal originated from.</param>
/// <param name="Kind">The instrument kind (Counter / Gauge / Histogram / UpDownCounter).</param>
/// <param name="Current">The most recently observed value for the instrument.</param>
/// <param name="P50">P50 from the histogram reservoir; null for non-histogram kinds.</param>
/// <param name="P99">P99 from the histogram reservoir; null for non-histogram kinds.</param>
/// <param name="Timestamp">Wall-clock at which this signal was materialised.</param>
public sealed record MeterSignal(
    string Name,
    MeterKind Kind,
    double Current,
    double? P50,
    double? P99,
    DateTimeOffset Timestamp);