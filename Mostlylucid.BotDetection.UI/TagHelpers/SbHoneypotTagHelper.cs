using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers;

/// <summary>
///     Injects hidden trap fields into a form. Bots that fill these fields are detected.
///     Use <see cref="HoneypotValidator.IsTriggered" /> on the server to check.
/// </summary>
/// <example>
///     <code>
///     &lt;form method="post"&gt;
///         &lt;sb-honeypot /&gt;
///         &lt;button type="submit"&gt;Submit&lt;/button&gt;
///     &lt;/form&gt;
///     </code>
/// </example>
[HtmlTargetElement("sb-honeypot", TagStructure = TagStructure.WithoutEndTag)]
public class SbHoneypotTagHelper : TagHelper
{
    private static readonly string[][] TrapFieldNames =
    [
        ["email_confirm", "email"],
        ["website_url", "url"],
        ["phone_verify", "tel"]
    ];

    /// <summary>Field name prefix (default "sb").</summary>
    [HtmlAttributeName("prefix")]
    public string Prefix { get; set; } = "sb";

    /// <summary>Number of trap fields: 1–3 (default 2).</summary>
    [HtmlAttributeName("fields")]
    public int Fields { get; set; } = 2;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;

        var count = Math.Clamp(Fields, 1, 3);

        // Render a wrapper div positioned offscreen
        output.Content.AppendHtml(
            "<div style=\"position:absolute;left:-9999px;top:-9999px;height:0;width:0;overflow:hidden\" aria-hidden=\"true\" tabindex=\"-1\">");

        for (var i = 0; i < count; i++)
        {
            var fieldName = $"{Prefix}_{TrapFieldNames[i][0]}";
            var fieldType = TrapFieldNames[i][1];
            output.Content.AppendHtml(
                $"<input type=\"{fieldType}\" name=\"{fieldName}\" autocomplete=\"off\" tabindex=\"-1\" />");
        }

        output.Content.AppendHtml("</div>");
    }
}

/// <summary>
///     Server-side validator for honeypot fields.
/// </summary>
public static class HoneypotValidator
{
    private static readonly string[] TrapSuffixes = ["email_confirm", "website_url", "phone_verify"];

    /// <summary>
    ///     Returns true if any honeypot trap field was filled in (indicating a bot).
    /// </summary>
    /// <param name="request">The HTTP request to check.</param>
    /// <param name="prefix">The prefix used in the honeypot tag helper (default "sb").</param>
    public static bool IsTriggered(HttpRequest request, string prefix = "sb")
    {
        if (!request.HasFormContentType)
            return false;

        foreach (var suffix in TrapSuffixes)
        {
            var key = $"{prefix}_{suffix}";
            if (request.Form.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return true;
        }

        return false;
    }
}
