using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Unified bot detection header tag helper - single slim bar with detection status + live ticker.
///     Usage: &lt;bot-detection-header /&gt;
/// </summary>
[HtmlTargetElement("bot-detection-header")]
public class BotDetectionHeaderTagHelper : TagHelper
{
    private readonly IViewComponentHelper _viewComponentHelper;

    public BotDetectionHeaderTagHelper(IViewComponentHelper viewComponentHelper)
    {
        _viewComponentHelper = viewComponentHelper;
    }

    [ViewContext] [HtmlAttributeNotBound] public ViewContext? ViewContext { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (_viewComponentHelper as IViewContextAware)?.Contextualize(ViewContext);

        output.TagName = null;
        output.TagMode = TagMode.StartTagAndEndTag;

        var content = await _viewComponentHelper.InvokeAsync("BotDetectionHeader");
        output.Content.SetHtmlContent(content);
    }
}
