using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 batch 2 regression coverage for
///     <see cref="BotListUpdateService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with an
///     initialise-then-loop body and exponential backoff on consecutive
///     failures; now subscribes to <see cref="TickCadence.Tick1h"/> and gates
///     the check on the backoff-adjusted check interval since the last
///     attempt.
/// </summary>
public sealed class BotListUpdateServiceTickTests
{
    private static BotListUpdateService NewService(
        RecordingScheduleCoordinator coordinator,
        bool enableBackgroundUpdates = true)
    {
        var database = new Mock<IBotListDatabase>(MockBehavior.Loose);
        database
            .Setup(d => d.InitializeAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        database
            .Setup(d => d.GetLastUpdateTimeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)DateTime.UtcNow);

        var options = Options.Create(new BotDetectionOptions
        {
            EnableBackgroundUpdates = enableBackgroundUpdates,
            StartupDelaySeconds = 0
        });

        return new BotListUpdateService(
            database.Object,
            NullLogger<BotListUpdateService>.Instance,
            options,
            patternCache: null,
            metrics: null,
            scheduleCoordinator: coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1h_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1h);
        sub.Name.Should().Be("BotListUpdateService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_is_noop_when_background_updates_disabled()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator, enableBackgroundUpdates: false);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();

        var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose is safe.
        sut.Dispose();
    }
}
