using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

/// <summary>
///     Wave 2 batch 2 regression coverage for
///     <see cref="GatewayMeterAccumulator"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///     <c>ExecuteAsync</c> only ran <see cref="GatewayMeterAccumulator.StartListening"/>
///     and returned -- there was no periodic body to schedule. Now a plain
///     singleton that calls <c>StartListening</c> in its constructor; the
///     three regression facts are "listener established at construction",
///     "snapshot reflects measurements that arrived after construction", and
///     "Dispose tears down the listener cleanly (double-dispose safe)".
/// </summary>
public sealed class GatewayMeterAccumulatorTests
{
    [Fact]
    public void Constructor_starts_listening_and_records_counter_measurements()
    {
        using var meter = new Meter("GatewayMeterAccumulatorTests.A", "1.0");
        var counter = meter.CreateCounter<long>("test.requests");

        var pack = new TestPack([
            new MeterCollectionGroup("GatewayMeterAccumulatorTests.A", [
                new InstrumentCollectionSpec("test.requests", CollectedValueType.Counter)
            ])
        ]);

        using var sut = new GatewayMeterAccumulator(
            [pack], NullLogger<GatewayMeterAccumulator>.Instance);

        counter.Add(7);

        var snapshot = sut.GetCurrentSnapshot();

        snapshot.Should().HaveCount(1);
        snapshot[0].Value.Should().Be(7.0);
        snapshot[0].ValueType.Should().Be("rate");
        snapshot[0].Instrument.Should().Be("test.requests");
    }

    [Fact]
    public void GetCurrentSnapshot_returns_empty_when_no_measurements()
    {
        var pack = new TestPack([
            new MeterCollectionGroup("GatewayMeterAccumulatorTests.B", [
                new InstrumentCollectionSpec("test.idle", CollectedValueType.Counter)
            ])
        ]);

        using var sut = new GatewayMeterAccumulator(
            [pack], NullLogger<GatewayMeterAccumulator>.Instance);

        sut.GetCurrentSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void Dispose_is_safe_to_call_twice()
    {
        var pack = new TestPack([
            new MeterCollectionGroup("GatewayMeterAccumulatorTests.C", [])
        ]);

        var sut = new GatewayMeterAccumulator(
            [pack], NullLogger<GatewayMeterAccumulator>.Instance);

        sut.Dispose();
        sut.Dispose(); // Double-dispose must be safe.
    }
}