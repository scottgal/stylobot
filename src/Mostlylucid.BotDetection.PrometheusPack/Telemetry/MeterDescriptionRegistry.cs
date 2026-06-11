using System.Reflection;
using Microsoft.Extensions.Logging;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.PrometheusPack.Telemetry;

/// <summary>
///     Looks up human-readable labels + descriptions for instrument names. The
///     /dashboard/insights table, every <c>SbMeterTable</c> caller and every
///     <c>SbStatTile</c>/<c>SbTrendCard</c> builder run instrument names through
///     this registry so the UI shows "Pack ingest" / "Total request hits the
///     AspNet pack has recorded" instead of the raw Prometheus key
///     <c>aspnet_pack_middleware_requests_total</c>.
///
///     Catalogs live in YAML, embedded as resources under
///     <c>MeterCatalogs/*.yaml</c>. Pack assemblies may register additional
///     source assemblies via <see cref="MeterDescriptionRegistryOptions.Assemblies"/>
///     so each pack ships descriptions for the meters it owns.
///
///     Lookups are name-canonical: meter names with dots
///     (<c>aspnet_pack.middleware.requests_total</c> from the in-process Meter
///     source) and meter names with underscores
///     (<c>aspnet_pack_middleware_requests_total</c> from the Prometheus
///     exporter) both resolve to the same entry.
/// </summary>
public sealed class MeterDescriptionRegistry
{
    private readonly Dictionary<string, MeterDescription> _byCanonical;

    public MeterDescriptionRegistry(
        MeterDescriptionRegistryOptions options,
        ILogger<MeterDescriptionRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _byCanonical = new Dictionary<string, MeterDescription>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in options.Assemblies.Distinct())
        {
            LoadFromAssembly(assembly, logger);
        }

        logger?.LogInformation(
            "MeterDescriptionRegistry loaded {Count} entries from {AsmCount} assemblies",
            _byCanonical.Count, options.Assemblies.Count);
    }

    public MeterDescription? TryGet(string meterName)
    {
        if (string.IsNullOrEmpty(meterName)) return null;
        return _byCanonical.GetValueOrDefault(Canonicalize(meterName));
    }

    internal static string Canonicalize(string meterName) =>
        meterName.Replace('.', '_');

    private void LoadFromAssembly(Assembly assembly, ILogger? logger)
    {
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains("MeterCatalogs", StringComparison.OrdinalIgnoreCase)) continue;
            if (!resourceName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var dto = YamlSerializer.Deserialize<MeterCatalogYaml>(ms.ToArray());
                if (dto?.Entries is null) continue;

                foreach (var entry in dto.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                    var key = Canonicalize(entry.Name);
                    _byCanonical[key] = new MeterDescription(
                        Name: entry.Name,
                        Label: entry.Label ?? PrettifyName(entry.Name),
                        Description: entry.Description,
                        Unit: entry.Unit);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "MeterDescriptionRegistry: failed to load {Resource} from {Assembly}",
                    resourceName, assembly.GetName().Name);
            }
        }
    }

    internal static string PrettifyName(string raw)
    {
        var collapsed = raw.Replace('.', '_').Replace('_', ' ').Trim();
        if (collapsed.Length == 0) return raw;
        var chars = collapsed.ToCharArray();
        chars[0] = char.ToUpperInvariant(chars[0]);
        return new string(chars);
    }
}

public sealed record MeterDescription(
    string Name,
    string Label,
    string? Description,
    string? Unit);

public sealed class MeterDescriptionRegistryOptions
{
    public IList<Assembly> Assemblies { get; } = new List<Assembly>();
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class MeterCatalogYaml
{
    public string? Family { get; set; }
    public List<MeterCatalogEntryYaml>? Entries { get; set; }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class MeterCatalogEntryYaml
{
    public string? Name { get; set; }
    public string? Label { get; set; }
    public string? Description { get; set; }
    public string? Unit { get; set; }
}
