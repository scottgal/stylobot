using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;
using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     SINGLE source of truth for the human-readable label of a signature row.
///     Every view that needs to render "what is this fingerprint called" calls
///     <see cref="Resolve"/>. No view-side inline fallback. No second store.
///     Resolution chain (highest priority first):
///     <list type="number">
///         <item>Operator-set custom label (<paramref name="customLabel"/>)</item>
///         <item>Recognised bot name in the BotPatternLoader catalog (Googlebot, GPTBot, Mastodon, ...) </item>
///         <item>Arcjet well-known-bots match against the raw UA -- the 600+ entry
///               catalog is the primary lookup when the matcher hasn't latched a
///               BotName yet. Hot UAs hit the index's per-UA LFU cache.</item>
///         <item>uap-core parse of the raw UA -- "Chrome 149 / macOS", "Safari 17 / iOS"</item>
///         <item>UA family + country + role -- "GB Chrome User"</item>
///         <item>UA family alone</item>
///         <item>Country + role -- "GB Visitor" / "GB Bot"</item>
///         <item>Honest terminal leaves: "No User-Agent" (UA literally missing) or
///               "Unparseable UA" (UA present but uap-core, Arcjet, and BotPattern
///               catalogs all failed to extract anything). NEVER the signature
///               hash; NEVER a weasel "Unclassified" filler.</item>
///     </list>
/// </summary>
public static class SignatureDisplayName
{
    /// <summary>
    ///     Honest terminal label when a fingerprint exists but its request carried
    ///     no User-Agent header at all. Different in meaning from
    ///     <see cref="UnparseableUserAgent"/>: this is "we got nothing".
    /// </summary>
    public const string NoUserAgent = "No User-Agent";

    /// <summary>
    ///     Honest terminal label when the request HAD a User-Agent but uap-core,
    ///     the Arcjet catalog, and the BotPatternLoader catalog all failed to
    ///     extract a recognised family or bot. The raw UA is still available via
    ///     <see cref="TitleAttr"/> for the operator to inspect.
    /// </summary>
    public const string UnparseableUserAgent = "Unparseable UA";

    /// <summary>
    ///     Resolve the visible name for a signature. See class doc for the full
    ///     chain. Pass everything you have; null parameters degrade gracefully.
    ///     Duplicate composite labels in the same render get a numeric tail
    ///     (" 2", " 3", ...) via the <paramref name="seen"/> dictionary.
    ///     Never returns null and never returns a hash-derived "name".
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
        // 1. Operator-set custom label always wins.
        if (!string.IsNullOrWhiteSpace(customLabel)) return customLabel.Trim();

        // 2. Recognised bot name in BotPatternLoader catalog (the matcher already
        //    latched it). Mode-shaped names ("Chrome XHR", "Mobile Chrome (header
        //    drift)") are deliberately NOT promoted here -- they fall through to
        //    the UA-rich path so the operator sees the client identity rather
        //    than a shape annotation. The Spoofed- prefix that VerifiedBot adds
        //    for unverified bot UAs DOES propagate -- a spoofed bot is a real
        //    identity (a hostile one) and we want operators to see that label.
        if (!string.IsNullOrWhiteSpace(botName) && !IsPatternIdLeak(botName))
        {
            if (botName.StartsWith("Spoofed-", StringComparison.OrdinalIgnoreCase))
                return botName.Trim();
            var catalogBotType = BotPatternLoader.Default.FindBotTypeByName(botName);
            if (!string.IsNullOrEmpty(catalogBotType))
                return botName.Trim();
        }

        // 3. Arcjet well-known-bots match against the raw UA -- 600+ regex
        //    entries with LFU per-UA cache. This catches every named bot whose
        //    BotName didn't latch into the row (cold-start, cache miss, remote-
        //    mode pull arrived before the matcher signal). The catalog's
        //    DisplayName is operator-readable ("GPTBot", "Google Crawler").
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            var arcjet = WellKnownBotIndex.Default.TryMatch(userAgent);
            if (arcjet is not null)
                return arcjet.DisplayName;
        }

        // 4. Rich UA label from uap-core -- "Chrome 149 / macOS". The dashboard
        //    SHOWING what it KNOWS. Every visitor row whose UA parsed at all
        //    gets a coherent client label instead of a synth placeholder.
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            var (family, version) = UserAgentParser.Parse(userAgent);
            var os = UserAgentParser.ExtractOs(userAgent);
            var familyClean = !string.IsNullOrWhiteSpace(family) && !IsUselessUaFamily(family) ? family.Trim() : null;
            var osClean = !string.IsNullOrWhiteSpace(os) ? os.Trim() : null;
            if (familyClean != null)
            {
                var head = !string.IsNullOrWhiteSpace(version) ? $"{familyClean} {version}" : familyClean;
                return osClean != null ? $"{head} / {osClean}" : head;
            }
        }

        // 5. botName that wasn't a catalog match but isn't a leak -- accept it
        //    (could be a fediverse instance suffix, an operator-named fingerprint
        //    not yet round-tripped to a CustomBotName).
        if (!string.IsNullOrWhiteSpace(botName) && !IsPatternIdLeak(botName))
            return botName.Trim();
        if (!string.IsNullOrWhiteSpace(botType) && !IsUselessBotType(botType))
            return botType.Trim();

        // 6. Composite signal-only fallbacks (country + UA family, country alone,
        //    family alone). Country codes (US, GB, DE) read tighter than the
        //    adjective form and the row's flag primitive carries the same
        //    information visually.
        var country = !string.IsNullOrEmpty(countryCode) && countryCode.Length == 2 && countryCode != "XX"
            ? countryCode.ToUpperInvariant()
            : null;
        var uaFam = string.IsNullOrWhiteSpace(uaFamily) || IsUselessUaFamily(uaFamily)
            ? null
            : uaFamily.Trim();
        var role = isBot ? "Bot" : "Visitor";

        if (country != null && uaFam != null)
            return $"{country} {uaFam} {role}";
        if (uaFam != null)
            return isBot ? $"Bot {uaFam}" : uaFam;
        if (country != null)
            return $"{country} {role}";

        // 7. Honest terminal -- NEVER a hash, NEVER a weasel "Unclassified". We
        //    distinguish "no UA at all" from "UA present but unparseable" so the
        //    operator can act on the right tooltip; the raw UA (if any) is
        //    available via TitleAttr on the row.
        return string.IsNullOrWhiteSpace(userAgent) ? NoUserAgent : UnparseableUserAgent;
    }

    /// <summary>
    ///     Render a per-row title attribute that gives operators the full hash
    ///     for incident notes without ever putting it in the visible label.
    /// </summary>
    public static string TitleAttr(string signature) => $"Signature: {signature}";

    private static bool IsUselessBotType(string botType) =>
        botType.Equals("Unknown",   StringComparison.OrdinalIgnoreCase) ||
        botType.Equals("Tool",      StringComparison.OrdinalIgnoreCase) ||
        botType.Equals("Other",     StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Reputation contributors store IP-pattern reputation under keys like
    ///     "ip:2a02:c7c:d293::/4" and UA-pattern reputation under "ua:{16hex}".
    ///     Those keys are internal cache identifiers, not display labels.
    /// </summary>
    private static bool IsPatternIdLeak(string name) =>
        name.StartsWith("ip:", StringComparison.Ordinal) ||
        name.StartsWith("ua:", StringComparison.Ordinal);

    private static bool IsUselessUaFamily(string family) =>
        family.Equals("Other", StringComparison.OrdinalIgnoreCase) ||
        family.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
        family.Equals("-", StringComparison.Ordinal);
}
