using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Console.Services;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.ConsoleTests.Services;

/// <summary>
///     Wave 2 pilot regression coverage for
///     <see cref="HeartbeatService"/>. The service used to be a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(5min)</c> loop; it now subscribes to
///     <see cref="TickCadence.Tick5m"/>.
///     <para>
///         These tests validate the per-service migration recipe documented in
///         <c>docs/superpowers/plans/2026-06-15-wave2-schedule-coordinator-migration.md</c>:
///         every batch-1 PR adds a (subscription-shape + tick-handler-fires)
///         pair like the two below.
///     </para>
/// </summary>
public sealed class HeartbeatServiceTests
{
    [Fact]
    public void Constructor_subscribes_to_Tick5m_with_HeartbeatService_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = new HeartbeatService(NullLogger<HeartbeatService>.Instance, coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick5m);
        sub.Name.Should().Be("HeartbeatService");
        sub.Hint.Should().Be(CostHint.Low);
    }

    [Fact]
    public async Task OnTickAsync_emits_heartbeat_when_fired()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = new HeartbeatService(NullLogger<HeartbeatService>.Instance, coordinator);

        // Fire the captured handler directly -- mirrors what the real
        // coordinator does on Tick5m. The handler is synchronous (returns
        // Task.CompletedTask) so this completes immediately.
        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // The handler doesn't throw, the subscription isn't disposed, and a
        // second tick is observable -- enough to prove the migration plumbing
        // works end-to-end without coupling to specific log output.
        captured.Disposed.Should().BeFalse();
        await captured.Handler(DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var sut = new HeartbeatService(NullLogger<HeartbeatService>.Instance, coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Disposed.Should().BeFalse();

        sut.Dispose();
        sub.Disposed.Should().BeTrue("HeartbeatService.Dispose must release its IDisposable subscription handle");

        // Double-dispose is safe (Interlocked guard).
        sut.Dispose();
    }
}