using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.ApiHolodeck.Services;

/// <summary>
///     API simulation types that the holodeck can mimic.
/// </summary>
public enum ApiSimulationType
{
    /// <summary>REST-style JSON API (default)</summary>
    RestJson,

    /// <summary>GraphQL API</summary>
    GraphQL,

    /// <summary>OpenAPI-defined schema</summary>
    OpenApi,

    /// <summary>Simple XML API</summary>
    Xml,

    /// <summary>HTML page (for web scrapers)</summary>
    Html,

    /// <summary>gRPC-style (JSON representation)</summary>
    GrpcJson,

    /// <summary>Plain text response</summary>
    PlainText
}

/// <summary>
///     Result from shape analysis.
/// </summary>
public sealed record ShapeAnalysisResult
{
    /// <summary>Detected/recommended API simulation type</summary>
    public ApiSimulationType SimulationType { get; init; } = ApiSimulationType.RestJson;

    /// <summary>Shape hint to pass to MockLLMApi (e.g., "graphql", "users-list", "product-detail")</summary>
    public string Shape { get; init; } = "generic";

    /// <summary>OpenAPI spec URL if detected/configured</summary>
    public string? OpenApiSpecUrl { get; init; }

    /// <summary>Content type to return</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>Confidence in the analysis (0-1)</summary>
    public double Confidence { get; init; } = 0.5;

    /// <summary>Reasoning for the analysis</summary>
    public string? Reasoning { get; init; }
}

/// <summary>
///     Service that analyzes incoming requests and determines the appropriate API simulation type.
///     Can optionally use LLM for intelligent shape detection based on request patterns.
/// </summary>
public interface IShapeBuilder
{
    /// <summary>
    ///     Analyze a request and determine the appropriate API simulation shape.
    /// </summary>
    Task<ShapeAnalysisResult> AnalyzeAsync(
        HttpContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Default implementation of shape builder using heuristics and optional LLM analysis.
/// </summary>
public class ShapeBuilder : IShapeBuilder
{
    // Known OpenAPI specs that can be used for realistic simulation
    private static readonly Dictionary<string, string> KnownOpenApiSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["petstore"] = "https://petstore.swagger.io/v2/swagger.json",
        ["github"] =
            "https://raw.githubusercontent.com/github/rest-api-description/main/descriptions/api.github.com/api.github.com.json",
        ["stripe"] = "https://raw.githubusercontent.com/stripe/openapi/master/openapi/spec3.json",
        // Built-in holodeck schemas (embedded resources)
        ["inventory"] = "embedded:inventory-openapi.json",
        ["ecommerce"] = "embedded:ecommerce-openapi.json",
        ["crm"] = "embedded:crm-openapi.json"
    };

    // API domain patterns for detection
    private static readonly Dictionary<string, (string schemaKey, ApiSimulationType type)> DomainPatterns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Inventory/Warehouse patterns
            ["inventory"] = ("inventory", ApiSimulationType.OpenApi),
            ["warehouse"] = ("inventory", ApiSimulationType.OpenApi),
            ["stock"] = ("inventory", ApiSimulationType.OpenApi),
            ["sku"] = ("inventory", ApiSimulationType.OpenApi),
            ["transfers"] = ("inventory", ApiSimulationType.OpenApi),

            // E-commerce patterns
            ["products"] = ("ecommerce", ApiSimulationType.OpenApi),
            ["cart"] = ("ecommerce", ApiSimulationType.OpenApi),
            ["checkout"] = ("ecommerce", ApiSimulationType.OpenApi),
            ["orders"] = ("ecommerce", ApiSimulationType.OpenApi),
            ["shop"] = ("ecommerce", ApiSimulationType.OpenApi),
            ["catalog"] = ("ecommerce", ApiSimulationType.OpenApi),

            // CRM patterns
            ["contacts"] = ("crm", ApiSimulationType.OpenApi),
            ["deals"] = ("crm", ApiSimulationType.OpenApi),
            ["leads"] = ("crm", ApiSimulationType.OpenApi),
            ["pipeline"] = ("crm", ApiSimulationType.OpenApi),
            ["companies"] = ("crm", ApiSimulationType.OpenApi),
            ["activities"] = ("crm", ApiSimulationType.OpenApi),

            // HR/Employee patterns (GraphQL)
            ["employees"] = ("employee-graphql", ApiSimulationType.GraphQL),
            ["employee"] = ("employee-graphql", ApiSimulationType.GraphQL),
            ["hr"] = ("employee-graphql", ApiSimulationType.GraphQL),
            ["departments"] = ("employee-graphql", ApiSimulationType.GraphQL),
            ["payroll"] = ("employee-graphql", ApiSimulationType.GraphQL),
            ["leave"] = ("employee-graphql", ApiSimulationType.GraphQL),
            ["timesheet"] = ("employee-graphql", ApiSimulationType.GraphQL),
            ["performance"] = ("employee-graphql", ApiSimulationType.GraphQL)
        };

    private readonly HttpClient _httpClient;
    private readonly ILogger<ShapeBuilder> _logger;
    private readonly ShapeBuilderOptions _options;

    public ShapeBuilder(
        IHttpClientFactory httpClientFactory,
        IOptions<ShapeBuilderOptions> options,
        ILogger<ShapeBuilder> logger)
    {
        _httpClient = httpClientFactory.CreateClient("ShapeBuilder");
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ShapeAnalysisResult> AnalyzeAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var path = context.Request.Path.Value ?? "/";
        var method = context.Request.Method;
        var contentType = context.Request.ContentType;
        var accept = context.Request.Headers.Accept.ToString();

        // Quick heuristic analysis first
        var heuristicResult = AnalyzeHeuristics(path, method, contentType, accept);

        // If LLM analysis is enabled and we're uncertain, use LLM for deeper analysis
        if (_options.EnableLlmAnalysis && heuristicResult.Confidence < _options.LlmAnalysisThreshold)
            try
            {
                var llmResult = await AnalyzeWithLlmAsync(context, heuristicResult, cancellationToken);
                if (llmResult != null && llmResult.Confidence > heuristicResult.Confidence) return llmResult;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM shape analysis failed, using heuristics");
            }

        return heuristicResult;
    }

    private ShapeAnalysisResult AnalyzeHeuristics(
        string path,
        string method,
        string? contentType,
        string? accept)
    {
        // Check for GraphQL indicators
        if (path.Contains("graphql", StringComparison.OrdinalIgnoreCase) ||
            contentType?.Contains("application/graphql", StringComparison.OrdinalIgnoreCase) == true)
            return new ShapeAnalysisResult
            {
                SimulationType = ApiSimulationType.GraphQL,
                Shape = "graphql",
                ContentType = "application/json",
                Confidence = 0.95,
                Reasoning = "Path or content-type indicates GraphQL"
            };

        // Check for XML/SOAP indicators
        if (contentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true ||
            accept?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true ||
            path.Contains(".xml", StringComparison.OrdinalIgnoreCase))
            return new ShapeAnalysisResult
            {
                SimulationType = ApiSimulationType.Xml,
                Shape = "xml",
                ContentType = "application/xml",
                Confidence = 0.9,
                Reasoning = "XML content-type or accept header detected"
            };

        // Check for HTML requests (web scrapers)
        if (accept?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true ||
            path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            return new ShapeAnalysisResult
            {
                SimulationType = ApiSimulationType.Html,
                Shape = "html",
                ContentType = "text/html",
                Confidence = 0.85,
                Reasoning = "HTML accept header or extension detected"
            };

        // Check for known API patterns to suggest OpenAPI/GraphQL specs
        var apiMatch = MatchKnownApiPattern(path);
        if (apiMatch != null)
            return new ShapeAnalysisResult
            {
                SimulationType = apiMatch.Value.type,
                Shape = apiMatch.Value.shape,
                OpenApiSpecUrl = apiMatch.Value.specUrl,
                ContentType =
                    apiMatch.Value.type == ApiSimulationType.GraphQL ? "application/json" : "application/json",
                Confidence = 0.85,
                Reasoning = $"Matched {apiMatch.Value.type} pattern: {apiMatch.Value.shape}"
            };

        // Detect REST resource patterns
        var restShape = DetectRestResourceShape(path, method);

        return new ShapeAnalysisResult
        {
            SimulationType = ApiSimulationType.RestJson,
            Shape = restShape.shape,
            ContentType = "application/json",
            Confidence = restShape.confidence,
            Reasoning = restShape.reasoning
        };
    }

    private (string shape, string specUrl, ApiSimulationType type)? MatchKnownApiPattern(string path)
    {
        // Check configured custom OpenAPI specs first
        foreach (var (pattern, specUrl) in _options.OpenApiSpecs)
            if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return (pattern, specUrl, ApiSimulationType.OpenApi);

        // Check domain patterns for built-in schemas
        var pathLower = path.ToLowerInvariant();
        foreach (var (keyword, (schemaKey, simType)) in DomainPatterns)
            if (pathLower.Contains(keyword))
            {
                var specUrl = KnownOpenApiSpecs.TryGetValue(schemaKey, out var url)
                    ? url
                    : $"embedded:{schemaKey}.json";
                return (schemaKey, specUrl, simType);
            }

        // Check well-known external patterns
        if (path.Contains("/pets", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/pet/", StringComparison.OrdinalIgnoreCase))
            return ("petstore", KnownOpenApiSpecs["petstore"], ApiSimulationType.OpenApi);

        if (path.Contains("/repos/", StringComparison.OrdinalIgnoreCase) ||
            (path.Contains("/users/", StringComparison.OrdinalIgnoreCase) && path.Contains("github")))
            return ("github", KnownOpenApiSpecs["github"], ApiSimulationType.OpenApi);

        return null;
    }

    private (string shape, double confidence, string reasoning) DetectRestResourceShape(string path, string method)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return ("generic", 0.3, "Root path - generic response");

        // Look for common resource patterns
        var lastSegment = segments[^1];
        var hasId = segments.Length > 1 &&
                    (Guid.TryParse(lastSegment, out _) ||
                     int.TryParse(lastSegment, out _) ||
                     lastSegment.Length > 20); // Likely ID

        // Common resource names
        var resourceName = hasId && segments.Length > 1 ? segments[^2] : lastSegment;

        var shape = resourceName.ToLowerInvariant() switch
        {
            "users" or "user" => hasId ? "user-detail" : "users-list",
            "products" or "product" => hasId ? "product-detail" : "products-list",
            "orders" or "order" => hasId ? "order-detail" : "orders-list",
            "items" or "item" => hasId ? "item-detail" : "items-list",
            "posts" or "post" => hasId ? "post-detail" : "posts-list",
            "comments" or "comment" => hasId ? "comment-detail" : "comments-list",
            "auth" or "login" or "authenticate" => "auth-response",
            "search" => "search-results",
            "config" or "settings" => "config-response",
            "status" or "health" => "status-response",
            "api" => "api-index",
            _ => hasId ? $"{resourceName}-detail" : $"{resourceName}-list"
        };

        var confidence = resourceName switch
        {
            "users" or "products" or "orders" or "posts" => 0.75,
            "api" or "v1" or "v2" => 0.4,
            _ => 0.5
        };

        return (shape, confidence, $"REST pattern detected: {method} /{resourceName}");
    }

    private async Task<ShapeAnalysisResult?> AnalyzeWithLlmAsync(
        HttpContext context,
        ShapeAnalysisResult heuristicResult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.LlmEndpoint))
            return null;

        var prompt = BuildLlmPrompt(context, heuristicResult);

        try
        {
            var requestBody = new
            {
                model = _options.LlmModel,
                messages = new[]
                {
                    new { role = "system", content = GetSystemPrompt() },
                    new { role = "user", content = prompt }
                },
                temperature = 0.3,
                max_tokens = 200
            };

            var response = await _httpClient.PostAsJsonAsync(
                _options.LlmEndpoint,
                requestBody,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("LLM request failed with status {Status}", response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var content = result.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return ParseLlmResponse(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze with LLM");
            return null;
        }
    }

    private static string GetSystemPrompt()
    {
        return """
               You are an API analysis expert. Given request details, determine the most appropriate API simulation type.
               Respond with JSON only in this format:
               {"type":"RestJson|GraphQL|OpenApi|Xml|Html","shape":"shape-name","confidence":0.0-1.0,"reasoning":"why"}

               Types:
               - RestJson: Standard REST API returning JSON
               - GraphQL: GraphQL API (detect from path/query containing 'graphql' or query structure)
               - OpenApi: Use existing OpenAPI spec for realistic responses
               - Xml: SOAP/XML based API
               - Html: HTML pages (for web scrapers)

               Common shapes: users-list, user-detail, products-list, product-detail, search-results, auth-response, graphql, generic
               """;
    }

    private static string BuildLlmPrompt(HttpContext context, ShapeAnalysisResult heuristic)
    {
        var body = "";
        if (context.Request.ContentLength > 0 && context.Request.ContentLength < 2000)
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            body = reader.ReadToEnd();
            context.Request.Body.Position = 0;
        }

        return $"""
                Analyze this API request:
                Method: {context.Request.Method}
                Path: {context.Request.Path}
                Query: {context.Request.QueryString}
                Content-Type: {context.Request.ContentType}
                Accept: {context.Request.Headers.Accept}
                Body preview: {(body.Length > 500 ? body[..500] + "..." : body)}

                My heuristic guess: {heuristic.SimulationType} / {heuristic.Shape} (confidence: {heuristic.Confidence:F2})

                What API simulation type and shape should we use?
                """;
    }

    private ShapeAnalysisResult? ParseLlmResponse(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        try
        {
            // Try to extract JSON from response (it might be wrapped in markdown)
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0)
                return null;

            var json = content[jsonStart..(jsonEnd + 1)];
            var parsed = JsonSerializer.Deserialize<JsonElement>(json);

            var typeStr = parsed.GetProperty("type").GetString() ?? "RestJson";
            var shape = parsed.GetProperty("shape").GetString() ?? "generic";
            var confidence = parsed.TryGetProperty("confidence", out var confProp)
                ? confProp.GetDouble()
                : 0.7;
            var reasoning = parsed.TryGetProperty("reasoning", out var reasonProp)
                ? reasonProp.GetString()
                : null;

            var simType = Enum.TryParse<ApiSimulationType>(typeStr, true, out var parsed2)
                ? parsed2
                : ApiSimulationType.RestJson;

            return new ShapeAnalysisResult
            {
                SimulationType = simType,
                Shape = shape,
                ContentType = simType switch
                {
                    ApiSimulationType.Xml => "application/xml",
                    ApiSimulationType.Html => "text/html",
                    ApiSimulationType.PlainText => "text/plain",
                    _ => "application/json"
                },
                Confidence = confidence,
                Reasoning = $"LLM analysis: {reasoning}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response: {Content}", content);
            return null;
        }
    }
}

/// <summary>
///     Configuration for shape builder.
/// </summary>
public class ShapeBuilderOptions
{
    /// <summary>
    ///     Enable LLM-based analysis for uncertain requests.
    ///     Default: false (use heuristics only)
    /// </summary>
    public bool EnableLlmAnalysis { get; set; } = false;

    /// <summary>
    ///     LLM endpoint for shape analysis.
    ///     Example: http://localhost:11434/v1/chat/completions
    /// </summary>
    public string? LlmEndpoint { get; set; }

    /// <summary>
    ///     LLM model name for shape analysis.
    ///     Default: qwen3:0.6b
    /// </summary>
    public string LlmModel { get; set; } = "qwen3:0.6b";

    /// <summary>
    ///     Confidence threshold below which to use LLM analysis.
    ///     Default: 0.6
    /// </summary>
    public double LlmAnalysisThreshold { get; set; } = 0.6;

    /// <summary>
    ///     Custom OpenAPI spec URLs keyed by path pattern.
    ///     Example: { "myapi": "https://example.com/openapi.json" }
    /// </summary>
    public Dictionary<string, string> OpenApiSpecs { get; set; } = new();

    /// <summary>
    ///     Timeout for LLM requests in milliseconds.
    ///     Default: 5000
    /// </summary>
    public int LlmTimeoutMs { get; set; } = 5000;
}