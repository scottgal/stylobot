using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Filters;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Extension methods for minimal API route builders
/// </summary>
public static class RouteBuilderExtensions
{
    /// <summary>
    ///     Adds bot blocking to this endpoint.
    /// </summary>
    /// <example>
    ///     app.MapGet("/api/data", () => "sensitive")
    ///     .BlockBots();
    /// </example>
    public static RouteHandlerBuilder BlockBots(this RouteHandlerBuilder builder,
        bool allowVerifiedBots = false,
        bool allowSearchEngines = false,
        double minConfidence = 0.0,
        int statusCode = 403)
    {
        return builder.AddEndpointFilter(new BlockBotsEndpointFilter(
            allowVerifiedBots, allowSearchEngines, minConfidence, statusCode));
    }

    /// <summary>
    ///     Requires human visitors for this endpoint (blocks all bots including verified).
    /// </summary>
    /// <example>
    ///     app.MapPost("/api/submit", () => "submitted")
    ///     .RequireHuman();
    /// </example>
    public static RouteHandlerBuilder RequireHuman(this RouteHandlerBuilder builder, int statusCode = 403)
    {
        return builder.AddEndpointFilter(new RequireHumanEndpointFilter(statusCode));
    }

    /// <summary>
    ///     Maps built-in bot detection diagnostic endpoints.
    /// </summary>
    /// <example>
    ///     app.MapBotDetectionEndpoints("/bot-detection");
    ///     // Creates:
    ///     //   GET /bot-detection/check   - Check current request
    ///     //   GET /bot-detection/stats   - Get statistics
    ///     //   GET /bot-detection/health  - Health check
    /// </example>
    public static IEndpointRouteBuilder MapBotDetectionEndpoints(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/bot-detection")
    {
        var group = endpoints.MapGroup(prefix)
            .WithTags("Bot Detection");

        // Check current request - returns full pipeline evidence when available
        group.MapGet("/check", (HttpContext context) =>
            {
                var result = context.GetBotDetectionResult();

                if (result == null)
                    return Results.Ok(new
                    {
                        status = "unknown",
                        message = "Bot detection middleware has not run for this request",
                        isBot = false,
                        isHuman = true,
                        isVerifiedBot = false,
                        isSearchEngineBot = false,
                        confidenceScore = 0.0,
                        botType = (string?)null,
                        botName = (string?)null
                    });

                // Get policy info
                var policyName = context.Items.TryGetValue(BotDetectionMiddleware.PolicyNameKey, out var pn)
                    ? pn?.ToString()
                    : "default";

                // Try to get full aggregated evidence for detailed results
                if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj)
                    && evidenceObj is AggregatedEvidence evidence)
                {
                    // Return full pipeline evidence
                    var isHuman = evidence.BotProbability < 0.5;
                    return Results.Ok(new
                    {
                        policy = policyName,
                        isBot = !isHuman,
                        isHuman,
                        isVerifiedBot = context.IsVerifiedBot(),
                        isSearchEngineBot = context.IsSearchEngineBot(),
                        // Primary score: how likely is this a human (0-1)?
                        humanProbability = Math.Round(1.0 - evidence.BotProbability, 4),
                        // Secondary: how likely is this a bot (0-1)?
                        botProbability = Math.Round(evidence.BotProbability, 4),
                        // Overall confidence in our classification (how certain are we?)
                        confidence = Math.Round(evidence.Confidence, 4),
                        botType = evidence.PrimaryBotType?.ToString(),
                        botName = evidence.PrimaryBotName,
                        riskBand = evidence.RiskBand.ToString(),
                        recommendedAction = GetRecommendedAction(evidence),
                        processingTimeMs = Math.Round(evidence.TotalProcessingTimeMs, 2),
                        // Pipeline execution info
                        aiRan = evidence.AiRan,
                        detectorsRan = evidence.ContributingDetectors.ToList(),
                        detectorCount = evidence.ContributingDetectors.Count,
                        failedDetectors = evidence.FailedDetectors.ToList(),
                        earlyExit = evidence.EarlyExit,
                        earlyExitVerdict = evidence.EarlyExitVerdict?.ToString(),
                        // All collected signals from the pipeline (PII signals filtered out)
                        signals = FilterPiiSignals(evidence.Signals),
                        categoryBreakdown = evidence.CategoryBreakdown.ToDictionary(
                            kv => kv.Key,
                            kv => new
                            {
                                score = Math.Round(kv.Value.Score, 4), weight = Math.Round(kv.Value.TotalWeight, 2)
                            }),
                        // Detailed detector contributions with signals, priority, and timing
                        // Sorted by timestamp for pipeline execution order
                        contributions = evidence.Contributions
                            .OrderBy(c => c.Timestamp)
                            .Select(c => new
                            {
                                detector = c.DetectorName,
                                category = c.Category,
                                priority = c.Priority,
                                timestamp = c.Timestamp,
                                processingMs = Math.Round(c.ProcessingTimeMs, 2),
                                impact = Math.Round(c.ConfidenceDelta, 4),
                                weight = Math.Round(c.Weight, 2),
                                weightedImpact = Math.Round(c.ConfidenceDelta * c.Weight, 4),
                                reason = c.Reason?.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim(),
                                botType = c.BotType?.ToString(),
                                botName = c.BotName,
                                // Include signals this detector emitted (PII signals filtered out)
                                signals = FilterPiiSignals(c.Signals),
                                earlyExit = c.TriggerEarlyExit ? c.EarlyExitVerdict?.ToString() : null
                            }),
                        // Legacy format for backward compatibility
                        reasons = evidence.Contributions.Select(c => new
                        {
                            category = c.Category,
                            detail = c.Reason?.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim(),
                            impact = Math.Round(c.ConfidenceDelta, 4),
                            weight = Math.Round(c.Weight, 2),
                            weightedImpact = Math.Round(c.ConfidenceDelta * c.Weight, 4),
                            detector = c.DetectorName
                        })
                    });
                }

                // Fall back to basic result
                return Results.Ok(new
                {
                    isBot = result.IsBot,
                    isHuman = !result.IsBot,
                    isVerifiedBot = context.IsVerifiedBot(),
                    isSearchEngineBot = context.IsSearchEngineBot(),
                    humanProbability = result.IsBot ? 1.0 - result.ConfidenceScore : result.ConfidenceScore,
                    botProbability = result.IsBot ? result.ConfidenceScore : 1.0 - result.ConfidenceScore,
                    botType = result.BotType?.ToString(),
                    botName = result.BotName,
                    processingTimeMs = result.ProcessingTimeMs,
                    reasons = result.Reasons.Select(r => new
                    {
                        category = r.Category,
                        detail = r.Detail?.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim(),
                        impact = r.ConfidenceImpact
                    })
                });
            })
            .WithName("BotDetection_Check")
            .WithSummary("Check if the current request is from a bot - returns full pipeline evidence when available");

        // Get statistics
        group.MapGet("/stats", (IBotDetectionService botService) =>
            {
                var stats = botService.GetStatistics();
                return Results.Ok(new
                {
                    totalRequests = stats.TotalRequests,
                    botsDetected = stats.BotsDetected,
                    botPercentage = stats.TotalRequests > 0
                        ? Math.Round((double)stats.BotsDetected / stats.TotalRequests * 100, 2)
                        : 0,
                    verifiedBots = stats.VerifiedBots,
                    maliciousBots = stats.MaliciousBots,
                    averageProcessingTimeMs = Math.Round(stats.AverageProcessingTimeMs, 2),
                    botTypeBreakdown = stats.BotTypeBreakdown
                });
            })
            .WithName("BotDetection_Stats")
            .WithSummary("Get bot detection statistics");

        // Health check
        group.MapGet("/health", (IBotDetectionService botService) =>
            {
                var stats = botService.GetStatistics();
                return Results.Ok(new
                {
                    status = "healthy",
                    service = "BotDetection",
                    totalRequests = stats.TotalRequests,
                    averageResponseMs = Math.Round(stats.AverageProcessingTimeMs, 2)
                });
            })
            .WithName("BotDetection_Health")
            .WithSummary("Bot detection service health check");

        return endpoints;
    }

    /// <summary>
    ///     Filter out PII signals (raw user agent and IP address) from signals dictionary.
    ///     These signals are kept internally for cross-detector communication but excluded from API responses.
    /// </summary>
    private static Dictionary<string, object>? FilterPiiSignals(IReadOnlyDictionary<string, object>? signals)
    {
        if (signals == null || signals.Count == 0)
            return null;

        // PII signal keys to exclude from API responses
        var piiKeys = new HashSet<string>
        {
            SignalKeys.UserAgent, // "ua.raw"
            SignalKeys.ClientIp // "ip.address"
        };

        var filtered = signals
            .Where(kv => !piiKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return filtered.Count > 0 ? filtered : null;
    }

    /// <summary>
    ///     Format probability as percentage, showing "<1%" for very low values.
    /// </summary>
    private static string FormatProbability(double probability)
    {
        if (probability < 0.01 && probability > 0)
            return "<1%";
        return probability.ToString("P0");
    }

    /// <summary>
    ///     Derive recommended action from evidence.
    ///     Uses explicit PolicyAction if set, otherwise derives from RiskBand.
    /// </summary>
    private static object GetRecommendedAction(AggregatedEvidence evidence)
    {
        // If there's an explicit policy action, use it with reason
        if (evidence.PolicyAction.HasValue)
            return new
            {
                action = evidence.PolicyAction.Value.ToString(),
                reason = GetPolicyActionReason(evidence.PolicyAction.Value, evidence)
            };

        // Derive from RiskBand (use fully qualified to avoid namespace conflicts)
        var probStr = FormatProbability(evidence.BotProbability);
        var (action, reason) = evidence.RiskBand switch
        {
            RiskBand.VeryHigh => ("Block", $"Very high risk (probability: {probStr})"),
            RiskBand.High => ("Block", $"High risk (probability: {probStr})"),
            RiskBand.Medium => ("Challenge", $"Medium risk (probability: {probStr})"),
            RiskBand.Elevated => ("Throttle", $"Elevated risk (probability: {probStr})"),
            RiskBand.Low => ("Allow", $"Low risk (probability: {probStr})"),
            RiskBand.VeryLow => ("Allow", $"Very low risk (probability: {probStr})"),
            _ => ("Allow", "Default action")
        };

        return new { action, reason };
    }

    private static string GetPolicyActionReason(PolicyAction action, AggregatedEvidence evidence)
    {
        var probStr = FormatProbability(evidence.BotProbability);
        return action switch
        {
            PolicyAction.Block => $"Policy triggered block at probability {probStr}",
            PolicyAction.Allow => "Policy allowed request",
            PolicyAction.Challenge => $"Policy triggered challenge at probability {probStr}",
            PolicyAction.Throttle => $"Policy triggered throttle at probability {probStr}",
            PolicyAction.EscalateToAi => $"Policy recommends AI escalation at probability {probStr}",
            PolicyAction.LogOnly => "Policy set to log only",
            _ => $"Policy action: {action}"
        };
    }
}