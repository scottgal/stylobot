using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Services.Llm;

namespace Mostlylucid.BotDetection.Test.Services.Llm;

/// <summary>
///     EC6e test pinning the writeback contract: after ApplyAsync, the in-flight
///     reservation is always released — even when the result callback throws —
///     so the picker can surface the key again on the next tick.
/// </summary>
public class SignatureLlmWritebackTests
{
    private static SignaturePickItem MakeItem(string signature) =>
        new(signature, new Dictionary<string, object?>());

    private static SignatureNamingResult MakeResult() =>
        new("CoolBot", "A bot");

    [Fact]
    public async Task ApplyAsync_releases_in_flight_after_success()
    {
        var inFlight = new SignatureInFlightSet();
        Assert.True(inFlight.TryReserve("sig-1"));

        var writeback = new SignatureLlmWriteback(inFlight, resultCallback: null);
        await writeback.ApplyAsync(MakeItem("sig-1"), MakeResult(), CancellationToken.None);

        // Key should now be re-reservable.
        Assert.True(inFlight.TryReserve("sig-1"));
    }

    [Fact]
    public async Task ApplyAsync_releases_in_flight_even_when_callback_throws()
    {
        var inFlight = new SignatureInFlightSet();
        Assert.True(inFlight.TryReserve("sig-2"));

        var throwingCallback = new ThrowingResultCallback();
        var writeback = new SignatureLlmWriteback(inFlight, throwingCallback);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writeback.ApplyAsync(MakeItem("sig-2"), MakeResult(), CancellationToken.None));

        // finally must have released the reservation despite the throw.
        Assert.True(inFlight.TryReserve("sig-2"));
    }

    private sealed class ThrowingResultCallback : ILlmResultCallback
    {
        public Task OnLlmResultAsync(string requestId, string primarySignature, string description, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task OnSignatureDescriptionAsync(string signature, string name, string description, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}
