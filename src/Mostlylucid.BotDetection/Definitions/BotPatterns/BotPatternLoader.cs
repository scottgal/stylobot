using System.Buffers;
using System.Reflection;
using Microsoft.Extensions.Logging;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Definitions.BotPatterns;

/// <summary>
///     Loads bot pattern definitions from embedded YAML files in Definitions/BotPatterns/.
///     Each YAML file covers one category (search engines, AI scrapers, social media, etc.).
///     Patterns are loaded once at startup and cached for the application lifetime.
///
///     Static <see cref="Default"/> is available for non-DI contexts (e.g. BotSignatures).
/// </summary>
public sealed class BotPatternLoader
{
    // Static singleton for non-DI contexts (lazy, loads on first access)
    private static readonly Lazy<BotPatternLoader> _default =
        new(() => new BotPatternLoader());

    private readonly ILogger<BotPatternLoader>? _logger;
    private Dictionary<string, string>? _botNameToTypeIndex;
    private (string Pattern, string BotType, string BotName)[]? _uaMatchTable;
    private SearchValues<string>? _matchFilter;

    public BotPatternLoader(ILogger<BotPatternLoader>? logger = null)
    {
        _logger = logger;
        AllPatterns = LoadFromEmbeddedResources();
    }

    /// <summary>Static default instance (no logger). Use when DI is not available.</summary>
    public static BotPatternLoader Default => _default.Value;

    /// <summary>All loaded bot patterns across all categories.</summary>
    public IReadOnlyList<BotPatternEntry> AllPatterns { get; }

    /// <summary>Patterns that have <see cref="BotPatternEntry.AiCategory"/> set.</summary>
    public IEnumerable<BotPatternEntry> AiPatterns =>
        AllPatterns.Where(p => p.AiCategory != null);

    /// <summary>Patterns with IP ranges URL or verified domains (for VerifiedBotRegistry).</summary>
    public IEnumerable<BotPatternEntry> VerifiablePatterns =>
        AllPatterns.Where(p => p.IpRangesUrl != null || p.VerifiedDomains is { Length: > 0 });

    /// <summary>
    ///     Returns the raw <see cref="BotPatternEntry.BotType"/> string for the given
    ///     <paramref name="botName"/>, or null if no pattern matches. Used as a safety
    ///     net by the risk-band determiner when bot_type propagation has failed but the
    ///     persisted bot_name still matches a YAML entry. Match is case-insensitive and
    ///     also accepts names with an appended discriminator (e.g. "Mastodon mastodon.social"
    ///     resolves to "Mastodon" -- the FingerprintNameComposer appends instance hostnames
    ///     for fediverse UAs that carry a +URL comment).
    /// </summary>
    public string? FindBotTypeByName(string? botName)
    {
        if (string.IsNullOrEmpty(botName)) return null;
        var idx = _botNameToTypeIndex ??= BuildBotNameIndex();
        if (idx.TryGetValue(botName, out var bt)) return bt;
        var space = botName.IndexOf(' ');
        if (space > 0 && idx.TryGetValue(botName[..space], out var bt2)) return bt2;

        // Fallback: arcjet well-known-bots catalog for names originating from
        // WellKnownBotIndex (display names like "Google Crawler" that aren't in YAML).
        return Definitions.WellKnownBots.WellKnownBotIndex.Default.TryFindBotTypeByDisplayName(botName);
    }

    /// <summary>
    ///     Scan the loaded patterns for the first substring match against
    ///     <paramref name="userAgent"/>. Returns the matched pattern's BotType
    ///     and BotName, or null when nothing matches. Substring match mirrors
    ///     <c>UserAgentContributor.IsCommonBotPattern</c> — the same source
    ///     table is used for both sites, so a match here is also what the
    ///     full pipeline would have produced.
    ///
    ///     Used by the verdict-cache short-circuit (SignatureVerdictGate.Skip
    ///     path in BotDetectionMiddleware) to derive a friendly BotType from
    ///     the live UA when the cached verdict alone doesn't carry one. UA
    ///     pattern matching is cheap and stateless, so re-running it on the
    ///     skip path costs negligible CPU but rescues googlebot / bingbot /
    ///     Mastodon rows from being labelled with a stale "Scraper" tag
    ///     persisted before earlier HeuristicEarly fixes landed.
    /// </summary>
    public (string? BotType, string? BotName) MatchUserAgent(
        string? userAgent,
        Definitions.WellKnownBots.WellKnownBotIndex? wellKnownBots = null)
    {
        if (string.IsNullOrEmpty(userAgent)) return (null, null);
        var table = _uaMatchTable ??= BuildUaMatchTable();

        // L1: single Aho-Corasick SIMD scan over the UA. All 145 YAML patterns are
        // pure literals, so this one pass covers every possible match. If no keyword
        // appears, skip the per-pattern loop entirely.
        if (_matchFilter == null || userAgent.AsSpan().ContainsAny(_matchFilter))
        {
            foreach (var (pattern, type, name) in table)
                if (userAgent.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return (type, name);
        }

        // Fallback: arcjet well-known-bots catalog (has its own L1 pre-filter).
        if (wellKnownBots?.Count > 0)
        {
            var match = wellKnownBots.TryMatch(userAgent);
            if (match is not null)
                return (match.BotType.ToString(), match.DisplayName);
        }

        return (null, null);
    }

    private (string Pattern, string BotType, string BotName)[] BuildUaMatchTable()
    {
        var table = AllPatterns
            .Where(p => !string.IsNullOrEmpty(p.Pattern)
                        && !string.IsNullOrEmpty(p.BotType)
                        && !string.IsNullOrEmpty(p.BotName))
            .Select(p => (p.Pattern!, p.BotType!, p.BotName!))
            .ToArray();

        // Build SearchValues pre-filter from all pattern strings. All YAML patterns
        // are pure literals, so every possible match has at least one keyword in here.
        _matchFilter = table.Length > 0
            ? SearchValues.Create(table.Select(t => t.Item1).ToArray(),
                StringComparison.OrdinalIgnoreCase)
            : null;

        return table;
    }

    private Dictionary<string, string> BuildBotNameIndex()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in AllPatterns)
            if (!string.IsNullOrEmpty(entry.BotName) && !string.IsNullOrEmpty(entry.BotType))
                dict.TryAdd(entry.BotName, entry.BotType);
        return dict;
    }

    private Dictionary<string, string>? _botNameCanonicalIndex;

    /// <summary>
    ///     Case-insensitive lookup that returns the catalog's canonical casing for
    ///     a known bot name, or null when the name is unknown to the catalog. The
    ///     single chokepoint used by the gateway broadcast + persistent store to
    ///     normalise contributor-emitted names ("googlebot", "GOOGLEBOT", or the
    ///     mixed "Googlebot/2.1" UA-regex capture) into the YAML-canonical
    ///     "Googlebot" before anything downstream sees the row. Bot names that
    ///     don't appear in any pattern catalog (custom matcher names, fediverse
    ///     instance suffixes, ad-hoc operator labels) read through unchanged --
    ///     the caller stores the input as-is. Discriminator suffixes ("Mastodon
    ///     mastodon.social") canonicalise the head and preserve the tail.
    /// </summary>
    public string? FindCanonicalCasing(string? botName)
    {
        if (string.IsNullOrWhiteSpace(botName)) return null;
        var trimmed = botName.Trim();
        var idx = _botNameCanonicalIndex ??= BuildBotNameCanonicalIndex();
        if (idx.TryGetValue(trimmed, out var canonical)) return canonical;

        var space = trimmed.IndexOf(' ');
        if (space > 0 && idx.TryGetValue(trimmed[..space], out var headCanonical))
            return headCanonical + trimmed[space..];

        return null;
    }

    private Dictionary<string, string> BuildBotNameCanonicalIndex()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in AllPatterns)
            if (!string.IsNullOrWhiteSpace(entry.BotName))
                dict.TryAdd(entry.BotName, entry.BotName);
        return dict;
    }

    /// <summary>
    ///     Builds the legacy GoodBots dictionary from all loaded patterns.
    ///     Format: { pattern -> display_name }
    ///     Used by BotSignatures.GoodBots for backward compatibility.
    /// </summary>
    public Dictionary<string, string> BuildGoodBotsDict()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in AllPatterns)
            if (!string.IsNullOrEmpty(entry.Pattern) && !string.IsNullOrEmpty(entry.BotName))
                dict.TryAdd(entry.Pattern, entry.BotName);
        return dict;
    }

    private List<BotPatternEntry> LoadFromEmbeddedResources()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("Definitions.BotPatterns") && n.EndsWith(".bot-patterns.yaml"))
            .OrderBy(n => n);

        var all = new List<BotPatternEntry>();

        foreach (var resourceName in resources)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    _logger?.LogWarning("Could not load bot-pattern resource: {Resource}", resourceName);
                    continue;
                }

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var file = YamlSerializer.Deserialize<BotPatternFile>(ms.ToArray());
                if (file?.Patterns is { Count: > 0 })
                {
                    all.AddRange(file.Patterns.Where(p => !string.IsNullOrEmpty(p.Pattern)));
                    _logger?.LogDebug("Loaded {Count} patterns from {Resource} ({Category})",
                        file.Patterns.Count, resourceName, file.Category);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error loading bot patterns from {Resource}", resourceName);
            }
        }

        _logger?.LogInformation("BotPatternLoader: loaded {Total} bot patterns from embedded resources", all.Count);
        return all;
    }
}
