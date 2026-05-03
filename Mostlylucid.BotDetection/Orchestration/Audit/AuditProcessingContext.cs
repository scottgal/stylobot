using Microsoft.AspNetCore.Http;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Audit;

/// <summary>
///     Completed detection context passed to audit processors.
///     HttpContext is nullable: it is null when the context is built before response
///     completion (fire-and-forget path). Processors must not access HttpContext.
/// </summary>
public sealed record AuditProcessingContext
{
    public HttpContext? HttpContext { get; init; }
    public required AggregatedEvidence Evidence { get; init; }
    public required IReadOnlyDictionary<string, object> Signals { get; init; }
    public required IReadOnlyList<DetectionContribution> Contributions { get; init; }
    public required AuditTraceMetadata Metadata { get; init; }
}

/// <summary>
///     Request and decision metadata copied from the completed detection.
/// </summary>
public sealed record AuditTraceMetadata
{
    public required DateTime Timestamp { get; init; }
    public required string RequestId { get; init; }
    public string? PrimarySignature { get; init; }
    public string? Path { get; init; }
    public string? Method { get; init; }
    public int? StatusCode { get; init; }
    public string? PolicyName { get; init; }
    public string? Action { get; init; }
    public string? RiskBand { get; init; }
    public double BotProbability { get; init; }
    public double Confidence { get; init; }
    public double ProcessingTimeMs { get; init; }
}
