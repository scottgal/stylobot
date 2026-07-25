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

    /// <summary>
    ///     When true, render the compact WARMING strip (daisyUI spinner + text) instead
    ///     of the content. Takes precedence over <see cref="EmptyWhen"/> so a cold-cache
    ///     miss shows "still warming" rather than the honest "no data" state -- the same
    ///     three-state contract (data / warming / empty) the list widgets (SbCountriesList,
    ///     SbSummaryStats) already render. This exists so a chart never paints a bare
    ///     Chart.js canvas on a cold cache: the warming strip replaces the canvas, and the
    ///     freshness beacon OOB-replaces the widget once the tick materializer warms it.
    /// </summary>
    [HtmlAttributeName("warming-when")] public bool WarmingWhen { get; set; }

    /// <summary>Text for the warming strip (rendered beside the spinner).</summary>
    [HtmlAttributeName("warming-text")] public string? WarmingText { get; set; }

    /// <summary>
    ///     Optional freshness surface key(s) stamped as <c>data-sb-depends</c> on the outer
    ///     element so the SignalR freshness beacon can OOB-replace this widget when the
    ///     underlying surface warms (matches the <c>data-sb-depends</c> convention on the
    ///     list widgets). Null (default) omits the attribute -- existing callers unaffected.
    /// </summary>
    [HtmlAttributeName("depends")] public string? Depends { get; set; }

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

        // Freshness surface marker so the SignalR beacon can OOB-replace this widget
        // once the tick materializer warms it (matches the list-widget convention).
        if (!string.IsNullOrWhiteSpace(Depends))
            output.Attributes.SetAttribute("data-sb-depends", Depends);

        var head = string.IsNullOrEmpty(Heading)
            ? string.Empty
            : $"<div class=\"sb-widget-head\">{enc.Encode(Heading)}</div>";

        if (WarmingWhen)
        {
            // Cold-cache miss: collapse to a compact WARMING strip (spinner + text) so a
            // chart never paints a bare canvas. Precedence over EmptyWhen -- "still warming"
            // is a truer signal than "no data" until the bundle is actually composed.
            output.Content.SetHtmlContent(
                $"<div class=\"card bg-base-100 border border-base-300\"><div class=\"card-body p-3\">{head}" +
                "<div class=\"sb-widget-empty\">" +
                "<span class=\"loading loading-spinner loading-sm\"></span>" +
                $"<span>{enc.Encode(WarmingText ?? "Warming up — data will appear shortly.")}</span>" +
                "</div></div></div>");
        }
        else if (EmptyWhen)
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
