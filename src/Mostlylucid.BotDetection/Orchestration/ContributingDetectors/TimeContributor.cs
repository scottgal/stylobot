using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Foundation-tier contributor that derives webmaster-readable time-of-day
///     facets from the gateway clock. Closes a real DSL gap: rules like
///     <c>time.is_business_hours = false and bot.family = "scraper"</c>
///     ("be stricter during off-hours when humans are rare") can only be
///     expressed once the contributor publishes
///     <see cref="SignalKeys.TimeHourOfDay"/>, <see cref="SignalKeys.TimeDayOfWeek"/>,
///     <see cref="SignalKeys.TimeIsWeekend"/>, and <see cref="SignalKeys.TimeIsBusinessHours"/>
///     on every request.
///     <para>
///         Constant-time and I/O free: one clock read, one timezone convert,
///         four writes. Runs on every request. The timezone, business-hours
///         window, and weekend boundaries are all on <see cref="TimeOptions"/>
///         so a webmaster can match their site's primary locale via
///         <c>BotDetection:Time</c> config -- no recompile, no rule duplication.
///     </para>
///     <para>
///         The contributor reads <c>IOptions&lt;TimeOptions&gt;.Value</c> on
///         every request so live config reloads take effect without restart.
///         Timezone resolution is best-effort: an unresolvable IANA id silently
///         falls back to UTC instead of throwing -- a bot-detection gateway must
///         not crash on a typo'd config string.
///     </para>
/// </summary>
public sealed class TimeContributor : ContributingDetectorBase, IFoundationContributor
{
    private readonly TimeProvider _clock;
    private readonly IOptions<TimeOptions> _options;

    /// <summary>
    ///     <paramref name="clock"/> is nullable so the default DI registration
    ///     works whether the host has bound <see cref="TimeProvider.System"/> or
    ///     not -- mirrors the <c>ScheduleCoordinator</c> /
    ///     <c>ScheduleCoordinatorWatchdog</c> pattern. Tests inject
    ///     <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>.
    /// </summary>
    public TimeContributor(TimeProvider? clock, IOptions<TimeOptions> options)
    {
        _clock = clock ?? TimeProvider.System;
        _options = options;
    }

    public override string Name => "Time";

    // Foundation tier sits at 5-8 (TransportProtocol = 5, PiiQueryString = 8).
    // Time has no dependencies -- only the clock + options -- so it earns the
    // earliest slot. Other contributors can read time.* signals downstream.
    public override int Priority => 5;

    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var opts = _options.Value;
        var tz = ResolveTimezone(opts.TimeZone);
        var nowUtc = _clock.GetUtcNow();
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);

        var hour = nowLocal.Hour;
        var dow = nowLocal.DayOfWeek switch
        {
            DayOfWeek.Monday => "mon",
            DayOfWeek.Tuesday => "tue",
            DayOfWeek.Wednesday => "wed",
            DayOfWeek.Thursday => "thu",
            DayOfWeek.Friday => "fri",
            DayOfWeek.Saturday => "sat",
            DayOfWeek.Sunday => "sun",
            _ => "unknown"
        };
        var isWeekend = nowLocal.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var isBusinessHours = hour >= opts.BusinessHoursStart && hour < opts.BusinessHoursEnd;

        state.WriteSignal(SignalKeys.TimeHourOfDay, hour);
        state.WriteSignal(SignalKeys.TimeDayOfWeek, dow);
        state.WriteSignal(SignalKeys.TimeIsWeekend, isWeekend);
        state.WriteSignal(SignalKeys.TimeIsBusinessHours, isBusinessHours);

        return Task.FromResult(None());
    }

    private static TimeZoneInfo ResolveTimezone(string id)
    {
        if (string.IsNullOrEmpty(id) || id == "UTC")
            return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
