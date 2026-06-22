using System.Threading;

namespace Mostlylucid.BotDetection.Identity.Triggers;

/// <summary>
///     The signal source for <see cref="IdentityWeightCalibrationService"/>'s
///     adaptive trigger. Tracks two axes since the last successful calibration:
///     <list type="bullet">
///         <item><c>obs.unabsorbed</c>: count of observations recorded.</item>
///         <item><c>drift.l2</c>: sum of per-fingerprint centroid L2 deltas at absorption.</item>
///     </list>
///
///     <para>
///         <b>Hot path cost:</b> one <see cref="Interlocked.Increment(ref long)"/>
///         per observation, one CAS-loop add per absorption. Single allocation
///         (the snapshot dict) per heartbeat. The dictionary is replaced
///         atomically on <see cref="Reset"/> so a concurrent <see cref="Snapshot"/>
///         either sees the pre-reset counters or the post-reset zero state,
///         never a torn read.
///     </para>
///
///     <para>
///         Wired into the request hot path via two seams:
///         <see cref="SqliteFingerprintStore.RecordObservationAsync"/>
///         calls <see cref="OnObservation"/>, and
///         <see cref="FingerprintAbsorptionService"/>'s AbsorbAsync
///         calls <see cref="OnAbsorption"/> with the L2 delta between the
///         old and new centroid.
///     </para>
/// </summary>
public sealed class CalibrationSignalSource : IAdaptiveTriggerSignalSource
{
    private long _observationsSinceLastRun;
    private long _driftL2Bits; // double stored as long via BitConverter for Interlocked.

    /// <summary>Hot-path hook: one observation arrived. Cheap (single increment).</summary>
    public void OnObservation() => Interlocked.Increment(ref _observationsSinceLastRun);

    /// <summary>
    ///     Hot-path hook: an absorption committed and the centroid moved by
    ///     <paramref name="centroidDeltaL2"/>. Accumulates via CAS loop so
    ///     concurrent absorptions don't lose updates. Negative or NaN deltas
    ///     are clamped to zero -- the signal is "how much movement", not "what
    ///     direction".
    /// </summary>
    public void OnAbsorption(double centroidDeltaL2)
    {
        if (double.IsNaN(centroidDeltaL2) || centroidDeltaL2 <= 0) return;

        // CAS loop on a long-encoded double. Interlocked has no double overload,
        // so we hop through the bit-pattern.
        long currentBits, updatedBits;
        do
        {
            currentBits = Interlocked.Read(ref _driftL2Bits);
            var current = BitConverter.Int64BitsToDouble(currentBits);
            var updated = current + centroidDeltaL2;
            updatedBits = BitConverter.DoubleToInt64Bits(updated);
        }
        while (Interlocked.CompareExchange(ref _driftL2Bits, updatedBits, currentBits) != currentBits);
    }

    public IReadOnlyDictionary<string, double> Snapshot()
    {
        var obs = (double)Interlocked.Read(ref _observationsSinceLastRun);
        var drift = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _driftL2Bits));
        return new Dictionary<string, double>(capacity: 2, StringComparer.Ordinal)
        {
            [SignalKeys.ObsUnabsorbed] = obs,
            [SignalKeys.DriftL2] = drift,
        };
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _observationsSinceLastRun, 0);
        Interlocked.Exchange(ref _driftL2Bits, 0);
    }

    /// <summary>Signal name constants. Keep in sync with the policy defaults in <c>IdentityCalibrationOptions</c>.</summary>
    public static class SignalKeys
    {
        public const string ObsUnabsorbed = "obs.unabsorbed";
        public const string DriftL2 = "drift.l2";
    }
}
