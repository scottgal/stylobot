using Mostlylucid.BotDetection.PrometheusPack.Policies;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Test.Policies.Signals;

/// <summary>
///     Phase F (F2): the contributor pumps the live meter snapshot into the
///     per-request signal bag under the <c>meter.{name}.{facet}</c> family so
///     composite predicates like
///     <c>score.bot_probability &gt; 0.9 AND meter.system.cpu_load.current &gt; 80</c>
///     can resolve naturally.
///
///     <para>
///         <b>Wave 4 update:</b> the contributor reads from a
///         <see cref="MeterSignalsAtom"/> instead of awaiting
///         <see cref="IMeterStream.GetAsync"/> on the request hot path. The
///         tests below build a fake stream, drive ONE atom rebuild via
///         <see cref="MeterSignalsAtom.RebuildNowAsync"/>, then assert the
///         resulting per-request merge. The old "N awaited GetAsync calls per
///         request" assertions are obsolete and replaced by snapshot-merge
///         assertions.
///     </para>
/// </summary>
public class MeterSnapshotSignalContributorTests
{
    private static async Task<MeterSignalsAtom> BuildAtomAsync(IMeterStream stream)
    {
        var atom = new MeterSignalsAtom(stream, NullScheduleCoordinator.Instance);
        await atom.RebuildNowAsync();
        return atom;
    }

    [Fact]
    public async Task Adds_current_for_every_catalogued_meter()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(
            new MeterCatalogEntry("alpha", "alpha", MeterKind.Gauge, null, null),
            current: 17,
            values: new[] { 1d });
        stream.AddMeter(
            new MeterCatalogEntry("beta", "beta", MeterKind.Counter, null, null),
            current: 42,
            values: new[] { 2d });

        using var atom = await BuildAtomAsync(stream);
        var contributor = new MeterSnapshotSignalContributor(atom);
        var signals = new Dictionary<string, object?>();
        await contributor.ContributeAsync(signals, CancellationToken.None);

        Assert.True(signals.ContainsKey("meter.alpha.current"));
        Assert.Equal(17d, Assert.IsType<double>(signals["meter.alpha.current"]));
        Assert.True(signals.ContainsKey("meter.beta.current"));
        Assert.Equal(42d, Assert.IsType<double>(signals["meter.beta.current"]));
    }

    [Fact]
    public async Task Adds_delta_1m_and_delta_5m_with_summed_bucket_values()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(
            new MeterCatalogEntry("alpha", "alpha", MeterKind.Counter, null, null),
            current: 5,
            valuesByWindow: new Dictionary<TimeSpan, IReadOnlyList<double>>
            {
                [TimeSpan.FromMinutes(1)] = new[] { 1d, 2d, 3d },
                [TimeSpan.FromMinutes(5)] = new[] { 10d, 20d, 30d, 40d }
            });

        using var atom = await BuildAtomAsync(stream);
        var contributor = new MeterSnapshotSignalContributor(atom);
        var signals = new Dictionary<string, object?>();
        await contributor.ContributeAsync(signals, CancellationToken.None);

        Assert.True(signals.ContainsKey("meter.alpha.delta_1m"));
        Assert.Equal(6d, Assert.IsType<double>(signals["meter.alpha.delta_1m"]));

        Assert.True(signals.ContainsKey("meter.alpha.delta_5m"));
        Assert.Equal(100d, Assert.IsType<double>(signals["meter.alpha.delta_5m"]));
    }

    [Fact]
    public async Task Counter_only_emits_current_and_deltas()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(
            new MeterCatalogEntry("counter1", "counter1", MeterKind.Counter, null, null),
            current: 5,
            values: new[] { 1d, 1d },
            p50: 99d,        // contributor must ignore percentiles on non-histogram kinds
            p99: 99d);

        using var atom = await BuildAtomAsync(stream);
        var contributor = new MeterSnapshotSignalContributor(atom);
        var signals = new Dictionary<string, object?>();
        await contributor.ContributeAsync(signals, CancellationToken.None);

        Assert.True(signals.ContainsKey("meter.counter1.current"));
        Assert.True(signals.ContainsKey("meter.counter1.delta_1m"));
        Assert.True(signals.ContainsKey("meter.counter1.delta_5m"));
        Assert.False(signals.ContainsKey("meter.counter1.p50"));
        Assert.False(signals.ContainsKey("meter.counter1.p99"));
    }

    [Fact]
    public async Task Histogram_emits_p50_and_p99_in_addition_to_current_and_deltas()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(
            new MeterCatalogEntry("hist1", "hist1", MeterKind.Histogram, "ms", null),
            current: 12,
            values: new[] { 0.5d },
            p50: 10d,
            p99: 99.9d);

        using var atom = await BuildAtomAsync(stream);
        var contributor = new MeterSnapshotSignalContributor(atom);
        var signals = new Dictionary<string, object?>();
        await contributor.ContributeAsync(signals, CancellationToken.None);

        Assert.True(signals.ContainsKey("meter.hist1.current"));
        Assert.True(signals.ContainsKey("meter.hist1.delta_1m"));
        Assert.True(signals.ContainsKey("meter.hist1.delta_5m"));
        Assert.True(signals.ContainsKey("meter.hist1.p50"));
        Assert.Equal(10d, signals["meter.hist1.p50"]);
        Assert.True(signals.ContainsKey("meter.hist1.p99"));
        Assert.Equal(99.9d, signals["meter.hist1.p99"]);
    }

    [Fact]
    public async Task Existing_keys_win_over_contributor_signals()
    {
        var stream = new FakeMeterStream();
        stream.AddMeter(
            new MeterCatalogEntry("foo", "foo", MeterKind.Gauge, null, null),
            current: 123,
            values: new[] { 5d });

        using var atom = await BuildAtomAsync(stream);
        var contributor = new MeterSnapshotSignalContributor(atom);
        var signals = new Dictionary<string, object?>
        {
            ["meter.foo.current"] = 999d
        };
        await contributor.ContributeAsync(signals, CancellationToken.None);

        Assert.Equal(999d, signals["meter.foo.current"]);
    }

    [Fact]
    public async Task Empty_catalog_adds_no_signals()
    {
        var stream = new FakeMeterStream();   // no meters registered
        using var atom = await BuildAtomAsync(stream);
        var contributor = new MeterSnapshotSignalContributor(atom);
        var signals = new Dictionary<string, object?>();
        await contributor.ContributeAsync(signals, CancellationToken.None);

        Assert.Empty(signals);
    }

    [Fact]
    public async Task Contribute_is_synchronous_on_request_hot_path()
    {
        // Wave 4 invariant: the contributor must NOT await on the request hot
        // path. ContributeAsync returns a completed Task without scheduling any
        // continuation; the dictionary merge runs inline.
        var stream = new FakeMeterStream();
        stream.AddMeter(
            new MeterCatalogEntry("alpha", "alpha", MeterKind.Gauge, null, null),
            current: 1,
            values: new[] { 0d });

        using var atom = await BuildAtomAsync(stream);
        var contributor = new MeterSnapshotSignalContributor(atom);
        var signals = new Dictionary<string, object?>();

        var task = contributor.ContributeAsync(signals, CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully,
            "Contributor must complete synchronously; the atom owns the awaited rebuild.");
    }

    private sealed class FakeMeterStream : IMeterStream
    {
        private readonly List<MeterCatalogEntry> _entries = new();
        private readonly Dictionary<string, FakeMeterSeries> _series = new(StringComparer.Ordinal);

        public void AddMeter(
            MeterCatalogEntry entry,
            double current,
            IReadOnlyList<double>? values = null,
            double? p50 = null,
            double? p99 = null,
            Dictionary<TimeSpan, IReadOnlyList<double>>? valuesByWindow = null)
        {
            _entries.Add(entry);
            _series[entry.Name] = new FakeMeterSeries(
                entry.Kind,
                current,
                values ?? Array.Empty<double>(),
                p50,
                p99,
                valuesByWindow);
        }

        public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(_entries);
        }

        public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_series.TryGetValue(meterName, out var s)) return Task.FromResult<MeterTimeSeries?>(null);

            var values = s.ResolveValuesFor(window);
            var bucketsList = new List<DateTimeOffset>(values.Count);
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < values.Count; i++) bucketsList.Add(now);

            return Task.FromResult<MeterTimeSeries?>(new MeterTimeSeries(
                meterName,
                s.Kind,
                bucketsList,
                values,
                s.Current,
                s.P50,
                s.P99));
        }

        private sealed record FakeMeterSeries(
            MeterKind Kind,
            double Current,
            IReadOnlyList<double> DefaultValues,
            double? P50,
            double? P99,
            Dictionary<TimeSpan, IReadOnlyList<double>>? ValuesByWindow)
        {
            public IReadOnlyList<double> ResolveValuesFor(TimeSpan window)
            {
                if (ValuesByWindow is not null && ValuesByWindow.TryGetValue(window, out var perWindow))
                    return perWindow;
                return DefaultValues;
            }
        }
    }
}
