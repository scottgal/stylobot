using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Data.Sources;

/// <summary>
///     Loads external-fetch-source manifests from embedded <c>*.source.yaml</c> resources.
///     Mirrors <see cref="Compliance.CompliancePackLoader"/> and
///     <see cref="Orchestration.Manifests.DetectorManifestLoader"/>: YAML is the shipped
///     default, <c>appsettings</c>/env overlay at options-configure time.
/// </summary>
public sealed class DataSourceManifestLoader
{
    private readonly ILogger<DataSourceManifestLoader> _logger;
    private readonly Lock _lock = new();
    private IReadOnlyDictionary<string, DataSourceManifestEntry>? _cached;

    public DataSourceManifestLoader(ILogger<DataSourceManifestLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<DataSourceManifestLoader>.Instance;
    }

    /// <summary>
    ///     Loaded once and cached — embedded resources never change at runtime, and
    ///     <see cref="DataSourcesYamlDefaultsConfigurator"/> re-runs on every options
    ///     rebuild (e.g. config-reload), so re-parsing YAML each time would be wasted work.
    /// </summary>
    public IReadOnlyDictionary<string, DataSourceManifestEntry> LoadEmbeddedManifests()
    {
        if (_cached is not null) return _cached;
        lock (_lock)
        {
            if (_cached is not null) return _cached;
            return _cached = LoadEmbeddedManifestsCore();
        }
    }

    private IReadOnlyDictionary<string, DataSourceManifestEntry> LoadEmbeddedManifestsCore()
    {
        var entries = new Dictionary<string, DataSourceManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".source.yaml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var entry = YamlSerializer.Deserialize<DataSourceManifestEntry>(ms.ToArray());

                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    _logger.LogError("Fetch-source manifest {Resource} has no id; skipping", resourceName);
                    continue;
                }

                entries[entry.Id] = entry;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse fetch-source manifest {Resource}; skipping", resourceName);
            }
        }

        return entries;
    }
}
