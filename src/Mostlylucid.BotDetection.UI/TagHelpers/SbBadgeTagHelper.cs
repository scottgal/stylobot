using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Renders an inline detection badge (icon + label + risk) via ViewComponent.
/// </summary>
/// <example>
///     <code>&lt;sb-badge /&gt;</code>
///     <code>&lt;sb-badge variant="compact" /&gt;</code>
/// </example>
[HtmlTargetElement("sb-badge", TagStructure = TagStructure.WithoutEndTag)]
public class SbBadgeTagHelper : TagHelper
{
    private readonly IViewComponentHelper _viewComponentHelper;

    public SbBadgeTagHelper(IViewComponentHelper viewComponentHelper)
    {
        _viewComponentHelper = viewComponentHelper;
    }

    [ViewContext] [HtmlAttributeNotBound] public ViewContext? ViewContext { get; set; }

    /// <summary>"full" (default), "compact", or "icon".</summary>
    [HtmlAttributeName("variant")]
    public string Variant { get; set; } = "full";

    /// <summary>Additional CSS classes.</summary>
    [HtmlAttributeName("class")]
    public string? CssClass { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (_viewComponentHelper as IViewContextAware)?.Contextualize(ViewContext);

        output.TagName = null;
        var content = await _viewComponentHelper.InvokeAsync("SbBadge", new { variant = Variant, cssClass = CssClass });
        output.Content.SetHtmlContent(content);
    }
}
