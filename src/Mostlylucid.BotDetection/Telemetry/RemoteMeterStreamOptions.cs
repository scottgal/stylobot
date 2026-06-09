namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Configuration for <see cref="RemoteMeterStream" /> -- the viewer-host
///     implementation of <see cref="IMeterStream" /> that polls the gateway's
///     <c>/metrics</c> endpoint over HTTP rather than subscribing to a local
///     <c>MeterListener</c>.
/// </summary>
/// <remarks>
///     <para>
///         The LFU-cache tuning (<see cref="MaxTrackedMeters" />,
///         <see cref="RecentBucketCount" />, <see cref="BucketWidth" />,
///         <see cref="PercentileReservoirSize" />, <see cref="HitCountDecayInterval" />,
///         <see cref="HitCountDecayFactor" />) mirrors
///         <see cref="LocalMeterStreamOptions" /> so dashboard widgets see the same
///         <see cref="MeterTimeSeries" /> shape regardless of mode.
///     </para>
///     <para>
///         <b>Inert mode:</b> when <see cref="BaseUrl" /> is empty the stream does
///         not schedule any polling. <see cref="IMeterStream.ListAsync" /> returns
///         an empty list and <see cref="IMeterStream.GetAsync" /> returns null. The
///         viewer host is sometimes booted in modes where remote metrics aren't
///         required; an inert stream lets DI register unconditionally.
///     </para>
/// </remarks>
public sealed class RemoteMeterStreamOptions
{
    /// <summary>
    ///     Base URL of the gateway's metrics endpoint, e.g. <c>https://gw.lan:8080</c>.
    ///     The poller appends <c>/metrics</c>. Empty value makes the stream inert.
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    ///     How often the background poller scrapes the gateway. Default 5s.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     HTTP timeout for a single scrape. Default 4s -- MUST be strictly less
    ///     than <see cref="PollInterval" /> so polls cannot overlap when the
    ///     gateway is slow or unreachable.
    /// </summary>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>
    ///     Optional bearer token / API key. When set, the poller sends it as
    ///     <c>Authorization: Bearer &lt;value&gt;</c> on every scrape.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    ///     Only ingest meters whose name starts with this prefix. Empty
    ///     (default) ingests every family the gateway exposes.
    /// </summary>
    public string MeterNamePrefixFilter { get; set; } = "";

    /// <summary>
    ///     LFU capacity for distinct meters tracked in memory.
    ///     Mirrors <see cref="LocalMeterStreamOptions.MaxTrackedMeters" />.
    /// </summary>
    public int MaxTrackedMeters { get; set; } = 1024;

    /// <summary>
    ///     Number of per-meter recent-bucket entries kept by each summary atom.
    ///     Mirrors <see cref="LocalMeterStreamOptions.RecentBucketCount" />.
    /// </summary>
    public int RecentBucketCount { get; set; } = 24;

    /// <summary>
    ///     Width of each summary bucket.
    ///     Mirrors <see cref="LocalMeterStreamOptions.BucketWidth" />.
    /// </summary>
    public TimeSpan BucketWidth { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Per-histogram reservoir capacity used to estimate P50 / P99 on read.
    ///     Mirrors <see cref="LocalMeterStreamOptions.PercentileReservoirSize" />.
    /// </summary>
    public int PercentileReservoirSize { get; set; } = 256;

    /// <summary>
    ///     How often the LFU decay + eviction sweep runs.
    ///     Mirrors <see cref="LocalMeterStreamOptions.HitCountDecayInterval" />.
    /// </summary>
    public TimeSpan HitCountDecayInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Multiplier applied to every atom's hit count each decay tick.
    ///     Mirrors <see cref="LocalMeterStreamOptions.HitCountDecayFactor" />.
    /// </summary>
    public double HitCountDecayFactor { get; set; } = 0.5;

    /// <summary>
    ///     Minimum wall-clock gap between successive <see cref="MeterSignal" />
    ///     emissions for the same meter. Mirrors
    ///     <see cref="LocalMeterStreamOptions.MinSignalEmitInterval" />.
    /// </summary>
    public TimeSpan MinSignalEmitInterval { get; set; } = TimeSpan.FromSeconds(1);
}