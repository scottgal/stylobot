using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Tag helper for rendering the real-time bot detection ticker.
///     Usage: &lt;bot-ticker /&gt;
/// </summary>
[HtmlTargetElement("bot-ticker")]
public class BotTickerTagHelper : TagHelper
{
    private readonly IViewComponentHelper _viewComponentHelper;

    public BotTickerTagHelper(IViewComponentHelper viewComponentHelper)
    {
        _viewComponentHelper = viewComponentHelper;
    }

    [ViewContext] [HtmlAttributeNotBound] public ViewContext? ViewContext { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (_viewComponentHelper as IViewContextAware)?.Contextualize(ViewContext);

        output.TagName = null; // Don't render wrapper tag
        output.TagMode = TagMode.StartTagAndEndTag;

        var content = await _viewComponentHelper.InvokeAsync("BotTicker");
        output.Content.SetHtmlContent(content);
    }
}
