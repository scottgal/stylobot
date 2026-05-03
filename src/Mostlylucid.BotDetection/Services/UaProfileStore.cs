using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
/// Holds UA family behavioral centroids. Centroids are seeded from <c>ua_profiles.yaml</c>
/// and auto-adapt via EWM as real traffic is observed (alpha=0.99, ~100 samples to drift 50%).
/// Restarts reset to seeds - by design, so bad seed values naturally self-correct while remaining stable.
/// </summary>
public sealed class UaProfileStore
{
    private readonly ILogger<UaProfileStore> _logger;
    private readonly double _alpha;

    // family string (lower) -> live centroid (mutated via EWM, not replaced)
    private readonly Dictionary<string, LiveCentroid> _byFamily;

    // alias (lower) -> canonical family name
    private readonly Dictionary<string, string> _aliases;

    // Per-signature tracking: signature -> (family, consistency score) for Leiden
    private readonly ConcurrentDictionary<string, SignatureProfile> _signatureProfiles = new();

    private record SignatureProfile(string Family, string Tier, double ConsistencyScore);

    public UaProfileStore(ILogger<UaProfileStore> logger, double alpha = 0.99)
    {
        _logger = logger;
        _alpha = alpha;
        (_byFamily, _aliases) = LoadProfiles();
        _logger.LogInformation("UaProfileStore: loaded {Count} UA family profiles", _byFamily.Count);
    }

    public string? ResolveFamily(string? uaFamily)
    {
        if (string.IsNullOrEmpty(uaFamily)) return null;
        var key = uaFamily.ToLowerInvariant();
        // Exact match first
        if (_byFamily.ContainsKey(key)) return uaFamily;
        // Alias lookup
        if (_aliases.TryGetValue(key, out var canonical)) return canonical;
        // Prefix match (e.g. "Chrome/124.0" → "chrome")
        var prefixMatch = _aliases.Keys.FirstOrDefault(a => key.StartsWith(a, StringComparison.Ordinal));
        if (prefixMatch != null) return _aliases[prefixMatch];
        return null;
    }

    public LiveCentroid? GetCentroid(string? uaFamily)
    {
        if (string.IsNullOrEmpty(uaFamily)) return null;
        var canonical = ResolveFamily(uaFamily);
        if (canonical == null) return null;
        return _byFamily.GetValueOrDefault(canonical.ToLowerInvariant());
    }

    /// <summary>
    /// Update centroid via EWM with observed dimension values.
    /// Only updates dimensions that were actually observed (skip null/missing).
    /// </summary>
    public void UpdateCentroid(string uaFamily, Dictionary<string, float> observed)
    {
        var centroid = GetCentroid(uaFamily);
        if (centroid == null) return;

        lock (centroid)
        {
            foreach (var (dim, value) in observed)
            {
                if (centroid.Dimensions.TryGetValue(dim, out var d))
                    d.Mean = _alpha * d.Mean + (1.0 - _alpha) * value;
            }
            centroid.SampleCount++;
        }
    }

    /// <summary>Records the last-seen family and consistency score for a signature (for Leiden).</summary>
    public void RecordSignature(string signature, string family, string tier, double score) =>
        _signatureProfiles[signature] = new SignatureProfile(family, tier, score);

    /// <summary>Returns (family, tier, score) for a signature, or null if not tracked.</summary>
    public (string? Family, string? Tier, double Score) GetSignatureProfile(string signature)
    {
        if (_signatureProfiles.TryGetValue(signature, out var p))
            return (p.Family, p.Tier, p.ConsistencyScore);
        return (null, null, 0.5);
    }

    private (Dictionary<string, LiveCentroid>, Dictionary<string, string>) LoadProfiles()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("ua_profiles.yaml", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            _logger.LogWarning("ua_profiles.yaml not found in embedded resources - no UA profiles loaded");
            return (new Dictionary<string, LiveCentroid>(), new Dictionary<string, string>());
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var file = deserializer.Deserialize<UaProfileFile>(reader);
        if (file?.Profiles == null)
            return (new Dictionary<string, LiveCentroid>(), new Dictionary<string, string>());

        var byFamily = new Dictionary<string, LiveCentroid>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in file.Profiles)
        {
            if (string.IsNullOrEmpty(profile.Family)) continue;

            var centroid = new LiveCentroid
            {
                Family = profile.Family,
                Tier = profile.Tier ?? "unknown",
                Dimensions = profile.Dimensions.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new LiveDimension { Mean = kvp.Value.Mean, Weight = kvp.Value.Weight },
                    StringComparer.OrdinalIgnoreCase)
            };

            byFamily[profile.Family.ToLowerInvariant()] = centroid;

            foreach (var alias in profile.Aliases ?? [])
            {
                if (!string.IsNullOrEmpty(alias))
                    aliases[alias.ToLowerInvariant()] = profile.Family.ToLowerInvariant();
            }
            // Self-alias so prefix lookup works
            aliases[profile.Family.ToLowerInvariant()] = profile.Family.ToLowerInvariant();
        }

        return (byFamily, aliases);
    }

    // YAML model
    private class UaProfileFile
    {
        public List<UaProfileEntry>? Profiles { get; set; }
    }

    private class UaProfileEntry
    {
        public string? Family { get; set; }
        public string? Tier { get; set; }
        public List<string>? Aliases { get; set; }
        public Dictionary<string, CentroidDimensionEntry> Dimensions { get; set; } = [];
    }

    private class CentroidDimensionEntry
    {
        public double Mean { get; set; }
        public double Weight { get; set; } = 1.0;
    }
}

/// <summary>Mutable centroid for a UA family; mutated in-place via EWM under lock.</summary>
public sealed class LiveCentroid
{
    public required string Family { get; init; }
    public required string Tier { get; init; }
    public required Dictionary<string, LiveDimension> Dimensions { get; init; }
    public long SampleCount { get; set; }
}

/// <summary>Single mutable dimension mean (EWM), plus its clustering weight.</summary>
public sealed class LiveDimension
{
    public double Mean { get; set; }
    public double Weight { get; init; }
}
