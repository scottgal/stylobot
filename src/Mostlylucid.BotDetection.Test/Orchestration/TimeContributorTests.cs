using System.Collections.Concurrent;
using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins the <c>time.*</c> facet surface emitted by <see cref="TimeContributor"/>.
///     The DSL "stricter rules during off-hours" use case depends on these four
///     signals being present and correctly typed on every request.
/// </summary>
public class TimeContributorTests
{
    [Theory]
    // Wednesday 2026-06-24 14:30 UTC -> hour 14, dow "wed", weekend false, business-hours true (9-17)
    [InlineData("2026-06-24T14:30:00Z", "UTC", 9, 17, 14, "wed", false, true)]
    // Sunday 2026-06-28 02:15 UTC -> hour 2, dow "sun", weekend true, business-hours false
    [InlineData("2026-06-28T02:15:00Z", "UTC", 9, 17, 2, "sun", true, false)]
    // Friday 2026-06-26 16:59 UTC -> hour 16, business-hours still true (exclusive end)
    [InlineData("2026-06-26T16:59:00Z", "UTC", 9, 17, 16, "fri", false, true)]
    // Friday 2026-06-26 17:00 UTC -> hour 17, business-hours false (exclusive end)
    [InlineData("2026-06-26T17:00:00Z", "UTC", 9, 17, 17, "fri", false, false)]
    public async Task Contribute_writes_time_facets(
        string utcNow, string tz, int bizStart, int bizEnd,
        int expectedHour, string expectedDow, bool expectedWeekend, bool expectedBusinessHours)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(utcNow));
        var options = Options.Create(new TimeOptions
        {
            TimeZone = tz,
            BusinessHoursStart = bizStart,
            BusinessHoursEnd = bizEnd
        });
        var contributor = new TimeContributor(clock, options);
        var state = NewState();

        await contributor.ContributeAsync(state, default);

        state.Signals[SignalKeys.TimeHourOfDay].Should().Be(expectedHour);
        state.Signals[SignalKeys.TimeDayOfWeek].Should().Be(expectedDow);
        state.Signals[SignalKeys.TimeIsWeekend].Should().Be(expectedWeekend);
        state.Signals[SignalKeys.TimeIsBusinessHours].Should().Be(expectedBusinessHours);
    }

    [Fact]
    public void Contributor_is_foundation_tier()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var contributor = new TimeContributor(clock, Options.Create(new TimeOptions()));

        contributor.Should().BeAssignableTo<IFoundationContributor>(
            "time-of-day signals are dependency-free and other contributors may read them");
        contributor.Priority.Should().BeLessThan(10,
            "foundation tier — TransportProtocolContributor / PiiQueryStringContributor sit at 5-8");
    }

    [Fact]
    public async Task Unknown_timezone_falls_back_to_UTC_without_throwing()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-24T14:30:00Z"));
        var options = Options.Create(new TimeOptions
        {
            TimeZone = "Not/A_Real_Zone",
            BusinessHoursStart = 9,
            BusinessHoursEnd = 17
        });
        var contributor = new TimeContributor(clock, options);
        var state = NewState();

        await contributor.ContributeAsync(state, default);

        // Should read as UTC, not throw
        state.Signals[SignalKeys.TimeHourOfDay].Should().Be(14);
        state.Signals[SignalKeys.TimeDayOfWeek].Should().Be("wed");
    }

    private static BlackboardState NewState()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        var signals = new ConcurrentDictionary<string, object>();
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N")
        };
    }
}
