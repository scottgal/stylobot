using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-threats-list", TagStructure = TagStructure.WithoutEndTag)]
public class SbThreatsListTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("page")]
    public int Page { get; set; } = 1;

    [HtmlAttributeName("page-size")]
    public int PageSize { get; set; } = 20;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(await vc.InvokeAsync("SbThreatsList", new { page = Page, pageSize = PageSize }));
    }
}
