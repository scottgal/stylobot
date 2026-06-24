namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Options for <c>TimeContributor</c>. Controls how the time-of-day facets
///     <see cref="SignalKeys.TimeHourOfDay"/>, <see cref="SignalKeys.TimeDayOfWeek"/>,
///     <see cref="SignalKeys.TimeIsWeekend"/>, and <see cref="SignalKeys.TimeIsBusinessHours"/>
///     are derived from the gateway clock.
///
///     <para>
///         Bound from the configuration section <c>BotDetection:Time</c>. All values
///         are webmaster-tunable -- the contributor reads the live options snapshot
///         on every request so config reloads take effect without a restart.
///     </para>
/// </summary>
public sealed class TimeOptions
{
    /// <summary>
    ///     IANA timezone identifier used to derive hour-of-day and day-of-week
    ///     from the UTC clock (e.g. "Europe/London", "America/New_York",
    ///     "Asia/Tokyo"). Defaults to <c>"UTC"</c> for backwards compatibility;
    ///     webmasters with policy rules keyed on local hours set this to their
    ///     site's primary timezone.
    ///     <para>
    ///         The gateway ships in linux-musl Docker containers so IANA IDs
    ///         resolve directly via <see cref="System.TimeZoneInfo.FindSystemTimeZoneById"/>.
    ///         An unresolvable id falls back to UTC silently -- the contributor
    ///         never throws on bad config.
    ///     </para>
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    ///     Inclusive hour (0-23) at which the business-hours window begins in
    ///     <see cref="TimeZone"/>. A rule keyed on
    ///     <c>time.is_business_hours = false</c> activates outside the window
    ///     <c>[BusinessHoursStart, BusinessHoursEnd)</c>. Default 9.
    /// </summary>
    public int BusinessHoursStart { get; set; } = 9;

    /// <summary>
    ///     Exclusive hour (0-23) at which the business-hours window ends in
    ///     <see cref="TimeZone"/>. The hour itself is NOT business hours --
    ///     17:00 with <c>BusinessHoursEnd = 17</c> evaluates to false.
    ///     Default 17.
    /// </summary>
    public int BusinessHoursEnd { get; set; } = 17;
}
