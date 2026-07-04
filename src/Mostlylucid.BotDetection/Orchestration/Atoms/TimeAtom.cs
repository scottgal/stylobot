using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Foundation SensorAtom (per Taxonomy.md) that derives webmaster-readable
///     time-of-day facets from the gateway clock. Publishes
///     <see cref="SignalKeys.TimeHourOfDay"/>, <see cref="SignalKeys.TimeDayOfWeek"/>,
///     <see cref="SignalKeys.TimeIsWeekend"/>, <see cref="SignalKeys.TimeIsBusinessHours"/>
///     on every request so DSL rules like
///     <c>time.is_business_hours = false and bot.family = "scraper"</c>
///     ("be stricter during off-hours when humans are rare") resolve.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for the legacy
///         <c>TimeContributor</c>. Constant-time and I/O free: one clock read,
///         one timezone convert, four signal raises. Reads
///         <c>IOptions&lt;TimeOptions&gt;.Value</c> on every call so live config
///         reloads take effect without restart.
///     </para>
///     <para>
///         Timezone resolution is best-effort: an unresolvable IANA id silently
///         falls back to UTC instead of throwing -- a bot-detection gateway must
///         not crash on a typo'd config string.
///     </para>
///     <para>
///         Priority 5 matches the legacy contributor's Wave-0 slot (Foundation
///         tier, earliest -- other atoms can read time.* signals downstream).
///     </para>
/// </remarks>
public sealed class TimeAtom : DetectorAtomBase
{
    private readonly TimeProvider _clock;
    private readonly IOptions<TimeOptions> _options;

    /// <summary>
    ///     <paramref name="clock"/> is nullable so the default DI registration
    ///     works whether the host has bound <see cref="TimeProvider.System"/> or
    ///     not (mirrors <c>ScheduleCoordinator</c> pattern). Tests inject
    ///     <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>.
    /// </summary>
    public TimeAtom(TimeProvider? clock, IOptions<TimeOptions> options)
        : base(name: "Time", category: "Time")
    {
        _clock = clock ?? TimeProvider.System;
        _options = options;
    }

    public override int Priority => 5;

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
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

        // Model 2 hints: signal name carries the value after the colon.
        // Downstream policy DSL / other atoms parse the hint from the signal name.
        sink.Raise($"{SignalKeys.TimeHourOfDay}:{hour}", sessionId);
        sink.Raise($"{SignalKeys.TimeDayOfWeek}:{dow}", sessionId);
        if (isWeekend) sink.Raise(SignalKeys.TimeIsWeekend, sessionId);
        if (isBusinessHours) sink.Raise(SignalKeys.TimeIsBusinessHours, sessionId);

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