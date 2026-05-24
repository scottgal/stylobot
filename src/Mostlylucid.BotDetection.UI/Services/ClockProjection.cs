namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds the 12-axis "clock" vector used by the Behavioral Evolution panel.
///     <para>
///     The clock interleaves the 8-axis semantic projection (existing
///     <c>VectorRadarProjection</c> output) with 4 distilled Markov state shares
///     so a single radar can show both "what the detectors are saying" and
///     "what the visitor is fetching" without two charts.
///     </para>
/// </summary>
public static class ClockProjection
{
    // State-freq index layout matches the filmstrip axis labels:
    // 0=Page 1=API 2=Asset 3=WS 4=SignalR 5=SSE 6=Form 7=Auth 8=404 9=Search
    private const int IdxAsset   = 2;
    private const int IdxWs      = 3;
    private const int IdxSignalR = 4;
    private const int IdxSse     = 5;
    private const int IdxForm    = 6;
    private const int Idx404     = 8;
    private const int IdxSearch  = 9;

    /// <summary>
    ///     Returns <c>[ Asset, Realtime, Form/Search, 404 ]</c>, each clamped to [0,1].
    ///     Returns four zeros when <paramref name="stateFreqs"/> is null or shorter than 10.
    /// </summary>
    public static double[] ProjectMarkovTo4Axes(float[] stateFreqs)
    {
        if (stateFreqs is null || stateFreqs.Length < 10)
            return new[] { 0.0, 0.0, 0.0, 0.0 };

        var asset    = Clamp01(stateFreqs[IdxAsset]);
        var realtime = Clamp01(stateFreqs[IdxWs] + stateFreqs[IdxSignalR] + stateFreqs[IdxSse]);
        var forms    = Clamp01(stateFreqs[IdxForm] + stateFreqs[IdxSearch]);
        var notFound = Clamp01(stateFreqs[Idx404]);

        return new[] { asset, realtime, forms, notFound };
    }

    /// <summary>
    ///     Interleaves the 8-axis semantic projection with the 4-axis Markov projection
    ///     into the fixed 12-axis clock order. Hours are indexed 12 → 11 as positions 0 → 11.
    ///     Missing input arrays contribute zeros for their hours.
    /// </summary>
    public static double[] Compose12Axes(double[] semantic8, double[] markov4)
    {
        var v = new double[12];

        v[0]  = GetClamped(semantic8, 0);   // 12 Browsing
        v[1]  = GetClamped(semantic8, 1);   //  1 API Activity
        v[2]  = GetClamped(markov4,   0);   //  2 Asset Share
        v[3]  = GetClamped(markov4,   1);   //  3 Realtime Share
        v[4]  = GetClamped(markov4,   2);   //  4 Form / Search
        v[5]  = GetClamped(semantic8, 3);   //  5 Auth Pressure
        v[6]  = GetClamped(semantic8, 5);   //  6 Burst Speed
        v[7]  = GetClamped(semantic8, 4);   //  7 Timing
        v[8]  = GetClamped(semantic8, 7);   //  8 Path Diversity
        v[9]  = GetClamped(markov4,   3);   //  9 404 Share
        v[10] = GetClamped(semantic8, 2);   // 10 Scan / Probe
        v[11] = GetClamped(semantic8, 6);   // 11 Fingerprint

        return v;
    }

    private static double GetClamped(double[]? src, int i)
        => src is null || i >= src.Length ? 0.0 : Clamp01(src[i]);

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
}
