namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     Registry of known AI-agent signing keys used to verify RFC 9421 HTTP
///     Message Signatures (Web Bot Auth). The source of truth is a JSON manifest
///     fetched from a configured URL (Cloudflare publishes the AI-agent registry;
///     anyone can host their own) plus any operator-supplied manual keys.
///     <para>
///         Taxonomy: <b>Coordinator</b> (per the ephemeral pyramid). Holds an
///         immutable in-memory snapshot swapped atomically by the refresh
///         coordinator; a durability wrapper (<see cref="PublicKeyRegistryAtom"/>,
///         an Escalator) promotes fetched snapshots into a durable store so a
///         cold start has the last-known-good keys before the first fetch.
///     </para>
///     <para>
///         Contract C1 from <c>docs/superpowers/specs/2026-07-07-agent-native-web-shape.md</c>.
///         Locked — do not re-invent.
///     </para>
/// </summary>
public interface IPublicKeyRegistry
{
    /// <summary>Non-blocking lookup by kid (JOSE-style key id).</summary>
    bool TryResolve(string keyId, out PublicKeyEntry entry);

    /// <summary>Snapshot of the whole registry — dashboard visibility.</summary>
    IReadOnlyList<PublicKeyEntry> Snapshot();

    /// <summary>Timestamp of the most recent successful refresh (or null if never).</summary>
    DateTimeOffset? LastRefreshedUtc { get; }
}

/// <summary>
///     One known-agent signing key. <see cref="PublicKey"/> is the raw key
///     material whose encoding depends on <see cref="Algorithm"/>: for
///     <c>ed25519</c> it is the 32-byte raw public key; for
///     <c>ecdsa-p256-sha256</c> it is the SubjectPublicKeyInfo (SPKI) DER blob.
/// </summary>
public sealed record PublicKeyEntry(
    string KeyId,
    string AgentName,            // e.g. "GPTBot", "PerplexityBot"
    ReadOnlyMemory<byte> PublicKey,
    string Algorithm,             // "ed25519", "ecdsa-p256-sha256", etc. — matches RFC 9421 alg names
    DateTimeOffset? NotAfter,     // key rotation
    string Source);               // manifest URL the key came from