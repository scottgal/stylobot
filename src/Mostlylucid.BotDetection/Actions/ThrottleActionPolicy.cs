using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Action policy that throttles requests by introducing delays.
///     Includes jitter to make throttling less detectable by bots.
/// </summary>
/// <remarks>
///     <para>
///         Configuration example (appsettings.json):
///         <code>
///         {
///           "BotDetection": {
///             "ActionPolicies": {
///               "softThrottle": {
///                 "Type": "Throttle",
///                 "BaseDelayMs": 500,
///                 "MaxDelayMs": 5000,
///                 "JitterPercent": 0.25,
///                 "ScaleByRisk": true,
///                 "Message": "Please slow down"
///               },
///               "aggressiveThrottle": {
///                 "Type": "Throttle",
///                 "BaseDelayMs": 2000,
///                 "MaxDelayMs": 30000,
///                 "JitterPercent": 0.5,
///                 "ScaleByRisk": true,
///                 "ExponentialBackoff": true,
///                 "BackoffFactor": 2.0
///               }
///             }
///           }
///         }
///         </code>
///     </para>
///     <para>
///         Code configuration:
///         <code>
///         var throttlePolicy = new ThrottleActionPolicy("softThrottle", new ThrottleActionOptions
///         {
///             BaseDelayMs = 500,
///             MaxDelayMs = 5000,
///             JitterPercent = 0.25,
///             ScaleByRisk = true
///         });
///         actionRegistry.RegisterPolicy(throttlePolicy);
///         </code>
///     </para>
///     <para>
///         Jitter makes delays unpredictable, hiding the fact that throttling is occurring.
///         ScaleByRisk adjusts delays based on bot probability - higher risk = longer delays.
///     </para>
/// </remarks>
public class ThrottleActionPolicy : IActionPolicy
{
    private readonly ILogger<ThrottleActionPolicy>? _logger;
    private readonly ThrottleActionOptions _options;

    /// <summary>
    ///     Creates a new throttle action policy with the specified options.
    /// </summary>
    public ThrottleActionPolicy(string name, ThrottleActionOptions options,
        ILogger<ThrottleActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Throttle;

    /// <inheritdoc />
    public PolicyIntent Intent => PolicyIntent.Throttle;

    /// <summary>
    ///     Read-only access to the configured options. Used by the dashboard state provider to
    ///     surface the real configured delay this policy applies.
    /// </summary>
    public ThrottleActionOptions Options => _options;

    /// <inheritdoc />
    async Task<ActionResult> IActionPolicy.ExecuteAsync(
        HttpContext httpContext,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(httpContext, evidence, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        // Calculate base delay
        var delay = CalculateDelay(context, evidence);

        _logger?.LogInformation(
            "Throttling request to {Path}: policy={Policy}, risk={Risk:F2}, delay={Delay}ms",
            context.Request.Path, Name, evidence.BotProbability, delay);

        // Add throttle headers (configurable)
        if (_options.IncludeHeaders)
        {
            context.Response.Headers.TryAdd("X-Throttle-Delay", delay.ToString());
            context.Response.Headers.TryAdd("X-Throttle-Policy", Name);

            if (_options.IncludeRetryAfter)
            {
                // Retry-After in seconds. RetryAfterSeconds when set lets policies advertise
                // a longer back-off than the per-request delay - the "throttle-status" policy
                // wants a fast 429 response but a meaningful "wait 60s" hint for the client.
                // Falls back to delay-derived behaviour when unset for backwards-compat.
                // RFC 7231 requires an integer; format invariantly so European locales don't
                // emit "60,0" and break the header.
                var retryAfter = (int)Math.Ceiling(_options.RetryAfterSeconds ?? delay / 1000.0);
                context.Response.Headers.TryAdd("Retry-After",
                    retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // Visible-429 path (ReturnStatus = true, e.g. throttle-tools, throttle-status):
        // respond IMMEDIATELY. The calculated `delay` is advisory only -- it has
        // already been written to Retry-After above. Holding the response
        // wall-clock for `delay` ms causes upstream proxies (Caddy default
        // dial_timeout = 3s, Cloudflare tunnel origin budget, nginx
        // proxy_connect_timeout) to give up first and return 502 in place of
        // the 429 the visible-throttle contract promised. The client (curl,
        // wget, python-requests, well-behaved scrapers) backs off according
        // to Retry-After, which is the actual throttle mechanism for this
        // preset.
        if (_options.ReturnStatus)
        {
            context.Response.StatusCode = _options.StatusCode;
            // Closed-loop feedback gate: stylobot-synthesised throttle response.
            context.MarkResponseFromStyloBot();
            context.Response.ContentType = _options.ContentType;

            // AOT-safe: no anonymous types with WriteAsJsonAsync
            var json = $$"""{"message":"{{BlockActionPolicy.EscapeJson(_options.Message)}}","retryAfterMs":{{delay}},"policy":"{{BlockActionPolicy.EscapeJson(Name)}}"}""";
            await context.Response.WriteAsync(json, cancellationToken);

            return ActionResult.Blocked(_options.StatusCode, $"Throttled by {Name}: Retry-After advertises {delay}ms back-off");
        }

        // Stealth path (ReturnStatus = false): apply the wall-clock delay,
        // THEN let the request continue upstream. The silent-throttle
        // contract requires the delay to be real wall-clock so the visitor
        // perceives a slow site (the goal: the bot operator gives up
        // without realising they were detected).
        await Task.Delay(delay, cancellationToken);

        // Continue with the request after delay
        return new ActionResult
        {
            Continue = true,
            StatusCode = 200,
            Description = $"Throttled by {Name}: {delay}ms delay",
            Metadata = new Dictionary<string, object>
            {
                ["delayMs"] = delay,
                ["policy"] = Name,
                ["risk"] = evidence.BotProbability
            }
        };
    }

    /// <summary>
    ///     Calculates the delay to apply based on options and risk score.
    /// </summary>
    private int CalculateDelay(HttpContext httpContext, AggregatedEvidence evidence)
    {
        var baseDelay = (double)_options.BaseDelayMs;

        // Scale by risk if enabled
        if (_options.ScaleByRisk)
        {
            // Higher risk = longer delay
            // At risk 0.5, use base delay
            // At risk 1.0, use max delay
            var riskFactor = Math.Max(0, evidence.BotProbability - 0.5) * 2; // 0-1 range from 0.5-1.0 risk
            baseDelay += riskFactor * (_options.MaxDelayMs - _options.BaseDelayMs);
        }

        // Apply exponential backoff if enabled
        if (_options.ExponentialBackoff)
        {
            // Track request count in HttpContext.Items (per-request; cross-request backoff
            // would require a MemoryCache keyed by client signature)
            var key = $"throttle_{Name}_count";
            var count = 1;
            if (httpContext.Items.TryGetValue(key, out var countObj) && countObj is int c) count = c + 1;
            httpContext.Items[key] = count;

            // Apply backoff factor
            if (count > 1) baseDelay *= Math.Pow(_options.BackoffFactor, count - 1);
        }

        // Clamp to max delay
        baseDelay = Math.Min(baseDelay, _options.MaxDelayMs);

        // Apply jitter (Random.Shared is thread-safe)
        if (_options.JitterPercent > 0)
        {
            var jitterRange = baseDelay * _options.JitterPercent;
            var jitter = (Random.Shared.NextDouble() * 2 - 1) * jitterRange; // -jitterRange to +jitterRange
            baseDelay += jitter;
        }

        // Ensure minimum delay
        return Math.Max(_options.MinDelayMs, (int)Math.Round(baseDelay));
    }
}

/// <summary>
///     Configuration options for <see cref="ThrottleActionPolicy" />.
/// </summary>
public class ThrottleActionOptions
{
    /// <summary>
    ///     Base delay in milliseconds before jitter is applied.
    ///     Default: 500ms
    /// </summary>
    public int BaseDelayMs { get; set; } = 500;

    /// <summary>
    ///     Minimum delay in milliseconds (floor after jitter).
    ///     Default: 100ms
    /// </summary>
    public int MinDelayMs { get; set; } = 100;

    /// <summary>
    ///     Maximum delay in milliseconds.
    ///     Used as ceiling and for ScaleByRisk calculations.
    ///     Default: 5000ms (5 seconds)
    /// </summary>
    public int MaxDelayMs { get; set; } = 5000;

    /// <summary>
    ///     Jitter percentage (0.0 to 1.0).
    ///     Adds randomness to make throttling less detectable.
    ///     0.25 means +/- 25% variation.
    ///     Default: 0.25
    /// </summary>
    public double JitterPercent { get; set; } = 0.25;

    /// <summary>
    ///     Whether to scale delay based on risk score.
    ///     Higher risk = longer delays (up to MaxDelayMs).
    ///     Default: true
    /// </summary>
    public bool ScaleByRisk { get; set; } = true;

    /// <summary>
    ///     Whether to use exponential backoff for repeated requests.
    ///     Each subsequent request from same source increases delay.
    ///     Default: false
    /// </summary>
    public bool ExponentialBackoff { get; set; }

    /// <summary>
    ///     Backoff multiplier for exponential backoff.
    ///     Each request multiplies previous delay by this factor.
    ///     Default: 2.0
    /// </summary>
    public double BackoffFactor { get; set; } = 2.0;

    /// <summary>
    ///     Whether to return an HTTP status code after throttling.
    ///     If false, request continues after delay.
    ///     If true, returns StatusCode with Message.
    ///     Default: false (continue after delay)
    /// </summary>
    public bool ReturnStatus { get; set; }

    /// <summary>
    ///     HTTP status code to return if ReturnStatus is true.
    ///     Default: 429 (Too Many Requests)
    /// </summary>
    public int StatusCode { get; set; } = 429;

    /// <summary>
    ///     Content type for response if ReturnStatus is true.
    ///     Default: "application/json"
    /// </summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>
    ///     Message to include in response body.
    ///     Default: "Request throttled - please slow down"
    /// </summary>
    public string Message { get; set; } = "Request throttled - please slow down";

    /// <summary>
    ///     Whether to include throttle headers in response.
    ///     Headers: X-Throttle-Delay, X-Throttle-Policy
    ///     Default: false
    /// </summary>
    public bool IncludeHeaders { get; set; }

    /// <summary>
    ///     Whether to include Retry-After header.
    ///     Only applies if IncludeHeaders is true.
    ///     Default: true
    /// </summary>
    public bool IncludeRetryAfter { get; set; } = true;

    /// <summary>
    ///     Explicit Retry-After value in seconds, decoupled from the per-request delay.
    ///     When null (default), Retry-After is derived from the actual delay (legacy
    ///     behaviour). Set this for policies that want a fast response but advertise a
    ///     long back-off - the throttle-status policy uses this to send a 429 in &lt;100ms
    ///     while telling the client to wait 60s before retrying.
    /// </summary>
    public double? RetryAfterSeconds { get; set; }

    /// <summary>
    ///     Creates options for a "gentle" throttle (short delays, high jitter).
    /// </summary>
    public static ThrottleActionOptions Gentle => new()
    {
        BaseDelayMs = 200,
        MaxDelayMs = 1000,
        JitterPercent = 0.5,
        ScaleByRisk = false
    };

    /// <summary>
    ///     Creates options for a "moderate" throttle (medium delays, scaled by risk).
    /// </summary>
    public static ThrottleActionOptions Moderate => new()
    {
        BaseDelayMs = 500,
        MaxDelayMs = 5000,
        JitterPercent = 0.25,
        ScaleByRisk = true
    };

    /// <summary>
    ///     Creates options for an "aggressive" throttle (long delays, exponential backoff).
    /// </summary>
    public static ThrottleActionOptions Aggressive => new()
    {
        BaseDelayMs = 1000,
        MaxDelayMs = 30000,
        JitterPercent = 0.3,
        ScaleByRisk = true,
        ExponentialBackoff = true,
        BackoffFactor = 2.0
    };

    /// <summary>
    ///     Creates options for "stealth" throttle (variable delays, high jitter, no headers).
    /// </summary>
    public static ThrottleActionOptions Stealth => new()
    {
        BaseDelayMs = 300,
        MaxDelayMs = 3000,
        JitterPercent = 0.5,
        ScaleByRisk = true,
        IncludeHeaders = false
    };

    /// <summary>
    ///     Creates options for "escalating" throttle - progressive delay that increases
    ///     rapidly with repeat detections. First hit ~200ms, escalates to 30s via backoff factor 3.
    /// </summary>
    public static ThrottleActionOptions Escalating => new()
    {
        BaseDelayMs = 200,
        MaxDelayMs = 30000,
        JitterPercent = 0.3,
        ScaleByRisk = true,
        ExponentialBackoff = true,
        BackoffFactor = 3.0,
        IncludeHeaders = false
    };

    /// <summary>
    ///     Creates options for "tools" throttle - 429 with Retry-After for developer tools (curl, wget, python-requests).
    ///     Uses exponential backoff so well-behaved tools auto-slow. Visible headers encourage proper rate limiting.
    /// </summary>
    public static ThrottleActionOptions Tools => new()
    {
        BaseDelayMs = 500,
        MaxDelayMs = 15000,
        JitterPercent = 0.2,
        ScaleByRisk = true,
        ExponentialBackoff = true,
        BackoffFactor = 1.5,
        ReturnStatus = true,
        StatusCode = 429,
        IncludeHeaders = true,
        IncludeRetryAfter = true
    };

    /// <summary>
    ///     Creates options for "status" throttle - the informational 429 path for friendly
    ///     bots that cluster (fediverse link previewers, search engines exceeding rate, etc.).
    ///     Fast response (no risk of timing out a federated link-preview fetch), explicit
    ///     Retry-After of 60s, no exponential backoff (cluster retries come from different
    ///     instances anyway). The 429 tells the bot it was rate-limited rather than blocked
    ///     so it doesn't stop trying entirely - matters for legitimate previewers we WANT
    ///     to see, just not all at once.
    /// </summary>
    public static ThrottleActionOptions Status => new()
    {
        BaseDelayMs = 50,
        MinDelayMs = 0,
        MaxDelayMs = 200,
        JitterPercent = 0.5,
        ScaleByRisk = false,
        ReturnStatus = true,
        StatusCode = 429,
        IncludeHeaders = true,
        IncludeRetryAfter = true,
        RetryAfterSeconds = 60,
        Message = "Rate limited - please retry after the Retry-After interval"
    };
}

/// <summary>
///     Factory for creating <see cref="ThrottleActionPolicy" /> from configuration.
/// </summary>
public class ThrottleActionPolicyFactory : IActionPolicyFactory
{
    private readonly ILogger<ThrottleActionPolicy>? _logger;

    public ThrottleActionPolicyFactory(ILogger<ThrottleActionPolicy>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Throttle;

    /// <inheritdoc />
    public IActionPolicy Create(string name, IDictionary<string, object> options)
    {
        var throttleOptions = new ThrottleActionOptions();

        if (options.TryGetValue("BaseDelayMs", out var baseDelay))
            throttleOptions.BaseDelayMs = Convert.ToInt32(baseDelay);

        if (options.TryGetValue("MinDelayMs", out var minDelay))
            throttleOptions.MinDelayMs = Convert.ToInt32(minDelay);

        if (options.TryGetValue("MaxDelayMs", out var maxDelay))
            throttleOptions.MaxDelayMs = Convert.ToInt32(maxDelay);

        if (options.TryGetValue("JitterPercent", out var jitter))
            throttleOptions.JitterPercent = Convert.ToDouble(jitter);

        if (options.TryGetValue("ScaleByRisk", out var scaleByRisk))
            throttleOptions.ScaleByRisk = Convert.ToBoolean(scaleByRisk);

        if (options.TryGetValue("ExponentialBackoff", out var expBackoff))
            throttleOptions.ExponentialBackoff = Convert.ToBoolean(expBackoff);

        if (options.TryGetValue("BackoffFactor", out var backoffFactor))
            throttleOptions.BackoffFactor = Convert.ToDouble(backoffFactor);

        if (options.TryGetValue("ReturnStatus", out var returnStatus))
            throttleOptions.ReturnStatus = Convert.ToBoolean(returnStatus);

        if (options.TryGetValue("StatusCode", out var statusCode))
            throttleOptions.StatusCode = Convert.ToInt32(statusCode);

        if (options.TryGetValue("ContentType", out var contentType))
            throttleOptions.ContentType = contentType?.ToString() ?? throttleOptions.ContentType;

        if (options.TryGetValue("Message", out var message))
            throttleOptions.Message = message?.ToString() ?? throttleOptions.Message;

        if (options.TryGetValue("IncludeHeaders", out var includeHeaders))
            throttleOptions.IncludeHeaders = Convert.ToBoolean(includeHeaders);

        if (options.TryGetValue("IncludeRetryAfter", out var includeRetry))
            throttleOptions.IncludeRetryAfter = Convert.ToBoolean(includeRetry);

        if (options.TryGetValue("RetryAfterSeconds", out var retryAfter) && retryAfter is not null)
            throttleOptions.RetryAfterSeconds = Convert.ToDouble(retryAfter);

        return new ThrottleActionPolicy(name, throttleOptions, _logger);
    }
}