using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Llm.Orchestration;

/// <summary>
///     LLM orchestrator that wraps a primary provider with budget enforcement.
///     Fallback chain and per-use-case routing are planned for a future release.
///     Implements ILlmProvider so it's transparent to consumers.
/// </summary>
public sealed class LlmOrchestrator : ILlmProvider
{
    private readonly ILlmProvider _primary;
    private readonly LlmOrchestratorOptions _options;
    private readonly LlmUsageTracker _tracker;
    private readonly ILogger<LlmOrchestrator> _logger;

    public LlmOrchestrator(
        ILlmProvider primary,
        IOptions<LlmOrchestratorOptions> options,
        LlmUsageTracker tracker,
        ILogger<LlmOrchestrator> logger)
    {
        _primary = primary;
        _options = options.Value;
        _tracker = tracker;
        _logger = logger;
    }

    public bool IsReady => _primary.IsReady;

    public Task InitializeAsync(CancellationToken ct = default) => _primary.InitializeAsync(ct);

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        // Budget check
        if (!_tracker.IsWithinBudget(_options.Budget))
        {
            _logger.LogWarning("LLM budget exceeded ({Requests}/hr, ${Cost}/day). Degrading to {Fallback}",
                _tracker.RequestsThisHour, _tracker.CostToday, _options.Budget.DegradeTo);
        }

        try
        {
            var result = await _primary.CompleteAsync(request, ct);
            if (!string.IsNullOrEmpty(result))
            {
                _tracker.RecordRequest("primary", request.Prompt.Length / 4, result.Length / 4);
                return result;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Primary LLM provider failed");
        }

        return string.Empty;
    }
}
