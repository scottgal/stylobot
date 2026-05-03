using System.IO.Enumeration;
using System.Reflection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Registry for loaded simulation packs.
///     Provides path matching against all honeypot and CVE probe paths across loaded packs.
/// </summary>
public interface ISimulationPackRegistry
{
    /// <summary>Get all loaded simulation packs.</summary>
    IReadOnlyList<SimulationPack> GetLoadedPacks();

    /// <summary>Get a specific pack by ID.</summary>
    SimulationPack? GetPack(string id);

    /// <summary>Get all CVE modules across all loaded packs.</summary>
    IReadOnlyList<PackCveModule> GetAllCveModules();

    /// <summary>
    ///     Check if a request path matches any honeypot or CVE probe path.
    ///     Returns the matched pack and CVE module (if the match was a CVE probe).
    /// </summary>
    bool IsHoneypotPath(string path, out SimulationPack? matchedPack, out PackCveModule? matchedCve);

    /// <summary>
    ///     Find a response template that matches the given path across all loaded packs.
    ///     Checks CVE module ProbeResponse templates first, then pack-level response templates.
    /// </summary>
    /// <param name="path">The request path to match.</param>
    /// <param name="pack">The simulation pack that owns the matched template, or null.</param>
    /// <returns>The matching response template, or null if no match found.</returns>
    PackResponseTemplate? FindResponseTemplate(string path, out SimulationPack? pack);
}

/// <summary>
///     Loads simulation pack YAML definitions from embedded resources and provides
///     fast path-matching across all loaded packs.
/// </summary>
public sealed class SimulationPackLoader : ISimulationPackRegistry
{
    private readonly ILogger<SimulationPackLoader> _logger;
    private readonly Dictionary<string, SimulationPack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _loadLock = new();
    private bool _loaded;

    // Flattened lookup structures for fast matching
    private List<(string Pattern, SimulationPack Pack, PackCveModule? Cve, double Confidence, double Weight)> _allPaths = [];

    public SimulationPackLoader(ILogger<SimulationPackLoader> logger)
    {
        _logger = logger;
        EnsureLoaded();
    }

    public IReadOnlyList<SimulationPack> GetLoadedPacks()
    {
        return _packs.Values.ToList();
    }

    public SimulationPack? GetPack(string id)
    {
        return _packs.GetValueOrDefault(id);
    }

    public IReadOnlyList<PackCveModule> GetAllCveModules()
    {
        return _packs.Values.SelectMany(p => p.CveModules).ToList();
    }

    public PackResponseTemplate? FindResponseTemplate(string path, out SimulationPack? pack)
    {
        pack = null;

        foreach (var p in _packs.Values)
        {
            // Check CVE module ProbeResponse templates first (higher specificity)
            foreach (var cve in p.CveModules)
            {
                if (cve.ProbeResponse is not null &&
                    FileSystemName.MatchesSimpleExpression(cve.ProbeResponse.PathPattern, path, ignoreCase: true))
                {
                    pack = p;
                    return cve.ProbeResponse;
                }
            }

            // Check pack-level response templates
            foreach (var template in p.ResponseTemplates)
            {
                if (FileSystemName.MatchesSimpleExpression(template.PathPattern, path, ignoreCase: true))
                {
                    pack = p;
                    return template;
                }
            }
        }

        return null;
    }

    public bool IsHoneypotPath(string path, out SimulationPack? matchedPack, out PackCveModule? matchedCve)
    {
        matchedPack = null;
        matchedCve = null;

        foreach (var (pattern, pack, cve, _, _) in _allPaths)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, path, ignoreCase: true))
            {
                matchedPack = pack;
                matchedCve = cve;
                return true;
            }
        }

        return false;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_loadLock)
        {
            if (_loaded) return;
            LoadEmbeddedPacks();
            BuildFlattenedLookup();
            _loaded = true;
        }
    }

    private void LoadEmbeddedPacks()
    {
        var assembly = typeof(SimulationPackLoader).Assembly;
        var resourcePrefix = "Mostlylucid.BotDetection.SimulationPacks.Packs.";

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(resourcePrefix, StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _logger.LogInformation("Found {Count} simulation pack YAML resources", resourceNames.Count);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        foreach (var resourceName in resourceNames)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    _logger.LogWarning("Could not load embedded resource: {Name}", resourceName);
                    continue;
                }

                using var reader = new StreamReader(stream);
                var yaml = reader.ReadToEnd();
                var pack = deserializer.Deserialize<SimulationPack>(yaml);

                if (pack is null || string.IsNullOrWhiteSpace(pack.Id))
                {
                    _logger.LogWarning("Invalid simulation pack in resource: {Name}", resourceName);
                    continue;
                }

                _packs[pack.Id] = pack;
                _logger.LogInformation(
                    "Loaded simulation pack: {Id} ({Name}) - {PathCount} honeypot paths, {CveCount} CVE modules",
                    pack.Id, pack.Name, pack.HoneypotPaths.Count, pack.CveModules.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load simulation pack from resource: {Name}", resourceName);
            }
        }
    }

    private void BuildFlattenedLookup()
    {
        var paths = new List<(string Pattern, SimulationPack Pack, PackCveModule? Cve, double Confidence, double Weight)>();

        foreach (var pack in _packs.Values)
        {
            // Add pack-level honeypot paths
            foreach (var hp in pack.HoneypotPaths)
            {
                paths.Add((hp.Path, pack, null, hp.Confidence, hp.Weight));
            }

            // Add CVE probe paths
            foreach (var cve in pack.CveModules)
            {
                foreach (var probePath in cve.ProbePaths)
                {
                    // CVE probes get high confidence by default
                    var confidence = cve.Severity?.ToLowerInvariant() switch
                    {
                        "critical" => 0.95,
                        "high" => 0.90,
                        "medium" => 0.80,
                        _ => 0.75
                    };
                    paths.Add((probePath, pack, cve, confidence, 2.5));
                }
            }
        }

        _allPaths = paths;
        _logger.LogInformation("Built flattened lookup with {Count} total paths across {PackCount} packs",
            paths.Count, _packs.Count);
    }
}
