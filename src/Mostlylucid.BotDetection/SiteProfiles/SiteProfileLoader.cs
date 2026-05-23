using System.Reflection;
using Microsoft.Extensions.Logging;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.SiteProfiles;

/// <summary>
///     Loads embedded <see cref="SiteProfile"/> YAMLs from the assembly's
///     <c>SiteProfiles/Profiles/*.yaml</c> resources. Read-only after init.
/// </summary>
public interface ISiteProfileCatalog
{
    /// <summary>All loaded profiles, keyed by profile id (lowercase).</summary>
    IReadOnlyDictionary<string, SiteProfile> Profiles { get; }

    /// <summary>Looks up a profile by id (case-insensitive). Null if unknown.</summary>
    SiteProfile? Get(string id);
}

internal sealed class SiteProfileCatalog : ISiteProfileCatalog
{
    private readonly Dictionary<string, SiteProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SiteProfileCatalog> _logger;

    public SiteProfileCatalog(ILogger<SiteProfileCatalog> logger)
    {
        _logger = logger;
        LoadEmbedded();
    }

    public IReadOnlyDictionary<string, SiteProfile> Profiles => _profiles;

    public SiteProfile? Get(string id) =>
        _profiles.TryGetValue(id, out var p) ? p : null;

    private void LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".SiteProfiles.Profiles.") && n.EndsWith(".yaml"))
            .ToArray();

        foreach (var name in resources)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var profile = YamlSerializer.Deserialize<SiteProfile>(ms.ToArray());
                if (string.IsNullOrEmpty(profile.Id))
                {
                    _logger.LogWarning("SiteProfile resource {Name} has empty id; skipping", name);
                    continue;
                }
                _profiles[profile.Id] = profile;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load site profile resource {Name}", name);
            }
        }

        _logger.LogInformation("Loaded {Count} site profiles: {Ids}",
            _profiles.Count, string.Join(", ", _profiles.Keys));
    }
}
