using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Tag helper for rendering bot detection details.
///     Usage: &lt;bot-detection-details /&gt;
/// </summary>
[HtmlTargetElement("bot-detection-details")]
public class BotDetectionDetailsTagHelper : TagHelper
{
    private readonly IViewComponentHelper _viewComponentHelper;

    public BotDetectionDetailsTagHelper(IViewComponentHelper viewComponentHelper)
    {
        _viewComponentHelper = viewComponentHelper;
    }

    [ViewContext] [HtmlAttributeNotBound] public ViewContext? ViewContext { get; set; }

    /// <summary>
    ///     CSS class to apply to the container
    /// </summary>
    [HtmlAttributeName("class")]
    public string? CssClass { get; set; }

    /// <summary>
    ///     Whether to show the details in collapsed state initially
    /// </summary>
    [HtmlAttributeName("collapsed")]
    public bool Collapsed { get; set; } = false;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (_viewComponentHelper as IViewContextAware)?.Contextualize(ViewContext);

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;

        // Add CSS class
        var cssClasses = "bot-detection-details";
        if (!string.IsNullOrEmpty(CssClass)) cssClasses += $" {CssClass}";
        if (Collapsed) cssClasses += " collapsed";

        output.Attributes.SetAttribute("class", cssClasses);

        // Render the view component
        var content = await _viewComponentHelper.InvokeAsync("BotDetectionDetails");
        output.Content.SetHtmlContent(content);
    }
}