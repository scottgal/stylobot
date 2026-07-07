using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     Escalator: the membrane between the ephemeral in-memory
///     <see cref="PublicKeyRegistry"/> and durable truth. Subscribes to
///     <see cref="PublicKeyRegistryRefreshedSignal"/> and promotes each refreshed
///     fetched snapshot into an <see cref="IPublicKeySnapshotStore"/>; re-hydrates
///     the registry from that store on cold start so a restart has the
///     last-known-good keys before the first successful fetch. Optional store — no
///     store is a safe no-op. Taxonomy: Escalator.
/// </summary>
public sealed class PublicKeyRegistryAtom : IDisposable
{
    private readonly PublicKeyRegistry _registry;
    private readonly IPublicKeySnapshotStore? _store;
    private readonly Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>? _sink;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Task? _pendingPersist;
    private int _disposed;

    public PublicKeyRegistryAtom(
        PublicKeyRegistry registry,
        IPublicKeySnapshotStore? store = null,
        Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>? refreshedSignals = null,
        ILogger<PublicKeyRegistryAtom>? logger = null)
    {
        _registry = registry;
        _store = store;
        _sink = refreshedSignals;
        _logger = logger ?? NullLogger<PublicKeyRegistryAtom>.Instance;

        // Only subscribe when there's somewhere durable to promote to.
        if (_store is not null && _sink is not null)
            _sink.TypedSignalRaised += OnRefreshed;

        // Best-effort cold-start rehydrate so a restart has the last-known-good
        // keys before the first fetch. Fire-and-forget: an async method captures
        // any synchronous exception into the returned task, so this never throws
        // out of the constructor.
        if (_store is not null)
            _ = RehydrateAsync();
    }

    /// <summary>Loads the durable snapshot into the registry when it has no fetched keys yet.</summary>
    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        if (_store is null) return;
        // A live fetch already populated the registry — don't overwrite it with a stale snapshot.
        if (_registry.FetchedSnapshot().Count > 0) return;

        PublicKeySnapshot? snapshot;
        try
        {
            snapshot = await _store.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PublicKeyRegistry: failed to load durable snapshot on cold start");
            return;
        }

        if (snapshot is null || snapshot.Keys.Count == 0) return;

        _registry.Replace(snapshot.Keys, snapshot.SavedUtc);
        _logger.LogInformation(
            "PublicKeyRegistry: re-hydrated {Count} keys from durable snapshot (saved {SavedUtc:u})",
            snapshot.Keys.Count, snapshot.SavedUtc);
    }

    /// <summary>Test/shutdown helper: awaits any in-flight persist triggered by a refresh signal.</summary>
    public Task WaitForPendingPersistAsync()
    {
        lock (_gate) return _pendingPersist ?? Task.CompletedTask;
    }

    private void OnRefreshed(Mostlylucid.Ephemeral.SignalEvent<PublicKeyRegistryRefreshedSignal> evt)
    {
        if (_disposed != 0 || _store is null) return;
        lock (_gate) _pendingPersist = PersistAsync();
    }

    private async Task PersistAsync()
    {
        if (_store is null) return;
        var snapshot = new PublicKeySnapshot(
            _registry.LastRefreshedUtc ?? DateTimeOffset.UtcNow,
            _registry.FetchedSnapshot());
        try
        {
            await _store.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PublicKeyRegistry: failed to persist snapshot to durable store");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_store is not null && _sink is not null)
            _sink.TypedSignalRaised -= OnRefreshed;
    }
}
