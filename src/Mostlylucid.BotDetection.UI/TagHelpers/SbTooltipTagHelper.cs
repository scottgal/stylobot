using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Attribute tag helper that decorates any element with a daisyUI tooltip.
///
///     Call site: <c>&lt;div class="rounded-lg border p-3" sb-tooltip="stat.probability"&gt;...&lt;/div&gt;</c>.
///
///     When <see cref="StyloBotDashboardOptions.EnableTooltips"/> is true and
///     <c>sb-tooltip</c> resolves to a known key in
///     <see cref="DashboardTooltipRegistry"/>, the helper:
///       1. Appends <c>tooltip tooltip-bottom</c> to the element's existing
///          <c>class</c> attribute (preserving operator-set classes — fixes the
///          <c>feedback_no_belt_and_braces</c> failure where emitting a second
///          <c>class</c> attribute produced two on one element).
///       2. Sets <c>data-tip</c> to the registry-resolved text.
///       3. Removes the <c>sb-tooltip</c> directive so the rendered HTML is clean.
///
///     When the option is off or the key is unknown, only step 3 happens — the
///     element renders unchanged so disabled tooltips leave no trace.
///     One rendering path; no native <c>title=</c> fallback.
/// </summary>
[HtmlTargetElement("*", Attributes = AttributeName)]
public sealed class SbTooltipTagHelper : TagHelper
{
    private const string AttributeName = "sb-tooltip";
    private readonly StyloBotDashboardOptions _options;
    private readonly DashboardTooltipRegistry _registry;

    public SbTooltipTagHelper(StyloBotDashboardOptions options, DashboardTooltipRegistry registry)
    {
        _options = options;
        _registry = registry;
    }

    /// <summary>The tooltip key to look up in the YAML inventory.</summary>
    [HtmlAttributeName(AttributeName)]
    public string? Key { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        // Always strip the directive from the rendered output, regardless of the
        // outcome below — sb-tooltip is our marker, not a real HTML attribute.
        output.Attributes.RemoveAll(AttributeName);

        if (!_options.EnableTooltips) return;
        if (string.IsNullOrEmpty(Key)) return;
        if (!_registry.TryGet(Key, out var text)) return;

        // Merge daisyUI's classes into the existing class attribute so we do not
        // produce two `class` attributes on the same element (which the browser
        // silently keeps just one of). The TagHelperOutput class attribute is
        // stored as a single concatenated string; we read, append, and put it
        // back.
        var existing = output.Attributes["class"]?.Value?.ToString();
        var merged = string.IsNullOrEmpty(existing)
            ? "tooltip tooltip-bottom"
            : existing + " tooltip tooltip-bottom";
        output.Attributes.SetAttribute("class", merged);
        output.Attributes.SetAttribute("data-tip", text);
    }
}
