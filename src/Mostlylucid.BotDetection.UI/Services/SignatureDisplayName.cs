namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Single source of truth for the human-readable label of a signature row.
///     Every view that needs to render "what is this fingerprint called" calls
///     <see cref="Resolve"/>. Labels come ONLY from real signals -- an operator
///     rename, a detector-emitted bot name, a real bot-type classification, or
///     a short prefix of the signature hash itself. There is no synthesis, no
///     wordlist, no country-prefix-on-unknowns. If detection has nothing to
///     say, we show the truncated hash so operators can correlate rows; we do
///     NOT invent "British Sage Fox".
/// </summary>
public static class SignatureDisplayName
{
    /// <summary>
    ///     Resolve the visible name for a signature. Order of preference:
    ///     1. Operator-set <paramref name="customLabel"/> (free-form rename).
    ///     2. Detected <paramref name="botName"/> (e.g. "Googlebot", "GPTBot").
    ///     3. Detected <paramref name="botType"/> (e.g. "Scraper") when not the
    ///        useless "Unknown"/"Tool"/"Other" sentinels.
    ///     4. <paramref name="uaFamily"/> when present, prefixed with "Bot · "
    ///        when <paramref name="isBot"/> is true so an unidentified bot
    ///        running Chrome reads "Bot · Chrome" and an unidentified human
    ///        running Chrome reads "Chrome". The UA family is a real signal
    ///        the detector pipeline already extracts -- no invention.
    ///     5. The first 8 characters of <paramref name="signature"/> -- the
    ///        real hash prefix when nothing else is known. Operators can grep
    ///        the same row in logs or the database without us making up a name.
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
        if (!string.IsNullOrWhiteSpace(botName))     return botName.Trim();
        if (!string.IsNullOrWhiteSpace(botType) && !IsUselessBotType(botType))
            return botType.Trim();

        if (!string.IsNullOrWhiteSpace(uaFamily))
        {
            var fam = uaFamily.Trim();
            return isBot ? $"Bot · {fam}" : fam;
        }

        return isBot ? $"Bot · {ShortHash(signature)}" : ShortHash(signature);
    }

    /// <summary>
    ///     Render a per-row title attribute that gives operators the full hash
    ///     for incident notes without ever putting it in the visible label.
    /// </summary>
    public static string TitleAttr(string signature) => $"Signature: {signature}";

    /// <summary>
    ///     Generic bot-types that carry no information beyond "we know nothing".
    ///     Falling through to the short hash is more useful than rendering
    ///     "Unknown" on every row.
    /// </summary>
    private static bool IsUselessBotType(string botType) =>
        botType.Equals("Unknown",   StringComparison.OrdinalIgnoreCase) ||
        botType.Equals("Tool",      StringComparison.OrdinalIgnoreCase) ||
        botType.Equals("Other",     StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     First 8 chars of the signature, or "—" when there is no signature at
    ///     all. This is a real signal (the hash itself) so operators can grep
    ///     the same row in logs or in the database without us inventing a name.
    /// </summary>
    private static string ShortHash(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return "—";
        return signature.Length <= 8 ? signature : signature[..8];
    }
}
