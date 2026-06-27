using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Services.Llm;

namespace Mostlylucid.BotDetection.Test.Services.Llm;

/// <summary>
///     LL1 tests pinning the per-FINGERPRINT writeback contract: after
///     <see cref="FingerprintLlmWriteback.ApplyAsync"/> returns, the in-flight
///     reservation is always released even when the store throws -- so the picker
///     can surface the same fingerprint id again on the next tick instead of
///     leaking the reservation until the staleness window expires.
/// </summary>
public class FingerprintLlmWritebackTests
{
    private static FingerprintPickItem MakeItem(string fingerprintId) =>
        new(fingerprintId, InducedName: "Mac Chrome");

    private static FingerprintNamingResult MakeResult() =>
        new("CoolBot", "A bot");

    [Fact]
    public async Task ApplyAsync_writes_llm_name_and_releases_in_flight()
    {
        var inFlight = new FingerprintInFlightSet();
        Assert.True(inFlight.TryReserve("fp-1"));

        var store = new RecordingStore();
        var writeback = new FingerprintLlmWriteback(store, inFlight);

        await writeback.ApplyAsync(MakeItem("fp-1"), MakeResult(), CancellationToken.None);

        Assert.Single(store.Writes);
        Assert.Equal("fp-1", store.Writes[0].FingerprintId);
        Assert.Equal("CoolBot", store.Writes[0].LlmName);
        Assert.Equal("A bot", store.Writes[0].Description);

        // Key must be re-reservable after release.
        Assert.True(inFlight.TryReserve("fp-1"));
    }

    [Fact]
    public async Task ApplyAsync_releases_in_flight_even_when_store_throws()
    {
        var inFlight = new FingerprintInFlightSet();
        Assert.True(inFlight.TryReserve("fp-2"));

        var store = new ThrowingStore();
        var writeback = new FingerprintLlmWriteback(store, inFlight);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writeback.ApplyAsync(MakeItem("fp-2"), MakeResult(), CancellationToken.None));

        // finally must have released the reservation despite the throw.
        Assert.True(inFlight.TryReserve("fp-2"));
    }

    private sealed record LlmNameWrite(
        string FingerprintId, string LlmName, string? Description, DateTime EvaluatedAt);

    private sealed class RecordingStore : NullFingerprintStore
    {
        public List<LlmNameWrite> Writes { get; } = new();

        public override Task UpdateLlmNameAsync(
            string fingerprintId, string llmName, string? description,
            DateTime evaluatedAt, CancellationToken ct)
        {
            Writes.Add(new LlmNameWrite(fingerprintId, llmName, description, evaluatedAt));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStore : NullFingerprintStore
    {
        public override Task UpdateLlmNameAsync(
            string fingerprintId, string llmName, string? description,
            DateTime evaluatedAt, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
