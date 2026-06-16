using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

/// <summary>
///     Wave 2 batch 2 regression coverage for
///     <see cref="MeterListenerService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///     <c>ExecuteAsync</c> looped on
///     <c>Task.Delay(packs.Min(p =&gt; p.CollectionInterval))</c> + flush; now
///     subscribes to <see cref="TickCadence.Tick1m"/> (the default pack
///     CollectionInterval and the snapshot-bucket resolution).
/// </summary>
public sealed class MeterListenerServiceTickTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteMetricSnapshotStore _store;

    public MeterListenerServiceTickTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new SqliteMetricSnapshotStore(_conn, NullLogger<SqliteMetricSnapshotStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    private MeterListenerService NewService(RecordingScheduleCoordinator coordinator)
    {
        var pack = new TestPack([
            new MeterCollectionGroup("TestMeter", [
                new InstrumentCollectionSpec("test.requests", CollectedValueType.Counter)
            ])
        ]);

        return new MeterListenerService(
            [pack],
            _store,
            NullLogger<MeterListenerService>.Instance,
            runtimeController: null,
            scheduleCoordinator: coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1m_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1m);
        sub.Name.Should().Be("MeterListenerService");
        sub.Hint.Should().Be(CostHint.Low);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_when_no_measurements()
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

    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync();
    }
}