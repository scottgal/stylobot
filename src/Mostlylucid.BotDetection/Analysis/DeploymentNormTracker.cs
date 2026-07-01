using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
/// Tracks deployment-specific prevalence of binary features using per-bucket sliding windows.
/// Detectors inject this singleton to calibrate penalties against observed local norms rather
/// than absolute rules. Example: if HTTP/2 is rare on this deployment, missing HTTP/2 should
/// not be penalized.
///
/// On cold start, the tracker is in a warm-up period (default: 50 total requests). During
/// warm-up, penalty-scaling detectors emit neutral contributions rather than bot signals,
/// since the population sample is too small to establish reliable norms.
/// </summary>
public sealed class DeploymentNormTracker
{
    // Feature name constants — use these in Record/GetRate calls to avoid typo-silent bucket splits.
    public static class Features
    {
        public const string Http2 = "http2";
        public const string AcceptHeader = "accept_header";
        public const string AcceptLanguage = "accept_language";

        // Transport-fingerprint features. Behind a TLS-terminating proxy / tunnel
        // (Cloudflare, k8s ingress) these never reach the origin, so the norm
        // learns they are absent-for-everyone here and stops penalising their
        // absence — the Signal Assay principle. See docs/architecture/signal-assay.md.
        public const string TcpConnectionHeader = "tcp_connection_header";
        public const string Http2StreamPriority = "http2_stream_priority";
    }

    private readonly record struct BucketState(int Total, int WithFeature)
    {
        public BucketState Halve() => new(Total / 2, WithFeature / 2);
        public BucketState Add(bool present) => new(Total + 1, WithFeature + (present ? 1 : 0));
        public (double Rate, int Samples) ToRate() =>
            Total == 0 ? (0.0, 0) : ((double)WithFeature / Total, Total);
    }

    private readonly ConcurrentDictionary<string, BucketState> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _windowSize;
    private readonly int _warmupRequests;

    private long _totalRequests;

    /// <summary>Total requests seen since startup. Exposed for dashboard diagnostics.</summary>
    public long TotalRequests => Volatile.Read(ref _totalRequests);

    /// <summary>
    /// Whether the tracker is still in its cold-start warm-up period.
    /// During warm-up, population-calibrated detectors should apply reduced penalties.
    /// </summary>
    public bool IsWarmingUp => Volatile.Read(ref _totalRequests) < _warmupRequests;

    public DeploymentNormTracker(int windowSize = 500, int warmupRequests = 50)
    {
        _windowSize = windowSize;
        _warmupRequests = warmupRequests;
    }

    /// <summary>
    /// Records an observation of <paramref name="feature"/> for <paramref name="bucket"/> and
    /// returns the updated (prevalence rate, sample count) for that bucket.
    /// Use <see cref="Features"/> constants for <paramref name="feature"/>.
    /// </summary>
    public (double Rate, int Samples) Record(string feature, string bucket, bool present)
    {
        // Cap at warmupRequests + 1 — beyond warm-up the counter only serves diagnostics
        // and the CompareExchange avoids an ever-growing long while staying lock-free.
        long current;
        do {
            current = Volatile.Read(ref _totalRequests);
            if (current > _warmupRequests) break;
        } while (Interlocked.CompareExchange(ref _totalRequests, current + 1, current) != current);

        var key = string.Concat(feature, ":", bucket);
        var updated = _buckets.AddOrUpdate(
            key,
            _ => new BucketState(1, present ? 1 : 0),
            (_, prev) =>
            {
                var next = prev.Total >= _windowSize ? prev.Halve() : prev;
                return next.Add(present);
            });
        return updated.ToRate();
    }

    /// <summary>Returns current (rate, samples) without recording an observation.</summary>
    public (double Rate, int Samples) GetRate(string feature, string bucket)
    {
        var key = string.Concat(feature, ":", bucket);
        return _buckets.TryGetValue(key, out var s) ? s.ToRate() : (0.0, 0);
    }

    /// <summary>
    /// Records an observation and evaluates whether to penalise the absence of a feature.
    /// Encapsulates the warm-up guard and population-threshold logic shared by all
    /// population-calibrated detectors.
    /// </summary>
    /// <param name="feature">Feature constant from <see cref="Features"/>.</param>
    /// <param name="bucket">Grouping key (e.g. UA family).</param>
    /// <param name="present">Whether the feature was present on this request.</param>
    /// <param name="minSamples">Minimum observations before the threshold is applied.</param>
    /// <param name="rateThreshold">Feature prevalence above which absence is penalised.</param>
    /// <param name="rate">Updated prevalence rate for this bucket.</param>
    /// <param name="samples">Updated sample count for this bucket.</param>
    /// <returns>
    /// <see cref="NormEvaluation.WarmingUp"/> — still in cold-start period;<br/>
    /// <see cref="NormEvaluation.BelowNorm"/> — feature is rare here, no penalty;<br/>
    /// <see cref="NormEvaluation.AboveNorm"/> — feature is expected, penalise absence.
    /// When <see cref="NormEvaluation.AboveNorm"/> and samples &lt; minSamples the caller
    /// should apply a half-confidence penalty rather than a full one.
    /// </returns>
    public NormEvaluation Evaluate(
        string feature, string bucket, bool present,
        int minSamples, double rateThreshold,
        out double rate, out int samples)
    {
        (rate, samples) = Record(feature, bucket, present);

        if (IsWarmingUp) return NormEvaluation.WarmingUp;
        if (samples >= minSamples && rate < rateThreshold) return NormEvaluation.BelowNorm;
        return NormEvaluation.AboveNorm;
    }
}

/// <summary>Result of <see cref="DeploymentNormTracker.Evaluate"/>.</summary>
public enum NormEvaluation
{
    /// <summary>Cold-start period — not enough global data to establish norms.</summary>
    WarmingUp,
    /// <summary>The feature is rare on this deployment — absence is not suspicious.</summary>
    BelowNorm,
    /// <summary>The feature is expected on this deployment — absence warrants a penalty.</summary>
    AboveNorm
}
