namespace Mostlylucid.BotDetection.Orchestration.Signals;

/// <summary>
///     Early request escalation signal sent when request analysis completes.
///     Used for immediate escalation (e.g., honeypot hits) before response is generated.
/// </summary>
public sealed record RequestCompleteSignal
{
    public required string Signature { get; init; }
    public required string RequestId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required double Risk { get; init; }
    public required bool Honeypot { get; init; }
    public string? Datacenter { get; init; }
    public string? Path { get; init; }
    public string? Method { get; init; }
    public required IReadOnlyDictionary<string, object> TriggerSignals { get; init; }
}