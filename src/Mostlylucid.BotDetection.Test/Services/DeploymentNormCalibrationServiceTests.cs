using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="DeploymentNormCalibrationService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>while (IsWarmingUp) await Task.Delay(1s)</c> loop; now subscribes
///     to <see cref="TickCadence.Tick1s"/> and disposes itself when warm-up
///     completes.
/// </summary>
public sealed class DeploymentNormCalibrationServiceTests
{
    private static IOptions<BotDetectionOptions> Options(int warmupRequests = 100)
        => Microsoft.Extensions.Options.Options.Create(new BotDetectionOptions
        {
            PopulationWarmupRequests = warmupRequests
        });

    private static DeploymentNormTracker NewTracker(int warmupRequests)
        => new DeploymentNormTracker(windowSize: 500, warmupRequests: warmupRequests);

    [Fact]
    public void Constructor_subscribes_to_Tick1s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var tracker = NewTracker(warmupRequests: 100);

        using var sut = new DeploymentNormCalibrationService(
            tracker,
            Options(warmupRequests: 100),
            NullLogger<DeploymentNormCalibrationService>.Instance,
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1s);
        sub.Name.Should().Be("DeploymentNormCalibrationService");
        sub.Hint.Should().Be(CostHint.Low);
    }

    [Fact]
    public async Task OnTickAsync_is_noop_while_tracker_is_warming_up()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var tracker = NewTracker(warmupRequests: 100);
        // tracker has zero observations -> IsWarmingUp == true

        using var sut = new DeploymentNormCalibrationService(
            tracker,
            Options(warmupRequests: 100),
            NullLogger<DeploymentNormCalibrationService>.Instance,
            coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
        await captured.Handler(DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);

        // Still subscribed; warm-up not complete.
        captured.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task OnTickAsync_disposes_subscription_when_warmup_completes()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var tracker = NewTracker(warmupRequests: 2);

        using var sut = new DeploymentNormCalibrationService(
            tracker,
            Options(warmupRequests: 2),
            NullLogger<DeploymentNormCalibrationService>.Instance,
            coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);
        captured.Disposed.Should().BeFalse();

        // Drive enough observations through the tracker that IsWarmingUp flips.
        // Record() bumps TotalRequests until > warmupRequests, then stops counting.
        tracker.Record(DeploymentNormTracker.Features.Http2, bucket: "test", present: true);
        tracker.Record(DeploymentNormTracker.Features.Http2, bucket: "test", present: true);
        tracker.Record(DeploymentNormTracker.Features.Http2, bucket: "test", present: true);
        tracker.IsWarmingUp.Should().BeFalse();

        // First tick after warm-up: handler logs completion and disposes itself.
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
        captured.Disposed.Should().BeTrue();

        // Second tick is harmless even after dispose -- the handler still
        // gets called by RecordingScheduleCoordinator (it's a test stub) but
        // is a no-op via the announce-once guard.
        await captured.Handler(DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var tracker = NewTracker(warmupRequests: 100);

        var sut = new DeploymentNormCalibrationService(
            tracker,
            Options(warmupRequests: 100),
            NullLogger<DeploymentNormCalibrationService>.Instance,
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose is safe.
        sut.Dispose();
    }
}
