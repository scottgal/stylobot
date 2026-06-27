using Mostlylucid.Atoms.Ephemeral;
using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Drift-triggered LFU picker for the fingerprint-naming path (LL1 / spec §3.2 +
///     §4). Replaces the per-SIGNATURE drift picker that pre-LL1 lived alongside
///     this file. Reads <see cref="IFingerprintStore.EnumerateLlmRepickCandidates"/>
///     which
///     walks the in-memory <c>_fingerprintById</c> map and returns hot fingerprints
///     whose induced name has drifted since the last LLM eval (or has never been
///     evaluated). The store's enumerator already enforces the §4a constraint that
///     the picker NEVER touches the DB; this picker just filters out fingerprints
///     already reserved in <see cref="FingerprintInFlightSet"/> and wraps each
///     into a <see cref="FingerprintPickItem"/>.
/// </summary>
public sealed class DriftTriggeredFingerprintPicker : IEphemeralPicker<FingerprintPickItem>
{
    private readonly IFingerprintStore _store;
    private readonly FingerprintInFlightSet _inFlight;

    public DriftTriggeredFingerprintPicker(IFingerprintStore store, FingerprintInFlightSet inFlight)
    {
        _store = store;
        _inFlight = inFlight;
    }

    public IReadOnlyList<FingerprintPickItem> Pick(int maxCount)
    {
        if (maxCount <= 0) return Array.Empty<FingerprintPickItem>();

        // Over-fetch slightly so that fingerprints already in-flight don't starve
        // the tick of work -- we want maxCount NEW picks even when a few are
        // racing the previous tick's invocations.
        var candidates = _store.EnumerateLlmRepickCandidates(maxCount * 2);
        if (candidates.Count == 0) return Array.Empty<FingerprintPickItem>();

        var picks = new List<FingerprintPickItem>(Math.Min(maxCount, candidates.Count));
        foreach (var fp in candidates)
        {
            if (picks.Count >= maxCount) break;
            if (!_inFlight.TryReserve(fp.FingerprintId)) continue;
            picks.Add(new FingerprintPickItem(fp.FingerprintId, fp.InducedName));
        }
        return picks;
    }
}
