namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     Configuration surface for the Web-Bot-Auth public-key registry (contract
///     C7). Every knob lives here — no magic numbers in code
///     (<c>feedback_all_settings_configurable</c>). Bound from
///     <c>BotDetection:PublicKeyRegistry</c>.
/// </summary>
public sealed class PublicKeyRegistryOptions
{
    /// <summary>Configuration section name when binding from <c>IConfiguration</c>.</summary>
    public const string SectionName = "BotDetection:PublicKeyRegistry";

    /// <summary>
    ///     Master switch for the Web-Bot-Auth foundation. Default <c>false</c>
    ///     (per the rollout-gates section of the shape doc). When <c>false</c>
    ///     the registry stays empty, no manifest is fetched, and manual keys are
    ///     not seeded — the foundation is dormant.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Absolute HTTPS URL of the JSON public-key manifest (Cloudflare's
    ///     AI-agent registry, or a self-hosted mirror). Empty = no remote fetch;
    ///     the registry then serves only <see cref="ManualKeys"/> (and any durable
    ///     snapshot). Matches the well-known-bots "empty Url is a no-op" shape.
    /// </summary>
    public string ManifestUrl { get; set; } = "";

    /// <summary>
    ///     How often the refresh coordinator re-fetches the manifest. Fetches are
    ///     driven by the hourly <c>Tick1h</c>; a value below one hour is clamped up
    ///     to the tick period. Default 1 hour.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     Response-size cap for the manifest download so a malicious or misbehaving
    ///     mirror cannot exhaust memory. Default 2 MB (same as the well-known-bots
    ///     catalog).
    /// </summary>
    public long MaxResponseBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    ///     Operator-supplied keys that are always trusted regardless of the remote
    ///     manifest. They win on a keyId collision and survive every refresh. This
    ///     is how the staging probe verifies a hand-signed request without a live
    ///     Cloudflare registry.
    /// </summary>
    public List<ManualPublicKeyOptions> ManualKeys { get; set; } = [];

    /// <summary>
    ///     Optional path to a durable snapshot file. When set, the registry's
    ///     Escalator (<see cref="PublicKeyRegistryAtom"/>) writes each fetched
    ///     snapshot here and re-hydrates from it on cold start so a restart has the
    ///     last-known-good keys before the first successful fetch. Null/empty =
    ///     durability disabled (rely on live fetch + manual keys).
    /// </summary>
    public string? SnapshotFilePath { get; set; }
}

/// <summary>An operator-supplied trusted signing key (see <see cref="PublicKeyRegistryOptions.ManualKeys"/>).</summary>
public sealed class ManualPublicKeyOptions
{
    /// <summary>JOSE-style key id (kid) matched against the request's Signature-Input.</summary>
    public string KeyId { get; set; } = "";

    /// <summary>Friendly agent name shown in the dashboard and used as the verified identity.</summary>
    public string AgentName { get; set; } = "";

    /// <summary>Base64 public key material; encoding is algorithm-specific (see <see cref="PublicKeyEntry.PublicKey"/>).</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>RFC 9421 algorithm name, e.g. <c>ed25519</c> or <c>ecdsa-p256-sha256</c>.</summary>
    public string Algorithm { get; set; } = "ed25519";

    /// <summary>Optional key-rotation expiry.</summary>
    public DateTimeOffset? NotAfter { get; set; }
}
