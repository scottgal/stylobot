using StyloExtract.Abstractions;

namespace Mostlylucid.BotDetection.StyloExtract.Options;

/// <summary>
/// Per-policy configuration for the StyloExtract action policies.
/// Bind from <c>StyloExtract:Actions:{policyName}</c> in appsettings.json.
/// </summary>
/// <remarks>
/// Example:
/// <code>
/// {
///   "StyloExtract": {
///     "Actions": {
///       "extract-markdown": {
///         "Profile": "RagFull",
///         "EnableQueryOverride": true,
///         "QueryParamName": "format",
///         "QueryParamValue": "markdown",
///         "Cache": {
///           "Mode": "Override",
///           "MaxAge": 86400,
///           "Public": true,
///           "VaryByBotType": true
///         }
///       }
///     }
///   }
/// }
/// </code>
/// </remarks>
public sealed class StyloExtractActionOptions
{
    /// <summary>
    /// Extraction profile controlling which content is included in the Markdown output.
    /// Default: RagFull.
    /// </summary>
    public ExtractionProfile Profile { get; set; } = ExtractionProfile.RagFull;

    /// <summary>
    /// When true, any request with the configured query parameter returns the Markdown form
    /// regardless of whether StyloBot's bot-type matcher triggered. Useful for demos and
    /// debugging. Default: true.
    /// </summary>
    public bool EnableQueryOverride { get; set; } = true;

    /// <summary>Name of the query parameter that triggers the query override. Default: "format".</summary>
    public string QueryParamName { get; set; } = "format";

    /// <summary>Value of the query parameter that triggers the query override. Default: "markdown".</summary>
    public string QueryParamValue { get; set; } = "markdown";

    /// <summary>
    /// Cache-Control behaviour applied to the transformed response.
    /// Default: Mode = Respect (leave Cache-Control untouched).
    /// </summary>
    public CacheOverrideOptions Cache { get; set; } = new();

    /// <summary>
    /// Route template used by the <c>extract-sidecar</c> policy to build the Link header.
    /// <c>{path}</c> interpolates the full request path (without leading slash).
    /// <c>{slug}</c> interpolates the last path segment.
    /// Default: "/{path}.md"
    /// </summary>
    public string SidecarRouteTemplate { get; set; } = "/{path}.md";
}
