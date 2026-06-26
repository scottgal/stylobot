using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Supplies the option lists that back HTML5 <c>&lt;datalist&gt;</c>
///     autocomplete on high-cardinality string facets in the policy rule
///     picker (e.g. <c>verifiedbot.name</c>). Operators get a type-to-filter
///     hint instead of a blank text box, which removes a class of typos
///     (case, hyphenation, fediverse spellings) that silently make rules
///     unreachable.
///
///     Sources are read at construction time from FOSS-side catalogs
///     (<see cref="WellKnownBotIndex"/>, <see cref="BotPatternLoader"/>); the
///     map is immutable for the lifetime of the process. Facets without an
///     authoritative FOSS source (e.g. <c>request.domain</c>,
///     <c>ip.asn_org</c>) are deliberately absent — the picker falls back
///     to a plain text input.
/// </summary>
public interface IFacetAutocompleteSource
{
    /// <summary>
    ///     Returns the option list for <paramref name="datalistKey"/>, or
    ///     null when the key is unknown. Keys are the values authored in
    ///     the picker catalog YAML's <c>datalist:</c> field (e.g.
    ///     <c>verifiedbot-names</c>).
    /// </summary>
    IReadOnlyList<string>? GetOptions(string datalistKey);

    /// <summary>All datalist keys this source can answer for, in stable order.</summary>
    IReadOnlyCollection<string> Keys { get; }
}

/// <summary>
///     Default implementation that snapshots verified-bot names from
///     <see cref="WellKnownBotIndex"/> and <see cref="BotPatternLoader"/>
///     at construction. Both upstream sources expose static
///     <c>.Default</c> singletons, so this works in DI hosts that haven't
///     yet wired bot detection (test fixtures, marketing-site embed).
/// </summary>
public sealed class FacetAutocompleteSource : IFacetAutocompleteSource
{
    private readonly Dictionary<string, IReadOnlyList<string>> _byKey;

    public FacetAutocompleteSource(
        WellKnownBotIndex? wellKnownBots = null,
        BotPatternLoader? botPatterns = null)
    {
        var wkb = wellKnownBots ?? WellKnownBotIndex.Default;
        var bpl = botPatterns ?? BotPatternLoader.Default;

        _byKey = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["verifiedbot-names"] = BuildVerifiedBotNames(wkb, bpl),
        };
    }

    public IReadOnlyList<string>? GetOptions(string datalistKey) =>
        _byKey.TryGetValue(datalistKey, out var list) ? list : null;

    public IReadOnlyCollection<string> Keys => _byKey.Keys;

    private static IReadOnlyList<string> BuildVerifiedBotNames(
        WellKnownBotIndex wkb,
        BotPatternLoader bpl)
    {
        // Both sources contribute the canonical bot display names operators
        // would type into a "verified bot name" rule. BotPatternLoader
        // covers the curated YAML catalog (Googlebot, Bingbot, Mastodon...);
        // WellKnownBotIndex covers the broader arcjet catalog. Dedupe
        // case-insensitively but preserve the first canonical casing seen,
        // sort for stable UI order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var entry in bpl.AllPatterns)
        {
            var name = entry.BotName;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(name)) ordered.Add(name);
        }

        foreach (var (_, displayName, _) in wkb.EnumerateForArchetypePromotion())
        {
            if (string.IsNullOrWhiteSpace(displayName)) continue;
            if (seen.Add(displayName)) ordered.Add(displayName);
        }

        ordered.Sort(StringComparer.OrdinalIgnoreCase);
        return ordered;
    }
}
