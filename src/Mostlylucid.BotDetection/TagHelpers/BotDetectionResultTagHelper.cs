using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.TagHelpers;

/// <summary>
///     Outputs the bot detection result as JSON for client-side JavaScript consumption.
///     Renders a script tag with the detection result in a global variable.
/// </summary>
/// <example>
///     &lt;bot-detection-result /&gt;
///     Renders:
///     &lt;script&gt;
///     window.__botDetection = {
///     risk: 0.15,
///     confidence: 0.92,
///     riskBand: "Low",
///     policy: "default",
///     isBot: false,
///     detectors: ["UserAgent", "Header", "Ip"],
///     categories: { "UserAgent": -0.2, "Header": -0.1 }
///     };
///     &lt;/script&gt;
///     Or with custom variable name:
///     &lt;bot-detection-result variable-name="botResult" /&gt;
/// </example>
[HtmlTargetElement("bot-detection-result")]
public class BotDetectionResultTagHelper : TagHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpContextAccessor _httpContextAccessor;

    public BotDetectionResultTagHelper(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    ///     Name of the JavaScript variable to store the result.
    ///     Default: "__botDetection"
    /// </summary>
    [HtmlAttributeName("variable-name")]
    public string VariableName { get; set; } = "__botDetection";

    /// <summary>
    ///     Whether to output the full result including all contributions.
    ///     Default: false (outputs summary only)
    /// </summary>
    [HtmlAttributeName("full")]
    public bool FullResult { get; set; } = false;

    /// <summary>
    ///     Whether to include the script tag wrapper.
    ///     Default: true
    /// </summary>
    [HtmlAttributeName("include-script-tag")]
    public bool IncludeScriptTag { get; set; } = true;

    /// <summary>
    ///     Custom data attribute prefix for data-* attributes.
    ///     If set, outputs data attributes instead of script tag.
    ///     Example: "bot" outputs data-bot-risk="0.15", data-bot-policy="default", etc.
    /// </summary>
    [HtmlAttributeName("output-data-prefix")]
    public string? DataPrefix { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            output.SuppressOutput();
            return;
        }

        // Try to get aggregated evidence first (new architecture)
        var aggregated = httpContext.Items[BotDetectionMiddleware.AggregatedEvidenceKey] as AggregatedEvidence;
        var legacy = httpContext.Items[BotDetectionMiddleware.BotDetectionResultKey] as BotDetectionResult;
        var policyName = httpContext.Items[BotDetectionMiddleware.PolicyNameKey] as string ?? "unknown";
        var policyAction = httpContext.Items[BotDetectionMiddleware.PolicyActionKey] as PolicyAction?;

        object resultObject;

        if (aggregated != null)
            resultObject = CreateResultFromAggregated(aggregated, policyName, policyAction);
        else if (legacy != null)
            resultObject = CreateResultFromLegacy(legacy, policyName);
        else
            // No detection result available
            resultObject = new { error = "No bot detection result available", detected = false };

        // Output as data attributes
        if (!string.IsNullOrEmpty(DataPrefix))
        {
            OutputDataAttributes(output, resultObject, DataPrefix);
            return;
        }

        // Output as script tag
        var json = JsonSerializer.Serialize(resultObject, JsonOptions);

        if (IncludeScriptTag)
        {
            output.TagName = "script";
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Content.SetHtmlContent($"window.{VariableName} = {json};");
        }
        else
        {
            output.TagName = null;
            output.Content.SetHtmlContent($"window.{VariableName} = {json};");
        }
    }

    private object CreateResultFromAggregated(
        AggregatedEvidence evidence,
        string policyName,
        PolicyAction? policyAction)
    {
        if (FullResult)
            return new
            {
                risk = evidence.BotProbability,
                confidence = evidence.Confidence,
                riskBand = evidence.RiskBand.ToString(),
                policy = policyName,
                action = policyAction?.ToString(),
                isBot = evidence.BotProbability >= 0.5,
                earlyExit = evidence.EarlyExit,
                verdict = evidence.EarlyExitVerdict?.ToString(),
                detectors = evidence.ContributingDetectors,
                failedDetectors = evidence.FailedDetectors,
                categories = evidence.CategoryBreakdown.ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        score = kv.Value.Score,
                        weight = kv.Value.TotalWeight,
                        contributions = kv.Value.ContributionCount
                    }),
                contributions = evidence.Contributions.Select(c => new
                {
                    detector = c.DetectorName,
                    category = c.Category,
                    delta = c.ConfidenceDelta,
                    weight = c.Weight,
                    reason = c.Reason
                }),
                processingMs = evidence.TotalProcessingTimeMs,
                botName = evidence.PrimaryBotName,
                botType = evidence.PrimaryBotType?.ToString()
            };

        // Summary only
        return new
        {
            risk = evidence.BotProbability,
            confidence = evidence.Confidence,
            riskBand = evidence.RiskBand.ToString(),
            policy = policyName,
            action = policyAction?.ToString(),
            isBot = evidence.BotProbability >= 0.5,
            detectors = evidence.ContributingDetectors,
            categories = evidence.CategoryBreakdown.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Score)
        };
    }

    private object CreateResultFromLegacy(BotDetectionResult result, string policyName)
    {
        if (FullResult)
            return new
            {
                risk = result.ConfidenceScore,
                confidence = result.ConfidenceScore,
                policy = policyName,
                isBot = result.IsBot,
                botType = result.BotType?.ToString(),
                botName = result.BotName,
                reasons = result.Reasons.Select(r => new
                {
                    category = r.Category,
                    detail = r.Detail,
                    impact = r.ConfidenceImpact
                })
            };

        return new
        {
            risk = result.ConfidenceScore,
            policy = policyName,
            isBot = result.IsBot,
            botType = result.BotType?.ToString(),
            botName = result.BotName
        };
    }

    private static void OutputDataAttributes(
        TagHelperOutput output,
        object result,
        string prefix)
    {
        output.TagName = "div";

        var json = JsonSerializer.Serialize(result, JsonOptions);
        using var doc = JsonDocument.Parse(json);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var attrName = $"data-{prefix}-{ToKebabCase(prop.Name)}";
            var attrValue = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetDouble().ToString("F3"),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                JsonValueKind.Array => string.Join(",", prop.Value.EnumerateArray().Select(e => e.ToString())),
                _ => prop.Value.GetRawText()
            };

            output.Attributes.SetAttribute(attrName, attrValue);
        }
    }

    private static string ToKebabCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var result = new StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c))
            {
                if (i > 0) result.Append('-');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}