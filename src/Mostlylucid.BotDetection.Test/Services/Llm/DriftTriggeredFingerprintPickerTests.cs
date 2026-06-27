using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Services.Llm;

namespace Mostlylucid.BotDetection.Test.Services.Llm;

/// <summary>
///     LL1 tests pinning the contract of the per-FINGERPRINT picker that replaced
///     the per-SIGNATURE drift picker:
///       1. Pick excludes fingerprint ids already reserved in the in-flight set.
///       2. Pick surfaces every candidate the store enumerates (drift filtering is
///          owned by <see cref="IFingerprintStore.EnumerateLlmRepickCandidates"/>;
///          the picker just wraps + filters in-flight).
/// </summary>
public class DriftTriggeredFingerprintPickerTests
{
    [Fact]
    public void Pick_excludes_fingerprints_already_in_flight()
    {
        var inFlight = new FingerprintInFlightSet();
        var store = new StubStore(new[]
        {
            MakeFingerprint("fp-A", "Mac Chrome"),
            MakeFingerprint("fp-B", "Win Firefox"),
        });
        var picker = new DriftTriggeredFingerprintPicker(store, inFlight);

        // Pre-reserve fp-A so the picker should skip it.
        Assert.True(inFlight.TryReserve("fp-A"));

        var picked = picker.Pick(maxCount: 10);

        Assert.DoesNotContain(picked, p => p.FingerprintId == "fp-A");
        Assert.Contains(picked, p => p.FingerprintId == "fp-B");
    }

    [Fact]
    public void Pick_returns_drift_triggered_candidates_from_store()
    {
        // The store's EnumerateLlmRepickCandidates is the drift filter; the picker
        // simply surfaces whatever it returns (post in-flight filter). This test
        // pins that the store's output IS what the picker hands the coordinator.
        var inFlight = new FingerprintInFlightSet();
        var store = new StubStore(new[]
        {
            MakeFingerprint("fp-drifted", "Mac Chrome 149"),
        });
        var picker = new DriftTriggeredFingerprintPicker(store, inFlight);

        var picked = picker.Pick(maxCount: 5);

        Assert.Single(picked);
        Assert.Equal("fp-drifted", picked[0].FingerprintId);
        Assert.Equal("Mac Chrome 149", picked[0].InducedName);
        // And the picker reserved the id so a second tick can't double-pick it
        // (in-flight gate is shared with the writeback release path).
        Assert.False(inFlight.TryReserve("fp-drifted"));
    }

    [Fact]
    public void Pick_returns_empty_when_store_has_no_candidates()
    {
        var picker = new DriftTriggeredFingerprintPicker(
            new StubStore(Array.Empty<Fingerprint>()),
            new FingerprintInFlightSet());

        Assert.Empty(picker.Pick(maxCount: 10));
    }

    private static Fingerprint MakeFingerprint(string id, string inducedName) => new()
    {
        FingerprintId = id,
        Centroid = Array.Empty<float>(),
        CentroidMaturity = 1,
        Weights = Array.Empty<float>(),
        MemberCount = 1,
        ObservationCount = 1,
        CorrectionCount = 0,
        FirstSeen = DateTime.UtcNow.AddMinutes(-10),
        LastSeen = DateTime.UtcNow,
        Quality = 1.0,
        InferredClientType = "browser",
        InferredTypeConfidence = 0.5,
        InferredTypeChangedAt = DateTime.UtcNow,
        InducedName = inducedName,
        InducedNameUpdatedAt = DateTime.UtcNow,
    };

    /// <summary>
    ///     Minimal IFingerprintStore fake that returns a fixed list from
    ///     <see cref="IFingerprintStore.EnumerateLlmRepickCandidates"/>. All other
    ///     store methods inherit the NullFingerprintStore no-ops.
    /// </summary>
    private sealed class StubStore : NullFingerprintStore
    {
        private readonly IReadOnlyList<Fingerprint> _candidates;

        public StubStore(IReadOnlyList<Fingerprint> candidates) => _candidates = candidates;

        public override IReadOnlyList<Fingerprint> EnumerateLlmRepickCandidates(int maxCount)
            => _candidates.Take(maxCount).ToList();
    }
}
