using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Wave 2 batch regression coverage for <see cref="RemoteMetricCollector"/>.
///     Was a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with
///     a <c>Task.Delay(_pollInterval)</c> loop; now subscribes to
///     <see cref="TickCadence.Tick10s"/> and gates the poll on the constructor-
///     supplied interval.
/// </summary>
public sealed class RemoteMetricCollectorTickTests
{
    private static RemoteMetricCollector NewService(RecordingScheduleCoordinator coordinator)
    {
        var httpFactory = new Mock<IHttpClientFactory>(MockBehavior.Loose).Object;
        var store = new Mock<IMetricSnapshotStore>(MockBehavior.Loose).Object;
        return new RemoteMetricCollector(
            httpFactory,
            "http://localhost:9999/metrics",
            TimeSpan.FromMinutes(1),
            store,
            NullLogger<RemoteMetricCollector>.Instance,
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("RemoteMetricCollector");
        sub.Hint.Should().Be(CostHint.Low);
    }

    [Fact]
    public async Task OnTickAsync_first_tick_announces_and_polls_without_throwing()
    {
        // The HttpClient factory returns a default client; the GetFromJsonAsync
        // call will fail to connect, the handler catches it and logs a warning.
        // The fact under test: the handler doesn't throw out of the tick and
        // doesn't dispose the subscription.
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