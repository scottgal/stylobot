namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Configuration for <c>LocalMeterStream</c>. All settings are configurable so callers
///     can scope the listener (tests in particular set <see cref="MeterNamePrefixFilter" />
///     to avoid ambient ASP.NET / runtime meters crowding the catalog).
/// </summary>
public sealed class LocalMeterStreamOptions
{
    /// <summary>
    ///     Per-meter sample capacity. The ring buffer keeps the most recent N samples per
    ///     instrument; once full, the oldest sample is evicted on the next write.
    /// </summary>
    public int RingBufferCapacity { get; set; } = 4096;

    /// <summary>
    ///     When empty, the listener subscribes to every meter the process publishes.
    ///     When non-empty, only instruments whose name <see cref="string.StartsWith(string,System.StringComparison)" />
    ///     this prefix (case-insensitive) are subscribed.
    /// </summary>
    public string MeterNamePrefixFilter { get; set; } = "";
}