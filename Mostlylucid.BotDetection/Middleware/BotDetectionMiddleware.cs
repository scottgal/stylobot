using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;

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
    IOptions<BotDetectionOptions> options)
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

        // Fast path: trust upstream detection from gateway proxy
        if (_options.TrustUpstreamDetection && TryHydrateFromUpstream(context))
        {
            if (_options.ResponseHeaders.Enabled)
            {
                var headerConfig = _options.ResponseHeaders;
                var prefix = headerConfig.HeaderPrefix;
                var confidence = (double)context.Items[BotConfidenceKey]!;
                var isBot = (bool)context.Items[IsBotKey]!;

                context.Response.Headers.TryAdd($"{prefix}Risk-Score", confidence.ToString("F3"));
                context.Response.Headers.TryAdd($"{prefix}Upstream-Trust", "true");
                context.Response.Headers.TryAdd($"{prefix}Processing-Ms", "0.0");

                if (headerConfig.IncludeConfidence)
                    context.Response.Headers.TryAdd($"{prefix}Confidence", confidence.ToString("F3"));

                if (isBot)
                {
                    var botName = context.Items[BotNameKey] as string;
                    if (headerConfig.IncludeBotName && !string.IsNullOrEmpty(botName))
                        context.Response.Headers.TryAdd($"{prefix}Bot-Name", botName);
                }
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
                await HandleCustomUaDetection(context, customUa, orchestrator, policyRegistry, actionPolicyRegistry);
                return;
            }

            // ml-bot-test-mode: Named test modes (googlebot, scrapy, etc.)
            var testMode = context.Request.Headers["ml-bot-test-mode"].FirstOrDefault();
            if (!string.IsNullOrEmpty(testMode))
            {
                await HandleTestModeWithRealDetection(context, testMode, orchestrator, policyRegistry,
                    actionPolicyRegistry);
                return;
            }
        }

        // Determine policy to use
        var policy = ResolvePolicy(context, endpoint, policyRegistry);
        var policyAttr = endpoint?.Metadata.GetMetadata<BotPolicyAttribute>();

        // Run full pipeline with orchestrator - always use full detection
        var aggregatedResult = await orchestrator.DetectWithPolicyAsync(context, policy, context.RequestAborted);
        PopulateContextFromAggregated(context, aggregatedResult, policy.Name);

        // Log detection result
        LogDetectionResult(context, aggregatedResult, policy.Name);

        // Add response headers if enabled
        if (_options.ResponseHeaders.Enabled) AddResponseHeaders(context, aggregatedResult, policy.Name);

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

        // Determine if we should block/throttle
        var shouldBlock = ShouldBlockRequest(aggregatedResult, policy, policyAttr);

        if (shouldBlock.Block)
        {
            await HandleBlockedRequest(context, aggregatedResult, policy, policyAttr, shouldBlock.Action);
            return;
        }

        // Register response recording callback (fires after response is sent)
        var requestStartTime = DateTime.UtcNow;
        context.Response.OnCompleted(async () =>
        {
            await RecordResponseAsync(context, aggregatedResult, responseCoordinator, requestStartTime);
        });

        // Continue pipeline
        await _next(context);
    }

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

    /// <summary>Double: confidence score (0.0-1.0)</summary>
    public const string BotConfidenceKey = "BotDetection.Confidence";

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

        // 2. Fall back to path-based policy resolution
        return policyRegistry.GetPolicyForPath(context.Request.Path);
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

        // Check if policy action says to block
        if (aggregated.PolicyAction == PolicyAction.Block)
            return (true, defaultAction == BotBlockAction.Default ? BotBlockAction.StatusCode : defaultAction);

        // Check if policy action says to throttle
        if (aggregated.PolicyAction == PolicyAction.Throttle)
            return (true, BotBlockAction.Throttle);

        // Check if policy action says to challenge
        if (aggregated.PolicyAction == PolicyAction.Challenge)
            return (true, BotBlockAction.Challenge);

        // Check if risk exceeds immediate block threshold
        if (aggregated.BotProbability >= policy.ImmediateBlockThreshold)
            return (true, defaultAction == BotBlockAction.Default ? BotBlockAction.StatusCode : defaultAction);

        // Check for verified bad bot
        if (aggregated.EarlyExit && aggregated.EarlyExitVerdict == EarlyExitVerdict.VerifiedBadBot)
            return (true, defaultAction == BotBlockAction.Default ? BotBlockAction.StatusCode : defaultAction);

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
        IActionPolicyRegistry actionPolicyRegistry)
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
        IActionPolicyRegistry actionPolicyRegistry)
    {
        _logger.LogInformation("Custom UA test: Running real detection with '{UA}'", customUa);

        await RunDetectionWithOverriddenUaAsync(
            context,
            customUa,
            orchestrator,
            policyRegistry,
            actionPolicyRegistry,
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

        // Handle post-detection actions (action policies and blocking)
        if (await HandlePostDetectionActionsAsync(context, aggregatedResult, policy, actionPolicyRegistry, logPrefix))
            return;

        await _next(context);
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

        var isBot = string.Equals(detectedHeader.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        // Parse confidence (required)
        if (!context.Request.Headers.TryGetValue("X-Bot-Confidence", out var confHeader) ||
            !double.TryParse(confHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var confidence))
            return false;

        // Clamp to valid range
        confidence = Math.Clamp(confidence, 0.0, 1.0);

        // Parse optional fields
        BotType? botType = null;
        if (context.Request.Headers.TryGetValue("X-Bot-Type", out var typeHeader) &&
            Enum.TryParse<BotType>(typeHeader.ToString(), true, out var parsedType))
            botType = parsedType;

        var botName = context.Request.Headers["X-Bot-Name"].FirstOrDefault();
        var category = context.Request.Headers["X-Bot-Category"].FirstOrDefault();

        // Populate HttpContext.Items (same keys as PopulateContextFromAggregated)
        context.Items[IsBotKey] = isBot;
        context.Items[BotConfidenceKey] = confidence;
        context.Items[BotTypeKey] = botType;
        context.Items[BotNameKey] = botName;
        context.Items[PolicyNameKey] = "upstream";
        context.Items[DetectionReasonsKey] = new List<DetectionReason>();
        if (!string.IsNullOrEmpty(category))
            context.Items[BotCategoryKey] = category;

        // Create legacy result for compatibility with views/TagHelpers/extension methods
        var legacyResult = new BotDetectionResult
        {
            IsBot = isBot,
            ConfidenceScore = confidence,
            BotType = isBot ? botType : null,
            BotName = isBot ? botName : null,
            Reasons = []
        };
        context.Items[BotDetectionResultKey] = legacyResult;

        _logger.LogDebug(
            "Trusted upstream detection for {Path}: isBot={IsBot}, confidence={Confidence:F2}",
            context.Request.Path, isBot, confidence);

        return true;
    }

    #endregion

    #region Context Population

    private static void PopulateContextFromAggregated(
        HttpContext context,
        AggregatedEvidence result,
        string policyName)
    {
        // Store full aggregated result
        context.Items[AggregatedEvidenceKey] = result;
        context.Items[PolicyNameKey] = policyName;
        context.Items[PolicyActionKey] = result.PolicyAction;

        // Map to legacy keys for compatibility
        var isBot = result.BotProbability >= 0.5;
        context.Items[IsBotKey] = isBot;
        context.Items[BotConfidenceKey] = result.BotProbability;
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