using System.Text.Json;
using System.Text.Json.Serialization;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Which aggregate slice a caller wants from a batched dashboard read.
///     String-serialized via <see cref="DatasetKindJsonConverter"/> (not the numeric default,
///     and not the built-in <c>JsonStringEnumConverter</c>) so remote/thin clients that send
///     the enum NAME in the compose-batch JSON bind correctly — the number-only binding 400'd
///     every string-kind request, which silently killed the site's prewarm composes (every
///     envelope stayed cold, the first-paint gate never met). deploy-/dash- 2026-08-14.
///     <para>
///     <see cref="Unrecognized"/> exists so an unknown/future/rolled-back kind string never
///     throws and fails the WHOLE compose-batch request — see <see cref="DatasetKindJsonConverter"/>.
///     </para>
/// </summary>
[JsonConverter(typeof(DatasetKindJsonConverter))]
public enum DatasetKind
{
    /// <summary>A kind string the reader didn't recognize (version skew). Inert by
    /// construction: no ComposeBatchAsync dispatch branch matches it.</summary>
    Unrecognized = -1,
    SummaryStats, TimeBuckets, BotAggregate, GeoBreakdown, EndpointStats, DegradationHistory,
    LiveTimeBuckets, LiveEndpointStats
}

/// <summary>
///     Tolerant string converter for <see cref="DatasetKind"/>: an unrecognized enum-name
///     string deserializes to <see cref="DatasetKind.Unrecognized"/> instead of throwing
///     <see cref="JsonException"/>. The built-in <c>JsonStringEnumConverter&lt;T&gt;</c> throws
///     on an unmatched string, which would fail the entire <c>/compose-batch</c> deserialization
///     over a single unknown dataset kind (e.g. a newer client asking an older gateway for a
///     kind it doesn't have yet, or vice versa on a rollback) — a permanent public contract must
///     degrade one requested kind, not the whole call. Serialization (write direction) always
///     emits the enum member name, matching the prior <c>JsonStringEnumConverter</c> behavior.
/// </summary>
public sealed class DatasetKindJsonConverter : JsonConverter<DatasetKind>
{
    public override DatasetKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return text is not null && Enum.TryParse<DatasetKind>(text, out var kind)
            ? kind
            : DatasetKind.Unrecognized;
    }

    public override void Write(Utf8JsonWriter writer, DatasetKind value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

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
    // Optional trailing slots (default null) so existing 5/6-arg constructions keep compiling.
    IReadOnlyList<DegradationSnapshot>? Degradations = null,
    IReadOnlyList<DashboardTimeSeriesPoint>? LiveTimeBuckets = null,
    IReadOnlyList<DashboardEndpointStats>? LiveEndpointStats = null);