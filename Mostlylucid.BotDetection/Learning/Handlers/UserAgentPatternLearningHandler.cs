using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Learning.Handlers;

/// <summary>
///     Learning handler for User-Agent pattern extraction and updates.
///     Keyed by: "ua.pattern"
/// </summary>
public class UserAgentPatternLearningHandler : IKeyedLearningHandler
{
    private readonly ILogger<UserAgentPatternLearningHandler> _logger;

    public UserAgentPatternLearningHandler(ILogger<UserAgentPatternLearningHandler> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(string signalKey, LearningOperationType operationType)
    {
        return signalKey.StartsWith("ua.") &&
               operationType is LearningOperationType.PatternExtraction or
                   LearningOperationType.PatternUpdate;
    }

    public async Task HandleAsync(string signalKey, LearningTask task, CancellationToken cancellationToken = default)
    {
        if (task.OperationType == LearningOperationType.PatternExtraction)
            await ExtractPatternAsync(task, cancellationToken);
        else if (task.OperationType == LearningOperationType.PatternUpdate)
            await UpdatePatternAsync(task, cancellationToken);
    }

    private async Task ExtractPatternAsync(LearningTask task, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(task.Pattern))
        {
            _logger.LogWarning("Pattern extraction requested but no pattern provided");
            return;
        }

        _logger.LogInformation(
            "Extracting UA pattern: {Pattern} (confidence: {Confidence:F2})",
            task.Pattern, task.Confidence ?? 0);

        // Extract regex or substring patterns from high-confidence UA strings
        // Example: "HeadlessChrome/120.0.0.0" -> extract "HeadlessChrome"
        // Example: "curl/8.4.0" -> extract "curl/"

        // Store pattern in fast-path pattern database
        await Task.CompletedTask; // Placeholder for actual storage
    }

    private async Task UpdatePatternAsync(LearningTask task, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(task.Pattern))
        {
            _logger.LogWarning("Pattern update requested but no pattern provided");
            return;
        }

        _logger.LogInformation(
            "Updating UA pattern confidence: {Pattern} -> {Confidence:F2}",
            task.Pattern, task.Confidence ?? 0);

        // Update confidence score for existing pattern
        // Or mark pattern as "false positive" if Label=false
        await Task.CompletedTask; // Placeholder for actual update
    }
}