using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Service for synthesizing human-readable bot names and descriptions from detection signals.
///     Implementations use LLMs to generate meaningful names like "GoogleBot", "SuspiciousScraperV2", etc.
/// </summary>
public interface IBotNameSynthesizer
{
    /// <summary>
    ///     Synthesize a bot name from detection signals.
    ///     Called asynchronously after detection, never blocks the request path.
    /// </summary>
    /// <param name="signals">Detection signals from all detectors</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Bot name (2-5 words) or null if synthesis fails/times out</returns>
    Task<string?> SynthesizeBotNameAsync(IReadOnlyDictionary<string, object?> signals, CancellationToken ct = default);

    /// <summary>
    ///     Synthesize both name and description for detailed analysis.
    ///     Used for cluster descriptions and detailed forensics.
    /// </summary>
    /// <param name="signals">Detection signals from all detectors</param>
    /// <param name="context">Optional additional context (behavior patterns, timeline, etc.)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>(Name, Description) or (null, null) if synthesis fails</returns>
    Task<(string? Name, string? Description)> SynthesizeDetailedAsync(
        IReadOnlyDictionary<string, object?> signals,
        string? context = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Check if the synthesizer is ready (model loaded, initialized).
    /// </summary>
    bool IsReady { get; }
}
