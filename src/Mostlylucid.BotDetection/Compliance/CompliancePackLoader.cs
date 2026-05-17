using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Compliance;

/// <summary>Loads compliance packs from embedded YAML resources.</summary>
public sealed class CompliancePackLoader
{
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
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var pack = YamlSerializer.Deserialize<CompliancePack>(ms.ToArray());
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
        return YamlSerializer.Deserialize<CompliancePack>(Encoding.UTF8.GetBytes(yaml));
    }
}
