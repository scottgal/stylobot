namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Configurable knobs for <see cref="DegradationHistoryAtom"/> +
///     <see cref="DegradationHistorySampler"/>. Per
///     <c>feedback_all_settings_configurable</c> every cap / cadence / ring
///     size lives on an Options class, not as a magic number in code.
/// </summary>
public sealed class SiteHealthHistoryOptions
{
    /// <summary>
    ///     Maximum number of <see cref="DegradationSnapshot"/> entries the
    ///     ring buffer retains. Default 360 = one hour at the Tick10s
    ///     cadence; the 24h / 7d windows on the dashboard get coarser data
    ///     because the ring overwrites past the cap. Bump this in config if
    ///     you want a longer in-memory window without a persistent store.
    /// </summary>
    public int Capacity { get; set; } = 360;
}