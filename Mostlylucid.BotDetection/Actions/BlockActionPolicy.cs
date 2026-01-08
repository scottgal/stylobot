using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Action policy that blocks requests by returning an HTTP error status.
///     Fully configurable: status code, message, content type, headers.
/// </summary>
/// <remarks>
///     <para>
///         Configuration example (appsettings.json):
///         <code>
///         {
///           "BotDetection": {
///             "ActionPolicies": {
///               "hardBlock": {
///                 "Type": "Block",
///                 "StatusCode": 403,
///                 "Message": "Access denied - bot detected",
///                 "ContentType": "application/json",
///                 "IncludeRiskScore": true,
///                 "Headers": {
///                   "X-Block-Reason": "bot-detection"
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
///         var blockPolicy = new BlockActionPolicy("hardBlock", new BlockActionOptions
///         {
///             StatusCode = 403,
///             Message = "Access denied",
///             IncludeRiskScore = true
///         });
///         actionRegistry.RegisterPolicy(blockPolicy);
///         </code>
///     </para>
/// </remarks>
public class BlockActionPolicy : IActionPolicy
{
    private readonly ILogger<BlockActionPolicy>? _logger;
    private readonly BlockActionOptions _options;

    /// <summary>
    ///     Creates a new block action policy with the specified options.
    /// </summary>
    public BlockActionPolicy(string name, BlockActionOptions options, ILogger<BlockActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Block;

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation(
            "Blocking request to {Path}: policy={Policy}, risk={Risk:F2}, statusCode={StatusCode}",
            context.Request.Path, Name, evidence.BotProbability, _options.StatusCode);

        context.Response.StatusCode = _options.StatusCode;
        context.Response.ContentType = _options.ContentType;

        // Add custom headers
        foreach (var header in _options.Headers) context.Response.Headers.TryAdd(header.Key, header.Value);

        // Build response body
        object responseBody;
        if (_options.IncludeRiskScore)
            responseBody = new
            {
                error = _options.Message,
                riskScore = evidence.BotProbability,
                riskBand = evidence.RiskBand.ToString(),
                policy = Name,
                timestamp = DateTimeOffset.UtcNow
            };
        else
            responseBody = new
            {
                error = _options.Message
            };

        await context.Response.WriteAsJsonAsync(responseBody, cancellationToken);

        return ActionResult.Blocked(_options.StatusCode, $"Blocked by {Name}: {_options.Message}");
    }
}

/// <summary>
///     Configuration options for <see cref="BlockActionPolicy" />.
/// </summary>
public class BlockActionOptions
{
    /// <summary>
    ///     HTTP status code to return.
    ///     Common values: 403 (Forbidden), 429 (Too Many Requests), 503 (Service Unavailable).
    ///     Default: 403
    /// </summary>
    public int StatusCode { get; set; } = 403;

    /// <summary>
    ///     Error message to include in the response body.
    ///     Default: "Access denied"
    /// </summary>
    public string Message { get; set; } = "Access denied";

    /// <summary>
    ///     Content type of the response.
    ///     Default: "application/json"
    /// </summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>
    ///     Whether to include risk score details in the response.
    ///     Useful for debugging, may want to disable in production.
    ///     Default: false
    /// </summary>
    public bool IncludeRiskScore { get; set; }

    /// <summary>
    ///     Additional headers to add to the response.
    ///     Example: { "X-Block-Reason": "bot-detection" }
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    ///     Creates default options for a "soft" block (429 status).
    /// </summary>
    public static BlockActionOptions Soft => new()
    {
        StatusCode = 429,
        Message = "Too many requests - please slow down",
        IncludeRiskScore = false
    };

    /// <summary>
    ///     Creates default options for a "hard" block (403 status).
    /// </summary>
    public static BlockActionOptions Hard => new()
    {
        StatusCode = 403,
        Message = "Access denied",
        IncludeRiskScore = false
    };

    /// <summary>
    ///     Creates default options for a debug block (includes risk details).
    /// </summary>
    public static BlockActionOptions Debug => new()
    {
        StatusCode = 403,
        Message = "Blocked by bot detection",
        IncludeRiskScore = true,
        Headers = new Dictionary<string, string>
        {
            ["X-Debug-Mode"] = "true"
        }
    };
}

/// <summary>
///     Factory for creating <see cref="BlockActionPolicy" /> from configuration.
/// </summary>
public class BlockActionPolicyFactory : IActionPolicyFactory
{
    private readonly ILogger<BlockActionPolicy>? _logger;

    public BlockActionPolicyFactory(ILogger<BlockActionPolicy>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Block;

    /// <inheritdoc />
    public IActionPolicy Create(string name, IDictionary<string, object> options)
    {
        var blockOptions = new BlockActionOptions();

        if (options.TryGetValue("StatusCode", out var statusCode))
            blockOptions.StatusCode = Convert.ToInt32(statusCode);

        if (options.TryGetValue("Message", out var message))
            blockOptions.Message = message?.ToString() ?? blockOptions.Message;

        if (options.TryGetValue("ContentType", out var contentType))
            blockOptions.ContentType = contentType?.ToString() ?? blockOptions.ContentType;

        if (options.TryGetValue("IncludeRiskScore", out var includeRisk))
            blockOptions.IncludeRiskScore = Convert.ToBoolean(includeRisk);

        if (options.TryGetValue("Headers", out var headers) && headers is IDictionary<string, object> headerDict)
            foreach (var kvp in headerDict)
                blockOptions.Headers[kvp.Key] = kvp.Value?.ToString() ?? "";

        return new BlockActionPolicy(name, blockOptions, _logger);
    }
}