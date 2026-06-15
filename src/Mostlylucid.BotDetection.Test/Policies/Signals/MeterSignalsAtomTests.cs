using Mostlylucid.BotDetection.PrometheusPack.Policies;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Policies.Signals;

/// <summary>
///     Wave 4 (architectural-drift remediation): the
///     <see cref="MeterSignalsAtom"/> rebuilds the canonical meter-signals
///     dictionary on <see cref="TickCadence.Tick10s"/> and exposes it as a
///     lock-free <see cref="MeterSignalsAtom.CurrentSnapshot"/>. These tests
///     drive the atom through its public <c>RebuildNowAsync</c> hook so the
///     coordinator's wall-clock cadence stays out of the test path.
/// </summary>
public class MeterSignalsAtomTests
{
    [Fact]
    public async Task Initial_snapshot_is_empty_until_first_rebuild()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(new MeterCatalogEntry("alpha", "alpha", MeterKind.Gauge, null, null), current: 17, values: new[] { 1d });

        using var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);

        // Before the first tick fires, the snapshot is empty (never null).
        Assert.NotNull(atom.CurrentSnapshot);
        Assert.Empty(atom.CurrentSnapshot);
        Assert.NotNull(atom.CurrentCatalog);
        Assert.Empty(atom.CurrentCatalog);
    }

    [Fact]
    public async Task First_tick_populates_snapshot_with_expected_keys()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(new MeterCatalogEntry("alpha", "alpha", MeterKind.Gauge, null, null), current: 17, values: new[] { 1d });
        stream.AddMeter(new MeterCatalogEntry("beta", "beta", MeterKind.Counter, null, null), current: 42, values: new[] { 2d });

        using var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();

        var snap = atom.CurrentSnapshot;
        Assert.True(snap.ContainsKey("meter.alpha.current"));
        Assert.Equal(17d, snap["meter.alpha.current"]);
        Assert.True(snap.ContainsKey("meter.beta.current"));
        Assert.Equal(42d, snap["meter.beta.current"]);
    }

    [Fact]
    public async Task Histogram_meter_contributes_p50_and_p99()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(
            new MeterCatalogEntry("hist1", "hist1", MeterKind.Histogram, "ms", null),
            current: 12,
            values: new[] { 0.5d },
            p50: 10d,
            p99: 99.9d);

        using var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();

        var snap = atom.CurrentSnapshot;
        Assert.True(snap.ContainsKey("meter.hist1.p50"));
        Assert.Equal(10d, snap["meter.hist1.p50"]);
        Assert.True(snap.ContainsKey("meter.hist1.p99"));
        Assert.Equal(99.9d, snap["meter.hist1.p99"]);
    }

    [Fact]
    public async Task Counter_meter_omits_percentile_keys()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(
            new MeterCatalogEntry("counter1", "counter1", MeterKind.Counter, null, null),
            current: 5,
            values: new[] { 1d, 1d },
            p50: 99d,
            p99: 99d);

        using var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();

        var snap = atom.CurrentSnapshot;
        Assert.True(snap.ContainsKey("meter.counter1.current"));
        Assert.True(snap.ContainsKey("meter.counter1.delta_1m"));
        Assert.True(snap.ContainsKey("meter.counter1.delta_5m"));
        Assert.False(snap.ContainsKey("meter.counter1.p50"));
        Assert.False(snap.ContainsKey("meter.counter1.p99"));
    }

    [Fact]
    public async Task Second_tick_publishes_updated_snapshot_via_reference_swap()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(new MeterCatalogEntry("alpha", "alpha", MeterKind.Gauge, null, null), current: 17, values: new[] { 1d });

        using var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();

        var firstRef = atom.CurrentSnapshot;
        Assert.Equal(17d, firstRef["meter.alpha.current"]);

        // Mutate the underlying stream and re-tick.
        stream.UpdateMeter("alpha", current: 99, values: new[] { 1d });
        await atom.RebuildNowAsync();

        var secondRef = atom.CurrentSnapshot;
        Assert.Equal(99d, secondRef["meter.alpha.current"]);

        // First reference is still readable (no in-place mutation) and still
        // shows the pre-tick value -- this is the atomic-swap contract.
        Assert.Equal(17d, firstRef["meter.alpha.current"]);
    }

    [Fact]
    public async Task Empty_catalog_publishes_empty_snapshot_not_null()
    {
        var stream = new FakeMeterStream();
        using var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();

        Assert.NotNull(atom.CurrentSnapshot);
        Assert.Empty(atom.CurrentSnapshot);
        Assert.NotNull(atom.CurrentCatalog);
        Assert.Empty(atom.CurrentCatalog);
    }

    [Fact]
    public async Task CurrentCatalog_mirrors_stream_catalog_after_tick()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(new MeterCatalogEntry("alpha", "alpha", MeterKind.Gauge, null, null), current: 1, values: new[] { 0d });
        stream.AddMeter(new MeterCatalogEntry("hist1", "hist1", MeterKind.Histogram, "ms", null), current: 1, values: new[] { 0d }, p50: 0d, p99: 0d);

        using var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();

        var catalog = atom.CurrentCatalog;
        Assert.Equal(2, catalog.Count);
        Assert.Contains(catalog, e => e.Name == "alpha");
        Assert.Contains(catalog, e => e.Name == "hist1");
    }

    [Fact]
    public async Task Faulty_stream_does_not_take_down_previous_snapshot()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(new MeterCatalogEntry("alpha", "alpha", MeterKind.Gauge, null, null), current: 17, values: new[] { 1d });

        using var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();
        Assert.Equal(17d, atom.CurrentSnapshot["meter.alpha.current"]);

        // Next tick: stream throws on ListAsync. The atom must keep the
        // previously-published snapshot, not blow it away.
        stream.SetListThrows(true);
        await atom.RebuildNowAsync();

        Assert.Equal(17d, atom.CurrentSnapshot["meter.alpha.current"]);
    }

    [Fact]
    public async Task Cancellation_during_rebuild_propagates_OperationCanceledException()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(new MeterCatalogEntry("alpha", "alpha", MeterKind.Gauge, null, null), current: 1, values: new[] { 0d });

        using var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => atom.RebuildNowAsync(cts.Token));
    }

    [Fact]
    public void Constructor_subscribes_to_Tick10s()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var atom = new MeterSignalsAtom(new FakeMeterStream(), coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        Assert.Equal(TickCadence.Tick10s, sub.Cadence);
        Assert.Equal(CostHint.Low, sub.Hint);
        Assert.Equal("MeterSignalsAtom.Rebuild", sub.Name);
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var atom = new MeterSignalsAtom(new FakeMeterStream(), coordinator);
        var sub = Assert.Single(coordinator.Subscriptions);

        atom.Dispose();
        Assert.True(sub.Disposed);

        // Double-dispose is safe.
        atom.Dispose();
    }

    private sealed class FakeMeterStream : IMeterStream
    {
        private readonly List<MeterCatalogEntry> _entries = new();
        private readonly Dictionary<string, FakeMeterSeries> _series = new(StringComparer.Ordinal);
        private bool _listThrows;

        public void AddMeter(
            MeterCatalogEntry entry,
            double current,
            IReadOnlyList<double>? values = null,
            double? p50 = null,
            double? p99 = null)
        {
            _entries.Add(entry);
            _series[entry.Name] = new FakeMeterSeries(entry.Kind, current, values ?? Array.Empty<double>(), p50, p99);
        }

        public void UpdateMeter(string name, double current, IReadOnlyList<double> values)
        {
            if (_series.TryGetValue(name, out var existing))
                _series[name] = existing with { Current = current, DefaultValues = values };
        }

        public void SetListThrows(bool value) => _listThrows = value;

        public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_listThrows) throw new InvalidOperationException("synthetic ListAsync fault");
            return Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(_entries);
        }

        public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_series.TryGetValue(meterName, out var s)) return Task.FromResult<MeterTimeSeries?>(null);
            var bucketsList = new List<DateTimeOffset>(s.DefaultValues.Count);
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < s.DefaultValues.Count; i++) bucketsList.Add(now);
            return Task.FromResult<MeterTimeSeries?>(new MeterTimeSeries(
                meterName, s.Kind, bucketsList, s.DefaultValues, s.Current, s.P50, s.P99));
        }

        private sealed record FakeMeterSeries(
            MeterKind Kind,
            double Current,
            IReadOnlyList<double> DefaultValues,
            double? P50,
            double? P99);
    }

}
