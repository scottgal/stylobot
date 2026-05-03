using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Content gate based on risk band. Shows or hides children based on the current request's risk level.
/// </summary>
/// <example>
///     <code>&lt;sb-risk max="Low"&gt;Normal access&lt;/sb-risk&gt;</code>
///     <code>&lt;sb-risk min="High" fallback="hide"&gt;Restricted&lt;/sb-risk&gt;</code>
/// </example>
[HtmlTargetElement("sb-risk")]
public class SbRiskTagHelper : SbTagHelperBase
{
    public SbRiskTagHelper(IHttpContextAccessor httpContextAccessor, DetectionDataExtractor extractor)
        : base(httpContextAccessor, extractor)
    {
    }

    /// <summary>Exact risk band match (VeryLow, Low, Elevated, Medium, High, VeryHigh).</summary>
    [HtmlAttributeName("band")]
    public string? Band { get; set; }

    /// <summary>Show when risk is at or above this band.</summary>
    [HtmlAttributeName("min")]
    public string? Min { get; set; }

    /// <summary>Show when risk is at or below this band.</summary>
    [HtmlAttributeName("max")]
    public string? Max { get; set; }

    /// <summary>"show" (default) or "hide" when detection hasn't run.</summary>
    [HtmlAttributeName("fallback")]
    public string Fallback { get; set; } = "show";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;

        var model = GetModel();

        if (!model.HasData)
        {
            if (string.Equals(Fallback, "hide", StringComparison.OrdinalIgnoreCase))
                output.SuppressOutput();
            return;
        }

        var current = ParseRiskBandFromModel(model.RiskBand);

        if (!string.IsNullOrWhiteSpace(Band))
        {
            var exact = ParseRiskBand(Band);
            if (current != exact)
            {
                output.SuppressOutput();
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(Min))
        {
            var minBand = ParseRiskBand(Min);
            if (current < minBand)
            {
                output.SuppressOutput();
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(Max))
        {
            var maxBand = ParseRiskBand(Max);
            if (current > maxBand)
            {
                output.SuppressOutput();
                return;
            }
        }
    }
}
