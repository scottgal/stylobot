using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Renders a colored risk band pill. No ViewComponent needed — outputs a single span.
/// </summary>
/// <example>
///     <code>&lt;sb-risk-pill /&gt;</code>
///     Output: <c>&lt;span class="sb-risk-pill sb-risk-pill--low" data-sb-risk="Low"&gt;Low&lt;/span&gt;</c>
/// </example>
[HtmlTargetElement("sb-risk-pill", TagStructure = TagStructure.WithoutEndTag)]
public class SbRiskPillTagHelper : SbTagHelperBase
{
    public SbRiskPillTagHelper(IHttpContextAccessor httpContextAccessor, DetectionDataExtractor extractor)
        : base(httpContextAccessor, extractor)
    {
    }

    /// <summary>Additional CSS classes.</summary>
    [HtmlAttributeName("class")]
    public string? CssClass { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var model = GetModel();
        var riskBand = model.HasData ? model.RiskBand : "Unknown";
        var riskLower = riskBand.ToLowerInvariant();

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;

        var classes = $"sb-risk-pill sb-risk-pill--{riskLower}";
        if (!string.IsNullOrWhiteSpace(CssClass))
            classes += $" {CssClass}";

        output.Attributes.SetAttribute("class", classes);
        output.Attributes.SetAttribute("data-sb-risk", riskBand);
        output.Content.SetContent(riskBand);
    }
}
