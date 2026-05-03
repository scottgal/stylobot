using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.Privacy;

/// <summary>
///     Strips PII from User-Agent strings before storage.
///     Most UAs are safe (browser/OS identifiers), but custom bot UAs
///     sometimes include emails, phone numbers, or internal URLs.
///     Uses simple regex patterns -- no Microsoft.Recognizers dependency
///     (UA strings are short and structured, not free text).
/// </summary>
public static partial class UaPiiStripper
{
    public static string Strip(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return string.Empty;

        var result = userAgent;

        // Strip email addresses (common in bot UAs: "MyBot/1.0 (admin@company.com)")
        result = EmailRegex().Replace(result, "[email]");

        // Strip URLs with credentials (http://user:pass@host)
        result = CredentialUrlRegex().Replace(result, "$1[creds]@");

        // Strip phone numbers (rare in UAs but possible)
        result = PhoneRegex().Replace(result, "[phone]");

        return result;
    }

    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(https?://)([^:]+):([^@]+)@", RegexOptions.Compiled)]
    private static partial Regex CredentialUrlRegex();

    [GeneratedRegex(@"\+?\d[\d\s\-().]{8,}\d", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();
}
