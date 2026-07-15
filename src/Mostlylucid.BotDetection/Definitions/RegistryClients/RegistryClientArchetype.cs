using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Definitions.RegistryClients;

/// <summary>
///     One UA-family seed for the RegistryClient archetype. The UA is only a
///     hint (Model 2) - a family match on its own never lowers the score; it
///     must be corroborated by the registry v2 protocol behaviour.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class RegistryClientFamily
{
    /// <summary>UA substring to match (case-insensitive), e.g. <c>docker/</c>.</summary>
    public string Pattern { get; set; } = "";

    /// <summary>Display name, e.g. <c>Docker</c>, <c>containerd</c>.</summary>
    public string ClientName { get; set; } = "";

    /// <summary>Operator / project, e.g. <c>Docker Inc</c>, <c>CNCF</c>.</summary>
    public string Vendor { get; set; } = "";

    /// <summary>
    ///     Token preceding <c>/&lt;version&gt;</c> in the UA. Null falls back to
    ///     <see cref="Pattern"/> (trimmed of a trailing <c>/</c>).
    /// </summary>
    public string? VersionToken { get; set; }

    /// <summary>
    ///     When true the UA alone is too generic to name a registry client (the
    ///     bare Go HTTP client) - only treated as a registry client when the
    ///     request is on an OCI <c>/v2/</c> path.
    /// </summary>
    public bool RequiresOciPath { get; set; }
}

/// <summary>Scoring knobs for a corroborated registry client (strong good bias, no early-exit).</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class RegistryClientScoring
{
    /// <summary>
    ///     Negative confidence delta applied when the UA family is corroborated by
    ///     registry protocol behaviour. Negative biases the aggregate toward
    ///     low-threat. Deliberately NOT an early exit - detection still runs.
    /// </summary>
    public double CorroboratedConfidenceDelta { get; set; } = -0.8;

    /// <summary>Weight of the corroborated contribution.</summary>
    public double CorroboratedWeight { get; set; } = 2.5;
}

/// <summary>Top-level structure of the <c>registry-client.archetype.yaml</c> seed manifest.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class RegistryClientArchetypeFile
{
    /// <summary>Stable archetype identifier (<c>registry-client</c>).</summary>
    public string ArchetypeId { get; set; } = "";

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Human-readable description.</summary>
    public string? Description { get; set; }

    /// <summary>UA-family seed list (hints only).</summary>
    public List<RegistryClientFamily> Families { get; set; } = [];

    /// <summary>Registry manifest / blob Accept media types (content-negotiation truth).</summary>
    public List<string> AcceptMedia { get; set; } = [];

    /// <summary>Scoring knobs.</summary>
    public RegistryClientScoring Scoring { get; set; } = new();
}
