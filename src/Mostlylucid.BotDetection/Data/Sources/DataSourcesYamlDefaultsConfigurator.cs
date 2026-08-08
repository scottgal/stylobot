using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Data.Sources;

/// <summary>
///     Seeds <see cref="DataSourcesOptions"/> from the YAML manifests in
///     <c>Data/Sources/*.source.yaml</c>. Registered as an <see cref="IConfigureOptions{TOptions}"/>
///     for <see cref="BotDetectionOptions"/> that must run BEFORE the
///     <c>appsettings</c>/env configuration-binding Configure call (see
///     <c>ServiceCollectionExtensions.AddBotDetection</c>) — <see cref="ConfigurationBinder"/>
///     only overwrites properties actually present in configuration, so running this
///     first gives the standard precedence: YAML ships the default, config overrides it,
///     including turning a source off entirely.
///     <para>
///         Explicit id-to-property mapping (no reflection) so a manifest with a typo'd
///         id fails loudly via <see cref="UnmappedManifestIdsException"/> instead of
///         silently never applying.
///     </para>
/// </summary>
public sealed class DataSourcesYamlDefaultsConfigurator : IConfigureOptions<BotDetectionOptions>
{
    private readonly DataSourceManifestLoader _loader;

    public DataSourcesYamlDefaultsConfigurator(DataSourceManifestLoader loader)
    {
        _loader = loader;
    }

    public void Configure(BotDetectionOptions options)
    {
        var manifest = _loader.LoadEmbeddedManifests();
        var sources = options.DataSources ??= new DataSourcesOptions();
        var unmapped = new List<string>(manifest.Keys);

        Apply(sources.IsBot, manifest, nameof(DataSourcesOptions.IsBot), unmapped);
        Apply(sources.Matomo, manifest, nameof(DataSourcesOptions.Matomo), unmapped);
        Apply(sources.CrawlerUserAgents, manifest, nameof(DataSourcesOptions.CrawlerUserAgents), unmapped);
        Apply(sources.AwsIpRanges, manifest, nameof(DataSourcesOptions.AwsIpRanges), unmapped);
        Apply(sources.GcpIpRanges, manifest, nameof(DataSourcesOptions.GcpIpRanges), unmapped);
        Apply(sources.AzureIpRanges, manifest, nameof(DataSourcesOptions.AzureIpRanges), unmapped);
        Apply(sources.CloudflareIpv4, manifest, nameof(DataSourcesOptions.CloudflareIpv4), unmapped);
        Apply(sources.CloudflareIpv6, manifest, nameof(DataSourcesOptions.CloudflareIpv6), unmapped);
        Apply(sources.VpnAsns, manifest, nameof(DataSourcesOptions.VpnAsns), unmapped);
        Apply(sources.BrowserVersions, manifest, nameof(DataSourcesOptions.BrowserVersions), unmapped);
        Apply(sources.ScannerUserAgents, manifest, nameof(DataSourcesOptions.ScannerUserAgents), unmapped);
        Apply(sources.CoreRuleSetScanners, manifest, nameof(DataSourcesOptions.CoreRuleSetScanners), unmapped);

        // A YAML file whose id doesn't match any DataSourcesOptions property is dead
        // weight that will never take effect - exactly the drift this exists to prevent.
        if (unmapped.Count > 0)
            throw new UnmappedManifestIdsException(unmapped);
    }

    private static void Apply(
        DataSourceConfig target,
        IReadOnlyDictionary<string, DataSourceManifestEntry> manifest,
        string id,
        List<string> unmapped)
    {
        if (!manifest.TryGetValue(id, out var entry)) return;
        unmapped.Remove(id);

        target.Url = entry.Url;
        target.Enabled = entry.Enabled;
        target.Description = entry.Purpose;
        target.Licence = entry.Licence;
    }
}

/// <summary>Thrown when a <c>*.source.yaml</c> manifest's id has no matching <see cref="DataSourcesOptions"/> property.</summary>
public sealed class UnmappedManifestIdsException : Exception
{
    public UnmappedManifestIdsException(IReadOnlyCollection<string> ids)
        : base($"Fetch-source manifest id(s) not mapped to any DataSourcesOptions property: {string.Join(", ", ids)}")
    {
    }
}
