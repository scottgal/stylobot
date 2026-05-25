using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Single source of truth for the 12-axis behavioural clock projection
///     rendered by both the marketing-site Your Detection card and the
///     dashboard signature detail / behavioral evolution surfaces.
///     <para>
///     <see cref="FromSessionVector"/> is the canonical entry point -- it
///     composes the 8 semantic detector axes from
///     <see cref="VectorRadarProjection.Project"/> and the 4 Markov
///     request-class axes from <see cref="ClockProjection.ProjectMarkovTo4Axes"/>
///     and hands them to <see cref="ClockProjection.Compose12Axes"/>. The
///     dashboard sessions API and the marketing card both go through this
///     helper so the radar polygon for one visitor is byte-identical
///     across surfaces.
///     </para>
///     <para>
///     <see cref="FromRadarShape"/> is the lower-resolution fallback for
///     surfaces that only have the 16-dim <c>RadarShape</c> (e.g. a
///     dashboard widget that hasn't been given the underlying session
///     vector). It folds the 16-dim vector into the 8 semantic axes and
///     leaves the Markov hours at zero -- still goes through the same
///     <see cref="ClockProjection.Compose12Axes"/> output so the chart
///     reads consistently with the higher-resolution path.
///     </para>
/// </summary>
public static class ClockAxesResolver
{
    /// <summary>
    ///     Canonical 12-axis clock projection from a 129-dim
    ///     <see cref="SessionVectorizer"/> session vector. Returns null
    ///     when the input is shorter than 118 dims (the minimum that
    ///     <see cref="VectorRadarProjection.Project"/> requires).
    /// </summary>
    public static double[]? FromSessionVector(float[]? sessionVector)
    {
        if (sessionVector is not { Length: >= 118 }) return null;

        var semantic8 = VectorRadarProjection.Project(sessionVector);
        if (semantic8 is null) return null;

        var stateFreqs = SliceStateFreqs(sessionVector);
        var markov4 = ClockProjection.ProjectMarkovTo4Axes(stateFreqs);

        return ClockProjection.Compose12Axes(semantic8, markov4);
    }

    /// <summary>
    ///     Fallback projection from a 16-dim <c>RadarShape</c>. Use this
    ///     when the consumer only has the cached radar shape (e.g. live
    ///     visitor cards that haven't yet been backed by a finalised
    ///     session vector). Markov hours stay at the origin -- the 8
    ///     semantic detector hours carry the visible signal.
    /// </summary>
    public static double[]? FromRadarShape(float[]? shape16)
    {
        if (shape16 is not { Length: 16 } rs) return null;
        var semantic8 = ProjectShape16ToSemantic8(rs);
        var markov4   = new double[] { 0, 0, 0, 0 };
        return ClockProjection.Compose12Axes(semantic8, markov4);
    }

    /// <summary>
    ///     State frequencies live at positions [100..109] of the
    ///     <see cref="SessionVectorizer"/> output. Identical extraction
    ///     to the helper that used to live inline in
    ///     <c>ServeSignatureSessionsApiAsync</c> -- hoisted so callers
    ///     can't drift.
    /// </summary>
    private static float[] SliceStateFreqs(float[] vector)
    {
        var sf = new float[10];
        if (vector.Length >= 110)
            Array.Copy(vector, 100, sf, 0, 10);
        return sf;
    }

    /// <summary>
    ///     Folds a 16-dim RadarDimensions vector into the 8 semantic detector
    ///     axes the clock projection consumes. Mapping matches what the
    ///     dashboard middleware used to do inline.
    /// </summary>
    private static double[] ProjectShape16ToSemantic8(float[] shape16)
    {
        float R(int i) => i < shape16.Length ? shape16[i] : 0f;
        double Clamp(double v) => Math.Max(0.05, Math.Min(1.0, v));

        return
        [
            Clamp(R(3)),                            // 0: Browsing       ← behavioral
            Clamp(R(14)),                           // 1: API Activity   ← rate_pattern
            Clamp(Math.Max(R(6), R(15))),           // 2: Scan/Probe     ← security_tool, payload_signature
            Clamp(Math.Max(R(0), R(9))),            // 3: Auth Pressure  ← ua_anomaly, inconsistency
            Clamp(R(4)),                            // 4: Timing Pattern ← advanced_behavioral
            Clamp(Math.Max(R(14) * 0.5f, R(12))),   // 5: Burst Speed    ← rate_pattern, cluster_signal
            Clamp(R(7)),                            // 6: Fingerprint    ← client_fingerprint
            Clamp(R(13))                            // 7: Path Diversity ← country_reputation (fold-in)
        ];
    }
}
