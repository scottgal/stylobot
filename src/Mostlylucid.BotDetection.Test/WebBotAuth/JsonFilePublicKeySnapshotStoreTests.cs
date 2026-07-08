using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.WebBotAuth;

namespace Mostlylucid.BotDetection.Test.WebBotAuth;

/// <summary>
///     Unit tests for <see cref="JsonFilePublicKeySnapshotStore"/> — the FOSS
///     file-backed durable snapshot store. Round-trips through JSON with the key
///     material base64-encoded; a missing or corrupt file loads as null (rely on
///     live fetch) rather than throwing.
/// </summary>
public sealed class JsonFilePublicKeySnapshotStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"stylobot-pk-snapshot-{Guid.NewGuid():N}.json");

    private JsonFilePublicKeySnapshotStore Store() => new(_path, NullLogger<JsonFilePublicKeySnapshotStore>.Instance);

    private static PublicKeyEntry Entry(string keyId)
        => new(keyId, "GPTBot", new byte[] { 9, 8, 7, 6, 5 }, "ed25519",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), "https://feed/keys.json");

    [Fact]
    public async Task Save_then_load_round_trips_entries()
    {
        var savedAt = new DateTimeOffset(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var store = Store();
        await store.SaveAsync(new PublicKeySnapshot(savedAt, [Entry("kid-1"), Entry("kid-2")]));

        var loaded = await store.LoadAsync();

        loaded.Should().NotBeNull();
        loaded!.SavedUtc.Should().Be(savedAt);
        loaded.Keys.Should().HaveCount(2);
        var e = loaded.Keys.Single(k => k.KeyId == "kid-1");
        e.AgentName.Should().Be("GPTBot");
        e.Algorithm.Should().Be("ed25519");
        e.Source.Should().Be("https://feed/keys.json");
        e.NotAfter.Should().Be(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        e.PublicKey.ToArray().Should().Equal(new byte[] { 9, 8, 7, 6, 5 });
    }

    [Fact]
    public async Task Load_returns_null_when_file_missing()
    {
        (await Store().LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Load_returns_null_on_corrupt_file()
    {
        await File.WriteAllTextAsync(_path, "{ this is not valid json");

        (await Store().LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Save_overwrites_the_previous_snapshot()
    {
        var store = Store();
        await store.SaveAsync(new PublicKeySnapshot(DateTimeOffset.UtcNow, [Entry("old")]));
        await store.SaveAsync(new PublicKeySnapshot(DateTimeOffset.UtcNow, [Entry("new")]));

        var loaded = await store.LoadAsync();

        loaded!.Keys.Should().ContainSingle().Which.KeyId.Should().Be("new");
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* best-effort */ }
    }
}
