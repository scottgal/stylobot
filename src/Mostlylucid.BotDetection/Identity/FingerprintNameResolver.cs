namespace Mostlylucid.BotDetection.Identity;

public enum FingerprintNameKind { None, Induced, Llm, Given }

/// <summary>
///     Pure resolver: <c>given ?? llm ?? induced</c>. Each writer owns exactly one
///     slot; the resolver decides what the UI shows. Drift between slots is
///     surfaced separately by <c>_DriftBadge</c>, not by mutating any slot.
/// </summary>
public static class FingerprintNameResolver
{
    public static string? Resolve(Fingerprint? fp)
    {
        if (fp is null) return null;
        return fp.GivenName ?? fp.LlmName ?? fp.InducedName;
    }

    public static FingerprintNameKind DisplayedSlot(Fingerprint? fp)
    {
        if (fp is null) return FingerprintNameKind.None;
        if (!string.IsNullOrEmpty(fp.GivenName)) return FingerprintNameKind.Given;
        if (!string.IsNullOrEmpty(fp.LlmName)) return FingerprintNameKind.Llm;
        if (!string.IsNullOrEmpty(fp.InducedName)) return FingerprintNameKind.Induced;
        return FingerprintNameKind.None;
    }
}
