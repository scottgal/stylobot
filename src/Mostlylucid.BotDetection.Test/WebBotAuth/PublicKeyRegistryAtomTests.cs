using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.WebBotAuth;

namespace Mostlylucid.BotDetection.Test.WebBotAuth;

/// <summary>
///     Unit tests for <see cref="PublicKeyRegistryAtom"/> — the Escalator that
///     persists each refreshed snapshot to a durable store and re-hydrates the
///     registry from it on cold start. Optional store: no store is a safe no-op.
/// </summary>
public sealed class PublicKeyRegistryAtomTests
{
    private sealed class InMemorySnapshotStore : IPublicKeySnapshotStore
    {
        public PublicKeySnapshot? Saved { get; private set; }
        public int SaveCalls;

        public Task<PublicKeySnapshot?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);

        public Task SaveAsync(PublicKeySnapshot snapshot, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SaveCalls);
            Saved = snapshot;
            return Task.CompletedTask;
        }
    }

    private static PublicKeyEntry Entry(string keyId)
        => new(keyId, "GPTBot", new byte[] { 1, 2, 3 }, "ed25519", null, "https://feed");

    private static Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal> NewSink()
        => new(new Mostlylucid.Ephemeral.SignalSink(maxCapacity: 8, maxAge: TimeSpan.FromMinutes(5)),
            maxCapacity: 8, maxAge: TimeSpan.FromMinutes(5));

    private static void RaiseRefreshed(Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal> sink, int count)
        => sink.Raise(PublicKeyRegistryRefreshedSignal.Key.Name, new PublicKeyRegistryRefreshedSignal
        {
            Timestamp = DateTimeOffset.UtcNow, KeyCount = count, Source = "https://feed"
        });

    private static PublicKeyRegistryAtom Make(
        PublicKeyRegistry registry, IPublicKeySnapshotStore? store,
        Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>? sink)
        => new(registry, store, sink, NullLogger<PublicKeyRegistryAtom>.Instance);

    // ── Rehydrate ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rehydrate_seeds_registry_from_stored_snapshot_when_empty()
    {
        var savedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var store = new InMemorySnapshotStore();
        await store.SaveAsync(new PublicKeySnapshot(savedAt, [Entry("kid-1")]));
        var registry = new PublicKeyRegistry();

        using var atom = Make(registry, store, NewSink());
        await atom.RehydrateAsync();

        registry.TryResolve("kid-1", out _).Should().BeTrue();
        registry.LastRefreshedUtc.Should().Be(savedAt);
    }

    [Fact]
    public async Task Rehydrate_does_not_clobber_already_fetched_keys()
    {
        var store = new InMemorySnapshotStore();
        await store.SaveAsync(new PublicKeySnapshot(DateTimeOffset.UtcNow.AddMinutes(-30), [Entry("stale")]));
        var registry = new PublicKeyRegistry();
        registry.Replace([Entry("fresh")], DateTimeOffset.UtcNow);

        using var atom = Make(registry, store, NewSink());
        await atom.RehydrateAsync();

        registry.TryResolve("fresh", out _).Should().BeTrue();
        registry.TryResolve("stale", out _).Should().BeFalse("a live fetch already populated the registry");
    }

    [Fact]
    public async Task Rehydrate_is_noop_when_store_empty()
    {
        var registry = new PublicKeyRegistry();
        using var atom = Make(registry, new InMemorySnapshotStore(), NewSink());

        await atom.RehydrateAsync();

        registry.Snapshot().Should().BeEmpty();
    }

    // ── Persist on refresh ───────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_signal_persists_the_fetched_snapshot()
    {
        var registry = new PublicKeyRegistry();
        registry.Replace([Entry("kid-1"), Entry("kid-2")], DateTimeOffset.UtcNow);
        var store = new InMemorySnapshotStore();
        var sink = NewSink();

        using var atom = Make(registry, store, sink);
        RaiseRefreshed(sink, 2);
        await atom.WaitForPendingPersistAsync();

        store.SaveCalls.Should().Be(1);
        store.Saved!.Keys.Select(k => k.KeyId).Should().BeEquivalentTo("kid-1", "kid-2");
    }

    // ── Optional store / dispose ─────────────────────────────────────────────────

    [Fact]
    public async Task Null_store_is_a_safe_noop()
    {
        var registry = new PublicKeyRegistry();
        var sink = NewSink();
        using var atom = Make(registry, store: null, sink);

        var act = async () =>
        {
            await atom.RehydrateAsync();
            RaiseRefreshed(sink, 1);
            await atom.WaitForPendingPersistAsync();
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispose_unsubscribes_so_later_refreshes_do_not_persist()
    {
        var registry = new PublicKeyRegistry();
        registry.Replace([Entry("kid-1")], DateTimeOffset.UtcNow);
        var store = new InMemorySnapshotStore();
        var sink = NewSink();
        var atom = Make(registry, store, sink);

        atom.Dispose();
        RaiseRefreshed(sink, 1);
        await atom.WaitForPendingPersistAsync();

        store.SaveCalls.Should().Be(0);
    }
}
