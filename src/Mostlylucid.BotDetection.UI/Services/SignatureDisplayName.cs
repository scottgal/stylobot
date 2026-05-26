namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Single source of truth for the human-readable label of a signature row.
///     Every view that needs to render "what is this fingerprint called" calls
///     <see cref="Resolve"/>. Labels come ONLY from real signals -- an operator
///     rename, a detector-emitted bot name, a real bot-type classification,
///     country + UA family composition for ordinary visitors, or the short
///     hash prefix as the final fallback. No wordlists, no invented personas.
/// </summary>
public static class SignatureDisplayName
{
    /// <summary>
    ///     Resolve the visible name for a signature. Order of preference:
    ///     1. Operator-set <paramref name="customLabel"/> (free-form rename).
    ///     2. Detected <paramref name="botName"/> (e.g. "Googlebot", "GPTBot").
    ///     3. Detected <paramref name="botType"/> (e.g. "Scraper") when not the
    ///        useless "Unknown"/"Tool"/"Other" sentinels.
    ///     4. Composite "{CountryCode} {UaFamily} {Role}" when both signals are
    ///        available -- e.g. "GB Chrome User", "US curl Bot". Country codes
    ///        beat adjectives (American / British / German) because table cells
    ///        are width-constrained and the country-flag primitive next to the
    ///        label already shows the country visually. A 4-char signature
    ///        suffix is appended ("GB Chrome User · 7faa") so two distinct
    ///        fingerprints sharing the same country / UA never collapse to one
    ///        visible label.
    ///     5. UaFamily on its own ("Chrome", "Bot · Chrome") when country is
    ///        unknown but the family resolved.
    ///     6. Short signature prefix as the bare-minimum fallback.
    ///     Never returns null and never returns a hallucinated label.
    /// </summary>
    public static string Resolve(
        string signature,
        string? botName,
        string? botType,
        string? customLabel,
        string? countryCode,
        bool isBot,
        string? uaFamily = null)
    {
        if (!string.IsNullOrWhiteSpace(customLabel)) return customLabel.Trim();
        if (!string.IsNullOrWhiteSpace(botName) && !IsPatternIdLeak(botName))
            return botName.Trim();
        if (!string.IsNullOrWhiteSpace(botType) && !IsUselessBotType(botType))
            return botType.Trim();

        // Country codes (US, GB, DE...) read shorter than the adjective form
        // (American, British, German) and table cells are width-constrained.
        // The codes are universally recognisable and the country-flag primitive
        // in the same row carries the same information visually.
        var country = !string.IsNullOrEmpty(countryCode) && countryCode.Length == 2 && countryCode != "XX"
            ? countryCode.ToUpperInvariant()
            : null;
        var family = string.IsNullOrWhiteSpace(uaFamily) || IsUselessUaFamily(uaFamily)
            ? null
            : uaFamily.Trim();
        var role = isBot ? "Bot" : "User";

        if (country != null && family != null)
            return $"{country} {family} {role} · {ShortHash(signature, 4)}";

        if (country != null)
            return $"{country} {role} · {ShortHash(signature, 4)}";

        if (family != null)
            return isBot ? $"Bot · {family}" : family;

        return isBot ? $"Bot · {ShortHash(signature)}" : ShortHash(signature);
    }

    /// <summary>
    ///     Render a per-row title attribute that gives operators the full hash
    ///     for incident notes without ever putting it in the visible label.
    /// </summary>
    public static string TitleAttr(string signature) => $"Signature: {signature}";

    /// <summary>
    ///     Generic bot-types that carry no information beyond "we know nothing".
    ///     Falling through to the country/UA/hash labels is more useful than
    ///     rendering "Unknown" on every row.
    /// </summary>
    private static bool IsUselessBotType(string botType) =>
        botType.Equals("Unknown",   StringComparison.OrdinalIgnoreCase) ||
        botType.Equals("Tool",      StringComparison.OrdinalIgnoreCase) ||
        botType.Equals("Other",     StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Reputation contributors store IP-pattern reputation under keys like
    ///     "ip:2a02:c7c:d293::/4" and UA-pattern reputation under "ua:{16hex}".
    ///     Those keys are internal cache identifiers, not display labels -- when
    ///     a contributor accidentally propagates one as the visible botName the
    ///     row reads as a raw CIDR string longer than the column. Detect the
    ///     prefix and fall through to the country / UA / shortHash composite
    ///     instead.
    /// </summary>
    private static bool IsPatternIdLeak(string name) =>
        name.StartsWith("ip:", StringComparison.Ordinal) ||
        name.StartsWith("ua:", StringComparison.Ordinal);

    /// <summary>
    ///     UA family values that the UA parser emits when it couldn't recognise
    ///     anything specific. Showing "Other" in the visible label is no better
    ///     than showing nothing -- it just clutters the row, and the UA column
    ///     already exposes the raw family separately.
    /// </summary>
    private static bool IsUselessUaFamily(string family) =>
        family.Equals("Other", StringComparison.OrdinalIgnoreCase) ||
        family.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
        family.Equals("-", StringComparison.Ordinal);

    /// <summary>
    ///     First N chars of the signature, or "—" when there is no signature
    ///     at all. The signature itself is real data; truncating it gives a
    ///     stable, grep-able row identity without inventing a label.
    /// </summary>
    private static string ShortHash(string signature, int length = 8)
    {
        if (string.IsNullOrEmpty(signature)) return "—";
        return signature.Length <= length ? signature : signature[..length];
    }
}
