namespace Mostlylucid.BotDetection.Definitions.TlsReference;

/// <summary>
///     Configuration for the opt-in TLS reference corpus refresh service.
///     Off by default. When <see cref="Enabled"/> is true, the background
///     service periodically downloads a signed corpus update from
///     <see cref="RefreshUrl"/>, verifies the Ed25519 signature against
///     <see cref="PublicKey"/> (or the env-var override), and atomically
///     replaces the in-memory <see cref="IJa3ReferenceIndex"/> with the
///     refreshed entries.
///     <para>
///     The public key is NOT a secret -- by definition it's safe to publish.
///     Marking it <c>[Secret]</c> would hide it from operators auditing
///     "is corpus refresh wired to the right key?", which defeats the
///     purpose. The corresponding private key lives in our build / release
///     pipeline only.
///     </para>
///     <para>
///     Env-var override (mirrors the existing licensing convention):
///     <c>STYLOBOT_TLS_CORPUS_PUBLIC_KEY</c>. Useful in development /
///     staging where you want to verify against a non-production keypair
///     without recompiling.
///     </para>
/// </summary>
public class TlsCorpusOptions
{
    /// <summary>Env-var name for the runtime public-key override.</summary>
    public const string OverridePublicKeyEnvironmentVariable = "STYLOBOT_TLS_CORPUS_PUBLIC_KEY";

    /// <summary>
    ///     Whether the refresh background service is registered. Default false.
    ///     With this off the embedded baseline corpus is the only source of
    ///     reference fingerprints.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     HTTPS URL of the signed corpus envelope. Default empty; the
    ///     refresh service refuses to start without a URL configured.
    /// </summary>
    public string RefreshUrl { get; set; } = "";

    /// <summary>
    ///     How often to fetch <see cref="RefreshUrl"/>. Default 6 hours.
    ///     Minimum enforced 5 minutes so a misconfigured value can't hammer
    ///     a public mirror.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    ///     Compile-time / config-time Ed25519 public key in base64-encoded
    ///     raw 32-byte format. Empty means the env-var override is the only
    ///     source. The refresh service refuses to start when no key is
    ///     resolvable from either source.
    /// </summary>
    public string PublicKey { get; set; } = "";

    /// <summary>
    ///     Hard cap on the downloaded envelope size. Default 256 KiB,
    ///     comfortably larger than a realistic corpus (a few KB for the
    ///     starter, low MB at maximum if we ever cover dozens of browser
    ///     versions). Prevents a malicious mirror from feeding us a
    ///     multi-GB payload that exhausts memory before the verifier
    ///     gets a chance to reject it.
    /// </summary>
    public int MaxEnvelopeBytes { get; set; } = 256 * 1024;

    /// <summary>
    ///     Resolves the public key to use for verification, in priority
    ///     order: env-var override, then config value. Returns null when
    ///     neither is set (the refresh service should then refuse to run).
    /// </summary>
    public string? ResolvePublicKey()
    {
        var env = Environment.GetEnvironmentVariable(OverridePublicKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return string.IsNullOrWhiteSpace(PublicKey) ? null : PublicKey.Trim();
    }
}

/// <summary>
///     Signed corpus envelope downloaded from <see cref="TlsCorpusOptions.RefreshUrl"/>.
///     The producer signs <see cref="CorpusYaml"/> with the vendor private
///     Ed25519 key; the verifier rejects the envelope when the signature does
///     not validate under the configured public key.
/// </summary>
public sealed class Ja3CorpusEnvelope
{
    /// <summary>Base64-encoded Ed25519 signature over <see cref="CorpusYaml"/>.</summary>
    public string Signature { get; set; } = "";

    /// <summary>Raw YAML body (the same shape as the embedded corpus).</summary>
    public string CorpusYaml { get; set; } = "";

    /// <summary>UTC ISO-8601 timestamp when the producer signed the envelope.</summary>
    public string GeneratedAt { get; set; } = "";

    /// <summary>
    ///     Optional vendor-controlled key identifier so future rotations can
    ///     advertise which key signed the envelope. Verifier ignores it
    ///     today but the field is reserved for the rotation flow.
    /// </summary>
    public string KeyId { get; set; } = "";
}
