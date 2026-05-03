using System.Reflection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

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

                using var reader = new StreamReader(stream);
                var file = deserializer.Deserialize<BotPatternFile>(reader);
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
