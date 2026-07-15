using System.Reflection;
using Microsoft.Extensions.Logging;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Definitions.RegistryClients;

/// <summary>
///     Loads the RegistryClient archetype seed (UA families + registry Accept
///     media + scoring knobs) from the embedded
///     <c>Definitions/RegistryClients/*.archetype.yaml</c> resource(s).
///     Mirrors <see cref="BotPatterns.BotPatternLoader"/>: static
///     <see cref="Default"/> for non-DI callers, per-resource fault isolation.
/// </summary>
public sealed class RegistryClientCatalog
{
    private static readonly Lazy<RegistryClientCatalog> _default = new(() => new RegistryClientCatalog());

    /// <summary>Process-wide default instance (embedded seed).</summary>
    public static RegistryClientCatalog Default => _default.Value;

    public RegistryClientCatalog(ILogger? logger = null)
    {
        var file = LoadFromEmbeddedResources(logger);
        Families = file.Families
            .Where(f => !string.IsNullOrEmpty(f.Pattern) && !string.IsNullOrEmpty(f.ClientName))
            .ToList();
        AcceptMedia = file.AcceptMedia.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        CorroboratedConfidenceDelta = file.Scoring.CorroboratedConfidenceDelta;
        CorroboratedWeight = file.Scoring.CorroboratedWeight;
    }

    /// <summary>UA-family seeds (hints only).</summary>
    public IReadOnlyList<RegistryClientFamily> Families { get; }

    /// <summary>Registry manifest / blob Accept media types.</summary>
    public IReadOnlyList<string> AcceptMedia { get; }

    /// <summary>Negative confidence delta for a corroborated registry client.</summary>
    public double CorroboratedConfidenceDelta { get; }

    /// <summary>Weight of the corroborated contribution.</summary>
    public double CorroboratedWeight { get; }

    private static RegistryClientArchetypeFile LoadFromEmbeddedResources(ILogger? logger)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("Definitions.RegistryClients") && n.EndsWith(".archetype.yaml"))
            .OrderBy(n => n);

        var merged = new RegistryClientArchetypeFile();

        foreach (var resourceName in resources)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    logger?.LogWarning("Could not load registry-client resource: {Resource}", resourceName);
                    continue;
                }

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var file = YamlSerializer.Deserialize<RegistryClientArchetypeFile>(ms.ToArray());
                if (file is null) continue;

                if (file.Families is { Count: > 0 }) merged.Families.AddRange(file.Families);
                if (file.AcceptMedia is { Count: > 0 }) merged.AcceptMedia.AddRange(file.AcceptMedia);
                // Last-writer-wins for scoring; a single seed file is the norm.
                merged.Scoring = file.Scoring;
                if (!string.IsNullOrEmpty(file.ArchetypeId)) merged.ArchetypeId = file.ArchetypeId;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error loading registry-client archetype from {Resource}", resourceName);
            }
        }

        logger?.LogInformation(
            "RegistryClientCatalog: loaded {Families} UA families, {Media} accept-media types",
            merged.Families.Count, merged.AcceptMedia.Count);
        return merged;
    }
}
