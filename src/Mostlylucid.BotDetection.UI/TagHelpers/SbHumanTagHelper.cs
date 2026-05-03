using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Shorthand for <c>&lt;sb-gate human-only&gt;</c>. Shows content only to non-bot visitors.
///     Default fallback is "show" (fail-open: shows content when detection has not run).
///     <para>
///     Equivalent to <c>&lt;sb-gate human-only fallback="show"&gt;</c>.
///     Use <c>&lt;sb-gate&gt;</c> directly when you also need bot-type filtering, risk band checks,
///     or negate behaviour. This shorthand is provided purely for readability.
///     </para>
///     <para>
///     Works transparently in all three detection modes: inline middleware, YARP gateway headers,
///     and API mode (when <c>SbDetectionMiddleware</c> is in the pipeline).
///     </para>
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
///     Shorthand for <c>&lt;sb-gate bot-only&gt;</c>. Shows content only to detected bots.
///     Default fallback is "hide" (fail-closed: hides content when detection has not run).
///     <para>
///     Equivalent to <c>&lt;sb-gate bot-only fallback="hide"&gt;</c>.
///     The fail-closed default differs from <c>&lt;sb-gate bot-only&gt;</c> which defaults to
///     fallback="show". Use this tag when you want strict suppression on unevaluated requests.
///     </para>
///     <para>
///     Works transparently in all three detection modes: inline middleware, YARP gateway headers,
///     and API mode (when <c>SbDetectionMiddleware</c> is in the pipeline).
///     </para>
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
