using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-useragents-list", TagStructure = TagStructure.WithoutEndTag)]
public class SbUserAgentsListTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("filter")]
    public string Filter { get; set; } = "all";

    [HtmlAttributeName("sort")]
    public string Sort { get; set; } = "requests";

    [HtmlAttributeName("dir")]
    public string Dir { get; set; } = "desc";

    [HtmlAttributeName("page")]
    public int Page { get; set; } = 1;

    [HtmlAttributeName("page-size")]
    public int PageSize { get; set; } = 25;

    [HtmlAttributeName("audience")]
    public string? Audience { get; set; }

    [HtmlAttributeName("range")]
    public string? Range { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(await vc.InvokeAsync("SbUserAgentsList", new { filter = Filter, sort = Sort, dir = Dir, page = Page, pageSize = PageSize, audience = Audience, range = Range }));
    }
}
