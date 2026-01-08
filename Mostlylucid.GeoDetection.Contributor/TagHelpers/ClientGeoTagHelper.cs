using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;

namespace Mostlylucid.GeoDetection.Contributor.TagHelpers;

/// <summary>
///     Tag helper that injects client-side geolocation collection script.
///     Uses browser Geolocation API to get client coordinates and timezone.
///
///     Usage:
///     <![CDATA[
///     <client-geo />
///     <!-- or with options -->
///     <client-geo endpoint="/api/v1/client-geo" defer="true" />
///     ]]>
/// </summary>
[HtmlTargetElement("client-geo")]
public class ClientGeoTagHelper : TagHelper
{
    private readonly GeoContributorOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ClientGeoTagHelper(
        IOptions<GeoContributorOptions> options,
        IHttpContextAccessor httpContextAccessor)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    ///     The endpoint to post geo data to.
    ///     Default: "/api/v1/client-geo"
    /// </summary>
    [HtmlAttributeName("endpoint")]
    public string Endpoint { get; set; } = "/api/v1/client-geo";

    /// <summary>
    ///     Whether to defer script execution.
    ///     Default: true
    /// </summary>
    [HtmlAttributeName("defer")]
    public bool Defer { get; set; } = true;

    /// <summary>
    ///     Custom nonce for CSP compliance.
    /// </summary>
    [HtmlAttributeName("nonce")]
    public string? Nonce { get; set; }

    /// <summary>
    ///     Whether to request high accuracy location.
    ///     Default: false (faster, less battery drain)
    /// </summary>
    [HtmlAttributeName("high-accuracy")]
    public bool HighAccuracy { get; set; } = false;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!_options.EnableClientGeo)
        {
            output.SuppressOutput();
            return;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            output.SuppressOutput();
            return;
        }

        // Generate session ID for tracking
        var sessionId = httpContext.TraceIdentifier;

        output.TagName = "script";
        output.TagMode = TagMode.StartTagAndEndTag;

        if (Defer)
        {
            output.Attributes.Add("defer", null);
        }

        if (!string.IsNullOrEmpty(Nonce))
        {
            output.Attributes.Add("nonce", Nonce);
        }

        output.Attributes.Add("type", "text/javascript");
        output.Attributes.Add("src", "/_content/Mostlylucid.GeoDetection.Contributor/client-geo.js");

        // Pass configuration via data attributes that the script will read
        output.Attributes.Add("data-endpoint", Endpoint);
        output.Attributes.Add("data-session-id", sessionId);
        output.Attributes.Add("data-high-accuracy", HighAccuracy.ToString().ToLowerInvariant());
    }
}
