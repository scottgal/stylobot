using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Filters;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Middleware that detects bots and adds detection result to HttpContext.
///     Supports policy-based detection via [BotPolicy] attributes and path patterns.
///     Results are stored in HttpContext.Items for access by downstream middleware, controllers, and views.
/// </summary>
/// <remarks>
///     The following keys are available in HttpContext.Items:
///     <list type="bullet">
///         <item><see cref="BotDetectionResultKey" /> - Full BotDetectionResult object</item>
///         <item><see cref="AggregatedEvidenceKey" /> - Full AggregatedEvidence from orchestrator</item>
///         <item><see cref="IsBotKey" /> - Boolean indicating if request is from a bot</item>
///         <item><see cref="BotConfidenceKey" /> - Double confidence score (0.0-1.0)</item>
///         <item><see cref="BotTypeKey" /> - BotType enum value (nullable)</item>
///         <item><see cref="BotNameKey" /> - String bot name (nullable)</item>
///         <item><see cref="BotCategoryKey" /> - String category from detection reasons (nullable)</item>
///         <item><see cref="DetectionReasonsKey" /> - List of DetectionReason objects</item>
///         <item><see cref="PolicyNameKey" /> - Name of the policy used for detection</item>
///         <item><see cref="PolicyActionKey" /> - PolicyAction taken (if any)</item>
///     </list>
/// </remarks>
public class BotDetectionMiddleware(
    RequestDelegate next,
    ILogger<BotDetectionMiddleware> logger,
    IOptions<BotDetectionOptions> options,
    CountryReputationTracker? countryTracker = null,
    BotClusterService? clusterService = null)
{
    // Default test mode simulations - used as fallback when options don't contain the mode
    private static readonly Dictionary<string, string> DefaultTestModeSimulations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Human browser
            ["human"] =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",

            // Generic bots
            ["bot"] = "bot/1.0 (+http://example.com/bot)",

            // Search engine crawlers
            ["googlebot"] = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            ["bingbot"] = "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",

            // Scrapers and tools
            ["scrapy"] = "Scrapy/2.11 (+https://scrapy.org)",
            ["curl"] = "curl/8.4.0",
            ["puppeteer"] =
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36",

            // Malicious patterns (known bad actors)
            ["malicious"] = "sqlmap/1.7 (http://sqlmap.org)",

            // AI/LLM crawlers
            ["gptbot"] =
                "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko); compatible; GPTBot/1.2; +https://openai.com/gptbot",
            ["claudebot"] = "ClaudeBot/1.0; +https://www.anthropic.com/claude-bot",

            // Social media bots
            ["twitterbot"] = "Twitterbot/1.0",
            ["facebookbot"] = "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)",

            // Monitoring bots
            ["uptimerobot"] = "UptimeRobot/2.0"
        };

    // Random.Shared is thread-safe in .NET 6+

    private readonly ILogger<BotDetectionMiddleware> _logger = logger;
    private readonly RequestDelegate _next = next;
    private readonly BotDetectionOptions _options = options.Value;

    /// <summary>
    ///     Main middleware entry point. Runs bot detection and handles blocking/throttling.
    ///     Uses the BlackboardOrchestrator for full pipeline detection with policy support.
    /// </summary>
    public async Task InvokeAsync(
        HttpContext context,
        BlackboardOrchestrator orchestrator,
        IPolicyRegistry policyRegistry,
        IActionPolicyRegistry actionPolicyRegistry,
        ResponseCoordinator responseCoordinator)
    {
        // Check if bot detection is globally enabled
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        // Check for [SkipBotDetection] attribute
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<SkipBotDetectionAttribute>() != null)
        {
            _logger.LogDebug("Skipping bot detection for {Path} (SkipBotDetection attribute)",
                context.Request.Path);
            await _next(context);
            return;
        }

        // Check skip paths from configuration
        if (ShouldSkipPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Rich API key: per-key detection overlay (detection still runs, but with detector exclusions)
        var apiKeyStore = context.RequestServices?.GetService<IApiKeyStore>();
        var apiKeyContext = TryValidateRichApiKey(context, apiKeyStore);
        if (apiKeyContext != null)
        {
            if (apiKeyContext.DisablesAllDetectors)
            {
                // Key disables all detectors — equivalent to legacy bypass
                context.Items[IsBotKey] = false;
                context.Items[BotProbabilityKey] = 0.0;
                context.Items["BotDetection.ApiKeyBypass"] = true;
                context.Items["BotDetection.ApiKeyContext"] = apiKeyContext;
                _logger.LogDebug("Skipping bot detection for {Path} (API key '{KeyName}' disables all detectors)",
                    context.Request.Path, apiKeyContext.KeyName);
                await _next(context);
                return;
            }

            // Store context for downstream use — detection will run with overlay
            context.Items["BotDetection.ApiKeyContext"] = apiKeyContext;
            _logger.LogDebug("Using API key '{KeyName}' overlay for {Path} (disabled: {Disabled})",
                apiKeyContext.KeyName, context.Request.Path,
                string.Join(", ", apiKeyContext.DisabledDetectors));
        }
        else if (context.Items.ContainsKey("BotDetection.ApiKeyRejection"))
        {
            // Key was found but rejected (expired, rate limited, path denied)
            var rejection = (ApiKeyRejection)context.Items["BotDetection.ApiKeyRejection"]!;
            var statusCode = rejection.Reason == ApiKeyRejectionReason.RateLimitExceeded ? 429 : 403;
            context.Response.StatusCode = statusCode;
            _logger.LogWarning("API key rejected: {Reason} ({Detail}) for {Path}",
                rejection.Reason, rejection.Detail, context.Request.Path);
            return;
        }
        else
        {
            // Legacy API key bypass: trusted keys skip detection entirely
            if (HasValidApiBypassKey(context))
            {
                context.Items[IsBotKey] = false;
                context.Items[BotProbabilityKey] = 0.0;
                context.Items["BotDetection.ApiKeyBypass"] = true;
                _logger.LogDebug("Skipping bot detection for {Path} (legacy API key bypass)", context.Request.Path);
                await _next(context);
                return;
            }
        }

        // Signature-only paths: compute signature for cache lookups but skip detection
        if (IsSignatureOnlyPath(context.Request.Path))
        {
            ComputeAndStoreSignature(context);
            await _next(context);
            return;
        }

        // Fast path: trust upstream detection from gateway proxy
        if (_options.TrustUpstreamDetection && TryHydrateFromUpstream(context))
        {
            // Feed country reputation from upstream detection
            var countryCode = context.Request.Headers["X-Bot-Detection-Country"].FirstOrDefault();
            if (countryTracker != null && !string.IsNullOrEmpty(countryCode) && countryCode != "LOCAL")
            {
                var prob = context.Items[BotProbabilityKey] is double p ? p : 0.0;
                countryTracker.RecordDetection(countryCode, countryCode, prob > 0.5, prob);
            }

            // Notify cluster service of bot detections
            if (clusterService != null && context.Items[IsBotKey] is true)
                clusterService.NotifyBotDetected();

            // Register response recording for upstream-trusted requests too
            // (captures 404s, errors, etc. for behavioral analysis)
            var upstreamEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence;
            if (upstreamEvidence != null)
            {
                var upstreamStartTime = DateTime.UtcNow;
                context.Response.OnCompleted(async () =>
                {
                    await RecordResponseAsync(context, upstreamEvidence, responseCoordinator, upstreamStartTime);
                });
            }

            // Only add the trust marker — the gateway already emits full X-Bot-* response headers
            if (_options.ResponseHeaders.Enabled)
            {
                var prefix = _options.ResponseHeaders.HeaderPrefix;
                context.Response.Headers.TryAdd($"{prefix}Upstream-Trust", "true");
                context.Response.Headers.TryAdd($"{prefix}Processing-Ms", "0.0");
            }

            await _next(context);
            return;
        }

        // Test mode: Allow overriding bot detection via header
        // In test mode, we still run the real pipeline but with a simulated User-Agent
        if (_options.EnableTestMode)
        {
            // ml-bot-test-ua: Custom UA string to test directly
            var customUa = context.Request.Headers["ml-bot-test-ua"].FirstOrDefault();
            if (!string.IsNullOrEmpty(customUa))
            {
                await HandleCustomUaDetection(context, customUa, orchestrator, policyRegistry, actionPolicyRegistry, responseCoordinator);
                return;
            }

            // ml-bot-test-mode: Named test modes (googlebot, scrapy, etc.)
            var testMode = context.Request.Headers["ml-bot-test-mode"].FirstOrDefault();
            if (!string.IsNullOrEmpty(testMode))
            {
                await HandleTestModeWithRealDetection(context, testMode, orchestrator, policyRegistry,
                    actionPolicyRegistry, responseCoordinator);
                return;
            }
        }

        // Determine policy to use
        var policy = ResolvePolicy(context, endpoint, policyRegistry);
        var policyAttr = endpoint?.Metadata.GetMetadata<BotPolicyAttribute>();

        // Run full pipeline with orchestrator - always use full detection
        var aggregatedResult = await orchestrator.DetectWithPolicyAsync(context, policy, context.RequestAborted);
        PopulateContextFromAggregated(context, aggregatedResult, policy.Name);

        // Compute multi-vector signatures for dashboard and bot identity tracking
        ComputeAndStoreSignature(context);

        // Feed country reputation and cluster services with detection results
        FeedDetectionServices(context, aggregatedResult);

        // Register response recording BEFORE any early exits (blocked, action policy, etc.)
        // so all response codes (403, 404, 500) are captured for behavioral analysis.
        var requestStartTime = DateTime.UtcNow;
        context.Response.OnCompleted(async () =>
        {
            // Read final evidence from context (may have been boosted by ApplyResponseStatusBoost)
            var finalEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence ?? aggregatedResult;
            await RecordResponseAsync(context, finalEvidence, responseCoordinator, requestStartTime);
        });

        // Log detection result
        LogDetectionResult(context, aggregatedResult, policy.Name);

        // Add response headers if enabled
        if (_options.ResponseHeaders.Enabled) AddResponseHeaders(context, aggregatedResult, policy.Name);

        // API key action policy override (e.g., "logonly" for monitoring keys)
        // Respect the policy's ActionPolicyOverridable flag — locked policies cannot be overridden by API keys
        if (apiKeyContext != null && !string.IsNullOrEmpty(apiKeyContext.ActionPolicyName)
            && policy.ActionPolicyOverridable)
        {
            aggregatedResult = aggregatedResult with
            {
                TriggeredActionPolicyName = apiKeyContext.ActionPolicyName
            };
            context.Items[AggregatedEvidenceKey] = aggregatedResult;
        }

        // Check for triggered action policy first (takes precedence over built-in actions)
        if (!string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName))
        {
            var actionPolicy = actionPolicyRegistry.GetPolicy(aggregatedResult.TriggeredActionPolicyName);
            if (actionPolicy != null)
            {
                _logger.LogInformation(
                    "[ACTION] Executing action policy '{ActionPolicy}' for {Path} (risk={Risk:F2})",
                    aggregatedResult.TriggeredActionPolicyName, context.Request.Path, aggregatedResult.BotProbability);

                var actionResult = await actionPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);

                if (!actionResult.Continue)
                    // Action policy handled the response - don't continue pipeline
                    return;
                // Action policy allows continuation - fall through to next middleware
            }
            else
            {
                _logger.LogWarning(
                    "Action policy '{ActionPolicy}' not found in registry, falling back to default handling",
                    aggregatedResult.TriggeredActionPolicyName);
            }
        }

        // Fallback: if no action policy was triggered by transitions, but a bot was detected
        // and DefaultActionPolicyName is configured, execute that as a fallback.
        // This enables "tarpit all detected bots" without needing per-policy transitions.
        // When DefaultActionPolicyName fires, it REPLACES the hard block/403 — the tarpit
        // IS the response. We skip ShouldBlockRequest so bots see a normal (delayed) response.
        if (string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName)
            && !string.IsNullOrEmpty(_options.DefaultActionPolicyName)
            && aggregatedResult.BotProbability >= _options.BotThreshold
            && aggregatedResult.EarlyExitVerdict is not (EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.Whitelisted))
        {
            var fallbackPolicy = actionPolicyRegistry.GetPolicy(_options.DefaultActionPolicyName);
            if (fallbackPolicy != null)
            {
                // Update the evidence so downstream (dashboard, logging) sees the action
                aggregatedResult = aggregatedResult with
                {
                    TriggeredActionPolicyName = _options.DefaultActionPolicyName
                };
                context.Items[AggregatedEvidenceKey] = aggregatedResult;

                _logger.LogInformation(
                    "[ACTION] Executing default action policy '{ActionPolicy}' for {Path} (risk={Risk:F2})",
                    _options.DefaultActionPolicyName, context.Request.Path, aggregatedResult.BotProbability);

                await fallbackPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);

                // DefaultActionPolicyName replaces ShouldBlockRequest — continue pipeline
                // after the action (e.g., throttle-stealth adds delay then lets the
                // request through, appearing normal to the bot).
                await _next(context);
                ApplyResponseStatusBoost(context);
                return;
            }
        }

        // Determine if we should block/throttle
        var shouldBlock = ShouldBlockRequest(aggregatedResult, policy, policyAttr);

        if (shouldBlock.Block)
        {
            await HandleBlockedRequest(context, aggregatedResult, policy, policyAttr, shouldBlock.Action);
            return;
        }

        // Continue pipeline
        await _next(context);

        // Fail2ban-style: after response, check status code and boost/reduce detection
        ApplyResponseStatusBoost(context);
    }

    #region Detection Service Feeds

    /// <summary>
    ///     Feeds detection data to CountryReputationTracker and BotClusterService.
    ///     Called after both local and test-mode detections.
    /// </summary>
    private void FeedDetectionServices(HttpContext context, AggregatedEvidence aggregated)
    {
        // Feed country reputation tracker
        if (countryTracker != null)
        {
            string? countryCode = null;
            string? countryName = null;

            // Try blackboard signals first (from GeoContributor if registered)
            if (aggregated.Signals != null)
            {
                if (aggregated.Signals.TryGetValue("geo.country_code", out var ccObj))
                    countryCode = ccObj as string ?? ccObj?.ToString();
                if (aggregated.Signals.TryGetValue("geo.country_name", out var cnObj))
                    countryName = cnObj as string ?? cnObj?.ToString();
            }

            // Fallback: read from GeoRouting middleware context items
            // (used when GeoContributor is not in the detection pipeline)
            if (string.IsNullOrEmpty(countryCode) &&
                context.Items.TryGetValue("GeoLocation", out var geoLocObj) &&
                geoLocObj != null)
            {
                var geoType = geoLocObj.GetType();
                var ccProp = geoType.GetProperty("CountryCode");
                if (ccProp?.GetValue(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC))
                    countryCode = geoCC;
                var cnProp = geoType.GetProperty("CountryName");
                if (cnProp?.GetValue(geoLocObj) is string geoCN && !string.IsNullOrEmpty(geoCN))
                    countryName = geoCN;
            }

            // Fallback: upstream headers (X-Country from CDN/proxy, CF-IPCountry from Cloudflare)
            if (string.IsNullOrEmpty(countryCode))
            {
                countryCode = context.Request.Headers["X-Country"].FirstOrDefault()
                              ?? context.Request.Headers["CF-IPCountry"].FirstOrDefault();
                if (countryCode is "XX" or "LOCAL" or "" or null)
                    countryCode = null;
            }

            // Fallback: GeoDetection.CountryCode context item
            if (string.IsNullOrEmpty(countryCode) &&
                context.Items.TryGetValue("GeoDetection.CountryCode", out var geoCtx) &&
                geoCtx is string geoCountry && !string.IsNullOrEmpty(geoCountry) && geoCountry != "LOCAL")
            {
                countryCode = geoCountry;
            }

            if (!string.IsNullOrEmpty(countryCode) && countryCode != "LOCAL")
            {
                countryTracker.RecordDetection(
                    countryCode,
                    countryName ?? countryCode,
                    aggregated.BotProbability > 0.5,
                    aggregated.BotProbability);
            }
        }

        // Feed cluster service for bot detections
        if (clusterService != null && aggregated.BotProbability > 0.5)
            clusterService.NotifyBotDetected();
    }

    #endregion

    #region Response Headers

    private void AddResponseHeaders(
        HttpContext context,
        AggregatedEvidence aggregated,
        string policyName)
    {
        var headerConfig = _options.ResponseHeaders;
        var prefix = headerConfig.HeaderPrefix;

        // Always add policy name if configured
        if (headerConfig.IncludePolicyName) context.Response.Headers.TryAdd($"{prefix}Policy", policyName);

        // Risk score (always included)
        context.Response.Headers.TryAdd($"{prefix}Risk-Score", aggregated.BotProbability.ToString("F3"));
        context.Response.Headers.TryAdd($"{prefix}Risk-Band", aggregated.RiskBand.ToString());

        if (headerConfig.IncludeConfidence)
            context.Response.Headers.TryAdd($"{prefix}Confidence", aggregated.Confidence.ToString("F3"));

        if (headerConfig.IncludeDetectors && aggregated.ContributingDetectors.Count > 0)
            context.Response.Headers.TryAdd($"{prefix}Detectors",
                string.Join(",", aggregated.ContributingDetectors));

        if (headerConfig.IncludeProcessingTime)
            context.Response.Headers.TryAdd($"{prefix}Processing-Ms",
                aggregated.TotalProcessingTimeMs.ToString("F1"));

        if (aggregated.PolicyAction.HasValue)
            context.Response.Headers.TryAdd($"{prefix}Action", aggregated.PolicyAction.Value.ToString());

        if (aggregated.PrimaryBotName != null && headerConfig.IncludeBotName)
            context.Response.Headers.TryAdd($"{prefix}Bot-Name", aggregated.PrimaryBotName);

        if (aggregated.EarlyExit)
        {
            context.Response.Headers.TryAdd($"{prefix}Early-Exit", "true");
            if (aggregated.EarlyExitVerdict.HasValue)
                context.Response.Headers.TryAdd($"{prefix}Verdict", aggregated.EarlyExitVerdict.Value.ToString());
        }

        // Include AI status for calibration visibility
        context.Response.Headers.TryAdd($"{prefix}Ai-Ran", aggregated.AiRan.ToString().ToLowerInvariant());

        // Full JSON result if enabled (useful for debugging)
        if (headerConfig.IncludeFullJson)
        {
            var jsonResult = new
            {
                risk = aggregated.BotProbability,
                confidence = aggregated.Confidence,
                riskBand = aggregated.RiskBand.ToString(),
                policy = policyName,
                aiRan = aggregated.AiRan,
                detectors = aggregated.ContributingDetectors,
                categories = aggregated.CategoryBreakdown.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Score)
            };
            var json = JsonSerializer.Serialize(jsonResult);
            // Base64 encode to avoid header encoding issues
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            context.Response.Headers.TryAdd($"{prefix}Result-Json", base64);
        }
    }

    #endregion

    #region Detection Logging

    private void LogDetectionResult(
        HttpContext context,
        AggregatedEvidence aggregated,
        string policyName)
    {
        // Always log at Information level for visibility
        // LogAllRequests controls whether to log human traffic (low risk)
        // LogDetailedReasons controls whether to include detection reasons

        var riskScore = aggregated.BotProbability;
        var riskBand = aggregated.RiskBand;
        var isLikelyBot = riskScore >= _options.BotThreshold;

        // For low-risk (likely human) requests, only log if LogAllRequests is true
        if (!isLikelyBot && !_options.LogAllRequests) return;

        var path = context.Request.Path;
        var method = context.Request.Method;
        var botName = aggregated.PrimaryBotName ?? "unknown";
        var detectors = string.Join(",", aggregated.ContributingDetectors);

        if (_options.LogDetailedReasons && aggregated.Contributions.Count > 0)
        {
            // Detailed logging with reasons
            var topReasons = aggregated.Contributions
                .OrderByDescending(c => Math.Abs(c.ConfidenceDelta))
                .Take(3)
                .Select(c => $"{c.Category}:{c.ConfidenceDelta:+0.00;-0.00}")
                .ToList();

            _logger.LogInformation(
                "Bot detection: {Method} {Path} -> risk={Risk:F2} ({RiskBand}), bot={BotName}, policy={Policy}, " +
                "detectors=[{Detectors}], reasons=[{Reasons}], time={Time:F1}ms",
                method, path, riskScore, riskBand, botName, policyName,
                detectors, string.Join(", ", topReasons), aggregated.TotalProcessingTimeMs);
        }
        else
        {
            // Simple logging
            _logger.LogInformation(
                "Bot detection: {Method} {Path} -> risk={Risk:F2} ({RiskBand}), bot={BotName}, policy={Policy}, time={Time:F1}ms",
                method, path, riskScore, riskBand, botName, policyName, aggregated.TotalProcessingTimeMs);
        }
    }

    #endregion

    #region HttpContext Item Keys

    /// <summary>Full BotDetectionResult object</summary>
    public const string BotDetectionResultKey = "BotDetectionResult";

    /// <summary>Full AggregatedEvidence from blackboard orchestrator</summary>
    public const string AggregatedEvidenceKey = "BotDetection.AggregatedEvidence";

    /// <summary>Boolean: true if request is from a bot</summary>
    public const string IsBotKey = "BotDetection.IsBot";

    /// <summary>Double: bot probability score (0.0-1.0). Legacy name kept for backward compatibility.</summary>
    public const string BotConfidenceKey = "BotDetection.Confidence";

    /// <summary>Double: bot probability (0.0-1.0) - how likely the request is from a bot</summary>
    public const string BotProbabilityKey = "BotDetection.Probability";

    /// <summary>Double: detection confidence (0.0-1.0) - how certain the system is of its verdict</summary>
    public const string DetectionConfidenceKey = "BotDetection.DetectionConfidence";

    /// <summary>BotType?: the detected bot type</summary>
    public const string BotTypeKey = "BotDetection.BotType";

    /// <summary>String?: the detected bot name</summary>
    public const string BotNameKey = "BotDetection.BotName";

    /// <summary>String?: primary detection category (e.g., "UserAgent", "IP", "Header")</summary>
    public const string BotCategoryKey = "BotDetection.Category";

    /// <summary>List&lt;DetectionReason&gt;: all detection reasons</summary>
    public const string DetectionReasonsKey = "BotDetection.Reasons";

    /// <summary>String: name of the policy used for this request</summary>
    public const string PolicyNameKey = "BotDetection.PolicyName";

    /// <summary>PolicyAction?: action taken by policy (if any)</summary>
    public const string PolicyActionKey = "BotDetection.PolicyAction";

    #endregion

    #region Policy Resolution

    private DetectionPolicy ResolvePolicy(
        HttpContext context,
        Endpoint? endpoint,
        IPolicyRegistry policyRegistry)
    {
        // 0a. Check for policy query parameter (for demo/testing - only when test mode enabled)
        if (_options.EnableTestMode && context.Request.Query.TryGetValue("policy", out var policyParam))
        {
            var queryPolicy = policyRegistry.GetPolicy(policyParam.ToString());
            if (queryPolicy != null)
            {
                _logger.LogDebug("Using policy '{Policy}' from query parameter for {Path}",
                    queryPolicy.Name, context.Request.Path);
                return queryPolicy;
            }
        }

        // 0b. Check for X-Bot-Policy header for direct policy selection (test mode only)
        if (_options.EnableTestMode)
        {
            // X-Bot-Policy: <policyName> - direct policy selection
            if (context.Request.Headers.TryGetValue("X-Bot-Policy", out var policyHeader) &&
                !string.IsNullOrEmpty(policyHeader))
            {
                var headerPolicy = policyRegistry.GetPolicy(policyHeader!);
                if (headerPolicy != null)
                {
                    _logger.LogDebug("Using '{Policy}' policy from X-Bot-Policy header for {Path}",
                        headerPolicy.Name, context.Request.Path);
                    return headerPolicy;
                }
            }

            // Legacy headers for backwards compatibility
            if (context.Request.Headers.ContainsKey("X-Force-Slow-Path"))
            {
                var demoPolicy = policyRegistry.GetPolicy("demo");
                if (demoPolicy != null)
                {
                    _logger.LogDebug("Using 'demo' policy from X-Force-Slow-Path header for {Path}",
                        context.Request.Path);
                    return demoPolicy;
                }
            }
            else if (context.Request.Headers.ContainsKey("X-Force-Fast-Path"))
            {
                var fastPolicy = policyRegistry.GetPolicy("fastpath") ??
                                 policyRegistry.GetPolicy("default") ??
                                 policyRegistry.DefaultPolicy;
                _logger.LogDebug("Using '{Policy}' policy from X-Force-Fast-Path header for {Path}",
                    fastPolicy.Name, context.Request.Path);
                return fastPolicy;
            }
        }

        // 1. Check for [BotPolicy] attribute on action/controller
        var policyAttr = endpoint?.Metadata.GetMetadata<BotPolicyAttribute>();
        if (policyAttr != null && !string.IsNullOrEmpty(policyAttr.PolicyName))
        {
            var attrPolicy = policyRegistry.GetPolicy(policyAttr.PolicyName);
            if (attrPolicy != null)
            {
                _logger.LogDebug("Using policy '{Policy}' from attribute for {Path}",
                    attrPolicy.Name, context.Request.Path);
                return attrPolicy;
            }

            _logger.LogWarning("Policy '{Policy}' from attribute not found, falling back to path-based",
                policyAttr.PolicyName);
        }

        // 1b. Check for sandbox/probation policy override (set by sandbox action on previous request)
        if (context.Items.TryGetValue("BotDetection.SandboxPolicy", out var sandboxPolicyObj) &&
            sandboxPolicyObj is string sandboxPolicyName)
        {
            var sandboxPolicy = policyRegistry.GetPolicy(sandboxPolicyName);
            if (sandboxPolicy != null)
            {
                // If LLM sampling is disabled for this request, exclude the LLM detector
                if (context.Items.TryGetValue("BotDetection.SandboxUseLlm", out var useLlmObj) &&
                    useLlmObj is false)
                {
                    sandboxPolicy = sandboxPolicy with
                    {
                        ExcludedDetectors = sandboxPolicy.ExcludedDetectors.Add("Llm")
                    };
                }

                _logger.LogDebug("Using sandbox policy '{Policy}' for {Path} (probation mode)",
                    sandboxPolicy.Name, context.Request.Path);
                return sandboxPolicy;
            }
        }

        // 2. Fall back to path-based policy resolution
        var resolvedPolicy = policyRegistry.GetPolicyForPath(context.Request.Path);

        // 3. Apply API key overlay if present
        if (context.Items.TryGetValue("BotDetection.ApiKeyContext", out var keyCtxObj) &&
            keyCtxObj is ApiKeyContext keyCtx)
        {
            // If key specifies a detection policy, use that instead
            if (!string.IsNullOrEmpty(keyCtx.DetectionPolicyName))
            {
                var keyPolicy = policyRegistry.GetPolicy(keyCtx.DetectionPolicyName);
                if (keyPolicy != null)
                    resolvedPolicy = keyPolicy;
            }

            // Apply detector exclusions and weight overrides
            resolvedPolicy = resolvedPolicy.WithApiKeyOverlay(keyCtx);
            _logger.LogDebug("Applied API key overlay '{KeyName}' to policy '{Policy}'",
                keyCtx.KeyName, resolvedPolicy.Name);
        }

        return resolvedPolicy;
    }

    private bool ShouldSkipPath(PathString path)
    {
        // Check ExcludedPaths first (complete bypass)
        foreach (var excludedPath in _options.ExcludedPaths)
            if (path.StartsWithSegments(excludedPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping bot detection for {Path} (ExcludedPaths)", path);
                return true;
            }

        // Also check ResponseHeaders.SkipPaths for backward compatibility
        foreach (var skipPath in _options.ResponseHeaders.SkipPaths)
            if (path.StartsWithSegments(skipPath, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    ///     Checks if the request carries a valid API bypass key.
    ///     Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    private bool HasValidApiBypassKey(HttpContext context)
    {
        if (_options.ApiBypassKeys.Count == 0)
            return false;

        var headerName = _options.ApiBypassHeaderName;
        if (!context.Request.Headers.TryGetValue(headerName, out var headerValue) ||
            string.IsNullOrEmpty(headerValue.ToString()))
            return false;

        var providedKey = headerValue.ToString();
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);

        foreach (var validKey in _options.ApiBypassKeys)
        {
            var validBytes = Encoding.UTF8.GetBytes(validKey);
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, validBytes))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Tries to validate a rich API key from the request header.
    ///     Returns the ApiKeyContext if valid, or null if no rich key matched.
    ///     Stores rejection reason in HttpContext.Items if key was found but rejected.
    /// </summary>
    private ApiKeyContext? TryValidateRichApiKey(HttpContext context, IApiKeyStore? apiKeyStore)
    {
        if (apiKeyStore == null || _options.ApiKeys.Count == 0)
            return null;

        var headerName = _options.ApiBypassHeaderName;
        if (!context.Request.Headers.TryGetValue(headerName, out var headerValue) ||
            string.IsNullOrEmpty(headerValue.ToString()))
            return null;

        var providedKey = headerValue.ToString();
        var (result, rejection) = apiKeyStore.ValidateKeyWithReason(providedKey, context.Request.Path);

        if (result != null)
            return result.Context;

        if (rejection != null && rejection.Reason != ApiKeyRejectionReason.NotFound)
        {
            // Key was found but rejected — store rejection for the caller
            context.Items["BotDetection.ApiKeyRejection"] = rejection;
        }

        return null;
    }

    private bool IsSignatureOnlyPath(PathString path)
    {
        foreach (var sigPath in _options.SignatureOnlyPaths)
            if (path.StartsWithSegments(sigPath, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void ComputeAndStoreSignature(HttpContext context)
    {
        var sigService = context.RequestServices.GetService<MultiFactorSignatureService>();
        if (sigService == null) return;

        var sigs = sigService.GenerateSignatures(context);
        context.Items["BotDetection.Signatures"] = sigs;
    }

    /// <summary>
    ///     Checks if the path has an override that allows it through regardless of detection.
    /// </summary>
    private bool HasPathOverride(PathString path, out string? overrideAction)
    {
        overrideAction = null;

        foreach (var (pattern, action) in _options.PathOverrides)
            if (MatchesPathPattern(path, pattern))
            {
                overrideAction = action;
                return true;
            }

        return false;
    }

    /// <summary>
    ///     Matches a path against a pattern with glob support.
    ///     Supports: exact match, prefix with *, and ** for recursive matching.
    /// </summary>
    private static bool MatchesPathPattern(PathString path, string pattern)
    {
        var pathValue = path.Value ?? "";

        // Exact match
        if (pattern.Equals(pathValue, StringComparison.OrdinalIgnoreCase))
            return true;

        // Prefix match with single * (e.g., "/api/public/*" matches "/api/public/foo" but not "/api/public/foo/bar")
        if (pattern.EndsWith("/*"))
        {
            var prefix = pattern[..^2];
            if (pathValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = pathValue[prefix.Length..];
                // Must have exactly one more segment (starts with / and no more /)
                return remainder.StartsWith('/') && !remainder[1..].Contains('/');
            }
        }

        // Recursive match with ** (e.g., "/api/public/**" matches any depth)
        if (pattern.EndsWith("/**"))
        {
            var prefix = pattern[..^3];
            return pathValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Simple prefix match (e.g., "/api/public" matches "/api/public" and "/api/public/anything")
        if (!pattern.Contains('*')) return pathValue.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    #endregion

    #region Blocking Logic

    private (bool Block, BotBlockAction Action) ShouldBlockRequest(
        AggregatedEvidence aggregated,
        DetectionPolicy policy,
        BotPolicyAttribute? policyAttr)
    {
        // Check attribute skip
        if (policyAttr?.Skip == true)
            return (false, BotBlockAction.Default);

        // Determine action from attribute or default
        var defaultAction = policyAttr?.BlockAction ?? BotBlockAction.Default;

        // Confidence gate: attribute override > policy default > 0 (disabled)
        var minConfidence = policyAttr?.MinConfidence is > 0 ? policyAttr.MinConfidence : policy.MinConfidence;

        // If confidence gate is set and not met, don't block (insufficient evidence)
        if (minConfidence > 0 && aggregated.Confidence < minConfidence)
        {
            _logger.LogDebug(
                "Confidence gate not met for {Path}: confidence={Confidence:F2} < required={Required:F2}, skipping block",
                "", aggregated.Confidence, minConfidence);
            return (false, BotBlockAction.Default);
        }

        // Check if policy action says to block
        if (aggregated.PolicyAction == PolicyAction.Block)
            return (true, defaultAction == BotBlockAction.Default ? BotBlockAction.StatusCode : defaultAction);

        // Check if policy action says to throttle
        if (aggregated.PolicyAction == PolicyAction.Throttle)
            return (true, BotBlockAction.Throttle);

        // Check if policy action says to challenge
        if (aggregated.PolicyAction == PolicyAction.Challenge)
            return (true, BotBlockAction.Challenge);

        // Check for verified bad bot (bypasses confidence gate — verified bots are always high confidence)
        if (aggregated.EarlyExit && aggregated.EarlyExitVerdict == EarlyExitVerdict.VerifiedBadBot)
            return (true, defaultAction == BotBlockAction.Default ? BotBlockAction.StatusCode : defaultAction);

        // Never block verified good bots (Googlebot, Bingbot, etc.) at the middleware level.
        // Endpoint-level filters ([BlockBots], .BlockBots()) decide whether to allow them.
        if (aggregated.EarlyExit && aggregated.EarlyExitVerdict == EarlyExitVerdict.VerifiedGoodBot)
            return (false, BotBlockAction.Default);

        // Check if risk exceeds immediate block threshold
        if (aggregated.BotProbability >= policy.ImmediateBlockThreshold)
            return (true, defaultAction == BotBlockAction.Default ? BotBlockAction.StatusCode : defaultAction);

        // Global BlockDetectedBots: block all detected bots app-wide (respecting allow-lists)
        if (_options.BlockDetectedBots
            && aggregated.BotProbability >= _options.MinConfidenceToBlock)
        {
            // Allow through bot types configured in global options
            if (BotTypeFilter.IsBotTypeAllowed(aggregated.PrimaryBotType,
                    allowSearchEngines: _options.AllowVerifiedSearchEngines,
                    allowSocialMediaBots: _options.AllowSocialMediaBots,
                    allowMonitoringBots: _options.AllowMonitoringBots,
                    allowVerifiedBots: _options.AllowVerifiedSearchEngines,
                    allowTools: _options.AllowTools))
                return (false, BotBlockAction.Default);

            return (true, BotBlockAction.StatusCode);
        }

        return (false, BotBlockAction.Default);
    }

    private async Task HandleBlockedRequest(
        HttpContext context,
        AggregatedEvidence aggregated,
        DetectionPolicy policy,
        BotPolicyAttribute? policyAttr,
        BotBlockAction action)
    {
        var riskScore = aggregated.BotProbability;

        _logger.LogWarning(
            "[BLOCK] Request blocked: {Path} policy={Policy} risk={Risk:F2} action={Action}",
            context.Request.Path, policy.Name, riskScore, action);

        switch (action)
        {
            case BotBlockAction.StatusCode:
            case BotBlockAction.Default:
                var statusCode = policyAttr?.BlockStatusCode ?? 403;
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new BlockedResponse(
                    "Access denied",
                    "Request blocked by bot detection",
                    riskScore,
                    policy.Name
                ));
                break;

            case BotBlockAction.Redirect:
                var redirectUrl = policyAttr?.BlockRedirectUrl ?? _options.Throttling.BlockRedirectUrl ?? "/blocked";
                context.Response.Redirect(redirectUrl);
                break;

            case BotBlockAction.Challenge:
                context.Response.StatusCode = 403;
                context.Response.Headers["X-Bot-Challenge"] = "required";
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new ChallengeResponse(
                    "Challenge required",
                    _options.Throttling.ChallengeType,
                    riskScore
                ));
                break;

            case BotBlockAction.Throttle:
                await HandleThrottle(context, riskScore);
                break;

            case BotBlockAction.LogOnly:
                // Log but don't block - continue to next middleware
                _logger.LogWarning(
                    "Bot detected (shadow mode): path={Path}, risk={Risk:F2}, policy={Policy}",
                    context.Request.Path, riskScore, policy.Name);
                await _next(context);
                break;
        }
    }

    private async Task HandleThrottle(HttpContext context, double riskScore)
    {
        var throttleConfig = _options.Throttling;

        // Calculate retry delay with optional jitter
        var baseDelay = throttleConfig.BaseDelaySeconds;
        var delay = baseDelay;

        if (throttleConfig.EnableJitter)
        {
            // Add jitter: ±JitterPercent of base delay
            var jitterRange = baseDelay * (throttleConfig.JitterPercent / 100.0);
            var jitterValue = (Random.Shared.NextDouble() * 2 - 1) * jitterRange;
            delay = Math.Max(1, baseDelay + (int)jitterValue);
        }

        // Scale delay by risk score if configured
        if (throttleConfig.ScaleByRisk) delay = (int)(delay * (1 + riskScore));

        // Cap at max delay
        delay = Math.Min(delay, throttleConfig.MaxDelaySeconds);

        context.Response.StatusCode = 429;
        context.Response.Headers["Retry-After"] = delay.ToString();

        // Add jitter indication header (helps with debugging, doesn't reveal exact algorithm)
        if (throttleConfig.EnableJitter) context.Response.Headers["X-Retry-Jitter"] = "applied";

        context.Response.ContentType = "application/json";

        // Optionally delay response to slow down bots
        if (throttleConfig.DelayResponse)
        {
            var responseDelay = Math.Min(throttleConfig.ResponseDelayMs, 5000);
            if (throttleConfig.EnableJitter)
            {
                // Add jitter to response delay too
                var delayJitter = (int)(responseDelay * (throttleConfig.JitterPercent / 100.0));
                responseDelay += Random.Shared.Next(-delayJitter, delayJitter);
                responseDelay = Math.Max(100, responseDelay);
            }

            await Task.Delay(responseDelay, context.RequestAborted);
        }

        await context.Response.WriteAsJsonAsync(new
        {
            error = "Too many requests",
            retryAfter = delay,
            message = throttleConfig.ThrottleMessage
        });
    }

    #endregion

    #region Test Mode

    private async Task HandleTestModeWithRealDetection(
        HttpContext context,
        string testMode,
        BlackboardOrchestrator orchestrator,
        IPolicyRegistry policyRegistry,
        IActionPolicyRegistry actionPolicyRegistry,
        ResponseCoordinator responseCoordinator)
    {
        _logger.LogInformation("Test mode: Running real detection with simulated UA for '{Mode}'", testMode);

        // Handle "disable" mode - skip detection entirely
        if (testMode.Equals("disable", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.TryAdd("X-Test-Mode", "disabled");
            await _next(context);
            return;
        }

        // Get simulated User-Agent from options first, then fallback to defaults
        string? simulatedUserAgent = null;
        if (_options.TestModeSimulations.TryGetValue(testMode, out var configuredUa))
            simulatedUserAgent = configuredUa;
        else if (DefaultTestModeSimulations.TryGetValue(testMode, out var defaultUa))
            simulatedUserAgent = defaultUa;

        await RunDetectionWithOverriddenUaAsync(
            context,
            simulatedUserAgent ?? context.Request.Headers.UserAgent.ToString(),
            orchestrator,
            policyRegistry,
            actionPolicyRegistry,
            responseCoordinator,
            testModeHeader: "true",
            uaHeader: ("X-Test-Simulated-UA", simulatedUserAgent ?? "none"),
            logPrefix: "Test mode");
    }

    /// <summary>
    ///     Handles custom User-Agent testing via ml-bot-test-ua header.
    ///     Allows testing with any arbitrary User-Agent string.
    /// </summary>
    private async Task HandleCustomUaDetection(
        HttpContext context,
        string customUa,
        BlackboardOrchestrator orchestrator,
        IPolicyRegistry policyRegistry,
        IActionPolicyRegistry actionPolicyRegistry,
        ResponseCoordinator responseCoordinator)
    {
        _logger.LogInformation("Custom UA test: Running real detection with '{UA}'", customUa);

        await RunDetectionWithOverriddenUaAsync(
            context,
            customUa,
            orchestrator,
            policyRegistry,
            actionPolicyRegistry,
            responseCoordinator,
            testModeHeader: "custom-ua",
            uaHeader: ("X-Test-Custom-UA", customUa),
            logPrefix: "Custom UA test");
    }

    /// <summary>
    ///     Common method for running detection with an overridden User-Agent.
    ///     Handles UA override, detection, action policies, and blocking.
    /// </summary>
    private async Task RunDetectionWithOverriddenUaAsync(
        HttpContext context,
        string overrideUserAgent,
        BlackboardOrchestrator orchestrator,
        IPolicyRegistry policyRegistry,
        IActionPolicyRegistry actionPolicyRegistry,
        ResponseCoordinator responseCoordinator,
        string testModeHeader,
        (string Name, string Value) uaHeader,
        string logPrefix)
    {
        var originalUserAgent = context.Request.Headers.UserAgent.ToString();
        context.Request.Headers.UserAgent = overrideUserAgent;

        AggregatedEvidence? aggregatedResult = null;
        DetectionPolicy? policy = null;

        try
        {
            var endpoint = context.GetEndpoint();
            policy = ResolvePolicy(context, endpoint, policyRegistry);
            aggregatedResult = await orchestrator.DetectWithPolicyAsync(context, policy, context.RequestAborted);
            PopulateContextFromAggregated(context, aggregatedResult, policy.Name);
            FeedDetectionServices(context, aggregatedResult);

            context.Response.Headers.TryAdd("X-Test-Mode", testModeHeader);
            context.Response.Headers.TryAdd(uaHeader.Name, uaHeader.Value);

            if (_options.ResponseHeaders.Enabled)
                AddResponseHeaders(context, aggregatedResult, policy.Name);

            _logger.LogInformation(
                "{LogPrefix} result: BotProbability={Probability:P0}, Detectors={Count}, Processing={Ms:F1}ms, ActionPolicy={ActionPolicy}",
                logPrefix,
                aggregatedResult.BotProbability,
                aggregatedResult.ContributingDetectors.Count,
                aggregatedResult.TotalProcessingTimeMs,
                aggregatedResult.TriggeredActionPolicyName ?? "none");
        }
        finally
        {
            context.Request.Headers.UserAgent = originalUserAgent;
        }

        // Register response recording BEFORE any early exits (blocked, action policy, etc.)
        if (aggregatedResult != null)
        {
            var testStartTime = DateTime.UtcNow;
            context.Response.OnCompleted(async () =>
            {
                await RecordResponseAsync(context, aggregatedResult, responseCoordinator, testStartTime);
            });
        }

        // Handle post-detection actions (action policies and blocking)
        if (await HandlePostDetectionActionsAsync(context, aggregatedResult, policy, actionPolicyRegistry, logPrefix))
            return;

        await _next(context);
        ApplyResponseStatusBoost(context);
    }

    /// <summary>
    ///     Handles action policies and blocking logic after detection.
    ///     Returns true if request was handled (blocked/action executed), false to continue pipeline.
    /// </summary>
    private async Task<bool> HandlePostDetectionActionsAsync(
        HttpContext context,
        AggregatedEvidence? aggregatedResult,
        DetectionPolicy? policy,
        IActionPolicyRegistry actionPolicyRegistry,
        string logPrefix)
    {
        if (aggregatedResult == null || policy == null)
            return false;

        // Execute action policy if triggered
        if (!string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName))
        {
            var actionPolicy = actionPolicyRegistry.GetPolicy(aggregatedResult.TriggeredActionPolicyName);
            if (actionPolicy != null)
            {
                _logger.LogInformation(
                    "[ACTION] {LogPrefix} executing action policy '{ActionPolicy}' for {Path} (risk={Risk:F2})",
                    logPrefix, aggregatedResult.TriggeredActionPolicyName, context.Request.Path, aggregatedResult.BotProbability);

                var actionResult = await actionPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);
                if (!actionResult.Continue)
                    return true;
            }
            else
            {
                _logger.LogWarning(
                    "Action policy '{ActionPolicy}' not found in registry ({LogPrefix})",
                    aggregatedResult.TriggeredActionPolicyName, logPrefix);
            }
        }

        // Fallback: per-bot-type action policy, then DefaultActionPolicyName.
        // When this fires, it replaces the hard block — the policy IS the response.
        if (string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName)
            && aggregatedResult.BotProbability >= _options.BotThreshold
            && aggregatedResult.EarlyExitVerdict is not (EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.Whitelisted))
        {
            // Try bot-type-specific policy first (e.g., Tool → throttle-tools)
            string? resolvedPolicyName = null;
            if (_options.BotTypeActionPolicies.Count > 0
                && aggregatedResult.PrimaryBotType is not null and not BotType.Unknown)
            {
                var botTypeName = aggregatedResult.PrimaryBotType.Value.ToString();
                _options.BotTypeActionPolicies.TryGetValue(botTypeName, out resolvedPolicyName);
            }

            // Fall back to default
            resolvedPolicyName ??= _options.DefaultActionPolicyName;

            if (!string.IsNullOrEmpty(resolvedPolicyName))
            {
                var fallbackPolicy = actionPolicyRegistry.GetPolicy(resolvedPolicyName);
                if (fallbackPolicy != null)
                {
                    aggregatedResult = aggregatedResult with
                    {
                        TriggeredActionPolicyName = resolvedPolicyName
                    };
                    context.Items[AggregatedEvidenceKey] = aggregatedResult;

                    _logger.LogInformation(
                        "[ACTION] {LogPrefix} executing action policy '{ActionPolicy}' for {Path} (risk={Risk:F2}, type={BotType})",
                        logPrefix, resolvedPolicyName, context.Request.Path, aggregatedResult.BotProbability, aggregatedResult.PrimaryBotType);

                    await fallbackPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);
                    return false;
                }
            }
        }

        // Check for blocking
        var endpoint = context.GetEndpoint();
        var policyAttr = endpoint?.Metadata.GetMetadata<BotPolicyAttribute>();
        var shouldBlock = ShouldBlockRequest(aggregatedResult, policy, policyAttr);

        if (shouldBlock.Block)
        {
            await HandleBlockedRequest(context, aggregatedResult, policy, policyAttr, shouldBlock.Action);
            return true;
        }

        return false;
    }

    private static BotDetectionResult CreateTestResult(string testMode)
    {
        // Simple fallback for legacy mode - just creates a generic result
        var isHuman = testMode.Equals("human", StringComparison.OrdinalIgnoreCase);
        return new BotDetectionResult
        {
            IsBot = !isHuman,
            ConfidenceScore = isHuman ? 0.0 : 0.7,
            BotType = isHuman ? null : BotType.Unknown,
            BotName = isHuman ? null : $"Test {testMode}",
            Reasons =
            [
                new DetectionReason
                {
                    Category = "Test Mode",
                    Detail = $"Simulated '{testMode}' (legacy fallback)",
                    ConfidenceImpact = isHuman ? 0.0 : 0.7
                }
            ]
        };
    }

    #endregion

    #region Upstream Trust

    /// <summary>
    ///     Attempts to hydrate HttpContext.Items from upstream gateway detection headers.
    ///     Returns true if upstream headers were found and context was populated, false to fall through.
    /// </summary>
    private bool TryHydrateFromUpstream(HttpContext context)
    {
        // Gateway must have sent X-Bot-Detected header
        if (!context.Request.Headers.TryGetValue("X-Bot-Detected", out var detectedHeader))
            return false;

        // Verify HMAC signature if configured
        if (!string.IsNullOrEmpty(_options.UpstreamSignatureHeader) &&
            !string.IsNullOrEmpty(_options.UpstreamSignatureSecret))
        {
            if (!context.Request.Headers.TryGetValue(_options.UpstreamSignatureHeader, out var signatureHeader) ||
                string.IsNullOrEmpty(signatureHeader.ToString()))
            {
                _logger.LogWarning("Upstream detection headers present but missing signature header '{Header}' — rejecting",
                    _options.UpstreamSignatureHeader);
                return false;
            }

            // Signature = HMACSHA256(X-Bot-Detected + ":" + X-Bot-Confidence + ":" + timestamp, secret)
            var timestamp = context.Request.Headers["X-Bot-Detection-Timestamp"].FirstOrDefault() ?? "";

            // Reject missing or invalid timestamps — replay protection requires a valid timestamp
            if (!long.TryParse(timestamp, out var epochSeconds))
            {
                _logger.LogWarning("Upstream detection missing or invalid timestamp — rejecting (replay protection)");
                return false;
            }

            var signedAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            var age = DateTimeOffset.UtcNow - signedAt;
            if (age.Duration() > TimeSpan.FromSeconds(_options.UpstreamSignatureMaxAgeSeconds))
            {
                _logger.LogWarning("Upstream detection signature expired (age: {Age}) — rejecting", age);
                return false;
            }

            var payload = $"{detectedHeader}:{context.Request.Headers["X-Bot-Confidence"].FirstOrDefault()}:{timestamp}";

            try
            {
                var secretBytes = Convert.FromBase64String(_options.UpstreamSignatureSecret);
                using var hmac = new System.Security.Cryptography.HMACSHA256(secretBytes);
                var expectedBytes = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));

                byte[] actualBytes;
                try { actualBytes = Convert.FromBase64String(signatureHeader.ToString()); }
                catch { actualBytes = []; }

                // Constant-time comparison to prevent timing attacks
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
                {
                    _logger.LogWarning("Upstream detection signature mismatch — possible spoofing attempt");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to verify upstream detection signature");
                return false;
            }
        }

        var isBot = string.Equals(detectedHeader.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        // Parse bot probability and confidence from headers
        // Prefer explicit X-Bot-Detection-Probability header, fall back to X-Bot-Confidence for backward compatibility
        var botProbability = 0.0;
        var confidence = 0.0;

        // Try explicit probability header first
        if (context.Request.Headers.TryGetValue("X-Bot-Detection-Probability", out var probHeader) &&
            double.TryParse(probHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedProb))
        {
            botProbability = Math.Clamp(parsedProb, 0.0, 1.0);
        }
        // Try confidence header as fallback for bot probability (backward compat)
        else if (context.Request.Headers.TryGetValue("X-Bot-Confidence", out var confHeader) &&
                 double.TryParse(confHeader.ToString(), System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var parsedConf))
        {
            botProbability = Math.Clamp(parsedConf, 0.0, 1.0);
        }
        else
        {
            // Neither header present
            return false;
        }

        // Parse actual confidence if available (distinct from bot probability)
        confidence = botProbability; // Default: confidence = probability
        if (context.Request.Headers.TryGetValue("X-Bot-Confidence", out var actualConfHeader) &&
            double.TryParse(actualConfHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var actualConf) &&
            context.Request.Headers.ContainsKey("X-Bot-Detection-Probability")) // Only use confidence header if probability header also exists
        {
            confidence = Math.Clamp(actualConf, 0.0, 1.0);
        }

        // Parse optional fields
        BotType? botType = null;
        if (context.Request.Headers.TryGetValue("X-Bot-Type", out var typeHeader) &&
            Enum.TryParse<BotType>(typeHeader.ToString(), true, out var parsedType))
            botType = parsedType;

        var botName = context.Request.Headers["X-Bot-Name"].FirstOrDefault();
        var category = context.Request.Headers["X-Bot-Category"].FirstOrDefault();

        // Parse risk band
        var riskBand = RiskBand.Unknown;
        if (context.Request.Headers.TryGetValue("X-Bot-Detection-RiskBand", out var riskHeader) &&
            Enum.TryParse<RiskBand>(riskHeader.ToString(), true, out var parsedRisk))
            riskBand = parsedRisk;
        else
            riskBand = botProbability switch
            {
                >= 0.85 => RiskBand.VeryHigh,
                >= 0.7 => RiskBand.High,
                >= 0.5 => RiskBand.Medium,
                >= 0.3 => RiskBand.Elevated,
                >= 0.15 => RiskBand.Low,
                _ => RiskBand.VeryLow
            };

        // Parse processing time
        double processingMs = 0;
        if (context.Request.Headers.TryGetValue("X-Bot-Detection-ProcessingMs", out var procHeader))
            double.TryParse(procHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out processingMs);

        // Parse action
        var actionName = context.Request.Headers["X-Bot-Detection-Action"].FirstOrDefault();

        // Parse contributions from gateway (JSON array)
        // This populates the detector breakdown, reasons, and processing metrics
        DetectionLedger? ledger = null;
        var contributingDetectorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var detectionReasons = new List<DetectionReason>();

        if (context.Request.Headers.TryGetValue("X-Bot-Detection-Contributions", out var contribHeader) &&
            !string.IsNullOrEmpty(contribHeader.ToString()))
        {
            try
            {
                var contribs = JsonSerializer.Deserialize<List<UpstreamContribution>>(contribHeader.ToString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (contribs is { Count: > 0 })
                {
                    ledger = new DetectionLedger(context.TraceIdentifier);
                    foreach (var c in contribs)
                    {
                        var contribution = new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution
                        {
                            DetectorName = c.Name ?? "Unknown",
                            Category = c.Category ?? "Unknown",
                            ConfidenceDelta = c.ConfidenceDelta,
                            Weight = c.Weight,
                            Reason = c.Reason ?? "",
                            ProcessingTimeMs = c.ExecutionTimeMs,
                            Priority = c.Priority
                        };
                        ledger.AddContribution(contribution);
                        contributingDetectorNames.Add(c.Name ?? "Unknown");

                        if (!string.IsNullOrEmpty(c.Reason))
                        {
                            detectionReasons.Add(new DetectionReason
                            {
                                Category = c.Category ?? "Unknown",
                                Detail = c.Reason,
                                ConfidenceImpact = c.ConfidenceDelta
                            });
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse upstream contributions JSON");
            }
        }

        // Fall back to reasons header if no contributions
        if (detectionReasons.Count == 0 &&
            context.Request.Headers.TryGetValue("X-Bot-Detection-Reasons", out var reasonsHeader) &&
            !string.IsNullOrEmpty(reasonsHeader.ToString()))
        {
            try
            {
                var reasons = JsonSerializer.Deserialize<List<string>>(reasonsHeader.ToString());
                if (reasons != null)
                {
                    detectionReasons.AddRange(reasons.Select(r => new DetectionReason
                    {
                        Category = "upstream",
                        Detail = r,
                        ConfidenceImpact = 0
                    }));
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse upstream reasons JSON");
            }
        }

        // Parse forwarded signals from upstream gateway (X-Bot-Detection-Signals JSON header)
        IReadOnlyDictionary<string, object> upstreamSignals = new Dictionary<string, object>();
        var signalsHeader = context.Request.Headers["X-Bot-Detection-Signals"].FirstOrDefault();
        if (!string.IsNullOrEmpty(signalsHeader) && signalsHeader.Length <= 16_384)
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(signalsHeader);
                if (parsed != null)
                {
                    var dict = new Dictionary<string, object>(parsed.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in parsed)
                    {
                        object value = kvp.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.String => kvp.Value.GetString()!,
                            System.Text.Json.JsonValueKind.Number => kvp.Value.TryGetInt64(out var l) ? l : kvp.Value.GetDouble(),
                            System.Text.Json.JsonValueKind.True => true,
                            System.Text.Json.JsonValueKind.False => false,
                            _ => kvp.Value.ToString()
                        };
                        dict[kvp.Key] = value;
                    }
                    upstreamSignals = dict;
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse upstream signals header");
            }
        }

        // Build AggregatedEvidence with ledger so Contributions property works
        var evidence = new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = botProbability,
            Confidence = confidence,
            RiskBand = riskBand,
            PrimaryBotType = botType,
            PrimaryBotName = botName,
            Signals = upstreamSignals,
            TotalProcessingTimeMs = processingMs,
            PolicyName = "upstream",
            TriggeredActionPolicyName = actionName,
            ContributingDetectors = contributingDetectorNames
        };

        // Populate HttpContext.Items (same keys as PopulateContextFromAggregated)
        context.Items[AggregatedEvidenceKey] = evidence;
        context.Items[IsBotKey] = isBot;
        context.Items[BotConfidenceKey] = botProbability; // Legacy: holds probability for backward compat
        context.Items[BotProbabilityKey] = botProbability;
        context.Items[DetectionConfidenceKey] = confidence;
        context.Items[BotTypeKey] = botType;
        context.Items[BotNameKey] = botName;
        context.Items[PolicyNameKey] = "upstream";
        context.Items[DetectionReasonsKey] = detectionReasons;
        if (!string.IsNullOrEmpty(category))
            context.Items[BotCategoryKey] = category;

        // Use upstream multi-factor signatures if forwarded by the gateway
        var upstreamPrimarySig = context.Request.Headers["X-Bot-Detection-PrimarySignature"].FirstOrDefault();
        if (!string.IsNullOrEmpty(upstreamPrimarySig))
        {
            var upstreamSigs = new Dashboard.MultiFactorSignatures
            {
                PrimarySignature = upstreamPrimarySig,
                IpSignature = context.Request.Headers["X-Bot-Detection-IpSignature"].FirstOrDefault(),
                UaSignature = context.Request.Headers["X-Bot-Detection-UaSignature"].FirstOrDefault(),
                ClientSideSignature = context.Request.Headers["X-Bot-Detection-ClientSideSignature"].FirstOrDefault(),
            };
            context.Items["BotDetection.Signatures"] = upstreamSigs;
        }

        // Create legacy result for compatibility with views/TagHelpers/extension methods
        var legacyResult = new BotDetectionResult
        {
            IsBot = isBot,
            ConfidenceScore = botProbability,
            BotType = isBot ? botType : null,
            BotName = isBot ? botName : null,
            Reasons = detectionReasons
        };
        context.Items[BotDetectionResultKey] = legacyResult;

        _logger.LogDebug(
            "Trusted upstream detection for {Path}: isBot={IsBot}, probability={Probability:F2}, risk={Risk}, detectors={DetectorCount}",
            context.Request.Path, isBot, botProbability, riskBand, contributingDetectorNames.Count);

        return true;
    }

    /// <summary>
    ///     JSON model for parsing upstream contribution headers.
    /// </summary>
    private sealed class UpstreamContribution
    {
        public string? Name { get; set; }
        public string? Category { get; set; }
        public double ConfidenceDelta { get; set; }
        public double Weight { get; set; }
        public double Contribution { get; set; }
        public string? Reason { get; set; }
        public double ExecutionTimeMs { get; set; }
        public int Priority { get; set; }
    }

    #endregion

    #region Context Population

    private void PopulateContextFromAggregated(
        HttpContext context,
        AggregatedEvidence result,
        string policyName)
    {
        // Store full aggregated result
        context.Items[AggregatedEvidenceKey] = result;
        context.Items[PolicyNameKey] = policyName;
        context.Items[PolicyActionKey] = result.PolicyAction;

        // Map to legacy keys for compatibility
        // Use configurable BotThreshold for consistency with blocking logic
        var isBot = result.BotProbability >= _options.BotThreshold;
        context.Items[IsBotKey] = isBot;
        context.Items[BotConfidenceKey] = result.BotProbability; // Legacy: holds probability for backward compat
        context.Items[BotProbabilityKey] = result.BotProbability;
        context.Items[DetectionConfidenceKey] = result.Confidence;
        context.Items[BotTypeKey] = result.PrimaryBotType;
        context.Items[BotNameKey] = result.PrimaryBotName;

        // Primary category from highest-contributing category
        if (result.CategoryBreakdown.Count > 0)
        {
            var primaryCategory = result.CategoryBreakdown
                .OrderByDescending(kv => Math.Abs(kv.Value.Score))
                .First();
            context.Items[BotCategoryKey] = primaryCategory.Key;
        }

        // Also create a legacy BotDetectionResult for compatibility
        var legacyResult = new BotDetectionResult
        {
            IsBot = isBot,
            ConfidenceScore = result.BotProbability,
            BotType = isBot ? result.PrimaryBotType : null, // Only set BotType if actually a bot
            BotName = isBot ? result.PrimaryBotName : null, // Only set BotName if actually a bot
            Reasons = result.Contributions.Select(c => new DetectionReason
            {
                Category = c.Category,
                Detail = c.Reason,
                ConfidenceImpact = c.ConfidenceDelta
            }).ToList()
        };
        context.Items[BotDetectionResultKey] = legacyResult;
    }

    private static void PopulateContextItems(HttpContext context, BotDetectionResult result)
    {
        // Full result object (for complete access)
        context.Items[BotDetectionResultKey] = result;

        // Individual properties (for quick access without casting)
        context.Items[IsBotKey] = result.IsBot;
        context.Items[BotConfidenceKey] = result.ConfidenceScore;
        context.Items[BotTypeKey] = result.BotType;
        context.Items[BotNameKey] = result.BotName;
        context.Items[DetectionReasonsKey] = result.Reasons;

        // Primary category (from highest-confidence reason)
        if (result.Reasons.Count > 0)
        {
            var primaryReason = result.Reasons.OrderByDescending(r => r.ConfidenceImpact).First();
            context.Items[BotCategoryKey] = primaryReason.Category;
        }
    }

    #endregion

    #region Post-Response Status Analysis

    /// <summary>
    ///     Fail2ban-style post-response analysis.
    ///     After next() returns, the response status code is available.
    ///     Suspicious status codes (404, 401, 500, etc.) boost the detection score.
    ///     Authenticated successful responses reduce suspicion for marginal cases only.
    ///     Updates AggregatedEvidence in HttpContext.Items so downstream middleware
    ///     (DetectionBroadcastMiddleware) sees the response-aware score.
    ///     All thresholds configurable via BotDetection:ResponseStatusBoost in appsettings.json.
    /// </summary>
    private void ApplyResponseStatusBoost(HttpContext context)
    {
        var boostOpts = _options.ResponseStatusBoost;
        if (!boostOpts.Enabled)
            return;

        // Only applies when detection actually ran
        if (!context.Items.TryGetValue(AggregatedEvidenceKey, out var evidenceObj) ||
            evidenceObj is not AggregatedEvidence evidence)
            return;

        var statusCode = context.Response.StatusCode;
        var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;

        // Compute delta based on response status code (all values from config)
        var (delta, reason) = statusCode switch
        {
            404 => (boostOpts.NotFoundDelta,
                $"Response 404 Not Found on {context.Request.Path}"),
            401 when !isAuthenticated => (boostOpts.UnauthorizedDelta,
                "Response 401 Unauthorized — unauthenticated probe"),
            403 when !isAuthenticated => (boostOpts.ForbiddenDelta,
                "Response 403 Forbidden — access denied"),
            >= 500 and < 600 => (boostOpts.ServerErrorDelta,
                $"Response {statusCode} Server Error triggered"),
            410 => (boostOpts.GoneDelta,
                "Response 410 Gone — probing removed resource"),
            405 => (boostOpts.MethodNotAllowedDelta,
                $"Response 405 Method Not Allowed on {context.Request.Path}"),
            // Authenticated successful response: clear suspicion for MARGINAL cases only.
            // High-confidence bots (> MaxProbability) are not cleared even if authenticated,
            // preventing abuse by authenticated bot accounts.
            >= 200 and < 300 when isAuthenticated
                && evidence.BotProbability > boostOpts.AuthenticatedClearThreshold
                && evidence.BotProbability <= boostOpts.AuthenticatedClearMaxProbability
                => (boostOpts.AuthenticatedClearDelta,
                    "Authenticated user — successful response clears suspicion"),
            _ => (0.0, (string?)null)
        };

        if (delta == 0.0 || reason == null)
            return;

        var newProbability = Math.Clamp(evidence.BotProbability + delta, 0.0, 1.0);

        // Add contribution to the ledger so it shows in detector breakdown
        var contribution = new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution
        {
            DetectorName = "ResponseStatusBoost",
            Category = "ResponseStatus",
            ConfidenceDelta = delta,
            Weight = 1.0,
            Reason = reason,
            ProcessingTimeMs = 0,
            Priority = 999 // Runs after all other detectors
        };

        // Recalculate risk band
        var newRiskBand = newProbability switch
        {
            >= 0.85 => RiskBand.VeryHigh,
            >= 0.7 => RiskBand.High,
            >= 0.5 => RiskBand.Medium,
            >= 0.3 => RiskBand.Elevated,
            >= 0.15 => RiskBand.Low,
            _ => RiskBand.VeryLow
        };

        // Build updated signals dictionary (handles immutable Signals safely)
        var signalDict = new Dictionary<string, object>(evidence.Signals, StringComparer.OrdinalIgnoreCase);
        signalDict["response.status_code"] = statusCode;
        signalDict["response.status_boost"] = delta;
        if (isAuthenticated)
            signalDict["response.authenticated"] = true;

        // Update the evidence with boosted probability and enriched signals
        var detectors = new HashSet<string>(evidence.ContributingDetectors, StringComparer.OrdinalIgnoreCase);
        detectors.Add("ResponseStatusBoost");

        var updatedEvidence = evidence with
        {
            BotProbability = newProbability,
            RiskBand = newRiskBand,
            ContributingDetectors = detectors,
            Signals = signalDict
        };

        // Add contribution to ledger AFTER creating updatedEvidence
        // (both share same Ledger reference since it's a ref type)
        updatedEvidence.Ledger?.AddContribution(contribution);

        // Write back to context for DetectionBroadcastMiddleware
        context.Items[AggregatedEvidenceKey] = updatedEvidence;
        context.Items[BotProbabilityKey] = newProbability;
        context.Items[BotConfidenceKey] = newProbability;
        context.Items[IsBotKey] = newProbability >= _options.BotThreshold;

        _logger.LogInformation(
            "[RESPONSE-BOOST] {Path} status={Status} delta={Delta:+0.00;-0.00} " +
            "prob={OldProb:F2}->{NewProb:F2} auth={Auth}: {Reason}",
            context.Request.Path, statusCode, delta,
            evidence.BotProbability, newProbability,
            isAuthenticated, reason);
    }

    #endregion

    #region Response Recording

    /// <summary>
    ///     Records response signal for behavioral analysis.
    ///     Called asynchronously after response is sent (zero request latency impact).
    /// </summary>
    private async Task RecordResponseAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        ResponseCoordinator coordinator,
        DateTime requestStartTime)
    {
        try
        {
            // Build client ID (same as ResponseBehaviorContributor)
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var ua = context.Request.Headers.UserAgent.ToString();
            var clientId = $"{ip}:{GetHash(ua)}";

            // Calculate processing time
            var processingTimeMs = (DateTime.UtcNow - requestStartTime).TotalMilliseconds;

            // Build response signal
            var signal = new ResponseSignal
            {
                RequestId = context.TraceIdentifier,
                ClientId = clientId,
                Timestamp = DateTimeOffset.UtcNow,
                StatusCode = context.Response.StatusCode,
                ResponseBytes = context.Response.ContentLength ?? 0,
                Path = context.Request.Path.Value ?? "/",
                Method = context.Request.Method,
                ProcessingTimeMs = processingTimeMs,
                RequestBotProbability = evidence.BotProbability,
                InlineAnalysis = false,
                BodySummary = new ResponseBodySummary
                {
                    IsPresent = context.Response.ContentLength > 0,
                    Length = (int)(context.Response.ContentLength ?? 0),
                    ContentType = context.Response.ContentType
                }
            };

            // Record response (async, fire-and-forget style)
            await coordinator.RecordResponseAsync(signal, CancellationToken.None);

            // Feed response content type back to behavioral waveform for Markov chain accuracy
            try
            {
                var waveform = context.RequestServices
                    .GetService<IEnumerable<IContributingDetector>>()?
                    .OfType<Orchestration.ContributingDetectors.BehavioralWaveformContributor>()
                    .FirstOrDefault();
                waveform?.UpdateResponseContentType(clientId, context.Response.ContentType);
            }
            catch
            {
                // Non-critical feedback - swallow silently
            }
        }
        catch (Exception ex)
        {
            // Never throw from OnCompleted callback - just log
            _logger.LogWarning(ex, "Failed to record response signal for {Path}", context.Request.Path);
        }
    }

    /// <summary>
    /// Computes a stable hash for the input string.
    /// Uses XxHash32 for performance - stable across app restarts.
    /// </summary>
    private static string GetHash(string input)
    {
        if (input.Length == 0) return "empty";
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = System.IO.Hashing.XxHash32.HashToUInt32(bytes);
        return hash.ToString("X8");
    }

    #endregion
}

/// <summary>
///     Response object for blocked requests (AOT-compatible)
/// </summary>
internal record BlockedResponse(string Error, string Reason, double RiskScore, string Policy);

/// <summary>
///     Response object for challenge requests (AOT-compatible)
/// </summary>
internal record ChallengeResponse(string Error, string ChallengeType, double RiskScore);

/// <summary>
///     Extension methods for adding bot detection middleware.
/// </summary>
public static class BotDetectionMiddlewareExtensions
{
    /// <summary>
    ///     Add bot detection middleware to the pipeline.
    ///     Should be called after UseRouting() but before UseAuthorization().
    /// </summary>
    /// <example>
    ///     app.UseRouting();
    ///     app.UseBotDetection();
    ///     app.UseAuthorization();
    /// </example>
    public static IApplicationBuilder UseBotDetection(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<BotDetectionMiddleware>();
    }
}