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

    /// <summary>
    ///     View mode: "default" for full display, "compact" for detection bar
    /// </summary>
    [HtmlAttributeName("view")]
    public string View { get; set; } = "default";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (_viewComponentHelper as IViewContextAware)?.Contextualize(ViewContext);

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;

        // Add CSS class
        var cssClasses = View == "compact" ? "stylobot-detection-bar-container" : "bot-detection-details";
        if (!string.IsNullOrEmpty(CssClass)) cssClasses += $" {CssClass}";
        if (Collapsed) cssClasses += " collapsed";

        output.Attributes.SetAttribute("class", cssClasses);

        // Render the view component with the view name
        var viewName = View == "compact" ? "Compact" : "Default";
        var content = await _viewComponentHelper.InvokeAsync("BotDetectionDetails", new { viewName });
        output.Content.SetHtmlContent(content);
    }
}