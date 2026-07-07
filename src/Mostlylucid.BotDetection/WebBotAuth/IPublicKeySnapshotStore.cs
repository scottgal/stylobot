namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     Durable persistence for the last-known-good fetched key snapshot. Optional:
///     when no store is registered, the registry relies on live fetch + manual
///     keys. When present, the Escalator (<see cref="PublicKeyRegistryAtom"/>)
///     saves each refreshed snapshot and re-hydrates from it on cold start so a
///     restart has keys before the first successful fetch.
/// </summary>
public interface IPublicKeySnapshotStore
{
    /// <summary>Loads the last persisted snapshot, or null if none / unreadable.</summary>
    Task<PublicKeySnapshot?> LoadAsync(CancellationToken ct = default);

    /// <summary>Persists the snapshot, replacing any previous one.</summary>
    Task SaveAsync(PublicKeySnapshot snapshot, CancellationToken ct = default);
}

/// <summary>A point-in-time durable snapshot of the fetched key layer.</summary>
public sealed record PublicKeySnapshot(DateTimeOffset SavedUtc, IReadOnlyList<PublicKeyEntry> Keys);
