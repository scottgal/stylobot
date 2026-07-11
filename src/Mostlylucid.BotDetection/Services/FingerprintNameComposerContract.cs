namespace Mostlylucid.BotDetection.Services;

public static class FingerprintNameComposerContract
{
    public static bool IsAllowedShape(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (candidate.Contains('(')) return false;
        if (candidate.Contains('/')) return false;
        if (candidate.Contains(" w/ ")) return false;
        return true;
    }
}