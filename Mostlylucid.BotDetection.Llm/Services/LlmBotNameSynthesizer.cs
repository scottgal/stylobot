using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Llm.Parsing;
using Mostlylucid.BotDetection.Llm.Prompts;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Llm.Services;

/// <summary>
///     Implements IBotNameSynthesizer using ILlmProvider.
///     Includes a used-names ring buffer to prevent duplicates.
/// </summary>
public class LlmBotNameSynthesizer : IBotNameSynthesizer
{
    private readonly ILlmProvider _provider;
    private readonly ILogger<LlmBotNameSynthesizer> _logger;
    private readonly ConcurrentQueue<string> _usedNames = new();
    private const int MaxUsedNamesTracked = 200;

    public LlmBotNameSynthesizer(
        ILlmProvider provider,
        ILogger<LlmBotNameSynthesizer> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public bool IsReady => _provider.IsReady;

    public async Task<string?> SynthesizeBotNameAsync(
        IReadOnlyDictionary<string, object?> signals,
        CancellationToken ct = default)
    {
        var (name, _) = await SynthesizeDetailedAsync(signals, ct: ct);
        return name;
    }

    public async Task<(string? Name, string? Description)> SynthesizeDetailedAsync(
        IReadOnlyDictionary<string, object?> signals,
        string? context = null,
        CancellationToken ct = default)
    {
        if (!_provider.IsReady)
            return (null, null);

        try
        {
            await _provider.InitializeAsync(ct);
            if (!_provider.IsReady) return (null, null);

            var recentNames = GetRecentlyUsedNames(20);
            var prompt = BotNamingPromptBuilder.Build(signals, context, recentNames);

            var response = await _provider.CompleteAsync(new LlmRequest
            {
                Prompt = prompt,
                Temperature = 0.1f,
                MaxTokens = 150,
                TimeoutMs = 10000
            }, ct);

            if (string.IsNullOrEmpty(response))
                return (null, null);

            var result = LlmResponseParser.ParseBotName(response);

            if (!string.IsNullOrEmpty(result.Name))
                TrackUsedName(result.Name);

            return (result.Name, result.Description);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bot name/description synthesis failed");
            return (null, null);
        }
    }

    private void TrackUsedName(string name)
    {
        _usedNames.Enqueue(name);
        while (_usedNames.Count > MaxUsedNamesTracked)
            _usedNames.TryDequeue(out _);
    }

    private List<string> GetRecentlyUsedNames(int maxNames = 20)
    {
        return _usedNames.ToArray().TakeLast(maxNames).ToList();
    }
}
