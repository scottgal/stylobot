using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.PrometheusPack.Policies;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Test.Policies.Signals;

/// <summary>
///     Phase F (F2): the editor's autocomplete catalog learns the
///     <c>meter.{name}.{facet}</c> vocabulary via
///     <see cref="MeterSignalCatalogSource" /> so dynamic meter names show
///     up alongside the static SignalKeys reflection.
/// </summary>
public class SignalCatalogMeterIntegrationTests
{
    [Fact]
    public async Task Histogram_meter_contributes_five_catalog_entries()
    {
        var stream = new FakeMeterStream();
        stream.Add(new MeterCatalogEntry("hist1", "hist1", MeterKind.Histogram, "ms", null));

        var catalog = await BuildCatalog(stream);

        Assert.NotNull(catalog.TryGet("meter.hist1.current"));
        Assert.NotNull(catalog.TryGet("meter.hist1.p50"));
        Assert.NotNull(catalog.TryGet("meter.hist1.p99"));
        Assert.NotNull(catalog.TryGet("meter.hist1.delta_1m"));
        Assert.NotNull(catalog.TryGet("meter.hist1.delta_5m"));
    }

    [Fact]
    public async Task Counter_meter_contributes_three_catalog_entries()
    {
        var stream = new FakeMeterStream();
        stream.Add(new MeterCatalogEntry("counter1", "counter1", MeterKind.Counter, null, null));

        var catalog = await BuildCatalog(stream);

        Assert.NotNull(catalog.TryGet("meter.counter1.current"));
        Assert.NotNull(catalog.TryGet("meter.counter1.delta_1m"));
        Assert.NotNull(catalog.TryGet("meter.counter1.delta_5m"));
        Assert.Null(catalog.TryGet("meter.counter1.p50"));
        Assert.Null(catalog.TryGet("meter.counter1.p99"));
    }

    [Fact]
    public async Task Gauge_meter_contributes_three_catalog_entries()
    {
        var stream = new FakeMeterStream();
        stream.Add(new MeterCatalogEntry("gauge1", "gauge1", MeterKind.Gauge, null, null));

        var catalog = await BuildCatalog(stream);

        Assert.NotNull(catalog.TryGet("meter.gauge1.current"));
        Assert.NotNull(catalog.TryGet("meter.gauge1.delta_1m"));
        Assert.NotNull(catalog.TryGet("meter.gauge1.delta_5m"));
        Assert.Null(catalog.TryGet("meter.gauge1.p50"));
        Assert.Null(catalog.TryGet("meter.gauge1.p99"));
    }

    [Fact]
    public async Task Meter_entries_belong_to_Meters_group()
    {
        var stream = new FakeMeterStream();
        stream.Add(new MeterCatalogEntry("hist1", "hist1", MeterKind.Histogram, null, null));

        var catalog = await BuildCatalog(stream);
        var entry = catalog.TryGet("meter.hist1.current");

        Assert.NotNull(entry);
        Assert.Equal("Meters", entry!.DeclaringType);
    }

    [Fact]
    public async Task Meter_entries_are_SignalKind_Number()
    {
        var stream = new FakeMeterStream();
        stream.Add(new MeterCatalogEntry("hist1", "hist1", MeterKind.Histogram, null, null));

        var catalog = await BuildCatalog(stream);

        foreach (var suffix in new[] { ".current", ".p50", ".p99", ".delta_1m", ".delta_5m" })
        {
            var entry = catalog.TryGet($"meter.hist1{suffix}");
            Assert.NotNull(entry);
            Assert.Equal(SignalKind.Number, entry!.Kind);
        }
    }

    [Fact]
    public async Task Empty_catalog_adds_no_meter_entries_to_signal_catalog()
    {
        var stream = new FakeMeterStream(); // no meters
        var catalog = await BuildCatalog(stream);

        // Sanity: no meter.* keys leaked in from the empty stream.
        foreach (var d in catalog.All)
            Assert.False(d.Key.StartsWith("meter.", StringComparison.Ordinal),
                $"Unexpected meter.* key {d.Key} from an empty meter stream.");
    }

    [Fact]
    public async Task Meters_appear_in_autocomplete_search_alongside_signal_keys()
    {
        // Composite catalog: reflected SignalKeys + meter source. Editor
        // autocomplete (prefix-search on '.') should surface meter.* keys.
        var stream = new FakeMeterStream();
        stream.Add(new MeterCatalogEntry("foo", "foo", MeterKind.Gauge, null, null));

        var catalog = await BuildCatalog(stream);
        var hits = catalog.Search("meter.").ToList();

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.StartsWith("meter.", h.Key));
    }

    [Fact]
    public async Task Live_meter_appearing_after_load_shows_up_on_next_read()
    {
        // The catalog enumerates sources lazily on read so a meter that
        // appears mid-process (the LocalMeterStream subscribes via
        // MeterListener and discovers instruments over time) is catalogued
        // the next time the editor pulls the vocabulary.
        //
        // Wave 4: the catalog source reads from MeterSignalsAtom, so a
        // newly-published meter only surfaces after the next atom tick.
        // Tests pump the rebuild explicitly to keep the cadence-free.
        var stream = new FakeMeterStream();
        var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();

        var asm = typeof(SignalKeys).Assembly;
        var source = new MeterSignalCatalogSource(atom);
        var catalog = await SignalCatalog.LoadAsync(asm, new ISignalCatalogSource[] { source });

        Assert.Null(catalog.TryGet("meter.late.current"));

        stream.Add(new MeterCatalogEntry("late", "late", MeterKind.Gauge, null, null));
        await atom.RebuildNowAsync();

        Assert.NotNull(catalog.TryGet("meter.late.current"));
    }

    [Fact]
    public async Task Faulty_source_does_not_take_down_catalog_reads()
    {
        // A source that throws should be swallowed by the catalog so the
        // reflected vocabulary continues to resolve.
        var faulty = new ThrowingCatalogSource();
        var asm = typeof(SignalKeys).Assembly;
        var catalog = await SignalCatalog.LoadAsync(asm, new ISignalCatalogSource[] { faulty });

        // risk.justification is one of the well-known SignalKeys constants
        // exercised by the existing SignalCatalogTests.
        Assert.NotNull(catalog.TryGet("risk.justification"));
    }

    private static async Task<SignalCatalog> BuildCatalog(IMeterStream stream)
    {
        // Wave 4: the catalog source reads from MeterSignalsAtom.CurrentCatalog
        // synchronously, so the test seeds the atom by pumping ONE rebuild
        // before standing up the catalog.
        var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();
        var asm = typeof(SignalKeys).Assembly;
        var source = new MeterSignalCatalogSource(atom);
        return await SignalCatalog.LoadAsync(asm, new ISignalCatalogSource[] { source });
    }

    private sealed class FakeMeterStream : IMeterStream
    {
        private readonly List<MeterCatalogEntry> _entries = new();

        public void Add(MeterCatalogEntry entry) => _entries.Add(entry);

        public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(_entries);

        public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
            => Task.FromResult<MeterTimeSeries?>(null);
    }

    private sealed class ThrowingCatalogSource : ISignalCatalogSource
    {
        public IReadOnlyList<SignalDescriptor> GetDescriptors()
            => throw new InvalidOperationException("source intentionally broken");
    }
}
