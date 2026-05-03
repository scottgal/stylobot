namespace Mostlylucid.BotDetection.Orchestration.Audit;

/// <summary>
///     Structured audit output produced from a completed detection trace.
///     Records are intentionally shaped; they are not automatic full-trace exports.
/// </summary>
public sealed record AuditRecord
{
    public required string Type { get; init; }
    public required string SourceProcessor { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string RequestId { get; init; }
    public string? PrimarySignature { get; init; }
    public string? Path { get; init; }
    public string? Method { get; init; }
    public string? Action { get; init; }
    public string? RiskBand { get; init; }
    public double? BotProbability { get; init; }
    public double? Confidence { get; init; }
    public string Severity { get; init; } = "Information";
    public IReadOnlyDictionary<string, object>? Signals { get; init; }
    public IReadOnlyDictionary<string, double>? DetectorDeltas { get; init; }
    public IReadOnlyList<string>? Reasons { get; init; }
    public IReadOnlyDictionary<string, object>? Properties { get; init; }
}
