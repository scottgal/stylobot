namespace Mostlylucid.BotDetection.Llm.Services;

/// <summary>
///     Generates plain English explanations when a signature's score changes significantly.
/// </summary>
public interface IScoreNarrativeService
{
    Task<string?> GenerateNarrativeAsync(
        string signature,
        double previousScore,
        double newScore,
        IReadOnlyDictionary<string, object?>? signals = null,
        CancellationToken ct = default);
}
