using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Scheduling;

/// <summary>
///     Coverage for <see cref="ScheduleCoordinatorWatchdog"/>. The watchdog
///     observes per-cadence freshness via subscriptions on the shared
///     coordinator and forces a process exit when any watched cadence has
///     gone silent past its staleness threshold. Tests drive the
///     <c>EvaluateOnce</c> hook directly under a frozen clock instead of
///     spinning the watchdog's PeriodicTimer.
/// </summary>
public sealed class ScheduleCoordinatorWatchdogTests
{
    [Fact]
    public void Stalled_cadence_calls_StopApplication()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new RecordingScheduleCoordinator();
        var lifetime = new CapturingHostApplicationLifetime();
        var options = new ScheduleCoordinatorOptions
        {
            // Trim the grace window so the test doesn't have to advance the
            // clock past the default 2-minute startup buffer.
            WatchdogStartupGrace = TimeSpan.FromSeconds(0),
            WatchdogStalenessMultiplier = 2.0
        };

        var watchdog = new ScheduleCoordinatorWatchdog(
            coordinator,
            lifetime,
            Options.Create(options),
            NullLogger<ScheduleCoordinatorWatchdog>.Instance,
            time);

        // Anchor the watchdog's startup instant by running a no-op evaluation
        // at t=0. This mirrors what happens at the start of ExecuteAsync.
        watchdog.EvaluateOnce(time.GetUtcNow());
        lifetime.StopRequested.Should().BeFalse(
            "the first evaluation just anchors the startup timestamp; no cadence is stale yet");

        // Advance the clock past the Tick10s staleness threshold
        // (10s * 2.0 = 20s). With no ticks observed, the watchdog sees a
        // 30-second silence on Tick10s and asks the lifetime to stop.
        time.Advance(TimeSpan.FromSeconds(30));
        watchdog.EvaluateOnce(time.GetUtcNow());

        lifetime.StopRequested.Should().BeTrue(
            "Tick10s has been silent for 30s, well past the 20s staleness threshold");
    }

    [Fact]
    public async Task Healthy_cadence_does_not_call_StopApplication()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new RecordingScheduleCoordinator();
        var lifetime = new CapturingHostApplicationLifetime();
        var options = new ScheduleCoordinatorOptions
        {
            WatchdogStartupGrace = TimeSpan.FromSeconds(0),
            WatchdogStalenessMultiplier = 2.0
        };

        var watchdog = new ScheduleCoordinatorWatchdog(
            coordinator,
            lifetime,
            Options.Create(options),
            NullLogger<ScheduleCoordinatorWatchdog>.Instance,
            time);

        // Anchor the startup instant.
        watchdog.EvaluateOnce(time.GetUtcNow());

        // Drive each watched cadence forward by firing its captured handler at
        // a healthy interval. The watchdog records the timestamp as "last seen
        // tick" for that cadence.
        var watchedCadences = coordinator.Subscriptions
            .Where(s => s.Name.StartsWith("ScheduleCoordinatorWatchdog."))
            .ToList();
        watchedCadences.Should().HaveCount(4,
            "the watchdog subscribes to Tick10s, Tick1m, Tick5m, Tick1h -- Tick1s is intentionally excluded");

        // Tick every observed cadence so freshness is recorded.
        foreach (var sub in watchedCadences)
        {
            await sub.Handler(time.GetUtcNow(), CancellationToken.None);
        }

        // Advance the clock by 15s -- still inside the Tick10s 20s threshold.
        time.Advance(TimeSpan.FromSeconds(15));
        watchdog.EvaluateOnce(time.GetUtcNow());

        lifetime.StopRequested.Should().BeFalse(
            "every cadence has been observed within its staleness threshold");
    }

    [Fact]
    public void Startup_grace_suppresses_false_positives()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new RecordingScheduleCoordinator();
        var lifetime = new CapturingHostApplicationLifetime();
        var options = new ScheduleCoordinatorOptions
        {
            WatchdogStartupGrace = TimeSpan.FromMinutes(2),
            WatchdogStalenessMultiplier = 2.0
        };

        var watchdog = new ScheduleCoordinatorWatchdog(
            coordinator,
            lifetime,
            Options.Create(options),
            NullLogger<ScheduleCoordinatorWatchdog>.Instance,
            time);

        // Anchor the startup instant + immediately advance well past the
        // Tick10s threshold but stay INSIDE the 2-minute startup grace.
        watchdog.EvaluateOnce(time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(45));
        watchdog.EvaluateOnce(time.GetUtcNow());

        lifetime.StopRequested.Should().BeFalse(
            "we are still inside the 2-minute startup grace window; no firing allowed");
    }

    [Fact]
    public async Task Disable_flag_makes_watchdog_a_noop()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var lifetime = new CapturingHostApplicationLifetime();
        var options = new ScheduleCoordinatorOptions { EnableWatchdog = false };

        var watchdog = new ScheduleCoordinatorWatchdog(
            coordinator,
            lifetime,
            Options.Create(options),
            NullLogger<ScheduleCoordinatorWatchdog>.Instance);

        // When disabled, ExecuteAsync returns early; tests still want to be
        // able to call StartAsync without the watchdog erroring. The watchdog
        // is internal so we can call StartAsync via the IHostedService surface
        // (it inherits BackgroundService).
        await watchdog.StartAsync(CancellationToken.None);
        await watchdog.StopAsync(CancellationToken.None);

        lifetime.StopRequested.Should().BeFalse();
        coordinator.Subscriptions.Should().BeEmpty(
            "disabled watchdog must not subscribe to the coordinator");
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>
    ///     <see cref="IHostApplicationLifetime"/> stand-in that records whether
    ///     <see cref="StopApplication"/> was called. The watchdog only ever
    ///     calls StopApplication; the other lifetime members are returned as
    ///     cancellation tokens that never fire.
    /// </summary>
    private sealed class CapturingHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => StopRequested = true;
    }
}