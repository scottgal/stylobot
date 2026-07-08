namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>Which aggregate slice a caller wants from a batched dashboard read.</summary>
public enum DatasetKind { SummaryStats, TimeBuckets, BotAggregate, GeoBreakdown, EndpointStats }

/// <summary>Specifies one slice of a batched dashboard read.</summary>
public sealed record DatasetRequest(DatasetKind Kind, int TopN = 50, int BucketMinutes = 60);

/// <summary>
///     Encapsulates the time window, audience filter, domain filter, and the set of
///     <see cref="DatasetKind"/>s to fetch in a single call to
///     <c>IDashboardEventStore.ComposeBatchAsync</c>.
/// </summary>
public sealed record DashboardBatchRequest(
    DateTime? StartTime,
    DateTime? EndTime,
    IReadOnlyList<DatasetRequest> Datasets,
    string? AudienceFilter = null,
    double? ProbMin = null,
    IReadOnlyList<string>? Domains = null);

/// <summary>
///     The composed result of a batched dashboard read. Each property is null when the
///     corresponding <see cref="DatasetKind"/> was not requested.
/// </summary>
public sealed record DashboardDatasetBundle(
    DashboardSummary? Summary,
    IReadOnlyList<DashboardTimeSeriesPoint>? TimeBuckets,
    IReadOnlyList<DashboardTopBotEntry>? BotAggregate,
    IReadOnlyList<DashboardCountryStats>? Geo,
    IReadOnlyList<DashboardEndpointStats>? Endpoints);