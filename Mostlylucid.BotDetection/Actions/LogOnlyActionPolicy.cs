using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Action policy that only logs detection results without blocking.
///     Ideal for shadow mode, monitoring, and gradual rollouts.
/// </summary>
/// <remarks>
///     <para>
///         Configuration example (appsettings.json):
///         <code>
///         {
///           "BotDetection": {
///             "ActionPolicies": {
///               "shadowMode": {
///                 "Type": "LogOnly",
///                 "LogLevel": "Information",
///                 "IncludeHeaders": true,
///                 "AddResponseHeaders": true,
///                 "MetricName": "bot_detection_shadow"
///               },
///               "debug": {
///                 "Type": "LogOnly",
///                 "LogLevel": "Debug",
///                 "LogFullEvidence": true,
///                 "AddResponseHeaders": true
///               }
///             }
///           }
///         }
///         </code>
///     </para>
///     <para>
///         Code configuration:
///         <code>
///         var logPolicy = new LogOnlyActionPolicy("shadow", new LogOnlyActionOptions
///         {
///             LogLevel = LogLevel.Information,
///             AddResponseHeaders = true
///         });
///         actionRegistry.RegisterPolicy(logPolicy);
///         </code>
///     </para>
///     <para>
///         Use cases:
///         <list type="bullet">
///             <item>Shadow mode: Test detection without blocking real traffic</item>
///             <item>Gradual rollout: Monitor before enabling blocking</item>
///             <item>Debugging: Add headers to see detection in browser dev tools</item>
///             <item>Metrics: Track detection rates without affecting users</item>
///         </list>
///     </para>
/// </remarks>
public class LogOnlyActionPolicy : IActionPolicy
{
    private readonly ILogger<LogOnlyActionPolicy>? _logger;
    private readonly LogOnlyActionOptions _options;

    /// <summary>
    ///     Creates a new log-only action policy with the specified options.
    /// </summary>
    public LogOnlyActionPolicy(
        string name,
        LogOnlyActionOptions options,
        ILogger<LogOnlyActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.LogOnly;

    /// <inheritdoc />
    public Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        // Log based on configured level
        LogDetection(context, evidence);

        // Add response headers for debugging
        if (_options.AddResponseHeaders)
        {
            context.Response.Headers.TryAdd("X-Bot-Detection-Mode", "shadow");
            context.Response.Headers.TryAdd("X-Bot-Risk-Score", evidence.BotProbability.ToString("F3"));
            context.Response.Headers.TryAdd("X-Bot-Risk-Band", evidence.RiskBand.ToString());
            context.Response.Headers.TryAdd("X-Bot-Policy", Name);

            if (_options.IncludeDetailedHeaders)
            {
                context.Response.Headers.TryAdd("X-Bot-Detectors",
                    string.Join(",", evidence.ContributingDetectors));
                context.Response.Headers.TryAdd("X-Bot-Confidence", evidence.Confidence.ToString("F3"));

                if (evidence.PrimaryBotName != null)
                    context.Response.Headers.TryAdd("X-Bot-Name", evidence.PrimaryBotName);

                if (evidence.PrimaryBotType != null)
                    context.Response.Headers.TryAdd("X-Bot-Type", evidence.PrimaryBotType.ToString());
            }
        }

        // Add to HttpContext items for downstream access
        if (_options.AddToContextItems)
        {
            context.Items["BotDetection.ShadowMode"] = true;
            context.Items["BotDetection.WouldBlock"] = evidence.BotProbability >= _options.WouldBlockThreshold;
            context.Items["BotDetection.Evidence"] = evidence;

            // Set action marker for downstream middleware (e.g., "degrade", "quarantine", "sandbox")
            if (!string.IsNullOrEmpty(_options.ActionMarker))
            {
                context.Items["BotDetection.Action"] = _options.ActionMarker;

                // Sandbox/probation: force learning policy with optional LLM sampling
                if (_options.ActionMarker == "sandbox")
                {
                    context.Items["BotDetection.SandboxPolicy"] = _options.SandboxPolicy;
                    context.Items["BotDetection.SandboxSampleRate"] = _options.SandboxSampleRate;

                    // Determine if this specific request should get full LLM analysis
                    // based on sampling rate (e.g., 5% of sandbox requests get LLM,
                    // the rest run all detectors except LLM for fast but thorough analysis)
                    var useLlm = _options.SandboxSampleRate >= 1.0
                                 || Random.Shared.NextDouble() < _options.SandboxSampleRate;
                    context.Items["BotDetection.SandboxUseLlm"] = useLlm;
                }
            }
        }

        // Always continue - this is log-only mode
        return Task.FromResult(new ActionResult
        {
            Continue = true,
            StatusCode = 200,
            Description = $"Logged by {Name} (shadow mode)",
            Metadata = new Dictionary<string, object>
            {
                ["mode"] = "shadow",
                ["policy"] = Name,
                ["risk"] = evidence.BotProbability,
                ["riskBand"] = evidence.RiskBand.ToString(),
                ["wouldBlock"] = evidence.BotProbability >= _options.WouldBlockThreshold
            }
        });
    }

    private void LogDetection(HttpContext context, AggregatedEvidence evidence)
    {
        if (_logger == null) return;

        var wouldBlock = evidence.BotProbability >= _options.WouldBlockThreshold;
        var action = wouldBlock ? "WOULD_BLOCK" : "WOULD_ALLOW";

        if (_options.LogFullEvidence)
            switch (_options.LogLevel)
            {
                case LogLevel.Trace:
                    _logger.LogTrace(
                        "[SHADOW] {Action} request to {Path}: risk={Risk:F3}, confidence={Confidence:F3}, " +
                        "riskBand={RiskBand}, detectors=[{Detectors}], botName={BotName}, botType={BotType}, " +
                        "categories={Categories}, processingMs={ProcessingMs}, policy={Policy}",
                        action, context.Request.Path, evidence.BotProbability, evidence.Confidence,
                        evidence.RiskBand, string.Join(",", evidence.ContributingDetectors),
                        evidence.PrimaryBotName, evidence.PrimaryBotType,
                        string.Join(",", evidence.CategoryBreakdown.Select(c => $"{c.Key}:{c.Value.Score:F2}")),
                        evidence.TotalProcessingTimeMs, Name);
                    break;

                case LogLevel.Debug:
                    _logger.LogDebug(
                        "[SHADOW] {Action} request to {Path}: risk={Risk:F3}, confidence={Confidence:F3}, " +
                        "riskBand={RiskBand}, detectors=[{Detectors}], botName={BotName}, policy={Policy}",
                        action, context.Request.Path, evidence.BotProbability, evidence.Confidence,
                        evidence.RiskBand, string.Join(",", evidence.ContributingDetectors),
                        evidence.PrimaryBotName, Name);
                    break;

                case LogLevel.Information:
                    _logger.LogInformation(
                        "[SHADOW] {Action} request to {Path}: risk={Risk:F3}, riskBand={RiskBand}, " +
                        "detectors=[{Detectors}], policy={Policy}",
                        action, context.Request.Path, evidence.BotProbability,
                        evidence.RiskBand, string.Join(",", evidence.ContributingDetectors), Name);
                    break;

                case LogLevel.Warning:
                    if (wouldBlock)
                        _logger.LogWarning(
                            "[SHADOW] {Action} request to {Path}: risk={Risk:F3}, riskBand={RiskBand}, policy={Policy}",
                            action, context.Request.Path, evidence.BotProbability, evidence.RiskBand, Name);
                    break;

                case LogLevel.Error:
                    if (evidence.BotProbability >= 0.9)
                        _logger.LogError(
                            "[SHADOW] HIGH RISK request to {Path}: risk={Risk:F3}, policy={Policy}",
                            context.Request.Path, evidence.BotProbability, Name);
                    break;
            }
        else
            // Simple logging
            _logger.Log(_options.LogLevel,
                "[SHADOW] {Action} {Method} {Path}: risk={Risk:F3}, band={RiskBand}",
                action, context.Request.Method, context.Request.Path,
                evidence.BotProbability, evidence.RiskBand);
    }
}

/// <summary>
///     Configuration options for <see cref="LogOnlyActionPolicy" />.
/// </summary>
public class LogOnlyActionOptions
{
    /// <summary>
    ///     Log level to use for detection events.
    ///     Default: Information
    /// </summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    ///     Whether to log full evidence details.
    ///     Default: false
    /// </summary>
    public bool LogFullEvidence { get; set; }

    /// <summary>
    ///     Whether to add response headers for debugging.
    ///     Default: false
    /// </summary>
    public bool AddResponseHeaders { get; set; }

    /// <summary>
    ///     Whether to include detailed headers (detectors, confidence, bot name).
    ///     Only applies if AddResponseHeaders is true.
    ///     Default: false
    /// </summary>
    public bool IncludeDetailedHeaders { get; set; }

    /// <summary>
    ///     Whether to add detection info to HttpContext.Items.
    ///     Default: true
    /// </summary>
    public bool AddToContextItems { get; set; } = true;

    /// <summary>
    ///     Risk threshold above which "would block" is logged.
    ///     Used for shadow mode comparison.
    ///     Default: 0.85
    /// </summary>
    public double WouldBlockThreshold { get; set; } = 0.85;

    /// <summary>
    ///     Optional metric name for telemetry.
    ///     If set, emits metrics with this name.
    /// </summary>
    public string? MetricName { get; set; }

    /// <summary>
    ///     Optional action marker to set in HttpContext.Items["BotDetection.Action"].
    ///     Used by downstream middleware to take action (e.g., "degrade", "quarantine", "sandbox").
    ///     Only set when AddToContextItems is true.
    /// </summary>
    public string? ActionMarker { get; set; }

    /// <summary>
    ///     For sandbox/probation mode: the detection policy name to use on sandboxed requests.
    ///     Sets HttpContext.Items["BotDetection.SandboxPolicy"] for the policy resolver.
    ///     Default: "learning" (full pipeline with all detectors + AI).
    /// </summary>
    public string SandboxPolicy { get; set; } = "learning";

    /// <summary>
    ///     For sandbox/probation mode: fraction of sandboxed requests that get full
    ///     LLM analysis (0.0-1.0). Remaining requests use the fast learning pipeline.
    ///     Example: 0.05 = 5% of sandbox requests run the full LLM for high-confidence verdicts.
    ///     Sets HttpContext.Items["BotDetection.SandboxSampleRate"] for downstream use.
    ///     Default: 1.0 (all sandbox requests get full analysis).
    /// </summary>
    public double SandboxSampleRate { get; set; } = 1.0;

    /// <summary>
    ///     Creates options for minimal shadow mode.
    /// </summary>
    public static LogOnlyActionOptions Minimal => new()
    {
        LogLevel = LogLevel.Information,
        LogFullEvidence = false,
        AddResponseHeaders = false,
        AddToContextItems = true
    };

    /// <summary>
    ///     Creates options for debug/development mode.
    /// </summary>
    public static LogOnlyActionOptions Debug => new()
    {
        LogLevel = LogLevel.Debug,
        LogFullEvidence = true,
        AddResponseHeaders = true,
        IncludeDetailedHeaders = true,
        AddToContextItems = true
    };

    /// <summary>
    ///     Creates options for production shadow mode with headers.
    /// </summary>
    public static LogOnlyActionOptions ShadowWithHeaders => new()
    {
        LogLevel = LogLevel.Information,
        LogFullEvidence = false,
        AddResponseHeaders = true,
        IncludeDetailedHeaders = false,
        AddToContextItems = true
    };

    /// <summary>
    ///     Creates options for quiet mode (only log high-risk).
    /// </summary>
    public static LogOnlyActionOptions HighRiskOnly => new()
    {
        LogLevel = LogLevel.Warning,
        LogFullEvidence = false,
        AddResponseHeaders = false,
        AddToContextItems = true,
        WouldBlockThreshold = 0.9
    };

    /// <summary>
    ///     Creates options for "degrade" mode — marks request for content degradation.
    ///     Downstream middleware reads BotDetection.Action = "degrade" from HttpContext.Items.
    /// </summary>
    public static LogOnlyActionOptions Degrade => new()
    {
        LogLevel = LogLevel.Information,
        LogFullEvidence = false,
        AddResponseHeaders = false,
        AddToContextItems = true,
        ActionMarker = "degrade"
    };

    /// <summary>
    ///     Creates options for "rate-limit-headers" mode — adds RateLimit-* response headers
    ///     without blocking. Signals to well-behaved bots.
    /// </summary>
    public static LogOnlyActionOptions RateLimitHeaders => new()
    {
        LogLevel = LogLevel.Information,
        LogFullEvidence = false,
        AddResponseHeaders = true,
        IncludeDetailedHeaders = false,
        AddToContextItems = true
    };

    /// <summary>
    ///     Creates options for "quarantine" mode — allows request through but tags
    ///     it for manual review downstream.
    /// </summary>
    public static LogOnlyActionOptions Quarantine => new()
    {
        LogLevel = LogLevel.Warning,
        LogFullEvidence = true,
        AddResponseHeaders = false,
        AddToContextItems = true,
        WouldBlockThreshold = 0.5,
        ActionMarker = "quarantine"
    };

    /// <summary>
    ///     Creates options for "sandbox" (probation) mode — forces the full learning
    ///     detection policy on uncertain traffic to build confidence before taking action.
    ///     The gateway still handles everything — no separate backend needed.
    ///     Sets HttpContext.Items:
    ///     <list type="bullet">
    ///         <item><c>BotDetection.Action = "sandbox"</c></item>
    ///         <item><c>BotDetection.SandboxPolicy = "learning"</c> — forces full pipeline</item>
    ///         <item><c>BotDetection.SandboxUseLlm</c> — true/false based on SandboxSampleRate</item>
    ///     </list>
    ///     Use SandboxSampleRate (default 0.05 = 5%) to control what fraction of
    ///     sandboxed requests get the expensive LLM call. The rest run all detectors
    ///     except LLM for fast but thorough analysis.
    /// </summary>
    public static LogOnlyActionOptions Sandbox => new()
    {
        LogLevel = LogLevel.Warning,
        LogFullEvidence = true,
        AddResponseHeaders = false,
        AddToContextItems = true,
        WouldBlockThreshold = 0.5,
        ActionMarker = "sandbox",
        SandboxSampleRate = 0.05 // 5% get full LLM, rest get everything else
    };

    /// <summary>
    ///     Creates options for full logging mode with maximum visibility.
    ///     Ideal for demos and development environments.
    /// </summary>
    /// <remarks>
    ///     This preset enables:
    ///     <list type="bullet">
    ///         <item>Debug-level logging with full evidence</item>
    ///         <item>All response headers (X-Bot-Risk-Score, X-Bot-Detectors, X-Bot-Name, etc.)</item>
    ///         <item>HttpContext.Items populated for downstream access</item>
    ///         <item>"Would block" indicator at 0.7 threshold</item>
    ///     </list>
    /// </remarks>
    public static LogOnlyActionOptions FullLog => new()
    {
        LogLevel = LogLevel.Debug,
        LogFullEvidence = true,
        AddResponseHeaders = true,
        IncludeDetailedHeaders = true,
        AddToContextItems = true,
        WouldBlockThreshold = 0.7
    };
}

/// <summary>
///     Factory for creating <see cref="LogOnlyActionPolicy" /> from configuration.
/// </summary>
public class LogOnlyActionPolicyFactory : IActionPolicyFactory
{
    private readonly ILogger<LogOnlyActionPolicy>? _logger;

    public LogOnlyActionPolicyFactory(ILogger<LogOnlyActionPolicy>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.LogOnly;

    /// <inheritdoc />
    public IActionPolicy Create(string name, IDictionary<string, object> options)
    {
        var logOptions = new LogOnlyActionOptions();

        if (options.TryGetValue("LogLevel", out var logLevel))
            if (Enum.TryParse<LogLevel>(logLevel?.ToString(), true, out var ll))
                logOptions.LogLevel = ll;

        if (options.TryGetValue("LogFullEvidence", out var fullEvidence))
            logOptions.LogFullEvidence = Convert.ToBoolean(fullEvidence);

        if (options.TryGetValue("AddResponseHeaders", out var addHeaders))
            logOptions.AddResponseHeaders = Convert.ToBoolean(addHeaders);

        if (options.TryGetValue("IncludeDetailedHeaders", out var detailedHeaders))
            logOptions.IncludeDetailedHeaders = Convert.ToBoolean(detailedHeaders);

        if (options.TryGetValue("AddToContextItems", out var addItems))
            logOptions.AddToContextItems = Convert.ToBoolean(addItems);

        if (options.TryGetValue("WouldBlockThreshold", out var threshold))
            logOptions.WouldBlockThreshold = Convert.ToDouble(threshold);

        if (options.TryGetValue("MetricName", out var metricName))
            logOptions.MetricName = metricName?.ToString();

        if (options.TryGetValue("ActionMarker", out var actionMarker))
            logOptions.ActionMarker = actionMarker?.ToString();

        if (options.TryGetValue("SandboxPolicy", out var sandboxPolicy))
            logOptions.SandboxPolicy = sandboxPolicy?.ToString() ?? logOptions.SandboxPolicy;

        if (options.TryGetValue("SandboxSampleRate", out var sandboxRate))
            logOptions.SandboxSampleRate = Convert.ToDouble(sandboxRate);

        return new LogOnlyActionPolicy(name, logOptions, _logger);
    }
}