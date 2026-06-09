namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Read-side abstraction over the gateway's live metric instruments.
///     Two implementations exist:
///     <list type="bullet">
///         <item>
///             <c>LocalMeterStream</c> -- runs in-process on the gateway, subscribes via
///             <c>MeterListener</c>, keeps a ring buffer per meter.
///         </item>
///         <item>
///             <c>RemoteMeterStream</c> -- polls <c>http://gateway:8080/metrics</c> and parses
///             Prometheus exposition (used by viewer hosts that don't run the gateway in-process).
///         </item>
///     </list>
///     Per the gateway-local data architecture: the gateway aggregates locally; viewer hosts
///     are thin REST clients hitting LAN-local gateways.
/// </summary>
public interface IMeterStream
{
    /// <summary>
    ///     Returns every instrument currently known to the stream. For LocalMeterStream this is
    ///     populated as the runtime publishes meters; for RemoteMeterStream it is the set of names
    ///     observed in the most recent scrape.
    /// </summary>
    Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct);

    /// <summary>
    ///     Returns a windowed time-series sliced into <paramref name="buckets" /> evenly-spaced
    ///     buckets covering the most recent <paramref name="window" />. The last bucket always
    ///     ends at "now". Empty buckets (no samples landed) return 0.
    ///     For histograms, P50/P99 are computed across the samples in the window (not per-bucket).
    /// </summary>
    Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct);
}