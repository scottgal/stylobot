using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 batch 1 regression coverage for
///     <see cref="SessionAtomizerService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     <c>Task.Delay(AtomizerRunInterval)</c> loop and a shutdown-time
///     force-flush; now subscribes to <see cref="TickCadence.Tick1m"/> and
///     gates the atomize pass on "last-success older than configured
///     interval".
/// </summary>
public sealed class SessionAtomizerServiceTests
{
    private static SessionAtomizerService NewService(
        RecordingScheduleCoordinator coordinator)
    {
        var store = new Mock<ISessionStore>(MockBehavior.Loose);
        // No unatomized requests -- the atomize pass short-circuits cleanly.
        store.Setup(s => s.GetUnatomizedRequestsAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PersistedRequest>());

        return new SessionAtomizerService(
            store.Object,
            NullLogger<SessionAtomizerService>.Instance,
            Options.Create(new BotDetectionOptions()),
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1m_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1m);
        sub.Name.Should().Be("SessionAtomizerService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_when_no_unatomized_requests()
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
