using System.Text.Json.Serialization;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Llm.Tunnel;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

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

// Site-health history surface (U3): chartlet on the Traffic page reads
// DegradationHistoryAtom via /api/v1/site-health/history.
[JsonSerializable(typeof(Mostlylucid.BotDetection.RateLimit.DegradationSnapshot))]
[JsonSerializable(typeof(PaginatedResponse<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>))]

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
// DashboardDetectorContribution + Dictionary<string,object> + JsonElement are the nested
// shapes inside DashboardDetectionEvent that broke /api/v1/detections responses on the
// AOT-published gateway: source-gen could not synthesise a JsonTypeInfo for them at
// publish time (warning SYSLIB1030) and STJ threw NotSupportedException at runtime for
// every detection that carried non-string signal values, which the website's REST-mode
// dashboard then rendered as empty "No detection signals recorded" / "No analysis
// available yet" panels.
[JsonSerializable(typeof(DashboardSignatureEvent))]
[JsonSerializable(typeof(DashboardDetectionEvent))]
[JsonSerializable(typeof(DashboardDetectorContribution))]
[JsonSerializable(typeof(Dictionary<string, DashboardDetectorContribution>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(List<string>))]
// ImportantSignals values arrive at the response path as JsonElement boxes
// when the gateway reads them out of Postgres, and as raw primitives when
// the gateway synthesises a detection from SignatureAggregate in the cache
// fast-path. Registering both the JsonElement and the raw primitive shapes
// keeps the polymorphic Dictionary<string,object> serialization AOT-safe
// regardless of which side of the cache produced the row.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
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

// Cluster surface
[JsonSerializable(typeof(BotCluster))]
[JsonSerializable(typeof(PaginatedResponse<BotCluster>))]
[JsonSerializable(typeof(BotClusterService.ClusterDiagnosticsSnapshot))]
[JsonSerializable(typeof(SingleResponse<BotClusterService.ClusterDiagnosticsSnapshot>))]

// Label surface
[JsonSerializable(typeof(SignatureLabel))]
[JsonSerializable(typeof(PaginatedResponse<SignatureLabel>))]
[JsonSerializable(typeof(SingleResponse<SignatureLabel>))]
[JsonSerializable(typeof(IReadOnlyDictionary<SignatureLabelKind, int>))]
[JsonSerializable(typeof(SingleResponse<IReadOnlyDictionary<SignatureLabelKind, int>>))]

// Approval surface
[JsonSerializable(typeof(ApprovalRecord))]
[JsonSerializable(typeof(PaginatedResponse<ApprovalRecord>))]
[JsonSerializable(typeof(SingleResponse<ApprovalRecord>))]

// Endpoint-pin surface
[JsonSerializable(typeof(PinnedEndpoint))]
[JsonSerializable(typeof(PaginatedResponse<PinnedEndpoint>))]

// Session surface
[JsonSerializable(typeof(PersistedSession))]
[JsonSerializable(typeof(PaginatedResponse<PersistedSession>))]

// User-agent search surface
[JsonSerializable(typeof(UserAgentSearchResult))]
[JsonSerializable(typeof(PaginatedResponse<UserAgentSearchResult>))]

// Investigation surface
[JsonSerializable(typeof(InvestigationFilter))]
[JsonSerializable(typeof(InvestigationResult))]
[JsonSerializable(typeof(SingleResponse<InvestigationResult>))]
[JsonSerializable(typeof(ShapeSearchFilter))]
[JsonSerializable(typeof(InvestigationPreset))]
[JsonSerializable(typeof(PaginatedResponse<InvestigationPreset>))]

// BDF export surface
[JsonSerializable(typeof(BdfExportDocument))]
[JsonSerializable(typeof(SingleResponse<BdfExportDocument>))]

// Configuration surface
[JsonSerializable(typeof(DetectorManifestSummary))]
[JsonSerializable(typeof(PaginatedResponse<DetectorManifestSummary>))]
[JsonSerializable(typeof(DetectorManifestDocument))]
[JsonSerializable(typeof(SingleResponse<DetectorManifestDocument>))]

// Identity surface
[JsonSerializable(typeof(Fingerprint))]
[JsonSerializable(typeof(PaginatedResponse<Fingerprint>))]
[JsonSerializable(typeof(SingleResponse<Fingerprint>))]
[JsonSerializable(typeof(NearestFingerprint))]
[JsonSerializable(typeof(PaginatedResponse<NearestFingerprint>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, int>))]
[JsonSerializable(typeof(SingleResponse<IReadOnlyDictionary<string, int>>))]
[JsonSerializable(typeof(SingleResponse<int>))]
[JsonSerializable(typeof(SingleResponse<string>))]

// Entity surface (durable visitor handle; /dashboard/entity/{id} URL key)
[JsonSerializable(typeof(ResolvedEntity))]
[JsonSerializable(typeof(SingleResponse<ResolvedEntity>))]
[JsonSerializable(typeof(EntityEdge))]
[JsonSerializable(typeof(PaginatedResponse<EntityEdge>))]
public partial class StyloBotJsonContext : JsonSerializerContext
{
}
