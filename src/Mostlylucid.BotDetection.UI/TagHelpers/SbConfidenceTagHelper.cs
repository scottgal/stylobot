using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Renders a bot probability confidence meter via ViewComponent.
/// </summary>
/// <example>
///     <code>&lt;sb-confidence /&gt;</code>
///     <code>&lt;sb-confidence display="both" width="200px" /&gt;</code>
/// </example>
[HtmlTargetElement("sb-confidence", TagStructure = TagStructure.WithoutEndTag)]
public class SbConfidenceTagHelper : TagHelper
{
    private readonly IViewComponentHelper _viewComponentHelper;

    public SbConfidenceTagHelper(IViewComponentHelper viewComponentHelper)
    {
        _viewComponentHelper = viewComponentHelper;
    }

    [ViewContext] [HtmlAttributeNotBound] public ViewContext? ViewContext { get; set; }

    /// <summary>"bar" (default), "text", or "both".</summary>
    [HtmlAttributeName("display")]
    public string Display { get; set; } = "bar";

    /// <summary>CSS width for the bar (default "120px").</summary>
    [HtmlAttributeName("width")]
    public string Width { get; set; } = "120px";

    /// <summary>Additional CSS classes.</summary>
    [HtmlAttributeName("class")]
    public string? CssClass { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (_viewComponentHelper as IViewContextAware)?.Contextualize(ViewContext);

        output.TagName = null;
        var content = await _viewComponentHelper.InvokeAsync("SbConfidence",
            new { display = Display, width = Width, cssClass = CssClass });
        output.Content.SetHtmlContent(content);
    }
}
