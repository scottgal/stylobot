namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Generates dynamic fake responses for holodeck honeypots.
///     Registered optionally by LLM holodeck plugin. SimulationPackResponder
///     resolves via optional constructor injection -- null when plugin not registered.
/// </summary>
public interface IHolodeckResponder
{
    Task<HolodeckResponse> GenerateAsync(
        PackResponseTemplate template,
        HolodeckRequestContext requestContext,
        string? canary,
        CancellationToken ct = default);

    bool IsAvailable { get; }
}

public sealed record HolodeckResponse
{
    public required string Content { get; init; }
    public required string ContentType { get; init; }
    public int StatusCode { get; init; } = 200;
    public Dictionary<string, string>? Headers { get; init; }
    public bool WasGenerated { get; init; }
}

public sealed record HolodeckRequestContext
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? QueryString { get; init; }
    public string? ContentType { get; init; }
    public string? Fingerprint { get; init; }
    public string? PackId { get; init; }
    public string? PackFramework { get; init; }
    public string? PackVersion { get; init; }
    public string? PackPersonality { get; init; }
}
