using System.Reflection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Loads identity archetypes from embedded YAML files in
///     <c>Definitions/IdentityArchetypes/*.yaml</c>, compiles each into a layout-conformant
///     <see cref="IdentityArchetype"/> with centroid + dimension_mask blobs, and exposes a
///     nearest-archetype scan over the in-memory set.
///
///     Archetypes are read-only at runtime; the calibration service refreshes them on its own
///     cycle. Same singleton holds both the YAML-seeded and the calibration-refined versions.
/// </summary>
public sealed class IdentityArchetypeRegistry
{
    private readonly ILogger<IdentityArchetypeRegistry> _logger;
    private readonly IdentityVectorEncoder _encoder;
    private IReadOnlyList<IdentityArchetype> _archetypes;

    public IdentityArchetypeRegistry(
        ILogger<IdentityArchetypeRegistry> logger,
        IdentityVectorEncoder encoder)
    {
        _logger = logger;
        _encoder = encoder;
        _archetypes = LoadFromEmbeddedResources();
        _logger.LogInformation("Loaded {Count} identity archetypes from embedded resources", _archetypes.Count);
    }

    public IReadOnlyList<IdentityArchetype> All => _archetypes;

    /// <summary>
    ///     Replace the in-memory archetype set (used by the calibration service after refining
    ///     each archetype's centroid against its descendants' mean).
    /// </summary>
    public void Replace(IReadOnlyList<IdentityArchetype> refreshed)
    {
        _archetypes = refreshed;
    }

    /// <summary>
    ///     Lookup by archetype id (case-insensitive). Returns null when the id is null, empty,
    ///     or not present in the registry. Used by <see cref="FingerprintMatchContributor"/>
    ///     to resolve a matched fingerprint's <c>InferredClientType</c> to a display name
    ///     without iterating <see cref="All"/> on every match.
    /// </summary>
    public IdentityArchetype? TryGetById(string? archetypeId)
    {
        if (string.IsNullOrEmpty(archetypeId)) return null;
        foreach (var a in _archetypes)
            if (string.Equals(a.ArchetypeId, archetypeId, StringComparison.OrdinalIgnoreCase))
                return a;
        return null;
    }

    /// <summary>
    ///     Brute-force scan: cosine of <paramref name="vector"/> against every archetype.
    ///     Archetype set is small (tens of entries), so the scan is fast and needs no index.
    ///     Returns null if no archetypes are loaded.
    /// </summary>
    public ArchetypeMatch? FindNearest(float[] vector)
    {
        if (_archetypes.Count == 0) return null;
        IdentityArchetype? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var a in _archetypes)
        {
            var s = BruteForceIdentityAnchorIndex.Cosine(vector, a.Centroid);
            if (s > bestScore)
            {
                bestScore = s;
                best = a;
            }
        }
        return best is null ? null : new ArchetypeMatch(best, bestScore);
    }

    private IReadOnlyList<IdentityArchetype> LoadFromEmbeddedResources()
    {
        var assembly = typeof(IdentityArchetypeRegistry).Assembly;
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var results = new List<IdentityArchetype>();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains("IdentityArchetypes", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!resourceName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                var dto = deserializer.Deserialize<IdentityArchetypeYaml>(reader);
                if (dto is null || string.IsNullOrEmpty(dto.ArchetypeId)) continue;

                var compiled = Compile(dto);
                results.Add(compiled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load identity archetype from {Resource}", resourceName);
            }
        }
        return results;
    }

    private IdentityArchetype Compile(IdentityArchetypeYaml dto)
    {
        // Build the raw-values dictionary the encoder consumes. Each named YAML dimension fills
        // its corresponding slot; unnamed dimensions stay absent (encoder leaves them at 0).
        var rawValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in dto.Dimensions ?? new())
            rawValues[name] = entry.Value;

        var centroid = _encoder.Encode(rawValues);

        // Mask: for every dim the YAML asserts, set the slot's full width to that confidence.
        // Anything the YAML doesn't mention stays at 0 (the archetype makes no claim about it).
        var mask = new float[_encoder.Layout.Dimension];
        foreach (var (name, entry) in dto.Dimensions ?? new())
        {
            var slot = _encoder.Layout.FindSlot(name);
            if (slot is null) continue;
            var confidence = (float)Math.Clamp(entry.Confidence, 0.0, 1.0);
            for (var i = slot.Offset; i < slot.Offset + slot.Width; i++)
                mask[i] = confidence;
        }

        return new IdentityArchetype
        {
            ArchetypeId = dto.ArchetypeId,
            Name = dto.Name ?? dto.ArchetypeId,
            Description = dto.Description,
            ArchetypeKind = dto.ArchetypeKind ?? "unknown",
            Centroid = centroid,
            DimensionMask = mask,
            DescendantCount = 0,
            LastRefinedAt = DateTime.UtcNow
        };
    }

    private sealed class IdentityArchetypeYaml
    {
        public string? ArchetypeId { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? ArchetypeKind { get; set; }
        public Dictionary<string, IdentityArchetypeDimensionYaml>? Dimensions { get; set; }
    }

    private sealed class IdentityArchetypeDimensionYaml
    {
        public object? Value { get; set; }
        public double Confidence { get; set; } = 1.0;
    }
}
