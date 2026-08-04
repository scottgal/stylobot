using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
///     Request state for Markov chain transitions within a session.
///     Maps to observable request-level behavior, not content.
/// </summary>
public enum RequestState
{
    PageView = 0,
    ApiCall = 1,
    StaticAsset = 2,
    WebSocket = 3,
    SignalR = 4,
    ServerSentEvent = 5,
    FormSubmit = 6,
    AuthAttempt = 7,
    NotFound = 8,
    Search = 9
}

/// <summary>
///     A single observed request within a session, capturing state and timing.
///     <para>
///     <paramref name="FromUpstream"/> distinguishes upstream-derived status
///     codes from stylobot-synthesised ones (load-shed 503, policy block 403,
///     honeypot 404, throttle 429) so the session-vector 4xx error-ratio arm
///     can suppress stylobot's own enforcement responses (closed-loop
///     feedback). Defaults to <c>true</c> when the producer doesn't yet
///     stamp the flag (back-compat).
///     </para>
///     <para>
///     <paramref name="Shed"/>, <paramref name="EnforcementMode"/> and
///     <paramref name="PolicyRevision"/> are the closed-loop envelope per
///     audit #8 + <c>project_centroid_learning_feedback_loop</c>. They tag
///     which (if any) stylobot enforcement shaped this request so the
///     centroid rollup / SessionStore filtering (follow-up) can segment
///     enforcement-shaped sessions out of the "natural prior" used by HNSW
///     similarity. <c>EnforcementMode</c> takes one of
///     <c>"natural" | "shed" | "throttle" | "block" | "challenge"</c>;
///     <c>"natural"</c> is the explicit value the BlackboardOrchestrator
///     stamps for a request that ran through detection without an
///     enforcement action firing. <c>PolicyRevision</c> is the
///     active-policy version string (opaque to this record) so post-hoc
///     analyses can correlate behavioural shift to policy changes. All
///     default to neutral values for back-compat with callers that don't
///     yet stamp.
///     </para>
/// </summary>
public readonly record struct SessionRequest(
    RequestState State,
    DateTimeOffset Timestamp,
    string PathTemplate,
    int StatusCode,
    bool FromUpstream = true,
    bool Shed = false,
    string? EnforcementMode = null,
    string? PolicyRevision = null,
    bool IsDocumentNavigation = false);

/// <summary>
///     A compressed behavioral snapshot of a session.
///     The session vector is the Markov transition matrix flattened + temporal features,
///     normalized for cosine similarity comparison.
/// </summary>
public sealed record SessionSnapshot
{
    /// <summary>Client signature (hashed IP:UA - zero PII)</summary>
    public required string Signature { get; init; }

    /// <summary>When this session started (first request)</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When this session ended (last request before boundary)</summary>
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>Number of requests in this session</summary>
    public required int RequestCount { get; init; }

    /// <summary>
    ///     The compressed session vector. Dimensions:
    ///     [0..N²-1]  Flattened Markov transition probabilities (N = RequestState count)
    ///     [N²..N²+N] Stationary distribution (time spent in each state)
    ///     [N²+N..]   Temporal features (timing entropy, burst ratio, session duration, etc.)
    /// </summary>
    public required float[] Vector { get; init; }

    /// <summary>
    ///     Confidence in this snapshot: f(request_count, state_diversity).
    ///     Low request counts produce unreliable vectors - consumers should gate on this.
    /// </summary>
    public required float Maturity { get; init; }

    /// <summary>Dominant request state by frequency</summary>
    public required RequestState DominantState { get; init; }

    /// <summary>
    ///     HMAC hashes of discriminatory headers at session start.
    ///     Persisted for retroactive stability analysis across sessions.
    ///     Null for sessions created before this field was added.
    /// </summary>
    public string? HeaderHashesJson { get; init; }

    /// <summary>
    ///     Frequency fingerprint: autocorrelation at 8 lag scales [1s, 3s, 10s, 30s, 1m, 3m, 10m, 30m].
    ///     Near-zero = aperiodic human; spike at lag k = bot with that rhythm.
    ///     Null when computed from fewer than 3 requests.
    /// </summary>
    public float[]? FrequencyFingerprint { get; init; }

    /// <summary>
    ///     Drift vector: linear regression slope over the preceding N session vectors.
    ///     Represents the behavioral direction this signature is heading in shape-space.
    ///     Null for the first session or when fewer than 3 prior sessions exist.
    /// </summary>
    public float[]? DriftVector { get; init; }
}

/// <summary>
///     Network/transport fingerprint context for a session.
///     These are per-session constants (TLS, TCP, H2 don't change mid-session)
///     and become dimensions in the unified session vector.
///     This unifies the "fingerprint" and "behavioral vector" concepts:
///     fingerprint mutation across sessions becomes velocity in these dimensions.
/// </summary>
public sealed record FingerprintContext
{
    /// <summary>TLS version bucket: 0=unknown, 0.5=TLS1.2, 1.0=TLS1.3</summary>
    public float TlsVersion { get; init; }

    /// <summary>HTTP protocol bucket: 0=HTTP/1.1, 0.5=HTTP/2, 1.0=HTTP/3</summary>
    public float HttpProtocol { get; init; }

    /// <summary>H2/H3 client type match: 0=unknown, 0.5=bot-like, 1.0=browser-like</summary>
    public float ProtocolClientType { get; init; }

    /// <summary>TCP OS matches UA OS: 0=mismatch, 0.5=unknown, 1.0=match</summary>
    public float TcpOsConsistency { get; init; }

    /// <summary>QUIC 0-RTT used (returning visitor): 0=no, 1=yes</summary>
    public float QuicZeroRtt { get; init; }

    /// <summary>Client-side fingerprint integrity: 0=missing, 0.5=suspicious, 1.0=legitimate</summary>
    public float ClientFingerprintIntegrity { get; init; }

    /// <summary>Client-side headless indicator: 0=not headless, 1.0=headless</summary>
    public float HeadlessScore { get; init; }

    /// <summary>IP is datacenter: 0=residential, 1.0=datacenter</summary>
    public float IsDatacenter { get; init; }

    public static readonly FingerprintContext Empty = new();
}

/// <summary>
///     Builds Markov-chain-based session vectors from request sequences.
///     The vector captures HOW a client navigates (transition probabilities between states),
///     WHEN (temporal features), and WITH WHAT (fingerprint context), compressed into a
///     fixed-size float[] for similarity search.
///     Fingerprints are unified into the same vector space as behavioral features,
///     so fingerprint mutation across sessions appears as velocity in those dimensions.
/// </summary>
public static class SessionVectorizer
{
    private static readonly int StateCount = Enum.GetValues<RequestState>().Length;

    private const int TemporalFeatureCount = 8;
    private const int FingerprintFeatureCount = 8;
    private const int TransitionTimingFeatureCount = 3;

    /// <summary>
    ///     Total vector dimensions:
    ///     N² Markov transitions + N stationary distribution + temporal + fingerprint
    /// </summary>
    public static int Dimensions =>
        StateCount * StateCount + StateCount + TemporalFeatureCount + FingerprintFeatureCount + TransitionTimingFeatureCount;

    /// <summary>
    ///     Encodes a sequence of session requests into a normalized float vector.
    /// </summary>
    public static float[] Encode(
        IReadOnlyList<SessionRequest> requests,
        FingerprintContext? fingerprint = null)
    {
        var vector = new float[Dimensions];

        if (requests.Count < 2)
            return vector; // Not enough data for transitions

        // === 1. Build transition matrix ===
        var transitions = new int[StateCount, StateCount];
        var stateCounts = new int[StateCount];

        for (var i = 0; i < requests.Count; i++)
        {
            var stateIdx = (int)requests[i].State;
            stateCounts[stateIdx]++;

            if (i > 0)
            {
                var fromIdx = (int)requests[i - 1].State;
                transitions[fromIdx, stateIdx]++;
            }
        }

        // === 2. Flatten transition probabilities into vector [0..N²-1] ===
        for (var from = 0; from < StateCount; from++)
        {
            var rowTotal = 0;
            for (var to = 0; to < StateCount; to++)
                rowTotal += transitions[from, to];

            for (var to = 0; to < StateCount; to++)
            {
                var idx = from * StateCount + to;
                vector[idx] = rowTotal > 0 ? (float)transitions[from, to] / rowTotal : 0f;
            }
        }

        // === 3. Stationary distribution [N²..N²+N-1] ===
        var stationaryOffset = StateCount * StateCount;
        var total = (float)requests.Count;
        for (var s = 0; s < StateCount; s++)
            vector[stationaryOffset + s] = stateCounts[s] / total;

        // === 4. Temporal features [N²+N..] ===
        var temporalOffset = stationaryOffset + StateCount;
        EncodeTemporalFeatures(requests, vector, temporalOffset);

        // === 5. Fingerprint features [N²+N+temporal..] ===
        var fpOffset = temporalOffset + TemporalFeatureCount;
        EncodeFingerprintFeatures(fingerprint ?? FingerprintContext.Empty, vector, fpOffset);

        // === 6. Per-transition timing features [N²+N+temporal+fingerprint..] ===
        var ttOffset = fpOffset + FingerprintFeatureCount;
        EncodeTransitionTimingFeatures(requests, vector, ttOffset);

        // === 7. L2-normalize for cosine similarity ===
        Normalize(vector);

        return vector;
    }

    /// <summary>
    ///     Computes maturity score: confidence in this vector based on data density.
    /// </summary>
    public static float ComputeMaturity(IReadOnlyList<SessionRequest> requests)
    {
        if (requests.Count < 2) return 0f;

        // Factor 1: Request count (diminishing returns past 20)
        var countFactor = Math.Min(1f, requests.Count / 20f);

        // Factor 2: State diversity (more states observed = more reliable transitions)
        var uniqueStates = requests.Select(r => r.State).Distinct().Count();
        var diversityFactor = Math.Min(1f, uniqueStates / 5f);

        // Factor 3: Session duration (very short sessions are unreliable)
        var duration = (requests[^1].Timestamp - requests[0].Timestamp).TotalMinutes;
        var durationFactor = Math.Min(1f, (float)(duration / 2.0)); // 2+ minutes = full confidence

        return countFactor * 0.5f + diversityFactor * 0.3f + durationFactor * 0.2f;
    }

    /// <summary>
    ///     Computes cosine similarity between two session vectors.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        // Handle dimension mismatch from vector evolution (e.g., 126-dim vs 129-dim):
        // use the shorter length for dot product, zero-pad implicitly.
        var len = Math.Min(a.Length, b.Length);
        if (len == 0) return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        // Include remaining dimensions in norms (zero-padded other vector contributes nothing to dot)
        for (var i = len; i < a.Length; i++)
            normA += a[i] * a[i];
        for (var i = len; i < b.Length; i++)
            normB += b[i] * b[i];

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0f;
    }

    /// <summary>
    ///     Computes the velocity vector (delta) between two session snapshots.
    ///     Positive values = dimension increased; negative = decreased.
    /// </summary>
    public static float[] ComputeVelocity(float[] current, float[] previous)
    {
        var velocity = new float[current.Length];
        for (var i = 0; i < current.Length; i++)
            velocity[i] = current[i] - previous[i];
        return velocity;
    }

    /// <summary>
    ///     Computes the L2 magnitude of a velocity vector.
    ///     High magnitude = large behavioral shift between sessions.
    /// </summary>
    public static float VelocityMagnitude(float[] velocity)
    {
        float sum = 0;
        for (var i = 0; i < velocity.Length; i++)
            sum += velocity[i] * velocity[i];
        return MathF.Sqrt(sum);
    }

    /// <summary>
    ///     Gap-normalized velocity magnitude: magnitude / sqrt(gapHours + 1).
    ///     Bots rotate fast (high normalized velocity); human drift is slow and gap-proportional.
    ///     Adding 1 prevents divide-by-zero for same-minute sessions.
    /// </summary>
    public static float GapNormalizedMagnitude(float[] velocity, TimeSpan gap)
    {
        var mag = VelocityMagnitude(velocity);
        var gapHours = (float)gap.TotalHours;
        return mag / MathF.Sqrt(gapHours + 1f);
    }

    /// <summary>
    ///     Decomposes a velocity vector into per-axis magnitudes.
    ///     Each component reveals which behavioral dimension is shifting:
    ///     - MarkovMagnitude: state transition pattern change (scripted path rotation)
    ///     - StationaryMagnitude: dwell-time shift (role change: scraper vs crawler)
    ///     - TemporalMagnitude: timing change (fingerprint randomizers adjust timing only)
    ///     - FingerprintMagnitude: TLS/HTTP2/TCP/headless change (rotation trail)
    ///     - TransitionTimingMagnitude: inter-state timing anomaly shift
    /// </summary>
    public static VelocityDecomposition DecomposeVelocity(float[] velocity)
    {
        var stateCount = Enum.GetValues<RequestState>().Length;
        var markovEnd = stateCount * stateCount;
        var stationaryEnd = markovEnd + stateCount;
        var temporalEnd = stationaryEnd + 8;
        var fingerprintEnd = temporalEnd + 8;

        return new VelocityDecomposition(
            L2Slice(velocity, 0, markovEnd),
            L2Slice(velocity, markovEnd, stationaryEnd),
            L2Slice(velocity, stationaryEnd, temporalEnd),
            L2Slice(velocity, temporalEnd, Math.Min(fingerprintEnd, velocity.Length)),
            L2Slice(velocity, Math.Min(fingerprintEnd, velocity.Length), velocity.Length));
    }

    /// <summary>
    ///     Computes the acceleration vector: the velocity between two consecutive velocity vectors.
    ///     Zero acceleration = constant rotation rate (scripted bot state machine).
    ///     High acceleration = escalating or decelerating behavior change.
    /// </summary>
    public static float[] ComputeAcceleration(float[] laterVelocity, float[] earlierVelocity)
    {
        var acc = new float[laterVelocity.Length];
        for (var i = 0; i < laterVelocity.Length; i++)
            acc[i] = laterVelocity[i] - earlierVelocity[i];
        return acc;
    }

    /// <summary>
    ///     Fits a linear regression over a time series of session vectors and returns the slope vector.
    ///     The slope vector is the behavioral "drift direction" -- where this entity is heading in shape-space.
    ///
    ///     Sessions are ordered by time (earliest first). Time values are normalized to [0, 1]
    ///     over the observed window so the slope is independent of the actual duration.
    ///
    ///     Returns a zero vector if fewer than 3 sessions are provided (not enough for a trend).
    ///
    ///     Uses ordinary least squares per dimension:
    ///     slope_d = cov(t, v_d) / var(t)
    /// </summary>
    public static float[] ComputeDriftVector(IReadOnlyList<SessionSnapshot> sessions)
    {
        if (sessions.Count < 3) return new float[Dimensions];

        var dims = Dimensions;
        var n = sessions.Count;

        // Normalize time to [0, 1] over the observed window
        var tMin = sessions[0].EndedAt.ToUnixTimeSeconds();
        var tMax = sessions[^1].EndedAt.ToUnixTimeSeconds();
        var tRange = tMax - tMin;

        if (tRange <= 0)
        {
            // All sessions at the same timestamp; use index as time
            tRange = n - 1;
            tMin = 0;
        }

        var times = new double[n];
        for (var i = 0; i < n; i++)
            times[i] = tRange > 0
                ? (double)(sessions[i].EndedAt.ToUnixTimeSeconds() - tMin) / tRange
                : (double)i / (n - 1);

        // Compute mean_t
        var meanT = 0.0;
        for (var i = 0; i < n; i++) meanT += times[i];
        meanT /= n;

        // Compute var(t)
        var varT = 0.0;
        for (var i = 0; i < n; i++)
        {
            var dt = times[i] - meanT;
            varT += dt * dt;
        }
        if (varT < 1e-12) return new float[dims]; // Degenerate case

        // Compute slope per dimension: cov(t, v_d) / var(t)
        var slope = new float[dims];
        for (var d = 0; d < dims; d++)
        {
            var covTv = 0.0;
            var meanV = 0.0;
            for (var i = 0; i < n; i++) meanV += sessions[i].Vector.Length > d ? sessions[i].Vector[d] : 0f;
            meanV /= n;

            for (var i = 0; i < n; i++)
            {
                var vd = sessions[i].Vector.Length > d ? sessions[i].Vector[d] : 0f;
                covTv += (times[i] - meanT) * (vd - meanV);
            }

            slope[d] = (float)(covTv / varT);
        }

        return slope;
    }

    /// <summary>
    ///     Projects a session vector forward in time by extrapolating along the drift vector.
    ///     driftVector is the slope from ComputeDriftVector (unit = change per normalized time unit).
    ///     steps is the number of normalized time units to project forward.
    ///     Returns an L2-normalized predicted position vector.
    /// </summary>
    public static float[] ProjectForward(float[] currentVector, float[] driftVector, float steps = 1.0f)
    {
        var dims = Math.Min(currentVector.Length, driftVector.Length);
        var predicted = new float[currentVector.Length];
        for (var i = 0; i < currentVector.Length; i++)
            predicted[i] = currentVector[i];
        for (var i = 0; i < dims; i++)
            predicted[i] += driftVector[i] * steps;

        // Re-normalize: predicted position must be on the unit sphere for cosine search
        Normalize(predicted);
        return predicted;
    }

    /// <summary>
    ///     Computes the Mahalanobis distance from a query vector to a centroid with a
    ///     diagonal covariance (per-dimension variance).
    ///
    ///     d_M = sqrt( sum_d (x_d - mu_d)^2 / (sigma_d^2 + epsilon) )
    ///
    ///     Unlike cosine similarity, this accounts for variance:
    ///     - A deviation in a low-variance dimension is anomalous (even if small)
    ///     - A deviation in a high-variance dimension is expected noise
    ///
    ///     epsilon prevents division by zero on degenerate dimensions (default 1e-6).
    ///     Returns float.MaxValue if vectors are incompatible lengths.
    /// </summary>
    public static float MahalanobisDistance(float[] query, float[] centroid, float[] variance,
        float epsilon = 1e-6f)
    {
        var dims = Math.Min(query.Length, Math.Min(centroid.Length, variance.Length));
        if (dims == 0) return float.MaxValue;

        var sum = 0.0f;
        for (var d = 0; d < dims; d++)
        {
            var diff = query[d] - centroid[d];
            sum += (diff * diff) / (variance[d] + epsilon);
        }

        return MathF.Sqrt(sum);
    }

    /// <summary>
    ///     Computes per-dimension variance from a set of vectors.
    ///     Used by the compaction service to build variance envelopes for ghost shapes.
    ///     Returns null if fewer than 2 vectors are provided.
    /// </summary>
    public static float[]? ComputeVarianceVector(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count < 2) return null;

        var dims = vectors[0].Length;
        var mean = new float[dims];

        foreach (var v in vectors)
            for (var d = 0; d < dims && d < v.Length; d++)
                mean[d] += v[d];
        for (var d = 0; d < dims; d++) mean[d] /= vectors.Count;

        var variance = new float[dims];
        foreach (var v in vectors)
            for (var d = 0; d < dims && d < v.Length; d++)
            {
                var diff = v[d] - mean[d];
                variance[d] += diff * diff;
            }
        for (var d = 0; d < dims; d++) variance[d] /= vectors.Count;

        return variance;
    }

    private static float L2Slice(float[] v, int start, int end)
    {
        float sum = 0;
        for (var i = start; i < end && i < v.Length; i++)
            sum += v[i] * v[i];
        return MathF.Sqrt(sum);
    }

    private static void EncodeTemporalFeatures(
        IReadOnlyList<SessionRequest> requests, float[] vector, int offset)
    {
        // Compute inter-request intervals
        var intervals = new List<double>(requests.Count - 1);
        for (var i = 1; i < requests.Count; i++)
            intervals.Add((requests[i].Timestamp - requests[i - 1].Timestamp).TotalMilliseconds);

        if (intervals.Count == 0) return;

        var mean = intervals.Average();
        var stdDev = intervals.Count > 1
            ? Math.Sqrt(intervals.Sum(x => Math.Pow(x - mean, 2)) / intervals.Count)
            : 0;

        // [0] Timing regularity: CV (coefficient of variation) - low = bot-like
        vector[offset + 0] = mean > 0 ? Math.Min(1f, (float)(stdDev / mean)) : 0f;

        // [1] Timing entropy (Shannon entropy of 100ms-bucketed intervals)
        vector[offset + 1] = Math.Min(1f, ComputeTimingEntropy(intervals) / 4f);

        // [2] Burst ratio: fraction of intervals < 100ms
        var burstCount = intervals.Count(i => i < 100);
        vector[offset + 2] = (float)burstCount / intervals.Count;

        // [3] Session duration (normalized: 0-30 minutes → 0-1)
        var durationMinutes = (requests[^1].Timestamp - requests[0].Timestamp).TotalMinutes;
        vector[offset + 3] = Math.Min(1f, (float)(durationMinutes / 30.0));

        // [4] Request rate (requests per minute, normalized)
        var rate = durationMinutes > 0 ? requests.Count / durationMinutes : requests.Count;
        vector[offset + 4] = Math.Min(1f, (float)(rate / 60.0)); // 60 rpm = 1.0

        // [5] 4xx error ratio -- only count upstream-derived 4xx. When
        // STYLOBOT synthesised the status (block 403, honeypot 404,
        // throttle 429, etc.) the response code reflects our own
        // enforcement choice, not visitor probing. Including those would
        // feed back as session-shape evidence and lock the visitor's
        // session vector toward "scanner" after a single enforcement
        // action (closed-loop feedback). Per "ONLY upstream status codes
        // should be factored in".
        var errorCount = requests.Count(r => r.FromUpstream && r.StatusCode >= 400 && r.StatusCode < 500);
        vector[offset + 5] = (float)errorCount / requests.Count;

        // [6] Unique path ratio (diversity)
        var uniquePaths = requests.Select(r => r.PathTemplate).Distinct().Count();
        vector[offset + 6] = (float)uniquePaths / requests.Count;

        // [7] Mean interval (normalized: 0-10s → 0-1)
        vector[offset + 7] = Math.Min(1f, (float)(mean / 10000.0));
    }

    private static void EncodeFingerprintFeatures(
        FingerprintContext fp, float[] vector, int offset)
    {
        vector[offset + 0] = fp.TlsVersion;
        vector[offset + 1] = fp.HttpProtocol;
        vector[offset + 2] = fp.ProtocolClientType;
        vector[offset + 3] = fp.TcpOsConsistency;
        vector[offset + 4] = fp.QuicZeroRtt;
        vector[offset + 5] = fp.ClientFingerprintIntegrity;
        vector[offset + 6] = fp.HeadlessScore;
        vector[offset + 7] = fp.IsDatacenter;
    }

    private static float ComputeTimingEntropy(List<double> intervals)
    {
        // Bucket into 100ms bins
        var buckets = new Dictionary<int, int>();
        foreach (var interval in intervals)
        {
            var bucket = (int)(interval / 100);
            buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
        }

        var total = (double)intervals.Count;
        double entropy = 0;
        foreach (var count in buckets.Values)
        {
            var p = count / total;
            if (p > 0) entropy -= p * Math.Log2(p);
        }

        return (float)entropy;
    }

    // Per-transition impossible timing thresholds (milliseconds).
    // If a transition happens faster than this, a human couldn't have caused it
    // (page hasn't rendered, API response hasn't arrived, etc.)
    private static readonly Dictionary<(int from, int to), double> ImpossibleTimingThresholds = new()
    {
        // PageView -> anything: page needs to render first
        { ((int)RequestState.PageView, (int)RequestState.PageView), 200 },
        { ((int)RequestState.PageView, (int)RequestState.ApiCall), 50 },
        { ((int)RequestState.PageView, (int)RequestState.FormSubmit), 300 },
        { ((int)RequestState.PageView, (int)RequestState.Search), 200 },
        // ApiCall -> PageView: needs navigation decision
        { ((int)RequestState.ApiCall, (int)RequestState.PageView), 100 },
        // FormSubmit -> anything: form processing visible to user
        { ((int)RequestState.FormSubmit, (int)RequestState.PageView), 150 },
    };

    private const double DefaultImpossibleThresholdMs = 30;
    private const double FastestTransitionBaselineMs = 100;

    private static void EncodeTransitionTimingFeatures(
        IReadOnlyList<SessionRequest> requests, float[] vector, int offset)
    {
        if (requests.Count < 3) return; // Need enough transitions for meaningful stats

        // Build per-transition timing map
        var transitionTimings = new Dictionary<(int from, int to), List<double>>();
        var allIntervals = new List<double>();

        for (var i = 1; i < requests.Count; i++)
        {
            var fromIdx = (int)requests[i - 1].State;
            var toIdx = (int)requests[i].State;
            var intervalMs = (requests[i].Timestamp - requests[i - 1].Timestamp).TotalMilliseconds;

            var key = (fromIdx, toIdx);
            if (!transitionTimings.TryGetValue(key, out var timings))
            {
                timings = new List<double>();
                transitionTimings[key] = timings;
            }
            timings.Add(intervalMs);
            allIntervals.Add(intervalMs);
        }

        if (allIntervals.Count == 0) return;

        // [0] Impossible timing ratio: fraction of transitions faster than physical threshold
        var impossibleCount = 0;
        var totalTransitions = 0;
        foreach (var (key, timings) in transitionTimings)
        {
            var threshold = ImpossibleTimingThresholds.GetValueOrDefault(key, DefaultImpossibleThresholdMs);
            foreach (var t in timings)
            {
                totalTransitions++;
                if (t < threshold) impossibleCount++;
            }
        }

        vector[offset + 0] = totalTransitions > 0
            ? Math.Clamp((float)impossibleCount / totalTransitions, 0f, 1f)
            : 0f;

        // [1] Timing consistency score: weighted avg CV across transition types (inverted: low CV = high score = bot-like)
        var totalWeight = 0.0;
        var weightedCvSum = 0.0;
        foreach (var (_, timings) in transitionTimings)
        {
            if (timings.Count < 2) continue;
            var mean = timings.Average();
            if (mean <= 0) continue;
            var stdDev = Math.Sqrt(timings.Select(t => Math.Pow(t - mean, 2)).Average());
            var cv = stdDev / mean;
            weightedCvSum += cv * timings.Count;
            totalWeight += timings.Count;
        }

        var avgCv = totalWeight > 0 ? weightedCvSum / totalWeight : 1.0;
        vector[offset + 1] = Math.Clamp(1f - (float)Math.Min(1.0, avgCv), 0f, 1f);

        // [2] Fastest transition z-score: how extreme is the minimum interval vs human baseline
        var minInterval = allIntervals.Min();
        vector[offset + 2] = Math.Clamp((float)Math.Max(0, 1.0 - minInterval / FastestTransitionBaselineMs), 0f, 1f);
    }

    private static void Normalize(float[] vector)
    {
        float norm = 0;
        for (var i = 0; i < vector.Length; i++)
            norm += vector[i] * vector[i];

        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        }
    }
}

/// <summary>
///     Manages session state per client: retrogressive boundary detection, history, and snapshots.
///     A session boundary is detected when:
///     - Inter-request gap exceeds threshold (default 30 minutes)
///     - User-Agent changes (new client context)
///     The boundary is retrogressive: we only know the previous session ended
///     when the NEXT request arrives and reveals the gap.
/// </summary>
public sealed class SessionStore
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<SessionStore> _logger;
    private readonly TimeSpan _sessionGapThreshold;
    private readonly int _maxSnapshotsPerSignature;
    private readonly int _maxRequestsPerSession;
    // Striped lock pool: a FIXED number of locks, chosen by hashing the signature.
    // Previously this was `ConcurrentDictionary<string, SemaphoreSlim>` with a
    // GetOrAdd per signature and no removal — one SemaphoreSlim per unique
    // signature, kept for the process lifetime. Under high-cardinality traffic
    // (rotating UAs/IPs) that grew without bound (memory + lazily-materialised
    // wait handles). A fixed stripe count bounds it to a constant; distinct
    // signatures may share a stripe (harmless: the lock only serialises the
    // per-signature session mutation, and cross-signature collisions are rare
    // and merely add brief contention). Static so all SessionStore instances
    // in a process share the (bounded) pool.
    private const int LockStripeCount = 512;

    private static readonly SemaphoreSlim[] _lockStripes = CreateLockStripes(LockStripeCount);

    private static SemaphoreSlim[] CreateLockStripes(int count)
    {
        var stripes = new SemaphoreSlim[count];
        for (var i = 0; i < count; i++) stripes[i] = new SemaphoreSlim(1, 1);
        return stripes;
    }

    private static SemaphoreSlim LockFor(string signature)
        => _lockStripes[(int)((uint)signature.GetHashCode() % LockStripeCount)];

    // Tracks signatures that have an active in-memory session, enabling flush-on-shutdown.
    // Value is the most recent FingerprintContext for vector encoding on shutdown.
    //
    // This is a PARALLEL shadow index of the session cache (_cache). Its lifetime must
    // track the session slot, otherwise under rotating-fingerprint traffic it grows one
    // entry per unique signature forever (the session in _cache expires after ~35 min,
    // but the shadow entry was previously only removed at shutdown) -> unbounded growth.
    // Two bounds keep it in step with the session cache:
    //  1) a PostEvictionCallback registered on the session slot (see RecordRequestAsync)
    //     removes the shadow entry when the session slot actually leaves the cache;
    //  2) ActiveSignaturesMaxEntries is a hard safety cap in case callbacks lag or are
    //     dropped, evicting the coldest slice so memory can never run away.
    private readonly ConcurrentDictionary<string, FingerprintContext?> _activeSignatures = new();

    // Safety cap on the shadow index. The session cache is the real bound; this only
    // guards against callback lag under extreme cardinality. Default 20,000 —
    // at 56k sigs/30min the old 100k cap would OOM at ~70min. Configurable via
    // BotDetection:SessionStore:ActiveSignaturesMaxEntries.
    private readonly int _activeSignaturesMaxEntries;

    /// <summary>Test hook: number of signatures in the active-session shadow index.</summary>
    internal int ActiveSignatureCount => _activeSignatures.Count;

    /// <summary>
    ///     Fired when a session is finalized (boundary detected).
    ///     Used by persistence layer to write completed sessions to SQLite.
    /// </summary>
    public event Action<SessionSnapshot, IReadOnlyList<SessionRequest>>? SessionFinalized;

    public SessionStore(
        IMemoryCache cache,
        ILogger<SessionStore> logger,
        TimeSpan? sessionGapThreshold = null,
        int maxSnapshotsPerSignature = 10,
        int maxRequestsPerSession = 200,
        int activeSignaturesMaxEntries = 20_000)
    {
        _cache = cache;
        _logger = logger;
        _sessionGapThreshold = sessionGapThreshold ?? TimeSpan.FromMinutes(30);
        _maxSnapshotsPerSignature = maxSnapshotsPerSignature;
        _maxRequestsPerSession = maxRequestsPerSession;
        _activeSignaturesMaxEntries = activeSignaturesMaxEntries > 0 ? activeSignaturesMaxEntries : 20_000;
    }

    /// <summary>
    ///     Records a request and returns a session boundary event if the previous session just ended.
    ///     This is the retrogressive detection: the arrival of THIS request tells us the
    ///     PREVIOUS session has ended (because the gap exceeded the threshold).
    /// </summary>
    public async Task<SessionSnapshot?> RecordRequestAsync(
        string signature,
        SessionRequest request,
        FingerprintContext? fingerprint = null)
    {
        var sessionLock = LockFor(signature);
        await sessionLock.WaitAsync();

        try
        {
            var sessionKey = $"session:current:{signature}";
            var currentSession = _cache.Get<List<SessionRequest>>(sessionKey);

            SessionSnapshot? completedSnapshot = null;

            if (currentSession != null && currentSession.Count > 0)
            {
                var lastRequest = currentSession[^1];
                var gap = request.Timestamp - lastRequest.Timestamp;

                // Retrogressive boundary: the gap tells us the PREVIOUS session is over
                if (gap >= _sessionGapThreshold)
                {
                    // Finalize the previous session into a snapshot
                    completedSnapshot = FinalizeSession(signature, currentSession, fingerprint);

                    // Start fresh
                    currentSession = new List<SessionRequest> { request };
                }
                else if (currentSession.Count < _maxRequestsPerSession)
                {
                    currentSession.Add(request);
                }
            }
            else
            {
                // First request - start new session
                currentSession = new List<SessionRequest> { request };
            }

            // Store with sliding expiration matching the gap threshold. Register a
            // post-eviction callback so the shadow index (_activeSignatures) is cleared
            // when the session slot leaves the cache — keeping the shadow index lifetime
            // bounded to the session lifetime instead of the process lifetime.
            var options = new MemoryCacheEntryOptions
            {
                SlidingExpiration = _sessionGapThreshold + TimeSpan.FromMinutes(5)
            };
            options.RegisterPostEvictionCallback(OnSessionSlotEvicted, signature);
            _cache.Set(sessionKey, currentSession, options);

            _activeSignatures[signature] = fingerprint;
            EvictActiveSignaturesIfNeeded();

            return completedSnapshot;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    ///     Fired by IMemoryCache when a session slot leaves the cache. Removes the parallel
    ///     shadow-index entry so its lifetime tracks the session's, preventing unbounded
    ///     growth under rotating-fingerprint traffic.
    /// </summary>
    private void OnSessionSlotEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        // Replaced == a fresh Set for the same signature superseded the old slot; the
        // session is still live, so keep the shadow entry.
        if (reason == EvictionReason.Replaced) return;
        if (state is not string signature) return;
        // A newer request may have re-created the session slot after this callback was
        // queued; only drop the shadow entry if the slot is genuinely gone.
        if (_cache.TryGetValue($"session:current:{signature}", out _)) return;
        _activeSignatures.TryRemove(signature, out _);
    }

    /// <summary>
    ///     Hard safety cap on the shadow index. Callback-driven removal is the primary
    ///     bound; this only fires if callbacks lag under extreme cardinality. Drops the
    ///     coldest slice (arbitrary enumeration order — memory bound, not hit-rate).
    /// </summary>
    private void EvictActiveSignaturesIfNeeded()
    {
        if (_activeSignatures.Count <= _activeSignaturesMaxEntries) return;
        // Knock 10% off so we don't re-evict on the very next insert.
        var target = _activeSignaturesMaxEntries - _activeSignaturesMaxEntries / 10;
        var overflow = _activeSignatures.Count - target;
        if (overflow <= 0) return;

        // LFU eviction: ConcurrentDictionary iteration order is roughly insertion
        // order — the oldest entries come first. Drop the first `overflow` entries.
        var drops = 0;
        foreach (var kv in _activeSignatures)
        {
            if (drops++ >= overflow) break;
            _activeSignatures.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    ///     Finalizes all active in-memory sessions and fires SessionFinalized for each.
    ///     Called on graceful shutdown to ensure no sessions are silently dropped.
    ///     Sessions with fewer than 3 requests are skipped (no meaningful vector).
    /// </summary>
    public async Task FlushAllActiveSessionsAsync()
    {
        var signatures = _activeSignatures.Keys.ToList();
        _logger.LogInformation("Flushing {Count} active sessions on shutdown", signatures.Count);

        foreach (var signature in signatures)
        {
            var sessionLock = LockFor(signature);
            await sessionLock.WaitAsync();
            try
            {
                var sessionKey = $"session:current:{signature}";
                var currentSession = _cache.Get<List<SessionRequest>>(sessionKey);
                if (currentSession is { Count: >= 3 })
                {
                    _activeSignatures.TryGetValue(signature, out var fingerprint);
                    FinalizeSession(signature, currentSession, fingerprint);
                    _cache.Remove(sessionKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush session for {Signature}", signature);
            }
            finally
            {
                sessionLock.Release();
            }
            _activeSignatures.TryRemove(signature, out _);
        }
    }

    /// <summary>
    ///     Gets the current (in-progress) session for a signature, if any.
    /// </summary>
    public IReadOnlyList<SessionRequest>? GetCurrentSession(string signature)
    {
        return _cache.Get<List<SessionRequest>>($"session:current:{signature}");
    }

    /// <summary>
    ///     Store header hashes for the current session. Called once per session (first request).
    ///     These get carried through to the SessionSnapshot at finalization.
    /// </summary>
    public void SetHeaderHashes(string signature, string headerHashesJson)
    {
        _cache.Set($"session:headers:{signature}", headerHashesJson,
            new MemoryCacheEntryOptions { SlidingExpiration = _sessionGapThreshold + TimeSpan.FromMinutes(5) });
    }

    /// <summary>
    ///     Gets a live radar projection of the current in-progress session.
    ///     Returns null if no active session or too few requests.
    ///     This is FAST - just encodes the cached request list into a vector and projects.
    /// </summary>
    public double[]? GetLiveRadarProjection(string signature, FingerprintContext? fingerprint = null)
    {
        var currentSession = GetCurrentSession(signature);
        if (currentSession == null || currentSession.Count < 2) return null;

        var vector = SessionVectorizer.Encode(currentSession, fingerprint);
        return VectorRadarProjection.Project(vector);
    }

    /// <summary>
    ///     Gets completed session snapshots for inter-session analysis.
    /// </summary>
    public IReadOnlyList<SessionSnapshot> GetHistory(string signature)
    {
        return _cache.Get<List<SessionSnapshot>>($"session:history:{signature}")
               ?? (IReadOnlyList<SessionSnapshot>)Array.Empty<SessionSnapshot>();
    }

    private SessionSnapshot? FinalizeSession(
        string signature, List<SessionRequest> requests, FingerprintContext? fingerprint = null)
    {
        if (requests.Count < 3) return null; // Too few requests for meaningful vector

        var vector = SessionVectorizer.Encode(requests, fingerprint);
        var maturity = SessionVectorizer.ComputeMaturity(requests);

        // Find dominant state
        var dominantState = requests
            .GroupBy(r => r.State)
            .OrderByDescending(g => g.Count())
            .First().Key;

        // Compute frequency fingerprint from request timestamps
        var frequencyFingerprint = FrequencyFingerprintEncoder.Encode(requests);

        // Compute drift vector from prior session history (needs at least 3 prior sessions)
        var historyKey = $"session:history:{signature}";
        var priorHistory = _cache.Get<List<SessionSnapshot>>(historyKey) ?? new List<SessionSnapshot>();
        var driftVector = priorHistory.Count >= 3
            ? SessionVectorizer.ComputeDriftVector(priorHistory)
            : null;

        var snapshot = new SessionSnapshot
        {
            Signature = signature,
            StartedAt = requests[0].Timestamp,
            EndedAt = requests[^1].Timestamp,
            RequestCount = requests.Count,
            Vector = vector,
            Maturity = maturity,
            DominantState = dominantState,
            HeaderHashesJson = _cache.Get<string>($"session:headers:{signature}"),
            FrequencyFingerprint = frequencyFingerprint.Any(f => f > 0) ? frequencyFingerprint : null,
            DriftVector = driftVector
        };

        // Append to history (ring buffer)
        var history = priorHistory;
        history.Add(snapshot);

        // Compact old snapshots into a root when history grows too large.
        // The root is a maturity-weighted average of old vectors - lossy compression
        // that preserves the client's baseline behavioral profile while discarding
        // per-session detail beyond the useful threshold.
        if (history.Count > _maxSnapshotsPerSignature)
            CompactHistory(history);

        _cache.Set(historyKey, history, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromHours(2)
        });

        _logger.LogDebug(
            "Session finalized for {Signature}: {Count} requests, maturity={Maturity:F2}, dominant={State}",
            signature, requests.Count, maturity, dominantState);

        // Notify persistence layer
        try { SessionFinalized?.Invoke(snapshot, requests); }
        catch (Exception ex) { _logger.LogWarning(ex, "SessionFinalized event handler failed"); }

        return snapshot;
    }

    /// <summary>
    ///     Compacts old snapshots into a single root snapshot via maturity-weighted averaging.
    ///     Keeps the most recent (maxSnapshots/2) snapshots intact for velocity analysis,
    ///     and merges all older ones into a root that preserves the baseline behavioral profile.
    ///     This is lossy compression: individual session detail is lost but the aggregate
    ///     behavioral signature is preserved for similarity comparison.
    /// </summary>
    private void CompactHistory(List<SessionSnapshot> history)
    {
        var keepCount = _maxSnapshotsPerSignature / 2;
        var compactCount = history.Count - keepCount;

        if (compactCount < 2) return; // Not enough to compact

        var toCompact = history.Take(compactCount).ToList();
        var toKeep = history.Skip(compactCount).ToList();

        // Weighted average of vectors by maturity
        var dims = SessionVectorizer.Dimensions;
        var rootVector = new float[dims];
        var totalWeight = 0f;
        var totalRequestCount = 0;

        foreach (var s in toCompact)
        {
            var weight = Math.Max(0.01f, s.Maturity);
            totalWeight += weight;
            totalRequestCount += s.RequestCount;

            for (var i = 0; i < dims; i++)
                rootVector[i] += s.Vector[i] * weight;
        }

        if (totalWeight > 0)
        {
            for (var i = 0; i < dims; i++)
                rootVector[i] /= totalWeight;
        }

        // Re-normalize after averaging
        float norm = 0;
        for (var i = 0; i < dims; i++)
            norm += rootVector[i] * rootVector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < dims; i++)
                rootVector[i] /= norm;
        }

        // Find dominant state across compacted sessions
        var dominantState = toCompact
            .GroupBy(s => s.DominantState)
            .OrderByDescending(g => g.Sum(s => s.RequestCount))
            .First().Key;

        // Preserve frequency fingerprint centroid so cross-session rhythm detection
        // can still match against compacted history
        var fingerprints = toCompact
            .Where(s => s.FrequencyFingerprint is { Length: > 0 })
            .Select(s => s.FrequencyFingerprint!)
            .ToList();
        float[]? rootFreqFingerprint = null;
        if (fingerprints.Count > 0)
        {
            var fpDims = fingerprints[0].Length;
            var fpSum = new float[fpDims];
            foreach (var fp in fingerprints)
                for (var i = 0; i < fpDims && i < fp.Length; i++)
                    fpSum[i] += fp[i];
            rootFreqFingerprint = fpSum;
            for (var i = 0; i < fpDims; i++) rootFreqFingerprint[i] /= fingerprints.Count;
        }

        var rootSnapshot = new SessionSnapshot
        {
            Signature = toCompact[0].Signature,
            StartedAt = toCompact[0].StartedAt,
            EndedAt = toCompact[^1].EndedAt,
            RequestCount = totalRequestCount,
            Vector = rootVector,
            Maturity = Math.Min(1f, totalWeight / compactCount), // Average maturity
            DominantState = dominantState,
            FrequencyFingerprint = rootFreqFingerprint
        };

        // Replace history: root + recent snapshots
        history.Clear();
        history.Add(rootSnapshot);
        history.AddRange(toKeep);

        _logger.LogDebug(
            "Compacted {CompactCount} snapshots into root ({TotalRequests} total requests), keeping {KeepCount} recent",
            compactCount, totalRequestCount, keepCount);
    }
}

/// <summary>
///     A behavioral archetype defined by a partial Markov transition vector.
///     Used for early detection when only 3-5 requests are available.
/// </summary>
public sealed record MarkovArchetype
{
    /// <summary>Human-readable archetype name</summary>
    public required string Name { get; init; }

    /// <summary>L2-normalized partial transition vector (100 dims, 10x10 Markov matrix)</summary>
    public required float[] PartialVector { get; init; }

    /// <summary>Default confidence delta to emit on match (positive = bot, negative = human)</summary>
    public required float DefaultConfidence { get; init; }

    /// <summary>Bot type to emit, or null for human archetypes</summary>
    public required BotType? DefaultBotType { get; init; }

    /// <summary>Whether this archetype represents human behavior</summary>
    public required bool IsHuman { get; init; }
}

/// <summary>
///     Hardcoded behavioral archetypes for partial Markov chain early detection.
///     Each archetype is a sparse transition matrix flattened to float[100] and L2-normalized.
/// </summary>
public static class MarkovArchetypes
{
    private static readonly int StateCount = Enum.GetValues<RequestState>().Length;

    public static IReadOnlyList<MarkovArchetype> All { get; } = BuildArchetypes();

    private static List<MarkovArchetype> BuildArchetypes()
    {
        return
        [
            Build("human-browser", new Dictionary<(int from, int to), float>
            {
                { ((int)RequestState.PageView, (int)RequestState.StaticAsset), 0.6f },
                { ((int)RequestState.StaticAsset, (int)RequestState.StaticAsset), 0.3f },
                { ((int)RequestState.StaticAsset, (int)RequestState.PageView), 0.4f },
                { ((int)RequestState.PageView, (int)RequestState.PageView), 0.1f }
            }, -0.15f, null, isHuman: true),

            Build("scraper", new Dictionary<(int from, int to), float>
            {
                { ((int)RequestState.PageView, (int)RequestState.PageView), 0.8f },
                { ((int)RequestState.PageView, (int)RequestState.ApiCall), 0.1f },
                { ((int)RequestState.ApiCall, (int)RequestState.PageView), 0.1f }
            }, 0.25f, BotType.Scraper, isHuman: false),

            Build("api-bot", new Dictionary<(int from, int to), float>
            {
                { ((int)RequestState.ApiCall, (int)RequestState.ApiCall), 0.9f },
                { ((int)RequestState.ApiCall, (int)RequestState.PageView), 0.05f },
                { ((int)RequestState.PageView, (int)RequestState.ApiCall), 0.05f }
            }, 0.30f, BotType.Scraper, isHuman: false),

            Build("scanner", new Dictionary<(int from, int to), float>
            {
                { ((int)RequestState.PageView, (int)RequestState.NotFound), 0.5f },
                { ((int)RequestState.NotFound, (int)RequestState.NotFound), 0.3f },
                { ((int)RequestState.NotFound, (int)RequestState.PageView), 0.2f }
            }, 0.35f, BotType.MaliciousBot, isHuman: false),

            Build("auth-bot", new Dictionary<(int from, int to), float>
            {
                { ((int)RequestState.PageView, (int)RequestState.FormSubmit), 0.3f },
                { ((int)RequestState.FormSubmit, (int)RequestState.AuthAttempt), 0.4f },
                { ((int)RequestState.AuthAttempt, (int)RequestState.AuthAttempt), 0.2f },
                { ((int)RequestState.AuthAttempt, (int)RequestState.FormSubmit), 0.1f }
            }, 0.30f, BotType.MaliciousBot, isHuman: false)
        ];
    }

    private static MarkovArchetype Build(
        string name,
        Dictionary<(int from, int to), float> transitions,
        float confidence,
        BotType? botType,
        bool isHuman)
    {
        var vector = new float[StateCount * StateCount];

        foreach (var ((from, to), prob) in transitions)
            vector[from * StateCount + to] = prob;

        // L2-normalize
        float norm = 0;
        for (var i = 0; i < vector.Length; i++)
            norm += vector[i] * vector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        }

        return new MarkovArchetype
        {
            Name = name,
            PartialVector = vector,
            DefaultConfidence = confidence,
            DefaultBotType = botType,
            IsHuman = isHuman
        };
    }
}

/// <summary>
///     Per-axis breakdown of a session velocity vector.
///     Pinpoints which behavioral dimension is shifting between sessions.
/// </summary>
public record VelocityDecomposition(
    float MarkovMagnitude,
    float StationaryMagnitude,
    float TemporalMagnitude,
    float FingerprintMagnitude,
    float TransitionTimingMagnitude)
{
    /// <summary>Total L2 magnitude across all dimensions.</summary>
    public float TotalMagnitude =>
        MathF.Sqrt(MarkovMagnitude * MarkovMagnitude
                   + StationaryMagnitude * StationaryMagnitude
                   + TemporalMagnitude * TemporalMagnitude
                   + FingerprintMagnitude * FingerprintMagnitude
                   + TransitionTimingMagnitude * TransitionTimingMagnitude);

    /// <summary>
    ///     True when fingerprint dims dominate: the behavioral shape is stable but
    ///     TLS/HTTP2/TCP/headless features changed. Classic rotation trail indicator.
    /// </summary>
    public bool IsFingerprintDominant =>
        FingerprintMagnitude > 0.01f &&
        FingerprintMagnitude > MarkovMagnitude * 1.5f &&
        FingerprintMagnitude > TemporalMagnitude * 1.5f;

    /// <summary>
    ///     True when Markov dims dominate: the path/state transition pattern shifted.
    ///     Bots rotating between task roles (crawler → form-submitter) show this.
    /// </summary>
    public bool IsMarkovDominant =>
        MarkovMagnitude > 0.01f &&
        MarkovMagnitude > FingerprintMagnitude * 1.5f &&
        MarkovMagnitude > TemporalMagnitude * 1.5f;

    /// <summary>
    ///     True when temporal dims dominate: timing changed but path/fingerprint stable.
    ///     Fingerprint randomizers that only adjust timing show this pattern.
    /// </summary>
    public bool IsTemporalDominant =>
        TemporalMagnitude > 0.01f &&
        TemporalMagnitude > MarkovMagnitude * 1.5f &&
        TemporalMagnitude > FingerprintMagnitude * 1.5f;
}
