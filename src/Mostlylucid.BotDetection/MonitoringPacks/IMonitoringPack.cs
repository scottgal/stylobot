namespace Mostlylucid.BotDetection.MonitoringPacks;

public interface IMonitoringPack
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string TabName { get; }
    TimeSpan CollectionInterval { get; }
    IReadOnlyList<MeterCollectionGroup> MeterGroups { get; }
}

public sealed record MeterCollectionGroup(
    string MeterName,
    IReadOnlyList<InstrumentCollectionSpec> Instruments);

public sealed record InstrumentCollectionSpec(
    string InstrumentName,
    CollectedValueType ValueType,
    IReadOnlyList<KeyValuePair<string, string>>? TagFilter = null);

public enum CollectedValueType
{
    Counter,
    Gauge,
    Histogram_P50,
    Histogram_P95,
    Histogram_P99
}
