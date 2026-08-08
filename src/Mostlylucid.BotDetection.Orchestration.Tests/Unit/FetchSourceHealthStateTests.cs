using Mostlylucid.BotDetection.Data.Sources;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     "Healthy" must require a successful fetch within cadence, not merely the absence of a
///     recorded failure — a source that succeeded once and silently stopped ticking (exactly how an
///     un-migrated BackgroundService off ScheduleCoordinator would fail: no errors, just nothing
///     happening) must read as Stale, not Healthy, once its cadence has elapsed.
/// </summary>
public class FetchSourceHealthStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan DefaultCadence = TimeSpan.FromHours(1);

    private static FetchSourceStatus Status(
        DateTimeOffset? lastSuccess, DateTimeOffset? lastFailure, bool hasLiveState = true,
        bool hasCadence = true, TimeSpan? cadenceInterval = null)
        => new(
            "Test", "Test", "https://example.com", true, "purpose", null, "hourly",
            hasCadence ? cadenceInterval ?? DefaultCadence : null, FetchFailureMode.FailOpen, null,
            lastSuccess, lastFailure, hasLiveState);

    [Fact]
    public void Not_instrumented_is_Unknown_regardless_of_timestamps()
    {
        var status = Status(Now, null, hasLiveState: false);
        Assert.Equal(FetchHealthState.Unknown, status.GetHealthState(Now));
    }

    [Fact]
    public void No_success_and_no_failure_is_NeverAttempted()
    {
        var status = Status(null, null);
        Assert.Equal(FetchHealthState.NeverAttempted, status.GetHealthState(Now));
    }

    [Fact]
    public void Recent_success_within_cadence_is_Healthy()
    {
        var status = Status(Now - TimeSpan.FromMinutes(30), null, cadenceInterval: TimeSpan.FromHours(1));
        Assert.Equal(FetchHealthState.Healthy, status.GetHealthState(Now));
    }

    [Fact]
    public void Success_older_than_cadence_times_tolerance_is_Stale_even_with_no_recorded_failure()
    {
        // The exact scenario overview- flagged: succeeded long ago, ticking silently stopped,
        // LastFailureUtc stayed null the whole time. Must not read as Healthy.
        var status = Status(Now - TimeSpan.FromDays(90), null, cadenceInterval: TimeSpan.FromHours(1));
        Assert.Equal(FetchHealthState.Stale, status.GetHealthState(Now));
    }

    [Fact]
    public void Success_just_past_cadence_but_within_tolerance_multiplier_is_still_Healthy()
    {
        // cadence=1h, tolerance=1.5 (default) -> healthy up to 90 minutes old.
        var status = Status(Now - TimeSpan.FromMinutes(80), null, cadenceInterval: TimeSpan.FromHours(1));
        Assert.Equal(FetchHealthState.Healthy, status.GetHealthState(Now));
    }

    [Fact]
    public void Failure_more_recent_than_success_is_Failing()
    {
        var status = Status(Now - TimeSpan.FromMinutes(30), Now - TimeSpan.FromMinutes(5));
        Assert.Equal(FetchHealthState.Failing, status.GetHealthState(Now));
    }

    [Fact]
    public void Success_more_recent_than_an_older_failure_is_not_Failing()
    {
        // Recovered: failed an hour ago, succeeded 5 minutes ago.
        var status = Status(Now - TimeSpan.FromMinutes(5), Now - TimeSpan.FromHours(1));
        Assert.Equal(FetchHealthState.Healthy, status.GetHealthState(Now));
    }

    [Fact]
    public void Failure_with_no_success_at_all_is_Failing()
    {
        var status = Status(null, Now - TimeSpan.FromMinutes(5));
        Assert.Equal(FetchHealthState.Failing, status.GetHealthState(Now));
    }

    [Fact]
    public void Old_success_with_no_cadence_interval_cannot_be_flagged_Stale()
    {
        // No structured cadence to measure against - best effort stays Healthy rather than a
        // false-negative Stale claim built on a number that doesn't exist.
        var status = Status(Now - TimeSpan.FromDays(365), null, hasCadence: false);
        Assert.Equal(FetchHealthState.Healthy, status.GetHealthState(Now));
    }
}
