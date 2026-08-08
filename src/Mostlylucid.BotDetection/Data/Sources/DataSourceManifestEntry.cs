using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Data.Sources;

/// <summary>
///     One external fetch source, declared in YAML under <c>Data/Sources/*.source.yaml</c>.
///     This is the single source of truth for what ships as a source's default URL,
///     enabled state, purpose, and licence — <see cref="Models.DataSourceConfig"/> is
///     seeded from this at startup, then <c>appsettings</c>/env overlay per the normal
///     configuration precedence. Never hardcode a source's default URL in C#; add or
///     edit a YAML file here instead.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class DataSourceManifestEntry
{
    /// <summary>Matches the <c>Models.DataSourcesOptions</c> property name this seeds.</summary>
    public string Id { get; set; } = "";

    public string Url { get; set; } = "";

    /// <summary>Optional second URL for a source that fetches two related feeds (e.g. Spamhaus DROP + EDROP). Null for single-URL sources.</summary>
    public string? SecondaryUrl { get; set; }

    public bool Enabled { get; set; }

    /// <summary>Response format: json, yaml, or text. Documentation only — not consumed by the fetcher.</summary>
    public string Format { get; set; } = "text";

    /// <summary>What detection capability degrades without this source.</summary>
    public string Purpose { get; set; } = "";

    /// <summary>Licence/attribution/redistribution terms for shipping a commercial product on top of this feed.</summary>
    public string? Licence { get; set; }
}
