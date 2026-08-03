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
    /// Process-local cache for a transformed representation. This is deliberately separate
    /// from <see cref="Cache"/>, which only controls outgoing HTTP cache headers.
    /// </summary>
    public TransformedContentCacheOptions TransformedContentCache { get; set; } = new();

    /// <summary>
    /// Route template used by the <c>extract-sidecar</c> policy to build the Link header.
    /// <c>{path}</c> interpolates the full request path (without leading slash).
    /// <c>{slug}</c> interpolates the last path segment.
    /// Default: "/{path}.md"
    /// </summary>
    public string SidecarRouteTemplate { get; set; } = "/{path}.md";
}

internal static class StyloExtractActionOptionsCacheExtensions
{
    internal static string VersionSaltForCache(this StyloExtractActionOptions options) =>
        string.IsNullOrWhiteSpace(options.TransformedContentCache.VersionSalt)
            ? "v1"
            : options.TransformedContentCache.VersionSalt;
}

/// <summary>Hard bounds for the in-process Markdown representation cache.</summary>
public sealed class TransformedContentCacheOptions
{
    public bool Enabled { get; set; }
    public int MaxEntries { get; set; } = 128;
    public int MaxEntryBytes { get; set; } = 256 * 1024;
    public int MaxTotalBytes { get; set; } = 32 * 1024 * 1024;
    public TimeSpan SlidingExpiration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan AbsoluteExpiration { get; set; } = TimeSpan.FromMinutes(15);
    public string VersionSalt { get; set; } = "v1";

    /// <summary>
    ///     Query parameter names to include in the cache key (case-insensitive).
    ///     Any param not in this set is dropped from the key, preventing cache
    ///     fragmentation from tracking parameters, random seeds, etc.
    ///     Default: empty (no query variance).
    /// </summary>
    public HashSet<string> AllowedQueryKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
