namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     Notification raised on the shared sink after a successful public-key
///     manifest refresh. Payload-free by design beyond light metadata — the
///     <see cref="PublicKeyRegistry"/> holds the authoritative snapshot; the
///     Escalator (<see cref="PublicKeyRegistryAtom"/>) subscribes and re-reads the
///     registry to persist. Same task-#65 shape as <c>BotListUpdatedSignal</c>.
/// </summary>
public sealed record PublicKeyRegistryRefreshedSignal
{
    /// <summary>Named typed key for this signal.</summary>
    public static readonly Mostlylucid.Ephemeral.SignalKey<PublicKeyRegistryRefreshedSignal> Key =
        new("publickey.registry.refreshed");

    /// <summary>When the refresh completed.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Number of keys in the refreshed fetched layer.</summary>
    public int KeyCount { get; init; }

    /// <summary>The manifest URL the keys came from.</summary>
    public required string Source { get; init; }
}
