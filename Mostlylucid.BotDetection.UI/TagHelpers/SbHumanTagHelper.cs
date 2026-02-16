using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Shorthand for <c>&lt;sb-gate human-only&gt;</c>. Shows content only to humans.
///     Default fallback is "show" (fail-open when detection hasn't run).
/// </summary>
[HtmlTargetElement("sb-human")]
public class SbHumanTagHelper : SbTagHelperBase
{
    public SbHumanTagHelper(IHttpContextAccessor httpContextAccessor, DetectionDataExtractor extractor)
        : base(httpContextAccessor, extractor)
    {
    }

    /// <summary>"show" (default) or "hide" when detection hasn't run.</summary>
    [HtmlAttributeName("fallback")]
    public string Fallback { get; set; } = "show";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;

        var model = GetModel();

        if (!model.HasData)
        {
            if (string.Equals(Fallback, "hide", StringComparison.OrdinalIgnoreCase))
                output.SuppressOutput();
            return;
        }

        if (model.IsBot)
            output.SuppressOutput();
    }
}

/// <summary>
///     Shorthand for <c>&lt;sb-gate bot-only&gt;</c>. Shows content only to bots.
///     Default fallback is "hide" (fail-closed when detection hasn't run).
/// </summary>
[HtmlTargetElement("sb-bot")]
public class SbBotTagHelper : SbTagHelperBase
{
    public SbBotTagHelper(IHttpContextAccessor httpContextAccessor, DetectionDataExtractor extractor)
        : base(httpContextAccessor, extractor)
    {
    }

    /// <summary>"show" or "hide" (default) when detection hasn't run.</summary>
    [HtmlAttributeName("fallback")]
    public string Fallback { get; set; } = "hide";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;

        var model = GetModel();

        if (!model.HasData)
        {
            if (string.Equals(Fallback, "hide", StringComparison.OrdinalIgnoreCase))
                output.SuppressOutput();
            return;
        }

        if (!model.IsBot)
            output.SuppressOutput();
    }
}
