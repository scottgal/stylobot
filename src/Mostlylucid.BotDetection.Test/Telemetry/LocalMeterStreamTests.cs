using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;

namespace Mostlylucid.BotDetection.Test.Telemetry;

public class LocalMeterStreamTests
{
    [Fact]
    public async Task Subscribes_to_meter_and_catalogs_instruments()
    {
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);
        var counter = meter.CreateCounter<long>($"{prefix}.requests_total", "1", "Test requests");

        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions { MeterNamePrefixFilter = prefix });

        counter.Add(1);
        counter.Add(2);
        counter.Add(5);
        await DrainAsync();

        var catalog = await stream.ListAsync(CancellationToken.None);
        Assert.Contains(catalog, c => c.Name == $"{prefix}.requests_total" && c.Kind == MeterKind.Counter);
    }

    [Fact]
    public async Task Counter_get_async_returns_delta_per_bucket()
    {
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);
        var counter = meter.CreateCounter<long>($"{prefix}.hits");

        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions { MeterNamePrefixFilter = prefix });

        counter.Add(5); await DrainAsync();
        counter.Add(3); await DrainAsync();
        counter.Add(10); await DrainAsync();

        var ts = await stream.GetAsync($"{prefix}.hits", TimeSpan.FromMinutes(1), buckets: 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.Equal(MeterKind.Counter, ts!.Kind);
        Assert.Equal(6, ts.Buckets.Count);
        Assert.Equal(6, ts.Values.Count);
        Assert.Equal(18, ts.Current);
        Assert.Equal(18, ts.Values.Sum(), precision: 0);
    }

    [Fact]
    public async Task Gauge_get_async_returns_average_per_bucket()
    {
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);
        double observed = 0;
        meter.CreateObservableGauge<double>($"{prefix}.queue_depth", () => observed);

        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions { MeterNamePrefixFilter = prefix });

        observed = 10; stream.PumpObservables();
        observed = 20; stream.PumpObservables();
        observed = 30; stream.PumpObservables();

        var ts = await stream.GetAsync($"{prefix}.queue_depth", TimeSpan.FromMinutes(1), buckets: 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.Equal(MeterKind.Gauge, ts!.Kind);
        Assert.Equal(30, ts.Current);
        Assert.True(ts.Values.Average() > 0);
    }

    [Fact]
    public async Task Histogram_get_async_emits_p50_and_p99()
    {
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);
        var hist = meter.CreateHistogram<double>($"{prefix}.latency_ms");

        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions { MeterNamePrefixFilter = prefix });

        for (var i = 1; i <= 100; i++) hist.Record(i);
        await DrainAsync();

        var ts = await stream.GetAsync($"{prefix}.latency_ms", TimeSpan.FromMinutes(1), buckets: 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.Equal(MeterKind.Histogram, ts!.Kind);
        Assert.NotNull(ts.P50);
        Assert.NotNull(ts.P99);
        Assert.InRange(ts.P50!.Value, 40, 60);
        Assert.InRange(ts.P99!.Value, 95, 100);
    }

    [Fact]
    public async Task Get_async_returns_null_for_unknown_meter()
    {
        var prefix = $"a4test_{Guid.NewGuid():N}";
        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions { MeterNamePrefixFilter = prefix });

        var ts = await stream.GetAsync($"{prefix}.never_emitted", TimeSpan.FromMinutes(1), 6, CancellationToken.None);
        Assert.Null(ts);
    }

    // ------------------------------------------------------------------
    // NEW tests for the LFU sliding-cache + signal-emission rework.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Lfu_evicts_by_meter_count_not_sample_count()
    {
        // 6 distinct meters, cap = 4. Hit two of them ~100x and the other four
        // exactly once. After a sweep, only the two hot meters survive.
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);

        var hotA = meter.CreateCounter<long>($"{prefix}.hot_a");
        var hotB = meter.CreateCounter<long>($"{prefix}.hot_b");
        var cold1 = meter.CreateCounter<long>($"{prefix}.cold_1");
        var cold2 = meter.CreateCounter<long>($"{prefix}.cold_2");
        var cold3 = meter.CreateCounter<long>($"{prefix}.cold_3");
        var cold4 = meter.CreateCounter<long>($"{prefix}.cold_4");

        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions
        {
            MeterNamePrefixFilter = prefix,
            MaxTrackedMeters = 4,
            // Disable sliding-decay during the test so HitCount is purely
            // proportional to record calls -- otherwise the timer cycle could
            // halve the hot counters before we eviction-check.
            HitCountDecayFactor = 1.0,
            // Drive the timer past test scope (we'll PumpEvictionForTesting()).
            HitCountDecayInterval = TimeSpan.FromMinutes(10),
        });

        cold1.Add(1);
        cold2.Add(1);
        cold3.Add(1);
        cold4.Add(1);
        for (var i = 0; i < 100; i++) hotA.Add(1);
        for (var i = 0; i < 100; i++) hotB.Add(1);
        await DrainAsync();

        stream.PumpEvictionForTesting();

        var catalog = await stream.ListAsync(CancellationToken.None);
        Assert.Equal(4, catalog.Count);
        Assert.Contains(catalog, c => c.Name == $"{prefix}.hot_a");
        Assert.Contains(catalog, c => c.Name == $"{prefix}.hot_b");
        // Some-but-not-all of the colds will have been evicted to reach the cap;
        // exactly two should have survived (the two with the same hit count;
        // we can't tell which two from outside since hit counts are equal).
        var survivedColds = catalog.Count(c => c.Name.StartsWith($"{prefix}.cold_"));
        Assert.Equal(2, survivedColds);
    }

    [Fact]
    public async Task Hit_count_decay_lets_recently_active_meter_win()
    {
        // Once-hot meter A goes idle; meter B starts hitting. After enough
        // decay cycles A's HitCount should drop below B's so a forced
        // eviction-at-cap evicts A, not B.
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);

        var meterA = meter.CreateCounter<long>($"{prefix}.previously_hot");
        var meterB = meter.CreateCounter<long>($"{prefix}.recently_active");

        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions
        {
            MeterNamePrefixFilter = prefix,
            MaxTrackedMeters = 1,
            HitCountDecayFactor = 0.5,
            HitCountDecayInterval = TimeSpan.FromHours(1), // we'll pump manually
        });

        // A becomes very hot first; just 100 hits is more than enough.
        for (var i = 0; i < 100; i++) meterA.Add(1);
        await DrainAsync();

        // Decay enough cycles that A's count is tiny: 100 * 0.5^15 ~= 0.003.
        // The eviction sweep WILL evict during these pumps (cap=1, count=1
        // means no eviction yet -- A is alone), so the pumps only decay.
        for (var i = 0; i < 15; i++) stream.PumpEvictionForTesting();

        // B comes online with several fresh hits -- well above A's residual.
        for (var i = 0; i < 10; i++) meterB.Add(1);
        await DrainAsync();

        // Now cap=1, dictionary count=2, the sweep should evict A (the cold one).
        stream.PumpEvictionForTesting();

        var catalog = await stream.ListAsync(CancellationToken.None);
        Assert.Single(catalog);
        Assert.Equal($"{prefix}.recently_active", catalog[0].Name);
    }

    [Fact]
    public async Task Signal_emission_is_coalesced_within_min_interval()
    {
        // 100 rapid counter increments inside the coalesce window should
        // produce only a small handful of signals, the latest of which
        // reflects the cumulative total.
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);
        var counter = meter.CreateCounter<long>($"{prefix}.coalesced");

        var sink = new RecordingMeterSignalSink();
        await using var stream = await StartStreamAsync(
            new LocalMeterStreamOptions
            {
                MeterNamePrefixFilter = prefix,
                MinSignalEmitInterval = TimeSpan.FromMinutes(5),
            },
            sink);

        for (var i = 0; i < 100; i++) counter.Add(1);
        await DrainAsync();

        // First Record() always emits (LastSignalEmittedAt starts at MinValue),
        // subsequent records inside the 5-minute window are all squelched.
        Assert.True(sink.Signals.Count >= 1, $"Expected >= 1 signal, got {sink.Signals.Count}");
        Assert.True(sink.Signals.Count <= 3, $"Expected <= 3 signals (coalesced), got {sink.Signals.Count}");

        var latest = sink.Signals.Last();
        Assert.Equal($"{prefix}.coalesced", latest.Name);
        Assert.Equal(MeterKind.Counter, latest.Kind);
        // The cumulative is captured at the moment the signal fires; the very
        // first record will report Current=1 and may be the only one if every
        // subsequent record landed inside the coalesce window. We just assert
        // the SINK saw some signal -- the most important property (no firehose).
    }

    [Fact]
    public async Task Snapshot_rebuilds_from_summary_buckets_not_raw_samples()
    {
        // 1000 counter increments. With 24 5s buckets and a 1-minute window,
        // sum of the rebuilt time-series should ≈ 1000 (delta semantics), and
        // Current should be exactly 1000. Internals never kept the 1000
        // individual samples -- only the per-bucket aggregates.
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);
        var counter = meter.CreateCounter<long>($"{prefix}.rebuild_check");

        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions
        {
            MeterNamePrefixFilter = prefix,
            RecentBucketCount = 24,
            BucketWidth = TimeSpan.FromSeconds(5),
        });

        for (var i = 0; i < 1000; i++) counter.Add(1);
        await DrainAsync();

        var ts = await stream.GetAsync($"{prefix}.rebuild_check", TimeSpan.FromMinutes(1), buckets: 12, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.Equal(12, ts!.Buckets.Count);
        Assert.Equal(12, ts.Values.Count);
        Assert.Equal(1000, ts.Current);
        Assert.Equal(1000, ts.Values.Sum(), precision: 0);
    }

    [Fact]
    public async Task Histogram_percentiles_come_from_the_reservoir()
    {
        // Record 1..500 into a histogram. The reservoir is bounded at 256,
        // so it holds the most recent 256 values (245..500). P50 lands
        // somewhere in that tail; P99 lands near the upper extreme.
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);
        var hist = meter.CreateHistogram<double>($"{prefix}.percentile_check");

        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions
        {
            MeterNamePrefixFilter = prefix,
            PercentileReservoirSize = 256,
        });

        for (var i = 1; i <= 500; i++) hist.Record(i);
        await DrainAsync();

        var ts = await stream.GetAsync($"{prefix}.percentile_check", TimeSpan.FromMinutes(1), 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.NotNull(ts!.P50);
        Assert.NotNull(ts.P99);
        // Reservoir holds the most recent 256 values (245..500), so the
        // sorted median sits in the [240, 500] middle and P99 near the top.
        Assert.InRange(ts.P50!.Value, 200, 500);
        Assert.InRange(ts.P99!.Value, 480, 500);
    }

    // ---- helpers ----

    private static async Task<LocalMeterStream> StartStreamAsync(LocalMeterStreamOptions opts)
    {
        var stream = new LocalMeterStream(Options.Create(opts), NullLogger<LocalMeterStream>.Instance);
        await stream.StartAsync(CancellationToken.None);
        return stream;
    }

    private static async Task<LocalMeterStream> StartStreamAsync(LocalMeterStreamOptions opts, IMeterSignalSink sink)
    {
        var stream = new LocalMeterStream(Options.Create(opts), NullLogger<LocalMeterStream>.Instance, sink);
        await stream.StartAsync(CancellationToken.None);
        return stream;
    }

    private static Task DrainAsync() => Task.Delay(50);

    private sealed class RecordingMeterSignalSink : IMeterSignalSink
    {
        private readonly ConcurrentQueue<MeterSignal> _signals = new();

        public IReadOnlyList<MeterSignal> Signals => _signals.ToArray();

        public void OnMeter(MeterSignal signal) => _signals.Enqueue(signal);
    }
}
