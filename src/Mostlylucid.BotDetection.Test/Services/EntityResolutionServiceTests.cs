using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="EntityResolutionService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     load-sensor-scaled <c>Task.Delay</c> loop; now subscribes to
///     <see cref="TickCadence.Tick1m"/> and short-circuits the tick when
///     the <see cref="PipelineLoadSensor"/> indicates we should back off.
/// </summary>
public sealed class EntityResolutionServiceTests
{
    private static EntityResolutionService NewService(
        RecordingScheduleCoordinator coordinator,
        Mock<ISessionStore>? storeOverride = null)
    {
        var store = storeOverride ?? new Mock<ISessionStore>(MockBehavior.Loose);
        // GetActiveEntityIdsAsync returning an empty list is the simplest
        // path -- the tick handler iterates nothing and returns cleanly.
        store.Setup(s => s.GetActiveEntityIdsAsync(
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        return new EntityResolutionService(
            store.Object,
            NullLogger<EntityResolutionService>.Instance,
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1m_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1m);
        sub.Name.Should().Be("EntityResolutionService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_when_no_entities()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator);

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
