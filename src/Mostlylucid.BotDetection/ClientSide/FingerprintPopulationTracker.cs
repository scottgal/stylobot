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
        return updated.ToRate();
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
}
