namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
///     Computes a compact frequency fingerprint from request timestamps.
///
///     Humans have noisy, non-periodic request timing (broadband spectrum).
///     Bots have rhythmic patterns: scrapers have 30-second retry loops;
///     credential stuffers have burst-rest-burst patterns; crawlers have 5-minute windows.
///
///     This encoder computes autocorrelation at 8 lag scales covering timescales from
///     1 second to 30 minutes. The resulting 8-dim vector captures the "rhythm" of a session:
///     - Near-zero values across all lags = human (broadband, no periodicity)
///     - A spike at lag k = requests clustered around that period (bot rhythm)
///
///     Lags: [1s, 3s, 10s, 30s, 60s, 3min, 10min, 30min]
///     These cover machine-speed (1s) through slow crawl windows (30min).
///
///     Two sessions from the same bot campaign will produce similar frequency fingerprints
///     even if their behavioral path (Markov) differs -- rotating campaigns maintain
///     the same temporal rhythm because the underlying crawler loop hasn't changed.
/// </summary>
public static class FrequencyFingerprintEncoder
{
    /// <summary>Lag values in seconds for the 8 frequency bins.</summary>
    public static readonly int[] LagSeconds = [1, 3, 10, 30, 60, 180, 600, 1800];

    public const int Dimensions = 8;

    /// <summary>
    ///     Computes the frequency fingerprint from a list of session requests.
    ///     Returns a Dimensions-length float[] with autocorrelation at each lag scale,
    ///     normalized to [0, 1].
    ///     Returns a zero vector for sessions with fewer than 3 requests.
    /// </summary>
    public static float[] Encode(IReadOnlyList<SessionRequest> requests)
    {
        var result = new float[Dimensions];

        if (requests.Count < 3) return result;

        // Build 1-second resolution time series of request counts.
        // Indexed from session start, capped at 1800 seconds (30 minutes).
        var sessionStart = requests[0].Timestamp.ToUnixTimeSeconds();
        var sessionEnd = requests[^1].Timestamp.ToUnixTimeSeconds();
        var durationSeconds = (int)Math.Min(sessionEnd - sessionStart + 1, 1800);

        if (durationSeconds < 2) return result; // All requests in the same second

        var counts = new int[durationSeconds + 1];
        foreach (var req in requests)
        {
            var offset = (int)(req.Timestamp.ToUnixTimeSeconds() - sessionStart);
            if (offset >= 0 && offset < counts.Length)
                counts[offset]++;
        }

        // Compute mean and variance for Pearson normalization
        double sum = 0;
        for (var i = 0; i < counts.Length; i++) sum += counts[i];
        var mean = sum / counts.Length;

        double varSum = 0;
        for (var i = 0; i < counts.Length; i++)
            varSum += (counts[i] - mean) * (counts[i] - mean);

        if (varSum < 1e-10) return result; // All requests in the same second (flat series)

        var stdDev = Math.Sqrt(varSum / counts.Length);

        // Compute autocorrelation at each lag
        for (var b = 0; b < Dimensions; b++)
        {
            var lag = LagSeconds[b];
            if (lag >= counts.Length)
            {
                // Lag is longer than the session; bin stays at 0
                continue;
            }

            var n = counts.Length - lag;
            double crossSum = 0;
            for (var t = 0; t < n; t++)
                crossSum += (counts[t] - mean) * (counts[t + lag] - mean);

            // Pearson autocorrelation at this lag, normalized to [0, 1]
            // Raw value is in [-1, 1]; we want [0, 1] where 1 = perfectly periodic at this lag
            var acf = crossSum / (n * stdDev * stdDev);

            // Map [-1, 1] → [0, 1] and clamp (negative correlations are not useful here)
            result[b] = Math.Clamp((float)((acf + 1.0) / 2.0), 0f, 1f);
        }

        return result;
    }

    /// <summary>
    ///     Computes the "periodicity score" from a frequency fingerprint:
    ///     how far the fingerprint deviates from white noise (0.5 in each bin).
    ///     High scores = strong periodic pattern (bot-like).
    ///     Near-zero = broadband, aperiodic (human-like).
    /// </summary>
    public static float PeriodicityScore(float[] fingerprint)
    {
        if (fingerprint.Length == 0) return 0f;

        float sumSq = 0;
        for (var i = 0; i < fingerprint.Length; i++)
        {
            var deviation = fingerprint[i] - 0.5f; // 0.5 = no periodicity (white noise)
            sumSq += deviation * deviation;
        }

        // RMS deviation from white noise, scaled to [0, 1]
        // Max possible: all bins at 0 or 1 → deviation 0.5 per bin → RMS = 0.5
        return Math.Min(1f, MathF.Sqrt(sumSq / fingerprint.Length) * 2f);
    }

    /// <summary>
    ///     Dominant lag: the lag index with the highest autocorrelation value.
    ///     Returns -1 if the fingerprint is flat or all zeros.
    /// </summary>
    public static int DominantLagIndex(float[] fingerprint)
    {
        if (fingerprint.Length == 0) return -1;

        var best = -1;
        var bestVal = 0.6f; // Only report if clearly above white-noise level
        for (var i = 0; i < fingerprint.Length; i++)
        {
            if (fingerprint[i] > bestVal)
            {
                bestVal = fingerprint[i];
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    ///     Cosine similarity between two frequency fingerprints.
    ///     Two sessions with the same bot rhythm score near 1.0 regardless
    ///     of any behavioral path changes.
    /// </summary>
    public static float Similarity(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0f;
    }
}
