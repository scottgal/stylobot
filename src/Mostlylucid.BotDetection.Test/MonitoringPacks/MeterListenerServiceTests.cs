using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class MeterListenerServiceTests : IAsyncDisposable
{
    private readonly Meter _testMeter = new("TestMeter", "1.0");
    private readonly SqliteConnection _conn;
    private readonly SqliteMetricSnapshotStore _store;

    public MeterListenerServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new SqliteMetricSnapshotStore(_conn, NullLogger<SqliteMetricSnapshotStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Counter_Accumulates_WritesRateSnapshot()
    {
        var counter = _testMeter.CreateCounter<long>("test.requests");

        var pack = new TestPack([
            new MeterCollectionGroup("TestMeter", [
                new InstrumentCollectionSpec("test.requests", CollectedValueType.Counter)
            ])
        ]);

        using var svc = new MeterListenerService(
            [pack], _store, NullLogger<MeterListenerService>.Instance);
        svc.StartListening();

        counter.Add(10);
        counter.Add(5);

        var snapshots = await svc.FlushSnapshotsAsync(CancellationToken.None);

        Assert.Single(snapshots);
        Assert.Equal(15.0, snapshots[0].Value);
        Assert.Equal("rate", snapshots[0].ValueType);
        Assert.Equal("test.requests", snapshots[0].Instrument);
    }

    [Fact]
    public async Task Gauge_WritesCurrentValue()
    {
        var gauge = _testMeter.CreateObservableGauge("test.active", () => 42);

        var pack = new TestPack([
            new MeterCollectionGroup("TestMeter", [
                new InstrumentCollectionSpec("test.active", CollectedValueType.Gauge)
            ])
        ]);

        using var svc = new MeterListenerService(
            [pack], _store, NullLogger<MeterListenerService>.Instance);
        svc.StartListening();

        var snapshots = await svc.FlushSnapshotsAsync(CancellationToken.None);

        var gaugeSnap = snapshots.FirstOrDefault(s => s.ValueType == "gauge");
        Assert.NotNull(gaugeSnap);
        Assert.Equal(42.0, gaugeSnap!.Value);
    }

    [Fact]
    public async Task Histogram_WritesP50AndP95()
    {
        var hist = _testMeter.CreateHistogram<double>("test.duration");

        var pack = new TestPack([
            new MeterCollectionGroup("TestMeter", [
                new InstrumentCollectionSpec("test.duration", CollectedValueType.Histogram_P50),
                new InstrumentCollectionSpec("test.duration", CollectedValueType.Histogram_P95)
            ])
        ]);

        using var svc = new MeterListenerService(
            [pack], _store, NullLogger<MeterListenerService>.Instance);
        svc.StartListening();

        for (var i = 1; i <= 100; i++) hist.Record(i);

        var snapshots = await svc.FlushSnapshotsAsync(CancellationToken.None);

        var p50 = snapshots.FirstOrDefault(s => s.ValueType == "p50");
        var p95 = snapshots.FirstOrDefault(s => s.ValueType == "p95");
        Assert.NotNull(p50);
        Assert.NotNull(p95);
        Assert.InRange(p50!.Value, 48, 52);
        Assert.InRange(p95!.Value, 93, 97);
    }

    [Fact]
    public async Task PackChanged_RebuildsListener_NewInstrumentIsCollected()
    {
        var counter1 = _testMeter.CreateCounter<long>("test.original");
        var counter2 = _testMeter.CreateCounter<long>("test.added");

        var initialPack = new TestPack(
            [
                new MeterCollectionGroup("TestMeter", [
                    new InstrumentCollectionSpec("test.original", CollectedValueType.Counter)
                ])
            ],
            id: "hot-reload-test");
        var fakeController = new FakePackRuntimeController();

        using var svc = new MeterListenerService(
            [initialPack], _store, NullLogger<MeterListenerService>.Instance, fakeController);
        svc.StartListening();

        counter1.Add(7);
        counter2.Add(99);

        var beforeReload = await svc.FlushSnapshotsAsync(CancellationToken.None);
        Assert.Single(beforeReload);
        Assert.Equal("test.original", beforeReload[0].Instrument);
        Assert.Equal(7.0, beforeReload[0].Value);

        var updatedPack = new TestPack(
            [
                new MeterCollectionGroup("TestMeter", [
                    new InstrumentCollectionSpec("test.original", CollectedValueType.Counter),
                    new InstrumentCollectionSpec("test.added", CollectedValueType.Counter)
                ])
            ],
            id: "hot-reload-test");
        fakeController.RaisePackChanged(updatedPack);

        counter2.Add(11);

        var afterReload = await svc.FlushSnapshotsAsync(CancellationToken.None);
        var newSnap = Assert.Single(afterReload, s => s.Instrument == "test.added");
        Assert.Equal(11.0, newSnap.Value);
    }

    public async ValueTask DisposeAsync()
    {
        _testMeter.Dispose();
        await _conn.DisposeAsync();
    }

    private sealed class FakePackRuntimeController : IPackRuntimeController
    {
        public bool SupportsHotReload(string packId) => true;

        public Task ReplacePackAsync(IMonitoringPack pack, CancellationToken ct)
        {
            RaisePackChanged(pack);
            return Task.CompletedTask;
        }

        public Task ReloadAllAsync(CancellationToken ct) => Task.CompletedTask;

        public event EventHandler<PackChangedEventArgs>? PackChanged;

        public void RaisePackChanged(IMonitoringPack pack)
            => PackChanged?.Invoke(this, new PackChangedEventArgs(pack));
    }
}
