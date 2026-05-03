namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
///     Projects a 129-dimensional session vector into 8 interpretable radar axes.
///     Each axis is a normalized [0,1] score representing a behavioral dimension
///     that maps directly to one or more contributing detectors.
///     Used for the behavioral shape radar chart in the dashboard.
/// </summary>
/// <remarks>
///     Vector layout (StateCount = 10):
///       [0..99]   Markov transition matrix (10×10), row = from-state, col = to-state
///                 State order: PageView(0) ApiCall(1) StaticAsset(2) WebSocket(3) SignalR(4)
///                              SSE(5) FormSubmit(6) AuthAttempt(7) NotFound(8) Search(9)
///       [100..109] Stationary distribution (time fraction in each state)
///       [110..117] Temporal: [0]CV [1]entropy [2]burst% [3]duration [4]rpm [5]4xx% [6]pathRatio [7]meanInterval
///       [118..125] Fingerprint: [0]tls [1]http [2]clientType [3]tcpOs [4]quic [5]clientFp [6]headless [7]datacenter
///       [126..128] Transition timing: [0]impossibleRatio [1]timingConsistency [2]fastestZScore
/// </remarks>
public static class VectorRadarProjection
{
    private const int StateCount = 10;
    private const int StationaryOffset = StateCount * StateCount; // 100
    private const int TemporalOffset = StationaryOffset + StateCount; // 110
    private const int FpOffset = TemporalOffset + 8; // 118
    private const int TtOffset = FpOffset + 8; // 126
    private const int MinLength = TemporalOffset; // 110 — Markov + stationary required; fingerprint/timing optional

    // State indices
    private const int PageView = 0;
    private const int ApiCall = 1;
    private const int FormSubmit = 6;
    private const int AuthAttempt = 7;
    private const int NotFound = 8;
    private const int Search = 9;

    /// <summary>Radar axis labels for the behavioral shape chart.</summary>
    public static readonly string[] AxisLabels =
    [
        "Browsing",        // Organic page-navigation pattern (human-like)
        "API Activity",    // API / programmatic access intensity
        "Scan / Probe",    // 404-hunting, error-rate probing (SecurityTool / Haxxor)
        "Auth Pressure",   // Auth & form submission hammering (AccountTakeover)
        "Timing Pattern",  // How regular / robotic the timing is (Periodicity)
        "Burst Speed",     // Impossibly fast or bursty requests (BehavioralWaveform)
        "Fingerprint",     // TLS / TCP / H2 / client-fp integrity
        "Path Diversity"   // Unique path coverage (ContentSequence)
    ];

    /// <summary>
    ///     Projects a session vector into 8 radar axes.
    ///     Returns null if the vector is too short to be useful (missing Markov + stationary dims).
    ///     Fingerprint (118+) and transition-timing (126+) dims are optional — defaults to 0 if absent.
    /// </summary>
    public static double[]? Project(float[] vector)
    {
        if (vector.Length < MinLength) return null;
        float V(int i) => i < vector.Length ? vector[i] : 0f;

        var axes = new double[8];

        // ── Axis 0: Browsing ──────────────────────────────────────────────────────
        // Fraction of session spent in PageView state (stationary) + self-loop strength.
        // High value = client is doing genuine page navigation.
        var pageViewStationary = V(StationaryOffset + PageView); // dim 100
        var pageViewSelfLoop = V(PageView * StateCount + PageView); // dim 0
        axes[0] = Math.Min(1.0, pageViewStationary * 1.5 + pageViewSelfLoop * 0.5);

        // ── Axis 1: API Activity ──────────────────────────────────────────────────
        var apiStationary = V(StationaryOffset + ApiCall); // dim 101
        var apiSelfLoop = V(ApiCall * StateCount + ApiCall); // dim 11
        axes[1] = Math.Min(1.0, apiStationary * 1.5 + apiSelfLoop * 0.5);

        // ── Axis 2: Scan / Probe ──────────────────────────────────────────────────
        var notFoundStationary = V(StationaryOffset + NotFound); // dim 108
        var errorRatio = V(TemporalOffset + 5); // dim 115
        var notFoundSelfLoop = V(NotFound * StateCount + NotFound); // dim 88
        axes[2] = Math.Min(1.0, notFoundStationary * 2.0 + errorRatio * 0.5 + notFoundSelfLoop * 0.5);

        // ── Axis 3: Auth Pressure ─────────────────────────────────────────────────
        var authStationary = V(StationaryOffset + AuthAttempt); // dim 107
        var formStationary = V(StationaryOffset + FormSubmit); // dim 106
        var authSelfLoop = V(AuthAttempt * StateCount + AuthAttempt); // dim 77
        axes[3] = Math.Min(1.0, authStationary * 2.0 + formStationary + authSelfLoop * 0.5);

        // ── Axis 4: Timing Pattern ────────────────────────────────────────────────
        var cv = V(TemporalOffset + 0); // dim 110
        var transitionConsistency = V(TtOffset + 1); // dim 127 (0 if absent)
        axes[4] = Math.Min(1.0, (1.0 - cv) * 0.6 + transitionConsistency * 0.4);

        // ── Axis 5: Burst Speed ───────────────────────────────────────────────────
        var burstRatio = V(TemporalOffset + 2); // dim 112
        var impossibleRatio = V(TtOffset + 0); // dim 126 (0 if absent)
        var fastestZScore = V(TtOffset + 2); // dim 128 (0 if absent)
        axes[5] = Math.Min(1.0, burstRatio * 0.4 + impossibleRatio * 0.4 + fastestZScore * 0.2);

        // ── Axis 6: Fingerprint ───────────────────────────────────────────────────
        var fpSum = 0f;
        for (var i = 0; i < 8; i++) fpSum += Math.Abs(V(FpOffset + i));
        var clientFp = Math.Abs(V(FpOffset + 5)); // ClientFingerprintIntegrity
        var tcpOs = Math.Abs(V(FpOffset + 3)); // TcpOsConsistency
        axes[6] = Math.Min(1.0, fpSum / 6.0 + clientFp * 0.15 + tcpOs * 0.15);

        // ── Axis 7: Path Diversity ────────────────────────────────────────────────
        var pathRatio = V(TemporalOffset + 6); // dim 116
        var searchStationary = V(StationaryOffset + Search); // dim 109
        axes[7] = Math.Min(1.0, pathRatio * 0.8 + searchStationary * 0.2);

        // Ensure minimum polygon shape (0.05 per axis so radar always has visible area)
        for (var i = 0; i < axes.Length; i++)
            axes[i] = Math.Max(0.05, axes[i]);

        return axes;
    }
}