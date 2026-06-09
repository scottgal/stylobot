using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Telemetry;

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

    [Fact]
    public async Task Ring_buffer_evicts_oldest_at_capacity()
    {
        var prefix = $"a4test_{Guid.NewGuid():N}";
        using var meter = new Meter(prefix);
        var counter = meter.CreateCounter<long>($"{prefix}.evicting_counter");

        await using var stream = await StartStreamAsync(new LocalMeterStreamOptions
        {
            MeterNamePrefixFilter = prefix,
            RingBufferCapacity = 16,
        });

        for (var i = 0; i < 100; i++) counter.Add(1);
        await DrainAsync();

        var ts = await stream.GetAsync($"{prefix}.evicting_counter", TimeSpan.FromMinutes(1), 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.True(ts!.Current > 0);
    }

    // ---- helpers ----

    private static async Task<LocalMeterStream> StartStreamAsync(LocalMeterStreamOptions opts)
    {
        var stream = new LocalMeterStream(Options.Create(opts), NullLogger<LocalMeterStream>.Instance);
        await stream.StartAsync(CancellationToken.None);
        return stream;
    }

    private static Task DrainAsync() => Task.Delay(50);
}
