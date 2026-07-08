using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     Wire schema for a public-key manifest — the JSON document fetched from
///     <c>PublicKeyRegistryOptions.ManifestUrl</c> (Cloudflare's AI-agent registry,
///     or any self-hosted mirror). Deliberately small and forward-compatible:
///     unknown fields are ignored, and a single malformed key entry is skipped
///     rather than failing the whole refresh.
///     <code>
///     {
///       "version": 1,
///       "keys": [
///         { "keyId": "…", "agentName": "GPTBot", "publicKey": "&lt;base64&gt;",
///           "algorithm": "ed25519", "notAfter": "2027-01-01T00:00:00Z" }
///       ]
///     }
///     </code>
/// </summary>
public sealed class PublicKeyManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("keys")]
    public List<PublicKeyManifestEntry> Keys { get; set; } = [];
}

/// <summary>One key row in a <see cref="PublicKeyManifest"/>.</summary>
public sealed class PublicKeyManifestEntry
{
    [JsonPropertyName("keyId")]
    public string? KeyId { get; set; }

    [JsonPropertyName("agentName")]
    public string? AgentName { get; set; }

    /// <summary>Base64 public key material. Encoding is algorithm-specific (see <see cref="PublicKeyEntry.PublicKey"/>).</summary>
    [JsonPropertyName("publicKey")]
    public string? PublicKey { get; set; }

    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; set; }

    [JsonPropertyName("notAfter")]
    public DateTimeOffset? NotAfter { get; set; }
}

/// <summary>
///     Source-generated <see cref="JsonSerializerContext"/> for the manifest.
///     Reflection-based JSON is disabled under <c>PublishAot=true</c>, so the
///     refresh coordinator and the durable snapshot store deserialize through
///     this context (same rationale as <c>WellKnownBotsJsonContext</c>).
/// </summary>
[JsonSerializable(typeof(PublicKeyManifest))]
internal sealed partial class PublicKeyManifestJsonContext : JsonSerializerContext;
