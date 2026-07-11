using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

/// <summary>
///     Uniform widget shell for the dashboard sizing system. Wraps child content in
///     the standard card chrome and declares a WIDTH span + HEIGHT tier so the
///     enclosing <c>.sb-widget-grid</c> dense-packs widgets instead of stretching a
///     short widget to a tall neighbour (the site-health whitespace problem). When
///     <c>empty-when</c> is true it renders a compact one-line empty-state strip
///     instead of a full card, so a no-data widget collapses.
///     <para>
///     Use for content that does NOT already render its own <c>.card</c>. Widgets that
///     self-card (view components like <c>sb-site-health</c>) go straight into the grid
///     wrapped in a tier <c>&lt;div class="sb-w-… sb-h-…"&gt;</c> instead.
///     </para>
///     <example>
///     <code>
///     &lt;div class="sb-widget-grid"&gt;
///       &lt;sb-widget width="half" height="tall" heading="Hits per period"&gt;
///         &lt;vc:sb-chartlet model="hitsChart" /&gt;
///       &lt;/sb-widget&gt;
///       &lt;sb-widget width="half" height="quarter" heading="Site health"
///                  empty-when="true" empty-text="Upstream healthy — no incidents" /&gt;
///     &lt;/div&gt;
///     </code>
///     </example>
/// </summary>
[HtmlTargetElement("sb-widget")]
public sealed class SbWidgetTagHelper : TagHelper
{
    /// <summary>quarter | third | half | 2third | full (12-col span). Default full.</summary>
    [HtmlAttributeName("width")] public string Width { get; set; } = "full";

    /// <summary>quarter | half | tall | full (row-tier height). Default tall.</summary>
    [HtmlAttributeName("height")] public string Height { get; set; } = "tall";

    /// <summary>Optional heading rendered muted above the content.</summary>
    [HtmlAttributeName("heading")] public string? Heading { get; set; }

    /// <summary>When true, render the compact empty-state strip instead of the content.</summary>
    [HtmlAttributeName("empty-when")] public bool EmptyWhen { get; set; }

    /// <summary>Text for the empty-state strip.</summary>
    [HtmlAttributeName("empty-text")] public string? EmptyText { get; set; }

    /// <summary>Extra classes appended to the outer tier element.</summary>
    [HtmlAttributeName("class")] public string? ExtraClass { get; set; }

    private static string WidthClass(string w) => w switch
    {
        "quarter" => "sb-w-quarter",
        "third" => "sb-w-third",
        "half" => "sb-w-half",
        "2third" => "sb-w-2third",
        _ => "sb-w-full",
    };

    private static string HeightClass(string h) => h switch
    {
        "quarter" => "sb-h-quarter",
        "half" => "sb-h-half",
        "full" => "sb-h-full",
        _ => "sb-h-tall",
    };

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var enc = HtmlEncoder.Default;
        output.TagName = "div";
        var cls = $"sb-widget {WidthClass(Width)} {HeightClass(Height)}";
        if (!string.IsNullOrWhiteSpace(ExtraClass)) cls += " " + ExtraClass;
        output.Attributes.SetAttribute("class", cls);

        var head = string.IsNullOrEmpty(Heading)
            ? string.Empty
            : $"<div class=\"sb-widget-head\">{enc.Encode(Heading)}</div>";

        if (EmptyWhen)
        {
            // Collapse to a compact strip — no full empty card.
            output.Content.SetHtmlContent(
                $"<div class=\"card bg-base-100 border border-base-300\"><div class=\"card-body p-3\">{head}" +
                $"<div class=\"sb-widget-empty\">{enc.Encode(EmptyText ?? "No data")}</div></div></div>");
        }
        else
        {
            // Wrap the child content (kept in output.Content by default) in the card chrome.
            output.PreContent.SetHtmlContent(
                $"<div class=\"card bg-base-100 border border-base-300\"><div class=\"card-body p-3\">{head}");
            output.PostContent.SetHtmlContent("</div></div>");
        }
    }
}
