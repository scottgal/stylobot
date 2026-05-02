namespace Mostlylucid.BotDetection.Data;

public record PersistedRequest
{
    public long Id { get; init; }
    public required string Signature { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Path { get; init; }
    public required string MarkovState { get; init; }
    public required int StatusCode { get; init; }
    public required double BotProbability { get; init; }
    public required double Confidence { get; init; }
    public required string RiskBand { get; init; }
    public required double ProcessingMs { get; init; }
    public long? SessionId { get; init; }
    public bool IsBot => BotProbability > 0.5;
}
