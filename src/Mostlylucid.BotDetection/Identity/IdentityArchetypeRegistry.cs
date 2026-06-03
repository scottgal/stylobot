using System.Reflection;
using Microsoft.Extensions.Logging;
using VYaml.Serialization;

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
    ///     Returns null if no archetypes are loaded. Always returns the nearest by cosine
    ///     regardless of score — callers that want a minimum-score gate apply it on the
    ///     returned <see cref="ArchetypeMatch.Score"/> (e.g. the matcher gates name signal
    ///     emission via <c>Match.MinArchetypeMatchScore</c> without changing the seeding
    ///     behaviour the rest of the pipeline relies on).
    /// </summary>
    public ArchetypeMatch? FindNearest(float[] vector)
        => FindNearest(vector, uaClaimedBrowser: null);

    /// <summary>
    ///     UA-aware nearest-archetype lookup. When <paramref name="uaClaimedBrowser"/> is
    ///     provided (e.g. "Chrome", "Firefox", "Safari"), archetypes whose ID contradicts
    ///     that browser are excluded from the cosine scan. This is the fix for the
    ///     Chrome-with-adblocker-and-GPC visitor being classified as <c>brave-desktop</c>
    ///     because Brave and Chrome+GPC are indistinguishable on header shape alone --
    ///     once we let the UA filter the candidate set, the matcher can only pick a
    ///     chrome-* archetype (chrome-desktop, chrome-privacy, headless-chrome, etc.)
    ///     for a Chrome-claiming visitor, never a brave-* one. Browser-agnostic
    ///     archetypes (bot/tool families, fediverse, googlebot) always remain in the
    ///     candidate set; they have nothing to disagree with at the UA level. When the
    ///     filter eliminates every candidate, fall back to the full scan so a malformed
    ///     UA cannot starve the matcher of any seed.
    /// </summary>
    public ArchetypeMatch? FindNearest(float[] vector, string? uaClaimedBrowser)
    {
        if (_archetypes.Count == 0) return null;
        IdentityArchetype? best = null;
        var bestScore = double.NegativeInfinity;
        var consideredAny = false;
        foreach (var a in _archetypes)
        {
            if (!IsArchetypeConsistentWithUa(a.ArchetypeId, uaClaimedBrowser))
                continue;
            consideredAny = true;
            var s = BruteForceIdentityAnchorIndex.Cosine(vector, a.Centroid);
            if (s > bestScore)
            {
                bestScore = s;
                best = a;
            }
        }
        if (!consideredAny)
        {
            // UA was something the filter didn't recognise; fall back to unfiltered scan
            // so the matcher still produces a seed (don't strand unknown UAs).
            foreach (var a in _archetypes)
            {
                var s = BruteForceIdentityAnchorIndex.Cosine(vector, a.Centroid);
                if (s > bestScore)
                {
                    bestScore = s;
                    best = a;
                }
            }
        }
        return best is null ? null : new ArchetypeMatch(best, bestScore);
    }

    /// <summary>
    ///     UA-consistency check used by <see cref="FindNearest(float[], string?)"/>.
    ///     Each archetype ID family is tagged with the browser identity it represents
    ///     (chrome-*, brave-*, firefox-*, etc.); the check returns true when that
    ///     family matches the UA-claimed browser, or when the archetype is browser-
    ///     agnostic (bots, tools, fediverse software, generic mobile profiles).
    ///     <c>uaClaimedBrowser == null</c> is the "no UA context" case and always
    ///     returns true (legacy callers still get the full scan).
    /// </summary>
    private static bool IsArchetypeConsistentWithUa(string archetypeId, string? uaClaimedBrowser)
    {
        if (string.IsNullOrEmpty(uaClaimedBrowser)) return true;
        if (string.IsNullOrEmpty(archetypeId)) return true;

        var id = archetypeId.ToLowerInvariant();
        var ua = uaClaimedBrowser.ToLowerInvariant();

        // Browser-agnostic archetypes: bot/tool families, fediverse, search crawlers,
        // and the generic mobile-chrome bucket. These can always match regardless of
        // UA; they're not browser identities.
        if (id.StartsWith("curl") || id.StartsWith("python") || id.StartsWith("wget")
            || id.StartsWith("go-") || id.StartsWith("java") || id.StartsWith("node")
            || id.StartsWith("googlebot") || id.StartsWith("bingbot")
            || id.StartsWith("mastodon") || id.StartsWith("fediverse")
            || id.StartsWith("headless-"))
            return true;

        // Map UA-claimed browser to its archetype prefix(es).
        return ua switch
        {
            "chrome"            => id.StartsWith("chrome") || id == "mobile-chrome",
            "brave"             => id.StartsWith("brave"),
            "firefox"           => id.StartsWith("firefox"),
            "safari"            => id.StartsWith("safari"),
            "edge"              => id.StartsWith("edge"),
            "opera"             => id.StartsWith("opera"),
            "vivaldi"           => id.StartsWith("vivaldi"),
            "internet explorer" => id.StartsWith("ie") || id.StartsWith("internet-explorer"),
            _ => true // unknown UA family: don't filter, let cosine decide
        };
    }

    /// <summary>
    ///     Cosine of <paramref name="vector"/> against one specific archetype.
    ///     Used by the dashboard's drift surface to score the fingerprint
    ///     against its <c>ArchetypeOrigin</c> (the prior it was anchored at)
    ///     alongside the current-nearest from <see cref="FindNearest"/>.
    ///     Returns null when the archetype is null or shapes don't match.
    /// </summary>
    public double? ScoreAgainst(float[] vector, IdentityArchetype? archetype)
    {
        if (archetype is null || vector is null) return null;
        if (vector.Length != archetype.Centroid.Length) return null;
        return BruteForceIdentityAnchorIndex.Cosine(vector, archetype.Centroid);
    }

    private IReadOnlyList<IdentityArchetype> LoadFromEmbeddedResources()
    {
        var assembly = typeof(IdentityArchetypeRegistry).Assembly;

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
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var dto = YamlSerializer.Deserialize<IdentityArchetypeYaml>(ms.ToArray());
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

}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class IdentityArchetypeYaml
{
    public string? ArchetypeId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ArchetypeKind { get; set; }
    public Dictionary<string, IdentityArchetypeDimensionYaml>? Dimensions { get; set; }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class IdentityArchetypeDimensionYaml
{
    public object? Value { get; set; }
    public double Confidence { get; set; } = 1.0;
}
