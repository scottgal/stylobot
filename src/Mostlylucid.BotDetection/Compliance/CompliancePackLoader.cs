using System.Reflection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.Compliance;

/// <summary>Loads compliance packs from embedded YAML resources.</summary>
public sealed class CompliancePackLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<CompliancePack> LoadEmbeddedPacks(ILogger? logger = null)
    {
        var assembly = typeof(CompliancePackLoader).Assembly;
        var packs = new List<CompliancePack>();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".yaml", StringComparison.Ordinal))
                     .Where(n => n.Contains("CompliancePacks"))
                     .OrderBy(n => n))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                using var reader = new StreamReader(stream);
                var yaml = reader.ReadToEnd();
                var pack = Deserializer.Deserialize<CompliancePack>(yaml);
                packs.Add(pack);
                logger?.LogInformation("Loaded compliance pack: {PackId} ({PackName})", pack.Id, pack.Name);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to load compliance pack from {Resource}", resourceName);
            }
        }

        return packs;
    }

    public static CompliancePack? LoadFromYaml(string yaml)
    {
        return Deserializer.Deserialize<CompliancePack>(yaml);
    }
}
