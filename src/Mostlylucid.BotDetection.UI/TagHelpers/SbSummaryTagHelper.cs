using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Renders a mini detection summary via ViewComponent.
/// </summary>
/// <example>
///     <code>&lt;sb-summary /&gt;</code>
///     <code>&lt;sb-summary variant="card" /&gt;</code>
/// </example>
[HtmlTargetElement("sb-summary", TagStructure = TagStructure.WithoutEndTag)]
public class SbSummaryTagHelper : TagHelper
{
    private readonly IViewComponentHelper _viewComponentHelper;

    public SbSummaryTagHelper(IViewComponentHelper viewComponentHelper)
    {
        _viewComponentHelper = viewComponentHelper;
    }

    [ViewContext] [HtmlAttributeNotBound] public ViewContext? ViewContext { get; set; }

    /// <summary>"inline" (default) or "card".</summary>
    [HtmlAttributeName("variant")]
    public string Variant { get; set; } = "inline";

    /// <summary>Additional CSS classes.</summary>
    [HtmlAttributeName("class")]
    public string? CssClass { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (_viewComponentHelper as IViewContextAware)?.Contextualize(ViewContext);

        output.TagName = null;
        var viewName = string.Equals(Variant, "card", StringComparison.OrdinalIgnoreCase) ? "Card" : "Default";
        var content = await _viewComponentHelper.InvokeAsync("SbSummary",
            new { viewName, cssClass = CssClass });
        output.Content.SetHtmlContent(content);
    }
}
