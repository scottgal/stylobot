using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Definitions.TlsReference;

/// <summary>
///     One JA3/JA4 reference entry for a specific browser+version+platform.
///     Loaded from <c>tls-reference-corpus.yaml</c>.
/// </summary>
public sealed class Ja3Reference
{
    /// <summary>Lowercase browser family ("chrome", "firefox", "safari", "edge", ...).</summary>
    public string Browser { get; init; } = "";

    /// <summary>Browser major version (e.g. 120).</summary>
    public int Version { get; init; }

    /// <summary>True for the mobile variant of this browser+version, false for desktop.</summary>
    public bool Mobile { get; init; }

    /// <summary>MD5 hash of the canonical JA3 string. Lowercase hex.</summary>
    public string Ja3Hash { get; init; } = "";

    /// <summary>Raw JA3 string: <c>SSLVersion,Cipher,SSLExtension,EllipticCurve,EllipticCurvePointFormat</c>.</summary>
    public string Ja3String { get; init; } = "";

    /// <summary>Optional JA4 fingerprint (Cloudflare's structured successor).</summary>
    public string? Ja4 { get; init; }

    /// <summary>Optional HTTP/2 SETTINGS frame order.</summary>
    public string? Http2Settings { get; init; }

    /// <summary>Citation for where this entry came from (writeup URL, capture date, etc.).</summary>
    public string? Source { get; init; }
}

// === YAML deserialisation shapes ===========================================
// VYaml reflects directly onto these. The shapes mirror the YAML schema
// at tls-reference-corpus.yaml.

/// <summary>Root of the corpus YAML.</summary>
[YamlObject]
public partial class Ja3CorpusFile
{
    [YamlMember("version")] public int Version { get; set; }
    [YamlMember("generated_at")] public string? GeneratedAt { get; set; }
    [YamlMember("references")] public Dictionary<string, Dictionary<int, Ja3CorpusPlatforms>>? References { get; set; }
}

/// <summary>Desktop + mobile variants for one browser version.</summary>
[YamlObject]
public partial class Ja3CorpusPlatforms
{
    [YamlMember("desktop")] public Ja3CorpusEntry? Desktop { get; set; }
    [YamlMember("mobile")] public Ja3CorpusEntry? Mobile { get; set; }
}

/// <summary>One JA3/JA4 reference entry as stored in the YAML.</summary>
[YamlObject]
public partial class Ja3CorpusEntry
{
    [YamlMember("ja3")] public string? Ja3 { get; set; }
    [YamlMember("ja3_string")] public string? Ja3String { get; set; }
    [YamlMember("ja4")] public string? Ja4 { get; set; }
    [YamlMember("h2_settings")] public string? H2Settings { get; set; }
    [YamlMember("source")] public string? Source { get; set; }
}
