using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Service for detecting bot traffic
/// </summary>
public interface IBotDetectionService
{
    /// <summary>
    ///     Analyze an HTTP request to determine if it's from a bot
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection result with confidence score and reasons</returns>
    Task<BotDetectionResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Get statistics about bot detection
    /// </summary>
    /// <returns>Detection statistics</returns>
    BotDetectionStatistics GetStatistics();
}

/// <summary>
///     Statistics about bot detection
/// </summary>
public class BotDetectionStatistics
{
    public int TotalRequests { get; set; }
    public int BotsDetected { get; set; }
    public int VerifiedBots { get; set; }
    public int MaliciousBots { get; set; }
    public double AverageProcessingTimeMs { get; set; }
    public Dictionary<string, int> BotTypeBreakdown { get; set; } = new();
}