using Mostlylucid.Ephemeral.Atoms.Llm;
using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Persists a fingerprint-naming result via
///     <see cref="IFingerprintStore.UpdateLlmNameAsync"/> -- the write-behind LFU
///     façade owned by the store, never a synchronous DB write on the hot path.
///     Always releases the in-flight reservation in <c>finally</c> so a writeback
///     exception still frees the key for the next tick. Invoker-side failures are
///     reclaimed by <see cref="FingerprintInFlightSet"/>'s staleness window per
///     Option C from the EC6a / spec §3.2 fingerprint-naming sequence.
/// </summary>
public sealed class FingerprintLlmWriteback : IEphemeralWriteback<FingerprintPickItem, FingerprintNamingResult>
{
    private readonly IFingerprintStore _store;
    private readonly FingerprintInFlightSet _inFlight;

    public FingerprintLlmWriteback(IFingerprintStore store, FingerprintInFlightSet inFlight)
    {
        _store = store;
        _inFlight = inFlight;
    }

    public async Task ApplyAsync(FingerprintPickItem item, FingerprintNamingResult result, CancellationToken ct)
    {
        try
        {
            await _store.UpdateLlmNameAsync(
                item.FingerprintId,
                result.Name,
                result.Description,
                DateTime.UtcNow,
                ct);
        }
        finally
        {
            _inFlight.Release(item.FingerprintId);
        }
    }
}
