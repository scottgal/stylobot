using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.Services;

public static class FingerprintNameComposerContract
{
    private static readonly Regex UnknownFallback = new(
        @"^Unknown [0-9a-f]{8}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsAllowedShape(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (candidate.Contains('(')) return false;
        if (candidate.Contains('/')) return false;
        if (candidate.Contains(" w/ ")) return false;
        return true;
    }
}