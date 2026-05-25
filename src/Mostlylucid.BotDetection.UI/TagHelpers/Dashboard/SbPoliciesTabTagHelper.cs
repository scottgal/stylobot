using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

/// <summary>
///     <c>&lt;sb-policies-tab /&gt;</c> -- renders the
///     <see cref="ViewComponents.Dashboard.SbPoliciesTabViewComponent"/>.
///     Pure config view in this iteration; bucket-state + per-rule hit
///     counters land in a follow-up.
/// </summary>
[HtmlTargetElement("sb-policies-tab", TagStructure = TagStructure.WithoutEndTag)]
public class SbPoliciesTabTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(await vc.InvokeAsync("SbPoliciesTab"));
    }
}
