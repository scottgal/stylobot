using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Single source of truth for the human-readable label of a signature row.
///     Every view that needs to render "what is this fingerprint called" calls
///     <see cref="Resolve"/>. Labels come ONLY from real signals -- an operator
///     rename, a recognised bot identity, the parsed UA family + version + OS
///     for ordinary visitors, or the short hash prefix as the final fallback.
///     No wordlists, no invented personas, no mode labels in the client slot.
/// </summary>
public static class SignatureDisplayName
{
    /// <summary>
    ///     Resolve the visible name for a signature. Order of preference:
    ///     1. Operator-set <paramref name="customLabel"/> (free-form rename).
    ///     2. Detected <paramref name="botName"/> ONLY when it matches a known
    ///        bot pattern in <see cref="BotPatternLoader"/> (Googlebot, GPTBot,
    ///        Mastodon, etc.). Mode-shaped archetype names like "Chrome XHR" or
    ///        "Mobile Chrome (header drift)" are NOT bot identities -- they
    ///        describe HOW a known client made the request, not WHAT it is.
    ///        Those fall through to the UA-rich label below so the operator
    ///        sees the client (Chrome 119 / macOS) plus the mode badge,
    ///        instead of "Chrome XHR" displacing the client identity. Per
    ///        spec docs/architecture/composite-browser-mode-fingerprints.md.
    ///     3. Rich UA label "{Family} {Version} / {OsFamily}" parsed from
    ///        <paramref name="userAgent"/> via uap-core (e.g. "Chrome 119 /
    ///        macOS", "Safari 17 / iOS"). When OS resolves but version does
    ///        not, the family alone is composed with the OS. This is where
    ///        the dashboard SHOWS what it KNOWS -- every visitor row whose
    ///        UA parsed at all gets a coherent client label instead of a
    ///        "GB User N" synth placeholder.
    ///     4. Detected <paramref name="botType"/> (e.g. "Scraper") when not
    ///        the useless "Unknown" / "Tool" / "Other" sentinels and no UA
    ///        label resolved.
    ///     5. Composite "{CountryCode} {UaFamily} {Role}" when raw UA is not
    ///        available but the family is.
    ///     6. {CountryCode} {Role} when only the country is known.
    ///     7. {UaFamily} alone when only the family is known.
    ///     8. Short signature prefix as the bare-minimum fallback.
    ///     Duplicate composite labels in the same render get a numeric tail
    ///     (" 2", " 3", ...) via the <paramref name="seen"/> dictionary.
    ///     Never returns null and never returns a hallucinated label.
    /// </summary>
    public static string Resolve(
        string signature,
        string? botName,
        string? botType,
        string? customLabel,
        string? countryCode,
        bool isBot,
        string? uaFamily = null,
        IDictionary<string, int>? seen = null,
        string? userAgent = null)
    {
        var label = BuildLabel(signature, botName, botType, customLabel, countryCode, isBot, uaFamily, userAgent);
        if (seen is null) return label;

        // Dedup with a numeric suffix when multiple rows in the same render
        // collapse to the same composite label (two distinct GB Chrome User
        // signatures, two unverified Googlebot fingerprints, etc.). First
        // occurrence reads cleanly; second and later get " 2", " 3", ... so
        // the operator can see they are different rows without seeing a
        // random shortHash. Identity grouping (Googlebot etc.) is handled
        // upstream in WidgetRenderHelpers.CollapseGroupableIdentities so
        // those usually never reach this counter twice.
        var count = seen.TryGetValue(label, out var c) ? c + 1 : 1;
        seen[label] = count;
        return count > 1 ? $"{label} {count}" : label;
    }

    private static string BuildLabel(
        string signature,
        string? botName,
        string? botType,
        string? customLabel,
        string? countryCode,
        bool isBot,
        string? uaFamily,
        string? userAgent)
    {
        if (!string.IsNullOrWhiteSpace(customLabel)) return customLabel.Trim();

        // botName wins ONLY when it's a recognised bot identity. Mode-shaped
        // archetype names like "Chrome XHR" / "Mobile Chrome (header drift)"
        // aren't bot identities -- they're shape annotations on a known client
        // -- and letting them displace the client identity is the parallel-
        // axis bug the composite-browser-mode spec was meant to kill. The
        // BotPatternLoader catalog is the canonical "is this a real bot UA
        // family" check; an operator-set rename is already handled by
        // customLabel above. (Pattern-id leaks "ip:..." / "ua:..." are
        // always rejected.)
        if (!string.IsNullOrWhiteSpace(botName) && !IsPatternIdLeak(botName))
        {
            var catalogBotType = BotPatternLoader.Default.FindBotTypeByName(botName);
            if (!string.IsNullOrEmpty(catalogBotType))
                return botName.Trim();
        }

        // Rich UA label -- the dashboard SHOWING what it KNOWS. uap-core gives
        // us family + version + OS family from the raw UA on every request,
        // and we already store it; "GB User N" / "Chrome XHR" instead of
        // "Chrome 119 / macOS" was a regression in operator legibility.
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            var (family, version) = UserAgentParser.Parse(userAgent);
            var os = UserAgentParser.ExtractOs(userAgent);
            var familyClean = !string.IsNullOrWhiteSpace(family) && !IsUselessUaFamily(family) ? family.Trim() : null;
            var osClean = !string.IsNullOrWhiteSpace(os) ? os.Trim() : null;
            if (familyClean != null)
            {
                var head = !string.IsNullOrWhiteSpace(version)
                    ? $"{familyClean} {version}"
                    : familyClean;
                return osClean != null ? $"{head} / {osClean}" : head;
            }
        }

        // botName fallback for matcher-derived names that aren't in the bot
        // pattern catalog but aren't mode-shaped either (operator-named
        // fingerprints, fediverse instance suffixes, etc.).
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
        var uaFam = string.IsNullOrWhiteSpace(uaFamily) || IsUselessUaFamily(uaFamily)
            ? null
            : uaFamily.Trim();
        var role = isBot ? "Bot" : "User";

        // No random-character hash suffix on the visible label. Operators read
        // "GB Chrome User" -- not "GB User * rVfs". Duplicate composite labels
        // get a numeric tail (" 2", " 3") via the seen dictionary in Resolve.
        if (country != null && uaFam != null)
            return $"{country} {uaFam} {role}";

        if (country != null)
            return $"{country} {role}";

        if (uaFam != null)
            return isBot ? $"Bot {uaFam}" : uaFam;

        return isBot ? $"Bot {ShortHash(signature)}" : ShortHash(signature);
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
    ///     First N chars of the signature, or "-" when there is no signature
    ///     at all. The signature itself is real data; truncating it gives a
    ///     stable, grep-able row identity without inventing a label.
    /// </summary>
    private static string ShortHash(string signature, int length = 8)
    {
        if (string.IsNullOrEmpty(signature)) return "-";
        return signature.Length <= length ? signature : signature[..length];
    }
}
