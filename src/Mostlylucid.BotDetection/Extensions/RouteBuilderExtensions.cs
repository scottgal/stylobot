using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Endpoints;
using Mostlylucid.BotDetection.Filters;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Request model for bot detection feedback.
/// </summary>
public record BotFeedbackRequest(string Outcome, string? RequestId = null, string? Notes = null);

/// <summary>
///     Extension methods for minimal API route builders
/// </summary>
public static class RouteBuilderExtensions
{
    /// <summary>
    ///     Adds bot blocking to this endpoint.
    ///     By default blocks ALL bots. Use allow* parameters to whitelist specific types.
    ///     Use block* parameters for geographic/network restrictions.
    /// </summary>
    /// <example>
    ///     app.MapGet("/api/data", () => "sensitive")
    ///     .BlockBots();
    ///
    ///     app.MapGet("/api/products", () => "data")
    ///     .BlockBots(allowSearchEngines: true, allowSocialMediaBots: true);
    ///
    ///     app.MapGet("/health", () => "ok")
    ///     .BlockBots(allowMonitoringBots: true);
    ///
    ///     app.MapGet("/honeypot", () => "welcome")
    ///     .BlockBots(allowScrapers: true, allowMaliciousBots: true);
    ///
    ///     app.MapGet("/api/restricted", () => "data")
    ///     .BlockBots(blockCountries: "CN,RU", blockVpn: true);
    /// </example>
    public static RouteHandlerBuilder BlockBots(this RouteHandlerBuilder builder,
        bool allowVerifiedBots = false,
        bool allowSearchEngines = false,
        bool allowSocialMediaBots = false,
        bool allowMonitoringBots = false,
        bool allowAiBots = false,
        bool allowGoodBots = false,
        bool allowScrapers = false,
        bool allowMaliciousBots = false,
        bool allowTools = false,
        double minConfidence = 0.0,
        int statusCode = 403,
        string? blockCountries = null,
        string? allowCountries = null,
        bool blockVpn = false,
        bool blockProxy = false,
        bool blockDatacenter = false,
        bool blockTor = false)
    {
        return builder.AddEndpointFilter(new BlockBotsEndpointFilter(
            allowVerifiedBots, allowSearchEngines, allowSocialMediaBots,
            allowMonitoringBots, allowAiBots, allowGoodBots,
            allowScrapers, allowMaliciousBots,
            minConfidence, statusCode,
            blockCountries, allowCountries,
            blockVpn, blockProxy, blockDatacenter, blockTor,
            allowTools));
    }

    /// <summary>
    ///     Blocks requests where a specific signal condition is met.
    ///     Use <see cref="SignalKeys" /> for available signal key constants.
    /// </summary>
    /// <example>
    ///     // Block VPN connections
    ///     app.MapGet("/api/payment", () => "ok")
    ///        .BlockIfSignal(SignalKeys.GeoIsVpn, SignalOperator.Equals, "True");
    ///
    ///     // Block datacenter IPs
    ///     app.MapGet("/api/submit", () => "ok")
    ///        .BlockIfSignal(SignalKeys.IpIsDatacenter, SignalOperator.Equals, "True");
    /// </example>
    public static RouteHandlerBuilder BlockIfSignal(this RouteHandlerBuilder builder,
        string signalKey, SignalOperator op, string? value = null, int statusCode = 403)
    {
        return builder.AddEndpointFilter(new BlockIfSignalEndpointFilter(signalKey, op, value, statusCode));
    }

    /// <summary>
    ///     Requires a specific signal condition to be met. Blocks when the condition is NOT met.
    /// </summary>
    /// <example>
    ///     // Only allow requests from the US
    ///     app.MapGet("/api/domestic", () => "ok")
    ///        .RequireSignal(SignalKeys.GeoCountryCode, SignalOperator.Equals, "US");
    /// </example>
    public static RouteHandlerBuilder RequireSignal(this RouteHandlerBuilder builder,
        string signalKey, SignalOperator op, string? value = null, int statusCode = 403)
    {
        return builder.AddEndpointFilter(new RequireSignalEndpointFilter(signalKey, op, value, statusCode));
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
    ///     Requires a detection signal to have one of the specified values.
    ///     Blocks requests where the signal exists but doesn't match.
    ///     Allows through if the signal is missing (detector may not have run).
    /// </summary>
    /// <example>
    ///     // Only allow US and Canadian traffic
    ///     app.MapGet("/api/domestic", () => "data")
    ///        .RequireSignal("geo.country_code", "US", "CA", "MX");
    ///
    ///     // Only allow search engine bot type
    ///     app.MapGet("/seo-only", () => "sitemap")
    ///        .RequireSignal("ua.bot_type", "SearchEngine", "VerifiedBot");
    /// </example>
    public static RouteHandlerBuilder RequireSignal(this RouteHandlerBuilder builder,
        string signalKey, params string[] allowedValues)
    {
        return builder.AddEndpointFilter(
            new RequireSignalEndpointFilter<string>(signalKey, allowedValues));
    }

    /// <summary>
    ///     Blocks requests where a detection signal matches the specified value.
    /// </summary>
    /// <example>
    ///     // Block datacenter traffic
    ///     app.MapGet("/api/organic", () => "data")
    ///        .BlockIfSignal("ip.is_datacenter", true);
    ///
    ///     // Block VPN traffic
    ///     app.MapGet("/api/direct", () => "data")
    ///        .BlockIfSignal("geo.is_vpn", true);
    /// </example>
    public static RouteHandlerBuilder BlockIfSignal<T>(this RouteHandlerBuilder builder,
        string signalKey, T blockValue, int statusCode = 403, string? message = null)
    {
        return builder.AddEndpointFilter(
            new BlockIfSignalEndpointFilter<T>(signalKey, blockValue, statusCode, message));
    }

    /// <summary>
    ///     Blocks requests where a numeric detection signal exceeds the specified threshold.
    /// </summary>
    /// <example>
    ///     // Block high temporal density (bot clustering signal)
    ///     app.MapGet("/api/premium", () => "data")
    ///        .BlockIfSignalAbove("cluster.temporal_density", 0.8);
    ///
    ///     // Block high headless likelihood
    ///     app.MapGet("/api/form", () => "form")
    ///        .BlockIfSignalAbove("fingerprint.headless_score", 0.7);
    /// </example>
    public static RouteHandlerBuilder BlockIfSignalAbove(this RouteHandlerBuilder builder,
        string signalKey, double threshold, int statusCode = 403, string? message = null)
    {
        return builder.AddEndpointFilter(
            new BlockIfSignalAboveEndpointFilter(signalKey, threshold, statusCode, message));
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

        // Feedback endpoint for marking false positives/negatives
        group.MapPost("/feedback", async (HttpContext context) =>
            {
                var feedback = await context.Request.ReadFromJsonAsync<BotFeedbackRequest>();
                if (feedback == null)
                    return Results.BadRequest(new { error = "Invalid feedback payload" });

                if (feedback.Outcome is not ("Human" or "Bot"))
                    return Results.BadRequest(new { error = "Outcome must be 'Human' or 'Bot'" });

                // Length validation to prevent log flooding
                if (feedback.Notes is { Length: > 500 })
                    return Results.BadRequest(new { error = "Notes must be 500 characters or fewer" });

                if (feedback.RequestId is { Length: > 128 })
                    return Results.BadRequest(new { error = "RequestId must be 128 characters or fewer" });

                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("BotDetection.Feedback");
                logger.LogInformation(
                    "Bot detection feedback: Outcome={Outcome}, RequestId={RequestId}, Notes={Notes}",
                    feedback.Outcome,
                    feedback.RequestId ?? "(none)",
                    feedback.Notes);

                return Results.Ok(new
                {
                    accepted = true,
                    outcome = feedback.Outcome,
                    requestId = feedback.RequestId
                });
            })
            .WithName("BotDetection_Feedback")
            .WithSummary("Submit detection feedback (false positive/negative)");

        // PoW challenge verification endpoint
        endpoints.MapChallengeEndpoints();

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
    ///     Format probability as percentage, showing "&lt;1%" for very low values.
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

    private static string GetPolicyActionReason(DetectionPolicyAction action, AggregatedEvidence evidence)
    {
        var probStr = FormatProbability(evidence.BotProbability);
        return action switch
        {
            DetectionPolicyAction.Block => $"Policy triggered block at probability {probStr}",
            DetectionPolicyAction.Allow => "Policy allowed request",
            DetectionPolicyAction.Challenge => $"Policy triggered challenge at probability {probStr}",
            DetectionPolicyAction.Throttle => $"Policy triggered throttle at probability {probStr}",
            DetectionPolicyAction.EscalateToAi => $"Policy recommends AI escalation at probability {probStr}",
            DetectionPolicyAction.LogOnly => "Policy set to log only",
            _ => $"Policy action: {action}"
        };
    }

    /// <summary>
    ///     Applies a named bot detection policy and action policy to this endpoint.
    /// </summary>
    /// <example>
    ///     app.MapGet("/api/data", () => "sensitive")
    ///        .BotPolicy("strict", actionPolicy: "block");
    ///
    ///     app.MapPost("/api/submit", () => "ok")
    ///        .BotPolicy("default", actionPolicy: "throttle-stealth", blockThreshold: 0.7);
    /// </example>
    public static RouteHandlerBuilder BotPolicy(this RouteHandlerBuilder builder,
        string policyName = "default", string? actionPolicy = null, double blockThreshold = 0.0)
    {
        return builder.AddEndpointFilter(new BotPolicyEndpointFilter(policyName, actionPolicy, blockThreshold));
    }

    /// <summary>
    ///     Applies bot blocking defaults to all endpoints in this group.
    ///     Individual endpoints can override with their own .BlockBots() or .BotPolicy() calls.
    /// </summary>
    /// <example>
    ///     var api = app.MapGroup("/api").WithBotProtection(allowSearchEngines: true);
    ///     api.MapGet("/products", () => "data");
    ///     api.MapGet("/categories", () => "cats");
    /// </example>
    public static RouteGroupBuilder WithBotProtection(this RouteGroupBuilder group,
        bool allowSearchEngines = false,
        bool allowSocialMediaBots = false,
        bool allowMonitoringBots = false,
        bool allowVerifiedBots = false,
        bool allowAiBots = false,
        bool allowGoodBots = false,
        double minConfidence = 0.0,
        int statusCode = 403,
        string? blockCountries = null,
        string? allowCountries = null,
        bool blockVpn = false,
        bool blockProxy = false,
        bool blockDatacenter = false,
        bool blockTor = false)
    {
        return group.AddEndpointFilter(new BlockBotsEndpointFilter(
            allowVerifiedBots, allowSearchEngines, allowSocialMediaBots,
            allowMonitoringBots, allowAiBots, allowGoodBots,
            false, false, // scrapers and malicious always blocked at group level
            minConfidence, statusCode,
            blockCountries, allowCountries,
            blockVpn, blockProxy, blockDatacenter, blockTor));
    }

    /// <summary>
    ///     Requires human visitors for all endpoints in this group.
    /// </summary>
    /// <example>
    ///     var secured = app.MapGroup("/secure").WithHumanOnly();
    ///     secured.MapPost("/submit", () => "ok");
    /// </example>
    public static RouteGroupBuilder WithHumanOnly(this RouteGroupBuilder group, int statusCode = 403)
    {
        return group.AddEndpointFilter(new RequireHumanEndpointFilter(statusCode));
    }

    /// <summary>
    ///     Maps an adaptive <c>/sitemap.xml</c> endpoint whose URL list and
    ///     verdict comment vary with the visitor's bot-detection evidence.
    ///     Search engines and humans get the full <see cref="StyloBotSitemapOptions.PublicUrls"/>
    ///     list. High-probability bots (probability &gt;= <see cref="StyloBotSitemapOptions.BotProbabilityThreshold"/>)
    ///     get the single <see cref="StyloBotSitemapOptions.HoneypotPath"/>.
    ///     Uncertain visitors get <see cref="StyloBotSitemapOptions.UncertainUrls"/>
    ///     (a subset of public URLs minus admin/api surfaces).
    ///
    ///     Every &lt;url&gt; is preceded by an XML comment naming the verdict so
    ///     SEO consumers can audit what stylobot served them.
    ///
    ///     The endpoint binds via <c>endpoints.MapGet</c> AFTER the detection
    ///     middleware has populated <c>HttpContext.Items[BotDetection.AggregatedEvidence]</c>,
    ///     and is filtered through an endpoint filter that touches the
    ///     detection result so the evidence is reliably available even on
    ///     minimal-API endpoints. Consumers do not write any sitemap markup
    ///     themselves.
    /// </summary>
    public static RouteHandlerBuilder MapStyloBotSitemap(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/sitemap.xml",
        Action<StyloBotSitemapOptions>? configure = null)
    {
        var options = new StyloBotSitemapOptions();
        configure?.Invoke(options);

        var handler = endpoints.MapGet(pattern, (HttpContext ctx) =>
        {
            var verdict = ResolveSitemapVerdict(ctx, options);
            var xml = RenderSitemap(ctx, options, verdict);
            return Results.Content(xml, "application/xml");
        });

        // Mark the endpoint so the detection middleware skips the
        // file-extension static-asset shortcut for this path. Without
        // this, every visitor would get the "Static" policy's neutral
        // verdict and the sitemap would never differentiate.
        handler.WithNotStaticAsset();

        return handler;
    }

    private static SitemapVerdict ResolveSitemapVerdict(HttpContext ctx, StyloBotSitemapOptions options)
    {
        var evidence = ctx.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
            ? evObj as AggregatedEvidence
            : null;

        var probability = evidence?.BotProbability ?? 0d;
        var confidence = evidence?.Confidence ?? 0d;
        var riskBand = evidence?.RiskBand.ToString() ?? "Unknown";
        var isVerifiedCrawler = ctx.IsVerifiedBot() || ctx.IsSearchEngineBot();

        string label;
        IReadOnlyList<string> urls;
        if (isVerifiedCrawler)
        {
            label = "verified-crawler";
            urls = options.PublicUrls.ToList();
        }
        else if (probability >= options.BotProbabilityThreshold)
        {
            label = "high-probability-bot";
            urls = new[] { options.HoneypotPath };
        }
        else if (probability < options.HumanProbabilityCeiling)
        {
            label = "human";
            urls = options.PublicUrls.ToList();
        }
        else
        {
            label = "uncertain";
            urls = options.UncertainUrls.Count > 0
                ? options.UncertainUrls.ToList()
                : options.PublicUrls.ToList();
        }

        return new SitemapVerdict(label, probability, confidence, riskBand, urls);
    }

    private static string RenderSitemap(HttpContext ctx, StyloBotSitemapOptions options, SitemapVerdict verdict)
    {
        var baseUrl = $"{ctx.Request.Scheme}://{(ctx.Request.Host.Value ?? string.Empty).TrimEnd('/')}";
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        if (options.EmitVerdictComment)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"  <!-- stylobot verdict: {verdict.Label} (risk={verdict.RiskBand}, probability={verdict.Probability:F2}, confidence={verdict.Confidence:F2}) -->\n");
        }
        foreach (var path in verdict.Urls)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"  <url><loc>{baseUrl}{path}</loc></url>\n");
        }
        sb.Append("</urlset>\n");
        return sb.ToString();
    }

    private readonly record struct SitemapVerdict(
        string Label,
        double Probability,
        double Confidence,
        string RiskBand,
        IReadOnlyList<string> Urls);

    /// <summary>
    ///     Maps a <c>/robots.txt</c> endpoint whose content is composed
    ///     from the configured <see cref="StyloBotRobotsTxtOptions.Rules"/>
    ///     and an automatically-derived <c>Sitemap:</c> link.
    ///
    ///     Per-deployment (not per-visitor), so the response is the same
    ///     for every caller. Routes through the same
    ///     <c>NotStaticAssetMarker</c> metadata as <c>MapStyloBotSitemap</c>
    ///     so the <c>.txt</c> path does not get diverted to the
    ///     <c>Static</c> detection policy.
    /// </summary>
    public static RouteHandlerBuilder MapStyloBotRobotsTxt(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/robots.txt",
        Action<StyloBotRobotsTxtOptions>? configure = null)
    {
        var options = new StyloBotRobotsTxtOptions();
        configure?.Invoke(options);

        var handler = endpoints.MapGet(pattern, async (HttpContext ctx) =>
        {
            var effective = options;
            if (options.IncludePolicyDerivedDisallows)
            {
                effective = await ApplyPolicyDerivedDisallowsAsync(ctx, options);
            }
            var body = RenderRobotsTxt(ctx, effective);
            return Results.Content(body, "text/plain; charset=utf-8");
        });

        handler.WithNotStaticAsset();
        return handler;
    }

    /// <summary>
    ///     Walks the registered <c>IPolicyRuleStore</c> for live Block-action
    ///     rules whose scope is a single endpoint, normalises their path
    ///     templates (strip HTTP verb if present, keep the path), de-duplicates
    ///     against the static <see cref="StyloBotRobotsTxtOptions.Rules"/>'s
    ///     existing <see cref="RobotsRule.Disallow"/> list under the catch-all
    ///     User-agent, and returns a copy of the options with the merged set.
    ///     Returns the original options unchanged when the store is not
    ///     registered or returns no qualifying rules.
    /// </summary>
    private static async Task<StyloBotRobotsTxtOptions> ApplyPolicyDerivedDisallowsAsync(
        HttpContext ctx,
        StyloBotRobotsTxtOptions options)
    {
        var store = ctx.RequestServices.GetService<Policies.Rules.IPolicyRuleStore>();
        if (store is null) return options;

        IReadOnlyList<Policies.Rules.PolicyRule> rules;
        try
        {
            rules = await store.GetAllRulesAsync(ctx.RequestAborted);
        }
        catch
        {
            // Store probe failed; the public document should stay rendered
            // from the static rules rather than 500.
            return options;
        }

        var derived = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.Mode != Policies.Rules.PolicyMode.Live) continue;
            if (rule.Action is not Policies.Rules.PolicyAction.Block) continue;
            if (rule.Scope.Host is not Policies.Rules.HostScope.Endpoint endpoint) continue;

            var path = NormaliseEndpointPath(endpoint.PathTemplate);
            if (!string.IsNullOrEmpty(path)) derived.Add(path);
        }

        if (derived.Count == 0) return options;

        // Clone so we never mutate the operator-supplied options object.
        var merged = CloneOptions(options);
        var wildcard = merged.Rules.FirstOrDefault(r => r.UserAgent == "*");
        if (wildcard is null)
        {
            wildcard = new RobotsRule { UserAgent = "*" };
            merged.Rules.Add(wildcard);
        }

        foreach (var path in derived)
        {
            if (wildcard.Disallow.Contains(path, StringComparer.Ordinal)) continue;
            wildcard.Disallow.Add(path);
        }
        return merged;
    }

    private static string NormaliseEndpointPath(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;
        // Endpoint templates often look like "GET /admin/users".
        // Strip a leading verb so the result is a robots-friendly path.
        var trimmed = template.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace > 0 && firstSpace < trimmed.Length - 1)
        {
            var possibleVerb = trimmed.Substring(0, firstSpace);
            if (possibleVerb.All(char.IsLetter))
            {
                trimmed = trimmed.Substring(firstSpace + 1).Trim();
            }
        }
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static StyloBotRobotsTxtOptions CloneOptions(StyloBotRobotsTxtOptions source)
    {
        return new StyloBotRobotsTxtOptions
        {
            SitemapUrl = source.SitemapUrl,
            Host = source.Host,
            HeaderComments = source.HeaderComments.ToList(),
            IncludePolicyDerivedDisallows = source.IncludePolicyDerivedDisallows,
            Rules = source.Rules.Select(r => new RobotsRule
            {
                UserAgent = r.UserAgent,
                Allow = r.Allow.ToList(),
                Disallow = r.Disallow.ToList(),
                CrawlDelaySeconds = r.CrawlDelaySeconds
            }).ToList()
        };
    }

    private static string RenderRobotsTxt(HttpContext ctx, StyloBotRobotsTxtOptions options)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var line in options.HeaderComments)
        {
            sb.Append("# ").Append(line).Append('\n');
        }
        if (options.HeaderComments.Count > 0) sb.Append('\n');

        if (!string.IsNullOrEmpty(options.Host))
        {
            sb.Append("Host: ").Append(options.Host).Append('\n').Append('\n');
        }

        for (var i = 0; i < options.Rules.Count; i++)
        {
            var rule = options.Rules[i];
            sb.Append("User-agent: ").Append(rule.UserAgent).Append('\n');
            foreach (var allow in rule.Allow)
            {
                sb.Append("Allow: ").Append(allow).Append('\n');
            }
            foreach (var disallow in rule.Disallow)
            {
                sb.Append("Disallow: ").Append(disallow).Append('\n');
            }
            if (rule.CrawlDelaySeconds is { } delay)
            {
                sb.Append("Crawl-delay: ").Append(delay).Append('\n');
            }
            if (i < options.Rules.Count - 1) sb.Append('\n');
        }

        var sitemap = options.SitemapUrl
                      ?? $"{ctx.Request.Scheme}://{(ctx.Request.Host.Value ?? string.Empty).TrimEnd('/')}/sitemap.xml";
        sb.Append('\n').Append("Sitemap: ").Append(sitemap).Append('\n');

        return sb.ToString();
    }
}