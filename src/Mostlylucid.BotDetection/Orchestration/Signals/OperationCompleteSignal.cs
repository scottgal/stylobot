namespace Mostlylucid.BotDetection.Orchestration.Signals;

/// <summary>
///     Operation complete escalation signal sent when both request and response analysis finish.
///     Contains aggregated data from the entire operation for signature-level learning.
/// </summary>
public sealed record OperationCompleteSignal
{
    public required string Signature { get; init; }
    public required string RequestId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required int Priority { get; init; }

    // Request side
    public required double RequestRisk { get; init; }
    public string? Path { get; init; }
    public string? Method { get; init; }

    // Response side
    public required double ResponseScore { get; init; }
    public required int StatusCode { get; init; }
    public required long ResponseBytes { get; init; }

    // Combined
    public required double CombinedScore { get; init; }
    public required bool Honeypot { get; init; }
    public string? Datacenter { get; init; }

    // All trigger signals from operation
    public required IReadOnlyDictionary<string, object> TriggerSignals { get; init; }
}