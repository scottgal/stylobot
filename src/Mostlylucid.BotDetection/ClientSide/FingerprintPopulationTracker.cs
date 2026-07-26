using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
/// Tracks per-(ua-family, transport-class) fingerprint send rate using a sliding window.
/// Calibration cache only — acceptable as in-memory (not detection state).
/// </summary>
public sealed class FingerprintPopulationTracker
{
    private readonly record struct BucketState(int Total, int WithFingerprint)
    {
        public BucketState Halve() => new(Total / 2, WithFingerprint / 2);
        public BucketState Add(bool has) => new(Total + 1, WithFingerprint + (has ? 1 : 0));
        public (double Rate, int Samples) ToRate() =>
            Total == 0 ? (0.0, 0) : ((double)WithFingerprint / Total, Total);
    }

    private readonly ConcurrentDictionary<string, BucketState> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _windowSize;

    // Bucket keys are {uaFamily}:{transportClass}. uaFamily is unbounded for bots
    // (per-instance discriminator), so without a cap this map grows one entry per unique
    // bot family forever. Cap the KEY COUNT and evict the coldest buckets (fewest samples)
    // on overflow; the per-bucket Halve() decay semantics are untouched.
    private const int MaxBuckets = 20_000;
    private readonly object _evictLock = new();

    /// <summary>Test hook: number of resident buckets.</summary>
    internal int BucketCount => _buckets.Count;

    public FingerprintPopulationTracker(int windowSize = 500) => _windowSize = windowSize;

    /// <summary>
    /// Records a request and returns the updated (rate, samples) for this bucket.
    /// Only call for document-class requests; API/asset requests skew the rate downward.
    /// </summary>
    public (double Rate, int Samples) Record(string uaFamily, string transportClass, bool hasFingerprint)
    {
        var key = $"{uaFamily}:{transportClass}";
        var updated = _buckets.AddOrUpdate(
            key,
            _ => new BucketState(1, hasFingerprint ? 1 : 0),
            (_, prev) =>
            {
                var state = prev.Total >= _windowSize ? prev.Halve() : prev;
                return state.Add(hasFingerprint);
            });
        EvictColdestIfNeeded();
        return updated.ToRate();
    }

    /// <summary>
    ///     Caps the number of resident buckets. On overflow, evicts the coldest slice
    ///     (fewest total observations) down to ~90% of <see cref="MaxBuckets"/> so the
    ///     next insert doesn't immediately re-trigger. Counter/decay semantics of the
    ///     surviving buckets are preserved. Single-threaded via <see cref="_evictLock"/>.
    /// </summary>
    private void EvictColdestIfNeeded()
    {
        if (_buckets.Count <= MaxBuckets) return;
        if (!Monitor.TryEnter(_evictLock)) return;
        try
        {
            if (_buckets.Count <= MaxBuckets) return;
            var target = MaxBuckets - MaxBuckets / 10; // trim to 90%
            var overflow = _buckets.Count - target;
            if (overflow <= 0) return;

            var snapshot = _buckets.ToArray();
            Array.Sort(snapshot, static (a, b) => a.Value.Total.CompareTo(b.Value.Total));
            var removed = 0;
            foreach (var kv in snapshot)
            {
                if (removed >= overflow) break;
                if (_buckets.TryRemove(kv.Key, out _)) removed++;
            }
        }
        finally
        {
            Monitor.Exit(_evictLock);
        }
    }

    /// <summary>Returns (rate, samples) without recording. Used for the fingerprint-found path.</summary>
    public (double Rate, int Samples) GetRate(string uaFamily, string transportClass)
    {
        var key = $"{uaFamily}:{transportClass}";
        return _buckets.TryGetValue(key, out var s) ? s.ToRate() : (0.0, 0);
    }
}

/// <summary>Sentinel detail strings written by ClientSideDetector, read by ClientSideContributor.</summary>
internal static class ClientSideReasons
{
    public const string NoFingerprint = "no_fingerprint";
    public const string FingerprintFoundClean = "fingerprint_found_clean";
    /// <summary>
    ///     The adblocker probe (rendered by AdblockerProbeTagHelper) reported back
    ///     that the configured ad-network resource was blocked. ClientSideContributor
    ///     reads this and (a) writes the ClientSideAdblockerDetected signal, (b)
    ///     suppresses the no-fingerprint penalty for the page request, (c) adds a
    ///     small human-affinity contribution. See docs/adblocker-detection.md.
    /// </summary>
    public const string AdblockerDetected = "adblocker_detected";
}
