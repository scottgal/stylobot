using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.Services;

public static class FingerprintNameComposerContract
{
    private static readonly Regex UnknownFallback = new(
        @"^Unknown [0-9a-f]{8}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Known multi-word UA families. Anything else with a space is a
    // distinguisher attempt ("Mac Chrome", "Mac Chrome 149") and banned.
    private static readonly HashSet<string> MultiWordFamilies = new(StringComparer.Ordinal)
    {
        "Mobile Safari",
    };

    public static bool IsAllowedShape(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (candidate.Contains('(')) return false;
        if (candidate.Contains('/')) return false;
        if (candidate.Contains(" w/ ")) return false;
        if (UnknownFallback.IsMatch(candidate)) return true;
        if (candidate.Contains(' ')) return MultiWordFamilies.Contains(candidate);
        return true;
    }
}