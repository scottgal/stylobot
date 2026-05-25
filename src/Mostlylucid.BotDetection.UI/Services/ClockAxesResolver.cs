namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Single source of truth for the 12-axis behavioural clock projection
///     rendered as the radar in both the marketing-site Your Detection card
///     and the dashboard signature detail page.
///
///     <para>
///     The 16-dim <c>RadarShape</c> emitted by detection (axes per
///     <see cref="Mostlylucid.BotDetection.Analysis.RadarDimensions"/>) is
///     folded into the 8 semantic detector hours and combined with the
///     4 Markov request-class hours by <see cref="ClockProjection.Compose12Axes"/>.
///     Both surfaces call <see cref="FromRadarShape"/> so the radar
///     visualisation is identical -- previously each had its own
///     inline projection and they drifted apart producing a glaring
///     "different shape for same visitor" bug.
///     </para>
/// </summary>
public static class ClockAxesResolver
{
    /// <summary>
    ///     Project a 16-dim <c>RadarShape</c> into the 12-axis clock layout.
    ///     Returns null when the input isn't a 16-element vector -- the
    ///     view layer should render an empty radar in that case rather
    ///     than synthesise something else (which is what caused the two
    ///     surfaces to disagree).
    /// </summary>
    public static double[]? FromRadarShape(float[]? shape16)
    {
        if (shape16 is not { Length: 16 } rs) return null;
        var semantic8 = ProjectShape16ToSemantic8(rs);
        var markov4   = new double[] { 0, 0, 0, 0 };
        return ClockProjection.Compose12Axes(semantic8, markov4);
    }

    /// <summary>
    ///     Folds the 16-dim RadarDimensions vector into the 8 semantic detector
    ///     axes the clock projection consumes (Browsing, API, Scan/Probe, Auth,
    ///     Timing, Burst, Fingerprint, Path Diversity). Identical mapping to the
    ///     inline helper that used to live in StyloBotDashboardMiddleware --
    ///     hoisted here so the two surfaces can never drift again.
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
