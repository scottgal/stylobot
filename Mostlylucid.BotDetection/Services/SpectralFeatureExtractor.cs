using System.Numerics;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Static helper that computes FFT-based spectral features from inter-request timing intervals.
///     All frequency-domain math is isolated here for testability.
/// </summary>
public static class SpectralFeatureExtractor
{
    private const int MinIntervals = 8;

    /// <summary>
    ///     Neutral defaults returned when insufficient data is available.
    /// </summary>
    private static readonly SpectralFeatures InsufficientData = new()
    {
        DominantFrequency = 0.0,
        SpectralEntropy = 1.0,
        HarmonicRatio = 0.0,
        SpectralCentroid = 0.5,
        PeakToAvgRatio = 0.0,
        HasSufficientData = false
    };

    /// <summary>
    ///     Extract spectral features from inter-request timing intervals.
    /// </summary>
    /// <param name="intervals">Inter-request intervals in seconds.</param>
    /// <returns>Spectral features; HasSufficientData=false if fewer than 8 intervals.</returns>
    public static SpectralFeatures Extract(double[] intervals)
    {
        if (intervals == null || intervals.Length < MinIntervals)
            return InsufficientData;

        // 1. Pad to next power of 2
        var n = (int)Euclid.CeilingToPowerOfTwo(intervals.Length);
        var complex = new Complex[n];
        for (var i = 0; i < intervals.Length; i++)
            complex[i] = new Complex(intervals[i], 0);
        // Remaining are zero-padded by default

        // 2. Forward FFT (no scaling)
        Fourier.Forward(complex, FourierOptions.NoScaling);

        // 3. Compute magnitude spectrum (first N/2 bins, excluding DC at index 0)
        var halfN = n / 2;
        var magnitudes = new double[halfN];
        for (var i = 0; i < halfN; i++)
            magnitudes[i] = complex[i + 1].Magnitude; // Skip DC (index 0)

        if (halfN == 0)
            return InsufficientData;

        // 4. Find dominant frequency bin
        var maxMag = 0.0;
        var dominantBin = 0;
        for (var i = 0; i < halfN; i++)
        {
            if (magnitudes[i] > maxMag)
            {
                maxMag = magnitudes[i];
                dominantBin = i;
            }
        }

        // Dominant frequency as fraction of Nyquist
        var dominantFrequency = halfN > 0 ? (double)(dominantBin + 1) / n : 0.0;

        // 5. Spectral entropy: Shannon entropy of normalized PSD, /log2(N/2) -> [0,1]
        var totalEnergy = 0.0;
        for (var i = 0; i < halfN; i++)
            totalEnergy += magnitudes[i] * magnitudes[i];

        var spectralEntropy = 1.0;
        if (totalEnergy > 1e-12)
        {
            var entropy = 0.0;
            for (var i = 0; i < halfN; i++)
            {
                var p = magnitudes[i] * magnitudes[i] / totalEnergy;
                if (p > 1e-12)
                    entropy -= p * Math.Log2(p);
            }

            var maxEntropy = Math.Log2(halfN);
            spectralEntropy = maxEntropy > 0 ? entropy / maxEntropy : 1.0;
        }

        // 6. Harmonic ratio: energy at 2f, 3f, 4f of dominant / total energy
        var harmonicEnergy = 0.0;
        for (var harmonic = 2; harmonic <= 4; harmonic++)
        {
            var harmonicBin = (dominantBin + 1) * harmonic - 1; // 0-indexed
            if (harmonicBin < halfN)
                harmonicEnergy += magnitudes[harmonicBin] * magnitudes[harmonicBin];
        }

        var harmonicRatio = totalEnergy > 1e-12 ? harmonicEnergy / totalEnergy : 0.0;

        // 7. Peak-to-average ratio, clamped [0,1]
        var avgMag = 0.0;
        for (var i = 0; i < halfN; i++)
            avgMag += magnitudes[i];
        avgMag /= halfN;

        var peakToAvg = avgMag > 1e-12 ? Math.Min(1.0, maxMag / (maxMag + avgMag * (halfN - 1))) : 0.0;

        // 8. Spectral centroid: weighted freq center of mass / (N/2) -> [0,1]
        var weightedSum = 0.0;
        var magSum = 0.0;
        for (var i = 0; i < halfN; i++)
        {
            weightedSum += (i + 1) * magnitudes[i];
            magSum += magnitudes[i];
        }

        var spectralCentroid = magSum > 1e-12 ? weightedSum / (magSum * halfN) : 0.5;

        return new SpectralFeatures
        {
            DominantFrequency = dominantFrequency,
            SpectralEntropy = Math.Clamp(spectralEntropy, 0.0, 1.0),
            HarmonicRatio = Math.Clamp(harmonicRatio, 0.0, 1.0),
            SpectralCentroid = Math.Clamp(spectralCentroid, 0.0, 1.0),
            PeakToAvgRatio = Math.Clamp(peakToAvg, 0.0, 1.0),
            HasSufficientData = true
        };
    }

    /// <summary>
    ///     Compute temporal correlation between two sets of inter-request intervals
    ///     using cross-correlation in the frequency domain. High values (>0.8) indicate
    ///     shared C2 server timing or cron schedule.
    /// </summary>
    /// <returns>Correlation value [0,1].</returns>
    public static double ComputeTemporalCorrelation(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length < MinIntervals || b.Length < MinIntervals)
            return 0.0;

        // Cap input lengths to prevent excessive memory allocation
        // 128 intervals is more than enough for detecting shared timing patterns
        const int maxInputLength = 128;
        var effectiveA = a.Length > maxInputLength ? a.AsSpan(0, maxInputLength).ToArray() : a;
        var effectiveB = b.Length > maxInputLength ? b.AsSpan(0, maxInputLength).ToArray() : b;

        // 1. Zero-pad both to same length (next power of 2 of max)
        var maxLen = Math.Max(effectiveA.Length, effectiveB.Length);
        var n = (int)Euclid.CeilingToPowerOfTwo(maxLen * 2); // *2 for linear (non-circular) correlation

        var ca = new Complex[n];
        var cb = new Complex[n];
        for (var i = 0; i < effectiveA.Length; i++)
            ca[i] = new Complex(effectiveA[i], 0);
        for (var i = 0; i < effectiveB.Length; i++)
            cb[i] = new Complex(effectiveB[i], 0);

        // 2. FFT both
        Fourier.Forward(ca, FourierOptions.NoScaling);
        Fourier.Forward(cb, FourierOptions.NoScaling);

        // 3. Cross-power spectrum: FFT(A) * conj(FFT(B))
        var cross = new Complex[n];
        for (var i = 0; i < n; i++)
            cross[i] = ca[i] * Complex.Conjugate(cb[i]);

        // 4. IFFT -> cross-correlation
        Fourier.Inverse(cross, FourierOptions.NoScaling);

        // 5. Find max |correlation|
        var maxCorr = 0.0;
        for (var i = 0; i < n; i++)
        {
            var mag = cross[i].Magnitude;
            if (mag > maxCorr)
                maxCorr = mag;
        }

        // Normalize by norms: norm(A) * norm(B)
        var normA = 0.0;
        var normB = 0.0;
        for (var i = 0; i < effectiveA.Length; i++)
            normA += effectiveA[i] * effectiveA[i];
        for (var i = 0; i < effectiveB.Length; i++)
            normB += effectiveB[i] * effectiveB[i];

        var denominator = Math.Sqrt(normA * normB);
        if (denominator < 1e-12)
            return 0.0;

        return Math.Clamp(maxCorr / denominator, 0.0, 1.0);
    }
}
