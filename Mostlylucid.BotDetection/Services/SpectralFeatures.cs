namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     FFT-derived spectral features from inter-request timing intervals.
///     Used for detecting bot timers, heartbeats, and coordinated C2 timing.
/// </summary>
public record SpectralFeatures
{
    /// <summary>Frequency bin with highest magnitude (excl. DC). Sharp peaks indicate timer-driven bots.</summary>
    public double DominantFrequency { get; init; }

    /// <summary>Shannon entropy of normalized PSD, scaled to [0,1]. Low = bot-like pure tone, high = human-like noise.</summary>
    public double SpectralEntropy { get; init; }

    /// <summary>Energy at harmonics (2f,3f,4f) of dominant frequency / total energy. High = timer with harmonics.</summary>
    public double HarmonicRatio { get; init; }

    /// <summary>Normalized spectral centroid [0,1]. Weighted center of mass of frequency spectrum.</summary>
    public double SpectralCentroid { get; init; }

    /// <summary>Max magnitude / avg magnitude, clamped [0,1]. High = sharp spectral line (bot), low = flat (human).</summary>
    public double PeakToAvgRatio { get; init; }

    /// <summary>Whether there were enough intervals (>= 8) to compute meaningful spectral features.</summary>
    public bool HasSufficientData { get; init; }
}
