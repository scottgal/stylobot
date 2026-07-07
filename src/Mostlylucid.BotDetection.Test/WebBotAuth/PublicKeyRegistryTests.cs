using FluentAssertions;
using Mostlylucid.BotDetection.WebBotAuth;

namespace Mostlylucid.BotDetection.Test.WebBotAuth;

/// <summary>
///     Unit tests for <see cref="PublicKeyRegistry"/> — the in-memory,
///     atomically-swapped index behind <see cref="IPublicKeyRegistry"/>.
///     Two layers: a sticky operator-supplied "manual" set and a fetched set
///     swapped on each refresh. Manual keys win on a keyId collision and survive
///     a fetched-layer replace.
/// </summary>
public sealed class PublicKeyRegistryTests
{
    private static PublicKeyEntry Entry(string keyId, string name = "TestBot", string source = "manual")
        => new(keyId, name, new byte[] { 1, 2, 3 }, "ed25519", null, source);

    [Fact]
    public void Empty_registry_resolves_nothing()
    {
        var registry = new PublicKeyRegistry();

        registry.TryResolve("kid-1", out _).Should().BeFalse();
        registry.Snapshot().Should().BeEmpty();
        registry.LastRefreshedUtc.Should().BeNull();
    }

    [Fact]
    public void Replace_makes_keys_resolvable_and_sets_last_refreshed()
    {
        var registry = new PublicKeyRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Replace([Entry("kid-1", "GPTBot", "https://example/keys.json")], now);

        registry.TryResolve("kid-1", out var entry).Should().BeTrue();
        entry.AgentName.Should().Be("GPTBot");
        registry.LastRefreshedUtc.Should().Be(now);
    }

    [Fact]
    public void Replace_atomically_swaps_the_fetched_layer()
    {
        var registry = new PublicKeyRegistry();
        registry.Replace([Entry("kid-old")], DateTimeOffset.UtcNow);

        registry.Replace([Entry("kid-new")], DateTimeOffset.UtcNow);

        registry.TryResolve("kid-old", out _).Should().BeFalse("the second replace swaps the whole fetched layer");
        registry.TryResolve("kid-new", out _).Should().BeTrue();
    }

    [Fact]
    public void Snapshot_returns_all_current_entries()
    {
        var registry = new PublicKeyRegistry();
        registry.Replace([Entry("kid-1"), Entry("kid-2")], DateTimeOffset.UtcNow);

        registry.Snapshot().Select(e => e.KeyId).Should().BeEquivalentTo("kid-1", "kid-2");
    }

    [Fact]
    public void Manual_keys_resolve_and_survive_a_replace()
    {
        var registry = new PublicKeyRegistry();
        registry.SeedManual([Entry("manual-kid", "OperatorBot")]);

        // A fetched replace must not evict operator-supplied manual keys.
        registry.Replace([Entry("fetched-kid")], DateTimeOffset.UtcNow);

        registry.TryResolve("manual-kid", out var manual).Should().BeTrue();
        manual.AgentName.Should().Be("OperatorBot");
        registry.TryResolve("fetched-kid", out _).Should().BeTrue();
    }

    [Fact]
    public void Manual_key_takes_precedence_over_fetched_with_same_keyid()
    {
        var registry = new PublicKeyRegistry();
        registry.SeedManual([Entry("kid-1", "ManualWins", "manual")]);
        registry.Replace([Entry("kid-1", "FetchedLoses", "https://feed")], DateTimeOffset.UtcNow);

        registry.TryResolve("kid-1", out var entry).Should().BeTrue();
        entry.AgentName.Should().Be("ManualWins");
        registry.Snapshot().Should().ContainSingle(e => e.KeyId == "kid-1")
            .Which.AgentName.Should().Be("ManualWins");
    }

    [Fact]
    public void Manual_only_registry_resolves_without_a_refresh()
    {
        var registry = new PublicKeyRegistry();
        registry.SeedManual([Entry("manual-kid")]);

        registry.TryResolve("manual-kid", out _).Should().BeTrue();
        registry.Snapshot().Should().ContainSingle();
        registry.LastRefreshedUtc.Should().BeNull("manual seeding is not a fetch");
    }
}