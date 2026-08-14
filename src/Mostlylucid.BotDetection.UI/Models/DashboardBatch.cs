using System.Text.Json.Serialization;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Which aggregate slice a caller wants from a batched dashboard read.
///     String-serialized (not the numeric default) so remote/thin clients that
///     send the enum NAME in the compose-batch JSON bind correctly — the
///     number-only binding 400'd every string-kind request, which silently
///     killed the site's prewarm composes (every envelope stayed cold, the
///     first-paint gate never met). deploy-/dash- 2026-08-14.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatasetKind>))]
public enum DatasetKind { SummaryStats, TimeBuckets, BotAggregate, GeoBreakdown, EndpointStats, DegradationHistory }

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
    IReadOnlyList<DashboardEndpointStats>? Endpoints,
    // Optional trailing slot (default null) so existing 5-arg constructions keep compiling.
    IReadOnlyList<DegradationSnapshot>? Degradations = null);