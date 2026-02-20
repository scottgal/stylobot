namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     No-op implementation of IBotNameSynthesizer.
///     Used when no LLM provider package is installed.
///     Replaced by LlmBotNameSynthesizer when an LLM plugin is registered.
/// </summary>
internal sealed class NoOpBotNameSynthesizer : IBotNameSynthesizer
{
    public bool IsReady => false;

    public Task<string?> SynthesizeBotNameAsync(
        IReadOnlyDictionary<string, object?> signals,
        CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<(string? Name, string? Description)> SynthesizeDetailedAsync(
        IReadOnlyDictionary<string, object?> signals,
        string? context = null,
        CancellationToken ct = default)
        => Task.FromResult<(string?, string?)>((null, null));
}
