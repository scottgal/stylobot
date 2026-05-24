using System.Security.Cryptography;
using System.Text;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Single source of truth for the human-readable label of a signature row.
///     Every view that needs to render "what is this fingerprint called" calls
///     <see cref="Resolve"/>. The raw signature hash MUST NOT be a visible label
///     anywhere -- operators see a stable, memorable name on every surface, the
///     full hash stays in the row's title attribute for copy/paste into incident
///     notes only.
/// </summary>
public static class SignatureDisplayName
{
    /// <summary>
    ///     Resolve the visible name for a signature. Order of preference:
    ///     1. Operator-set <paramref name="customLabel"/> (free-form rename).
    ///     2. Detected <paramref name="botName"/> (e.g. "Googlebot", "GPTBot").
    ///     3. Detected <paramref name="botType"/> (e.g. "Scraper") when no name fired.
    ///     4. Synthesized deterministic two-word slug from the signature bytes,
    ///        prefixed with the country adjective when known. Stable across
    ///        processes, restarts, and views -- the same signature always
    ///        resolves to the same label.
    ///     Never returns null and never returns the raw signature hash.
    /// </summary>
    public static string Resolve(
        string signature,
        string? botName,
        string? botType,
        string? customLabel,
        string? countryCode,
        bool isBot)
    {
        if (!string.IsNullOrWhiteSpace(customLabel)) return customLabel.Trim();
        if (!string.IsNullOrWhiteSpace(botName))     return botName.Trim();
        if (!string.IsNullOrWhiteSpace(botType) && !IsUselessBotType(botType))
            return botType.Trim();

        var synth = Synthesize(signature);
        var country = !string.IsNullOrEmpty(countryCode) && countryCode.Length == 2 && countryCode != "XX"
            ? BotDisplayHelpers.CountryAdjective(countryCode)
            : null;
        return string.IsNullOrEmpty(country) ? synth : $"{country} {synth}";
    }

    /// <summary>
    ///     Render a per-row title attribute that gives operators the full hash
    ///     for incident notes without ever putting it in the visible label.
    /// </summary>
    public static string TitleAttr(string signature) => $"Signature: {signature}";

    /// <summary>
    ///     Generic bot-types that carry no information beyond "we know nothing".
    ///     Falling through to a synthesized name is more useful than rendering
    ///     "Unknown" on every row.
    /// </summary>
    private static bool IsUselessBotType(string botType) =>
        botType.Equals("Unknown",   StringComparison.OrdinalIgnoreCase) ||
        botType.Equals("Tool",      StringComparison.OrdinalIgnoreCase) ||
        botType.Equals("Other",     StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Deterministic two-word slug from the signature bytes. SHA1 over the
    ///     signature gives a uniform distribution; bytes 0 and 1 pick the
    ///     adjective and noun. Same signature -> same words, every time, every
    ///     process. The adjective/noun pools are small on purpose -- operators
    ///     should be able to read "Bronze Otter" as easily as "Blue Hawk", and
    ///     two unrelated signatures colliding on the same pair is acceptable
    ///     because the row also shows country, IsBot, score, etc.
    /// </summary>
    private static string Synthesize(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return "Quiet Visitor";
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(Encoding.UTF8.GetBytes(signature), hash);
        var adj  = _adjectives[hash[0] % _adjectives.Length];
        var noun = _nouns     [hash[1] % _nouns.Length];
        return $"{adj} {noun}";
    }

    // 64 adjectives x 64 nouns = 4096 distinct slugs before country prefix.
    // Country prefix multiplies by ~200 -> roughly one collision per million
    // distinct fingerprints; the visible row also carries country flag, bot
    // probability, hit count, and the path the visitor walked, so identification
    // never depends on the slug being globally unique.
    private static readonly string[] _adjectives =
    {
        "Amber",   "Azure",   "Bold",    "Brass",   "Bright",  "Bronze",  "Calm",   "Clever",
        "Copper",  "Coral",   "Cosmic",  "Crimson", "Curious", "Dark",    "Dusky",  "Eager",
        "Ember",   "Fair",    "Frost",   "Gentle",  "Gilded",  "Glass",   "Golden", "Green",
        "Hidden",  "Indigo",  "Iron",    "Ivory",   "Jade",    "Keen",    "Lazy",   "Lively",
        "Lone",    "Lucid",   "Misty",   "Modest",  "Mossy",   "Noble",   "Nova",   "Onyx",
        "Pale",    "Pearl",   "Plain",   "Polar",   "Proud",   "Quick",   "Quiet",  "Restless",
        "Rusty",   "Sage",    "Sapphire","Scarlet", "Silent",  "Silver",  "Slate",  "Smooth",
        "Stoic",   "Storm",   "Sunny",   "Swift",   "Tame",    "Velvet",  "Wild",   "Wise"
    };

    private static readonly string[] _nouns =
    {
        "Antelope","Badger",  "Bear",    "Beaver",  "Bison",   "Boar",    "Bobcat", "Buck",
        "Buffalo", "Cobra",   "Condor",  "Cougar",  "Coyote",  "Crane",   "Crow",   "Deer",
        "Dolphin", "Dove",    "Eagle",   "Egret",   "Elk",     "Falcon",  "Ferret", "Finch",
        "Fox",     "Gecko",   "Goat",    "Goose",   "Hare",    "Hawk",    "Heron",  "Hound",
        "Ibex",    "Jaguar",  "Jay",     "Lemur",   "Leopard", "Lynx",    "Magpie", "Marten",
        "Mink",    "Moose",   "Newt",    "Otter",   "Owl",     "Panda",   "Panther","Pelican",
        "Pheasant","Pike",    "Puma",    "Raven",   "Raptor",  "Robin",   "Salmon", "Seal",
        "Shrike",  "Sparrow", "Stag",    "Stork",   "Swan",    "Tiger",   "Vulture","Wolf"
    };
}
