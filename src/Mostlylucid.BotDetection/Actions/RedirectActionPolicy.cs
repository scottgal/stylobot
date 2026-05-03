using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Action policy that redirects requests to a different URL.
///     Supports permanent/temporary redirects, honeypot URLs, and tarpit pages.
/// </summary>
/// <remarks>
///     <para>
///         Configuration example (appsettings.json):
///         <code>
///         {
///           "BotDetection": {
///             "ActionPolicies": {
///               "honeypot": {
///                 "Type": "Redirect",
///                 "TargetUrl": "/honeypot",
///                 "Permanent": false,
///                 "PreserveQueryString": false,
///                 "LogOnly": false
///               },
///               "tarpit": {
///                 "Type": "Redirect",
///                 "TargetUrl": "/tarpit?delay=30000",
///                 "PreserveQueryString": false,
///                 "AddMetadata": true,
///                 "Headers": {
///                   "X-Trapped": "true"
///                 }
///               }
///             }
///           }
///         }
///         </code>
///     </para>
///     <para>
///         Code configuration:
///         <code>
///         var redirectPolicy = new RedirectActionPolicy("honeypot", new RedirectActionOptions
///         {
///             TargetUrl = "/honeypot",
///             Permanent = false,
///             AddMetadata = true
///         });
///         actionRegistry.RegisterPolicy(redirectPolicy);
///         </code>
///     </para>
///     <para>
///         Honeypot strategy: Redirect bots to fake endpoints that track their behavior
///         or serve intentionally misleading content.
///     </para>
/// </remarks>
public class RedirectActionPolicy : IActionPolicy
{
    private readonly ILogger<RedirectActionPolicy>? _logger;
    private readonly RedirectActionOptions _options;

    /// <summary>
    ///     Creates a new redirect action policy with the specified options.
    /// </summary>
    public RedirectActionPolicy(
        string name,
        RedirectActionOptions options,
        ILogger<RedirectActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Redirect;

    /// <inheritdoc />
    public Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var targetUrl = BuildTargetUrl(context, evidence);

        _logger?.LogInformation(
            "Redirecting request from {SourcePath} to {TargetUrl}: policy={Policy}, risk={Risk:F2}",
            context.Request.Path, targetUrl, Name, evidence.BotProbability);

        // Add custom headers before redirect
        foreach (var header in _options.Headers) context.Response.Headers.TryAdd(header.Key, header.Value);

        // Add metadata headers if configured
        if (_options.AddMetadata)
        {
            context.Response.Headers.TryAdd("X-Bot-Redirect-Policy", Name);
            context.Response.Headers.TryAdd("X-Bot-Risk-Score", evidence.BotProbability.ToString("F3"));
            context.Response.Headers.TryAdd("X-Bot-Original-Path", context.Request.Path.ToString());
        }

        // Perform redirect
        var statusCode = _options.Permanent ? 301 : 302;
        context.Response.Redirect(targetUrl, _options.Permanent);

        return Task.FromResult(new ActionResult
        {
            Continue = false,
            StatusCode = statusCode,
            Description = $"Redirected to {targetUrl} by {Name}",
            Metadata = new Dictionary<string, object>
            {
                ["targetUrl"] = targetUrl,
                ["permanent"] = _options.Permanent,
                ["policy"] = Name,
                ["risk"] = evidence.BotProbability
            }
        });
    }

    private string BuildTargetUrl(HttpContext context, AggregatedEvidence evidence)
    {
        var url = _options.TargetUrl;

        // Apply URL template replacements
        url = url.Replace("{risk}", evidence.BotProbability.ToString("F3"));
        url = url.Replace("{riskBand}", evidence.RiskBand.ToString());
        url = url.Replace("{policy}", Name);
        url = url.Replace("{originalPath}", Uri.EscapeDataString(context.Request.Path.ToString()));

        // Preserve query string if configured
        if (_options.PreserveQueryString && context.Request.QueryString.HasValue)
        {
            var separator = url.Contains('?') ? "&" : "?";
            url += separator + context.Request.QueryString.Value?.TrimStart('?');
        }

        // Add return URL if configured
        if (_options.IncludeReturnUrl)
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            var separator = url.Contains('?') ? "&" : "?";
            url += $"{separator}{_options.ReturnUrlParam}={Uri.EscapeDataString(returnUrl)}";
        }

        return url;
    }
}

/// <summary>
///     Configuration options for <see cref="RedirectActionPolicy" />.
/// </summary>
public class RedirectActionOptions
{
    /// <summary>
    ///     Target URL to redirect to.
    ///     Supports template placeholders: {risk}, {riskBand}, {policy}, {originalPath}
    ///     Default: "/blocked"
    /// </summary>
    public string TargetUrl { get; set; } = "/blocked";

    /// <summary>
    ///     Whether to use permanent redirect (301) vs temporary (302).
    ///     Default: false (temporary 302 redirect)
    /// </summary>
    public bool Permanent { get; set; }

    /// <summary>
    ///     Whether to preserve the original query string in the redirect URL.
    ///     Default: false
    /// </summary>
    public bool PreserveQueryString { get; set; }

    /// <summary>
    ///     Whether to include a return URL parameter.
    ///     Default: false
    /// </summary>
    public bool IncludeReturnUrl { get; set; }

    /// <summary>
    ///     Query parameter name for return URL.
    ///     Default: "returnUrl"
    /// </summary>
    public string ReturnUrlParam { get; set; } = "returnUrl";

    /// <summary>
    ///     Whether to add metadata headers to the response.
    ///     Default: false
    /// </summary>
    public bool AddMetadata { get; set; }

    /// <summary>
    ///     Additional headers to add to the redirect response.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    ///     Creates options for a "honeypot" redirect (silent trap).
    /// </summary>
    public static RedirectActionOptions Honeypot => new()
    {
        TargetUrl = "/honeypot",
        Permanent = false,
        PreserveQueryString = false,
        AddMetadata = false
    };

    /// <summary>
    ///     Creates options for a "tarpit" redirect (slow response trap).
    /// </summary>
    public static RedirectActionOptions Tarpit => new()
    {
        TargetUrl = "/tarpit?delay=30000",
        Permanent = false,
        PreserveQueryString = false,
        AddMetadata = false,
        Headers = new Dictionary<string, string>
        {
            ["X-Tarpit"] = "true"
        }
    };

    /// <summary>
    ///     Creates options for a "blocked" page redirect.
    /// </summary>
    public static RedirectActionOptions BlockedPage => new()
    {
        TargetUrl = "/blocked",
        Permanent = false,
        IncludeReturnUrl = true,
        AddMetadata = true
    };

    /// <summary>
    ///     Creates options for a custom error page with risk info.
    /// </summary>
    public static RedirectActionOptions ErrorPage => new()
    {
        TargetUrl = "/error?reason=bot-detected&risk={risk}&band={riskBand}",
        Permanent = false,
        AddMetadata = true
    };
}

/// <summary>
///     Factory for creating <see cref="RedirectActionPolicy" /> from configuration.
/// </summary>
public class RedirectActionPolicyFactory : IActionPolicyFactory
{
    private readonly ILogger<RedirectActionPolicy>? _logger;

    public RedirectActionPolicyFactory(ILogger<RedirectActionPolicy>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Redirect;

    /// <inheritdoc />
    public IActionPolicy Create(string name, IDictionary<string, object> options)
    {
        var redirectOptions = new RedirectActionOptions();

        if (options.TryGetValue("TargetUrl", out var targetUrl))
            redirectOptions.TargetUrl = targetUrl?.ToString() ?? redirectOptions.TargetUrl;

        if (options.TryGetValue("Permanent", out var permanent))
            redirectOptions.Permanent = Convert.ToBoolean(permanent);

        if (options.TryGetValue("PreserveQueryString", out var preserveQs))
            redirectOptions.PreserveQueryString = Convert.ToBoolean(preserveQs);

        if (options.TryGetValue("IncludeReturnUrl", out var includeReturn))
            redirectOptions.IncludeReturnUrl = Convert.ToBoolean(includeReturn);

        if (options.TryGetValue("ReturnUrlParam", out var returnParam))
            redirectOptions.ReturnUrlParam = returnParam?.ToString() ?? redirectOptions.ReturnUrlParam;

        if (options.TryGetValue("AddMetadata", out var addMeta))
            redirectOptions.AddMetadata = Convert.ToBoolean(addMeta);

        if (options.TryGetValue("Headers", out var headers) && headers is IDictionary<string, object> headerDict)
            foreach (var kvp in headerDict)
                redirectOptions.Headers[kvp.Key] = kvp.Value?.ToString() ?? "";

        return new RedirectActionPolicy(name, redirectOptions, _logger);
    }
}