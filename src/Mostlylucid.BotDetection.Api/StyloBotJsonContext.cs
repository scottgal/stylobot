using System.Text.Json.Serialization;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Llm.Tunnel;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Api;

/// <summary>
///     Source-generated <see cref="JsonSerializerContext"/> covering every concrete type the
///     API endpoints serialize. Required for Native AOT: System.Text.Json's reflection-based
///     <c>JsonSerializerOptions.DefaultTypeInfoResolver</c> is incompatible with AOT (no
///     dynamic code), so consumers wire this context via
///     <c>services.ConfigureHttpJsonOptions(opts =&gt; opts.SerializerOptions.TypeInfoResolverChain.Insert(0, StyloBotJsonContext.Default))</c>.
///
///     Adding an endpoint that returns a new concrete type: add the type here as a
///     <c>[JsonSerializable(typeof(NewType))]</c> attribute. The source generator emits a
///     statically-resolvable <c>JsonTypeInfo</c> at compile time.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]

// Detect surface
[JsonSerializable(typeof(DetectRequest))]
[JsonSerializable(typeof(DetectRequest[]))]
[JsonSerializable(typeof(DetectResponse))]
[JsonSerializable(typeof(DetectResponse[]))]
[JsonSerializable(typeof(TlsInfo))]
[JsonSerializable(typeof(VerdictDto))]
[JsonSerializable(typeof(ReasonDto))]
[JsonSerializable(typeof(MetaDto))]

// Response envelopes
[JsonSerializable(typeof(PaginationInfo))]
[JsonSerializable(typeof(ResponseMeta))]
[JsonSerializable(typeof(ApiError))]

// Routes surface
[JsonSerializable(typeof(RouteEntryDto))]
[JsonSerializable(typeof(SetRouteNameRequest))]
[JsonSerializable(typeof(PaginatedResponse<RouteEntryDto>))]

// Metrics surface
[JsonSerializable(typeof(MetricSnapshot))]
[JsonSerializable(typeof(PaginatedResponse<MetricSnapshot>))]
[JsonSerializable(typeof(SingleResponse<IReadOnlyList<MetricSnapshot>>))]
[JsonSerializable(typeof(MetricSnapshotDto))]
[JsonSerializable(typeof(MetricSnapshotDto[]))]

// Account surface
[JsonSerializable(typeof(ApiKeyContextDto))]
[JsonSerializable(typeof(SingleResponse<ApiKeyContextDto>))]

// LLM node controller surface
[JsonSerializable(typeof(LlmNodeImportRequest))]
[JsonSerializable(typeof(LlmNodeImportResponse))]
[JsonSerializable(typeof(LlmNodeSummary))]
[JsonSerializable(typeof(IReadOnlyList<LlmNodeSummary>))]
[JsonSerializable(typeof(LlmNodeTestResult))]

// Dashboard read surface (Api references UI today for these models; persistence extraction
// would let these move to Dashboard.Persistence — see plan doc 2026-05-17-extract-dashboard-
// persistence.md).
[JsonSerializable(typeof(DashboardSignatureEvent))]
[JsonSerializable(typeof(DashboardDetectionEvent))]
[JsonSerializable(typeof(DashboardSummary))]
[JsonSerializable(typeof(DashboardTimeSeriesPoint))]
[JsonSerializable(typeof(DashboardCountryStats))]
[JsonSerializable(typeof(DashboardCountryDetail))]
[JsonSerializable(typeof(DashboardEndpointStats))]
[JsonSerializable(typeof(DashboardEndpointDetail))]
[JsonSerializable(typeof(DashboardTopBotEntry))]
[JsonSerializable(typeof(ThreatEntry))]
[JsonSerializable(typeof(PaginatedResponse<DashboardSignatureEvent>))]
[JsonSerializable(typeof(PaginatedResponse<DashboardDetectionEvent>))]
[JsonSerializable(typeof(PaginatedResponse<DashboardCountryStats>))]
[JsonSerializable(typeof(PaginatedResponse<DashboardEndpointStats>))]
[JsonSerializable(typeof(PaginatedResponse<DashboardTopBotEntry>))]
[JsonSerializable(typeof(PaginatedResponse<DashboardTimeSeriesPoint>))]
[JsonSerializable(typeof(PaginatedResponse<ThreatEntry>))]
[JsonSerializable(typeof(SingleResponse<DashboardSummary>))]
[JsonSerializable(typeof(SingleResponse<DashboardCountryDetail>))]
[JsonSerializable(typeof(SingleResponse<DashboardEndpointDetail>))]
public partial class StyloBotJsonContext : JsonSerializerContext
{
}
