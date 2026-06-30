using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Session-level behavioral vector contributor.
///     Compresses per-request Markov chain transitions into a fixed-dimension vector per session,
///     enabling inter-session anomaly detection via cosine similarity and velocity analysis.
///
///     Key capabilities:
///     - Retrogressive session boundary detection (gap-based, not time-windowed)
///     - Markov transition matrix → normalized vector compression
///     - Inter-session velocity: detects sudden behavioral shifts
///     - Human baseline comparison: sessions that don't look like normal browsing
///
///     Runs in Wave 1+ after transport classification and behavioral signals are available.
///     Configuration loaded from: sessionvector.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:SessionVectorContributor:*
/// </summary>
public class SessionVectorContributor : ConfiguredContributorBase
{
    private readonly ILogger<SessionVectorContributor> _logger;
    private readonly SessionStore _sessionStore;
    private readonly ISessionVectorSearch? _vectorSearch;
    private readonly SessionEscalationService? _escalationService;

    public SessionVectorContributor(
        ILogger<SessionVectorContributor> logger,
        IDetectorConfigProvider configProvider,
        SessionStore sessionStore,
        ISessionVectorSearch? vectorSearch = null,
        SessionEscalationService? escalationService = null)
        : base(configProvider)
    {
        _logger = logger;
        _sessionStore = sessionStore;
        _vectorSearch = vectorSearch;
        _escalationService = escalationService;
    }

    public override string Name => "SessionVector";
    public override int Priority => Manifest?.Priority ?? 30;

    // Requires the unified HMAC primary signature.
    // SequenceGuard: skip when on-track at positions 0-2 (not enough data for meaningful signal).
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.PrimarySignature),
        SequenceGuardTrigger.Default
    ];

    // Config-driven thresholds
    private int MinSessionRequests => GetParam("min_session_requests", 5);
    private float MinMaturityForScoring => (float)GetParam("min_maturity_for_scoring", 0.3);
    private float VelocityAnomalyThreshold => (float)GetParam("velocity_anomaly_threshold", 0.6);
    private float GapNormalizedVelocityThreshold => (float)GetParam("gap_normalized_velocity_threshold", 0.4);
    private float FingerprintRotationThreshold => (float)GetParam("fingerprint_rotation_threshold", 0.15);
    private float AccelerationThreshold => (float)GetParam("acceleration_threshold", 0.25);
    private float DissimilarityBotThreshold => (float)GetParam("dissimilarity_bot_threshold", 0.3);
    private double HighVelocityConfidence => GetParam("high_velocity_confidence", 0.5);
    private double GapNormalizedVelocityConfidence => GetParam("gap_normalized_velocity_confidence", 0.45);
    private double FingerprintRotationConfidence => GetParam("fingerprint_rotation_confidence", 0.55);
    private double AccelerationConfidence => GetParam("acceleration_confidence", 0.35);
    private double DissimilarSessionConfidence => GetParam("dissimilar_session_confidence", 0.4);
    private double ConsistentSessionHumanConfidence => GetParam("consistent_session_human_confidence", -0.2);
    private double StableVelocityHumanConfidence => GetParam("stable_velocity_human_confidence", -0.15);

    // Partial chain early detection thresholds
    private int PartialChainMinRequests => GetParam("partial_chain_min_requests", 3);
    private float PartialChainSimilarityThreshold => (float)GetParam("partial_chain_similarity_threshold", 0.6);
    private float PartialChainMaxConfidence => (float)GetParam("partial_chain_max_confidence", 0.35);

    // Frequency fingerprinting thresholds
    private float PeriodicityBotThreshold => (float)GetParam("periodicity_bot_threshold", 0.5);
    private double PeriodicityBotConfidence => GetParam("periodicity_bot_confidence", 0.35);
    private float FrequencyRhythmSimilarityThreshold => (float)GetParam("frequency_rhythm_similarity_threshold", 0.85);

    // Trajectory modeling thresholds
    private float TrajectoryAttackClusterThreshold => (float)GetParam("trajectory_attack_cluster_threshold", 0.72);
    private double TrajectoryAttackClusterConfidence => GetParam("trajectory_attack_cluster_confidence", 0.45);
    private float TrajectoryProjectionSteps => (float)GetParam("trajectory_projection_steps", 1.5f);

    // Void detection thresholds
    private float VoidDetectionMinSimilarity => (float)GetParam("void_detection_min_similarity", 0.40);
    private int VoidDetectionTopK => GetParam("void_detection_top_k", 5);
    private int VoidMinIndexSize => GetParam("void_min_index_size", 50);
    private double VoidBotConfidence => GetParam("void_bot_confidence", 0.30);
    private float VoidNearVoidThreshold => (float)GetParam("void_near_void_threshold", 0.50);
    private double VoidNearVoidMaxConfidence => GetParam("void_near_void_max_confidence", 0.15);
    private float VoidMahalanobisThreshold => (float)GetParam("void_mahalanobis_threshold", 3.0);

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
            if (string.IsNullOrEmpty(signature))
            {
                contributions.Add(NeutralContribution("No waveform signature available"));
                return contributions;
            }

            // Classify the current request into a Markov state
            var requestState = RequestMarkovClassifier.Classify(state);
            var statusCode = state.HttpContext.Response.StatusCode;
            var path = TemplatizePath(state.HttpContext.Request.Path.Value ?? "/");
            // Carry the per-request from_upstream flag so the
            // session-vector 4xx error-ratio arm can suppress stylobot-
            // synthesised statuses (closed-loop feedback). Default true
            // when the orchestrator hasn't stamped.
            var fromUpstream = state.GetSignal<bool?>(SignalKeys.ResponseFromUpstream) ?? true;

            var sessionRequest = new SessionRequest(
                requestState,
                DateTimeOffset.UtcNow,
                path,
                statusCode > 0 ? statusCode : 200,
                FromUpstream: fromUpstream);

            // Build fingerprint context from blackboard signals - these are per-session
            // constants that become dimensions in the unified vector. Fingerprint mutation
            // across sessions appears as velocity in these dimensions.
            var fpContext = BuildFingerprintContext(state);

            // Record request - may return a completed session snapshot (retrogressive boundary)
            var completedSession = await _sessionStore.RecordRequestAsync(signature, sessionRequest, fpContext);

            // Store header hashes on first request of a session (for progressive identity)
            var currentSession = _sessionStore.GetCurrentSession(signature);
            if (currentSession is { Count: 1 })
            {
                var headerHashesJson = state.GetSignal<string>(SignalKeys.HeaderHashes);
                if (!string.IsNullOrEmpty(headerHashesJson))
                    _sessionStore.SetHeaderHashes(signature, headerHashesJson);
            }

            // Write session signals
            var sessionHistory = _sessionStore.GetHistory(signature);

            state.WriteSignals([
                new(SignalKeys.SessionRequestCount, currentSession?.Count ?? 1),
                new(SignalKeys.SessionHistoryCount, sessionHistory.Count),
                new(SignalKeys.SessionCurrentState, requestState.ToString())
            ]);

            if (completedSession != null)
            {
                state.WriteSignals([
                    new(SignalKeys.SessionBoundaryDetected, true),
                    new(SignalKeys.SessionCompletedMaturity, completedSession.Maturity),
                    new(SignalKeys.SessionCompletedRequestCount, completedSession.RequestCount),
                    new(SignalKeys.SessionDominantState, completedSession.DominantState.ToString())
                ]);

                _logger.LogDebug(
                    "Session boundary detected for {Signature}: {Count} requests, maturity={Maturity:F2}",
                    signature, completedSession.RequestCount, completedSession.Maturity);
            }

            // Gateway-warmup gate: while the gateway is in cold-start warmup
            // (process uptime / total samples under floor) the behavioural
            // analyses below all source from under-sampled aggregates and
            // would push noisy cold-start contributions into the model + the
            // persisted centroid prior. Session recording above still runs
            // so the session vector continues to accumulate; only the
            // contribution-emitting branches stand down.
            //
            // Detector also OR-composes with UpstreamHealthy at the
            // RequestMarkovClassifier level: if EITHER gate says cold, the
            // Markov state assignment demotes status-derived transitions to
            // PageView so partial-chain / current-session / velocity
            // analyses don't bake outage shape either.
            var gatewayWarming = state.GetSignal<bool?>(SignalKeys.GatewayWarmup) ?? false;
            if (gatewayWarming)
            {
                if (contributions.Count == 0)
                    contributions.Add(NeutralContribution(
                        $"Gateway warmup active; session tracking accumulating ({currentSession?.Count ?? 0} requests, {sessionHistory.Count} prior sessions)"));
                return contributions;
            }

            // === Partial chain early detection (before full maturity) ===
            if (currentSession != null &&
                currentSession.Count >= PartialChainMinRequests &&
                currentSession.Count < MinSessionRequests)
            {
                AnalyzePartialChain(state, currentSession, fpContext, contributions);
            }

            // === Analyze current session (in-progress) ===
            if (currentSession != null && currentSession.Count >= MinSessionRequests)
            {
                var currentVector = SessionVectorizer.Encode(currentSession, fpContext);
                var currentMaturity = SessionVectorizer.ComputeMaturity(currentSession);

                state.WriteSignal(SignalKeys.SessionVectorMaturity, currentMaturity);

                if (currentMaturity >= MinMaturityForScoring)
                    AnalyzeCurrentSession(state, currentVector, contributions);
            }

            // === Analyze inter-session velocity (if we have history) ===
            if (sessionHistory.Count >= 2)
                AnalyzeInterSessionVelocity(state, sessionHistory, contributions);

            // === Frequency fingerprinting: detect temporal periodicity + cross-session rhythm match ===
            if (currentSession != null && currentSession.Count >= 5)
            {
                AnalyzeFrequencyRhythm(state, currentSession, contributions);
                if (sessionHistory.Count >= 2)
                    AnalyzeCrossSessionRhythm(state, currentSession, sessionHistory, contributions);
            }

            // === Void detection + trajectory analysis (require HNSW) ===
            if (_vectorSearch != null && currentSession != null && currentSession.Count >= MinSessionRequests)
            {
                var currentVector = SessionVectorizer.Encode(currentSession, BuildFingerprintContext(state));
                await AnalyzeVoidnessAsync(state, currentVector, contributions, cancellationToken);

                if (sessionHistory.Count >= 3)
                    await AnalyzeTrajectoryAsync(state, currentVector, sessionHistory, contributions, cancellationToken);
            }

            // Default: neutral if no analysis triggered
            if (contributions.Count == 0)
                contributions.Add(NeutralContribution(
                    $"Session tracking active ({currentSession?.Count ?? 0} requests, {sessionHistory.Count} prior sessions)"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in session vector analysis");
            contributions.Add(NeutralContribution("Session vector analysis error"));
        }

        return contributions;
    }

    /// <summary>
    ///     Analyzes the current in-progress session against historical baselines.
    /// </summary>
    private void AnalyzeCurrentSession(
        BlackboardState state,
        float[] currentVector,
        List<DetectionContribution> contributions)
    {
        var history = _sessionStore.GetHistory(
            state.GetSignal<string>(SignalKeys.PrimarySignature) ?? "");

        if (history.Count == 0) return;

        // Compare current session against prior sessions from this same client
        var similarities = history
            .Where(h => h.Maturity >= MinMaturityForScoring)
            .Select(h => SessionVectorizer.CosineSimilarity(currentVector, h.Vector))
            .ToList();

        if (similarities.Count == 0) return;

        var avgSimilarity = similarities.Average();
        state.WriteSignal(SignalKeys.SessionSelfSimilarity, avgSimilarity);

        // Very dissimilar from own history = possible session hijack or bot rotation
        if (avgSimilarity < DissimilarityBotThreshold)
        {
            contributions.Add(BotContribution(
                DissimilarSessionConfidence,
                $"Current session behavior diverges from client history (similarity={avgSimilarity:F2})",
                BotType.Scraper));
        }
        // Consistent with own history = human-like continuity
        else if (avgSimilarity > 0.7f)
        {
            contributions.Add(HumanContribution(
                Math.Abs(ConsistentSessionHumanConfidence),
                $"Session behavior consistent with client history (similarity={avgSimilarity:F2})"));
        }
    }

    /// <summary>
    ///     Detects anomalous velocity between recent sessions.
    ///     Human sessions drift gradually; bots show binary state switches.
    ///     Emits dimensional decomposition, gap-normalized velocity, and acceleration signals.
    /// </summary>
    private void AnalyzeInterSessionVelocity(
        BlackboardState state,
        IReadOnlyList<SessionSnapshot> history,
        List<DetectionContribution> contributions)
    {
        // Use up to 3 most recent mature sessions for acceleration analysis
        var recent = history
            .Where(h => h.Maturity >= MinMaturityForScoring)
            .OrderByDescending(h => h.EndedAt)
            .Take(3)
            .ToList();

        if (recent.Count < 2) return;

        // Primary velocity: most recent pair
        var v1 = SessionVectorizer.ComputeVelocity(recent[0].Vector, recent[1].Vector);
        var magnitude = SessionVectorizer.VelocityMagnitude(v1);
        var decomp = SessionVectorizer.DecomposeVelocity(v1);

        // Gap-normalized velocity: bots rotate quickly relative to elapsed time
        var gap = recent[0].EndedAt - recent[1].EndedAt;
        var gapNormMag = SessionVectorizer.GapNormalizedMagnitude(v1, gap);

        state.WriteSignals([
            new(SignalKeys.SessionVelocityMagnitude, magnitude),
            new(SignalKeys.SessionVelocityVector, v1),
            new(SignalKeys.SessionVelocityGapNormalized, gapNormMag),
            new(SignalKeys.SessionVelocityMarkovMagnitude, decomp.MarkovMagnitude),
            new(SignalKeys.SessionVelocityTemporalMagnitude, decomp.TemporalMagnitude),
            new(SignalKeys.SessionVelocityFingerprintMagnitude, decomp.FingerprintMagnitude),
            new(SignalKeys.SessionVelocityIsFingerprintRotation, decomp.IsFingerprintDominant)
        ]);

        // === Acceleration: change in velocity across 3 sessions ===
        if (recent.Count == 3)
        {
            var v2 = SessionVectorizer.ComputeVelocity(recent[1].Vector, recent[2].Vector);
            var acceleration = SessionVectorizer.ComputeAcceleration(v1, v2);
            var accMag = SessionVectorizer.VelocityMagnitude(acceleration);
            state.WriteSignal(SignalKeys.SessionVelocityAcceleration, accMag);

            // Near-zero acceleration with non-trivial velocity = constant rotation rate (bot state machine)
            if (magnitude > 0.15f && accMag < AccelerationThreshold * magnitude)
            {
                contributions.Add(BotContribution(
                    AccelerationConfidence,
                    $"Constant rotation rate across 3 sessions (velocity={magnitude:F2}, accel={accMag:F2})",
                    BotType.Scraper));
            }
        }

        // === Fingerprint rotation: behavioral shape stable, fingerprint changed ===
        if (decomp.IsFingerprintDominant && decomp.FingerprintMagnitude > FingerprintRotationThreshold)
        {
            contributions.Add(BotContribution(
                FingerprintRotationConfidence,
                $"Fingerprint rotation trail: behavior stable but transport fingerprint shifted (fp_delta={decomp.FingerprintMagnitude:F2})",
                BotType.Scraper));
        }

        // === Gap-normalized velocity: fast rotation ===
        if (gapNormMag > GapNormalizedVelocityThreshold)
        {
            contributions.Add(BotContribution(
                GapNormalizedVelocityConfidence,
                $"Fast behavioral rotation (gap-normalized velocity={gapNormMag:F2}, gap={gap.TotalMinutes:F0}m)",
                BotType.Scraper));
        }
        // Raw high velocity (unchanged for backward compat with existing signals)
        else if (magnitude > VelocityAnomalyThreshold)
        {
            contributions.Add(BotContribution(
                HighVelocityConfidence,
                $"Sudden behavioral shift between sessions (velocity={magnitude:F2})",
                BotType.Scraper));
        }

        // Low velocity across multiple sessions = stable human behavior
        if (magnitude < 0.15f && history.Count >= 3)
        {
            contributions.Add(HumanContribution(
                Math.Abs(StableVelocityHumanConfidence),
                $"Stable behavior across {history.Count} sessions (velocity={magnitude:F2})"));
        }
    }

    /// <summary>
    ///     Computes the frequency fingerprint for the current session and emits periodicity signals.
    ///     High periodicity = bot rhythm (scraper retry loop, crawl window, credential stuffer).
    ///     Low periodicity = human (broadband, aperiodic).
    /// </summary>
    private void AnalyzeFrequencyRhythm(
        BlackboardState state,
        IReadOnlyList<SessionRequest> currentSession,
        List<DetectionContribution> contributions)
    {
        var fingerprint = FrequencyFingerprintEncoder.Encode(currentSession);
        var periodicityScore = FrequencyFingerprintEncoder.PeriodicityScore(fingerprint);
        var dominantLag = FrequencyFingerprintEncoder.DominantLagIndex(fingerprint);

        state.WriteSignals([
            new(SignalKeys.SessionFrequencyFingerprint, fingerprint),
            new(SignalKeys.SessionFrequencyPeriodicityScore, periodicityScore),
            new(SignalKeys.SessionFrequencyDominantLag, dominantLag)
        ]);

        if (periodicityScore < PeriodicityBotThreshold) return;

        var lagDesc = dominantLag >= 0
            ? $"{FrequencyFingerprintEncoder.LagSeconds[dominantLag]}s"
            : "unknown";

        contributions.Add(BotContribution(
            PeriodicityBotConfidence,
            $"Periodic request rhythm detected (score={periodicityScore:F2}, dominant_lag={lagDesc})",
            BotType.Scraper));
    }

    /// <summary>
    ///     Cross-session rhythm comparison: checks if the current frequency fingerprint matches
    ///     prior sessions for this signature. A rhythm-preserving behavioral rotation (same timing
    ///     cadence, different Markov paths) is a strong indicator of deliberate evasion:
    ///     the bot changed what it requests but kept the same mechanical timing loop.
    /// </summary>
    private void AnalyzeCrossSessionRhythm(
        BlackboardState state,
        IReadOnlyList<SessionRequest> currentSession,
        IReadOnlyList<SessionSnapshot> sessionHistory,
        List<DetectionContribution> contributions)
    {
        var currentFingerprint = FrequencyFingerprintEncoder.Encode(currentSession);
        var currentPeriodicity = FrequencyFingerprintEncoder.PeriodicityScore(currentFingerprint);

        // Only flag rhythm-preserving rotation when current session is itself periodic
        if (currentPeriodicity < PeriodicityBotThreshold) return;

        var priorFingerprints = sessionHistory
            .Where(s => s.FrequencyFingerprint is { Length: > 0 })
            .Select(s => s.FrequencyFingerprint!)
            .ToList();

        if (priorFingerprints.Count == 0) return;

        var maxSimilarity = priorFingerprints
            .Select(fp => FrequencyFingerprintEncoder.Similarity(currentFingerprint, fp))
            .Max();

        if (maxSimilarity < FrequencyRhythmSimilarityThreshold) return;

        contributions.Add(BotContribution(
            PeriodicityBotConfidence + 0.1, // Slightly higher confidence: cross-session pattern match
            $"Rhythm-preserving rotation detected: periodicity preserved across sessions (similarity={maxSimilarity:F2})",
            BotType.Scraper));
    }

    /// <summary>
    ///     Void detection: checks if the current session vector is in empty shape-space.
    ///     A session with no similar vectors in the HNSW index is genuinely novel behavior --
    ///     the highest-priority alert: cannot match against known signatures, but the absence
    ///     of neighbors is itself a strong signal that this doesn't look like any known human either.
    ///
    ///     Note: this fires as a signal, not a hard bot contribution. Novel sessions might be
    ///     legitimate (new service, new user population). The signal feeds into LLM escalation.
    /// </summary>
    private async Task AnalyzeVoidnessAsync(
        BlackboardState state,
        float[] currentVector,
        List<DetectionContribution> contributions,
        CancellationToken ct)
    {
        if (_vectorSearch == null) return;

        try
        {
            var similar = await _vectorSearch.FindSimilarAsync(
                currentVector, topK: VoidDetectionTopK, minSimilarity: VoidDetectionMinSimilarity);

            var topSimilarity = similar.Count > 0 ? similar[0].Similarity : 0f;
            var isVoid = similar.Count == 0;

            // Second pass: Mahalanobis search for variance-aware matching.
            // A session that looks void under cosine but lands inside a known centroid's variance
            // envelope is NOT novel — it's a known campaign with high within-cluster variance.
            // A session that looks void under BOTH is genuinely novel: highest-priority alert.
            var mahalanobisNearest = float.MaxValue;
            try
            {
                var mahalMatches = await _vectorSearch.FindSimilarMahalanobisAsync(
                    currentVector, topK: 1, maxDistance: 5.0f);
                if (mahalMatches.Count > 0)
                {
                    // Similarity is converted back from distance: high similarity = small distance
                    mahalanobisNearest = 1f - mahalMatches[0].Similarity; // approx distance
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mahalanobis search failed (non-fatal)");
            }

            state.WriteSignals([
                new(SignalKeys.SessionIsVoid, isVoid),
                new(SignalKeys.SessionTopSimilarity, topSimilarity),
                new(SignalKeys.SessionMahalanobisNearestDistance, mahalanobisNearest)
            ]);

            if (isVoid)
            {
                var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
                _logger.LogDebug("Void detection: session in empty shape-space for {Signature}", signature);

                // Flag for LLM escalation at session end: novel behavior needs deeper analysis.
                if (signature != null)
                    _escalationService?.FlagForEscalation(signature);
            }

            // Graduated novelty contribution — only fires when the index is mature enough
            // that void means something (not just an empty index on a fresh install).
            var indexSize = _vectorSearch.Count;
            if (indexSize >= VoidMinIndexSize)
            {
                if (isVoid && mahalanobisNearest >= VoidMahalanobisThreshold)
                {
                    // Double-void: outside cosine neighbors AND outside all Mahalanobis variance envelopes.
                    // Single-void (cosine void but within a known variance envelope) is a known-campaign
                    // variant with high intra-cluster scatter — cluster detectors handle it.
                    contributions.Add(BotContribution(VoidBotConfidence,
                        "Session has no behavioral neighbors in any known shape-space (double-void)",
                        BotType.Scraper));
                }
                else if (topSimilarity < VoidNearVoidThreshold)
                {
                    // Near-void: found neighbors but they're all weak. Scale contribution by
                    // how far below the threshold we are (0 at threshold, max at similarity=0).
                    var scale = (VoidNearVoidThreshold - topSimilarity) / VoidNearVoidThreshold;
                    var confidence = VoidNearVoidMaxConfidence * scale;
                    contributions.Add(BotContribution(confidence,
                        $"Session behavioral shape is weakly matched (topSimilarity={topSimilarity:F2})",
                        BotType.Scraper));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Void detection HNSW search failed");
        }
    }

    /// <summary>
    ///     Trajectory analysis: fits a linear regression over recent session vectors to compute
    ///     the drift direction, then projects forward to predict where this entity is heading.
    ///     If the predicted position lands inside a known attack cluster, flags as pre-crime.
    ///
    ///     This detects reactivating campaigns: a signature that reappears moving in the same
    ///     direction it was drifting when it went dormant is continuing, not returning.
    /// </summary>
    private async Task AnalyzeTrajectoryAsync(
        BlackboardState state,
        float[] currentVector,
        IReadOnlyList<SessionSnapshot> sessionHistory,
        List<DetectionContribution> contributions,
        CancellationToken ct)
    {
        if (_vectorSearch == null) return;

        try
        {
            // Use the most recent sessions (ordered oldest-first for regression)
            var recent = sessionHistory
                .Where(h => h.Maturity >= MinMaturityForScoring)
                .OrderBy(h => h.EndedAt)
                .TakeLast(8)
                .ToList();

            if (recent.Count < 3) return;

            var driftVector = SessionVectorizer.ComputeDriftVector(recent);
            var driftMagnitude = SessionVectorizer.VelocityMagnitude(driftVector);

            state.WriteSignal(SignalKeys.SessionDriftVector, driftVector);

            // Only project if there is meaningful drift; zero drift = stable behavior
            if (driftMagnitude < 0.05f) return;

            // Project forward by TrajectoryProjectionSteps normalized time units
            var predicted = SessionVectorizer.ProjectForward(currentVector, driftVector, TrajectoryProjectionSteps);

            // Search HNSW for vectors near the predicted position
            var nearPredicted = await _vectorSearch.FindSimilarAsync(
                predicted, topK: 3, minSimilarity: TrajectoryAttackClusterThreshold);

            if (nearPredicted.Count == 0) return;

            // Take the highest-similarity match; if it's from a known bot signature, flag it.
            // We use IsBot from the vector index metadata (set when vectors are added).
            var top = nearPredicted[0];
            var allVectors = _vectorSearch.GetAllVectorsSnapshot();
            var topMeta = allVectors.FirstOrDefault(v => v.Metadata.Signature == top.Signature).Metadata;
            if (topMeta == null || !topMeta.IsBot) return;

            var similarity = top.Similarity;

            state.WriteSignals([
                new(SignalKeys.SessionTrajectoryClusterSimilarity, similarity),
                new(SignalKeys.SessionTrajectoryInAttackCluster, true)
            ]);

            contributions.Add(BotContribution(
                TrajectoryAttackClusterConfidence,
                $"Behavioral trajectory points toward known attack cluster (predicted_similarity={similarity:F2}, drift_magnitude={driftMagnitude:F2})",
                BotType.Scraper));

            _logger.LogDebug(
                "Trajectory alert: {Signature} drifting toward bot cluster (predicted_sim={Sim:F2})",
                state.GetSignal<string>(SignalKeys.PrimarySignature), similarity);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Trajectory analysis failed");
        }
    }

    /// <summary>
    ///     Analyzes a partial Markov chain (3-5 requests) against known behavioral archetypes
    ///     for early bot/human signal scoring before the full session vector matures.
    /// </summary>
    private void AnalyzePartialChain(
        BlackboardState state,
        IReadOnlyList<SessionRequest> currentSession,
        FingerprintContext fpContext,
        List<DetectionContribution> contributions)
    {
        // Encode only the transition matrix (first 100 dims) from the partial session
        var fullVector = SessionVectorizer.Encode(currentSession, fpContext);
        var stateCount = Enum.GetValues<RequestState>().Length;
        var transitionDims = stateCount * stateCount;
        var partialVector = new float[transitionDims];
        Array.Copy(fullVector, partialVector, Math.Min(fullVector.Length, transitionDims));

        // L2-normalize the partial vector
        float norm = 0;
        for (var i = 0; i < partialVector.Length; i++)
            norm += partialVector[i] * partialVector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < partialVector.Length; i++)
                partialVector[i] /= norm;
        }
        else
        {
            // Zero vector - no transitions to analyze
            return;
        }

        // Compare against each archetype
        var bestSimilarity = 0f;
        MarkovArchetype? bestMatch = null;

        foreach (var archetype in MarkovArchetypes.All)
        {
            var similarity = CosineSimilarity100(partialVector, archetype.PartialVector);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestMatch = archetype;
            }
        }

        var threshold = PartialChainSimilarityThreshold;
        if (bestMatch == null || bestSimilarity < threshold)
            return;

        // Scale confidence by similarity, clamped to max
        var scaledConfidence = Math.Min(
            PartialChainMaxConfidence,
            Math.Abs(bestMatch.DefaultConfidence) * bestSimilarity);

        // Write signals
        state.WriteSignals([
            new(SignalKeys.SessionPartialChainMatch, bestMatch.Name),
            new(SignalKeys.SessionPartialChainSimilarity, bestSimilarity),
            new(SignalKeys.SessionPartialChainConfidence, scaledConfidence)
        ]);

        if (bestMatch.IsHuman)
        {
            contributions.Add(HumanContribution(
                scaledConfidence,
                $"Partial chain ({currentSession.Count} requests) matches '{bestMatch.Name}' archetype (similarity={bestSimilarity:F2})"));
        }
        else
        {
            contributions.Add(BotContribution(
                scaledConfidence,
                $"Partial chain ({currentSession.Count} requests) matches '{bestMatch.Name}' archetype (similarity={bestSimilarity:F2})",
                bestMatch.DefaultBotType ?? BotType.Scraper));
        }

        _logger.LogDebug(
            "Partial chain match: {Archetype} (similarity={Similarity:F2}, confidence={Confidence:F2}, requests={Count})",
            bestMatch.Name, bestSimilarity, scaledConfidence, currentSession.Count);
    }

    /// <summary>
    ///     Cosine similarity for two float[100] vectors (already L2-normalized).
    /// </summary>
    private static float CosineSimilarity100(float[] a, float[] b)
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

    /// <summary>
    ///     Builds a FingerprintContext from blackboard signals.
    ///     These are per-session constants that become vector dimensions,
    ///     unifying network fingerprints with behavioral vectors.
    /// </summary>
    private static FingerprintContext BuildFingerprintContext(BlackboardState state)
    {
        // TLS version
        var tlsProtocol = state.GetSignal<string>(SignalKeys.TlsProtocol) ?? "";
        var tlsVersion = tlsProtocol switch
        {
            _ when tlsProtocol.Contains("1.3") => 1.0f,
            _ when tlsProtocol.Contains("1.2") => 0.5f,
            _ when tlsProtocol.Length > 0 => 0.2f, // Old TLS
            _ => 0f // Unknown
        };

        // HTTP protocol version
        var h2 = state.GetSignal<string>(SignalKeys.H2Protocol);
        var h3 = state.GetSignal<string>(SignalKeys.H3Protocol);
        var httpProtocol = h3 != null ? 1.0f : h2 != null ? 0.5f : 0f;

        // H2/H3 client type (browser vs tool)
        var clientType = state.GetSignal<string>(SignalKeys.H2ClientType)
                         ?? state.GetSignal<string>(SignalKeys.H3ClientType) ?? "";
        var protocolClientType = clientType.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                                 clientType.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ||
                                 clientType.Contains("Safari", StringComparison.OrdinalIgnoreCase)
            ? 1.0f
            : clientType.Contains("Bot", StringComparison.OrdinalIgnoreCase) ? 0.2f
            : clientType.Length > 0 ? 0.5f : 0f;

        // TCP OS vs UA OS consistency
        var tcpOs = state.GetSignal<string>(SignalKeys.TcpOsHint)
                    ?? state.GetSignal<string>(SignalKeys.TcpOsHintTtl) ?? "";
        var uaOs = state.GetSignal<string>(SignalKeys.UserAgentOs) ?? "";
        float tcpOsConsistency;
        if (tcpOs.Length == 0 || uaOs.Length == 0)
            tcpOsConsistency = 0.5f; // Unknown
        else
            tcpOsConsistency = tcpOs.Contains(uaOs, StringComparison.OrdinalIgnoreCase) ||
                               uaOs.Contains(tcpOs, StringComparison.OrdinalIgnoreCase)
                ? 1.0f
                : 0f;

        // QUIC 0-RTT (returning visitor)
        var zeroRtt = state.GetSignal<bool?>(SignalKeys.H3ZeroRtt) ?? false;

        // Client-side fingerprint
        var fpIntegrity = state.GetSignal<double?>(SignalKeys.FingerprintIntegrityScore);
        var clientFpIntegrity = fpIntegrity.HasValue ? Math.Min(1f, (float)fpIntegrity.Value) : 0f;

        var headless = state.GetSignal<double?>(SignalKeys.FingerprintHeadlessScore);
        var headlessScore = headless.HasValue ? Math.Min(1f, (float)headless.Value) : 0f;

        // Datacenter IP
        var isDatacenter = state.GetSignal<bool?>(SignalKeys.IpIsDatacenter) ?? false;

        return new FingerprintContext
        {
            TlsVersion = tlsVersion,
            HttpProtocol = httpProtocol,
            ProtocolClientType = protocolClientType,
            TcpOsConsistency = tcpOsConsistency,
            QuicZeroRtt = zeroRtt ? 1f : 0f,
            ClientFingerprintIntegrity = clientFpIntegrity,
            HeadlessScore = headlessScore,
            IsDatacenter = isDatacenter ? 1f : 0f
        };
    }

    /// <summary>
    ///     Simplifies paths for Markov state comparison: /users/123/posts → /users/{id}/posts
    /// </summary>
    private static string TemplatizePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            // Replace numeric IDs
            if (long.TryParse(segments[i], out _))
                segments[i] = "{id}";
            // Replace GUIDs
            else if (Guid.TryParse(segments[i], out _))
                segments[i] = "{guid}";
            // Replace base64-like tokens (>20 chars, alphanumeric)
            else if (segments[i].Length > 20 && segments[i].All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '='))
                segments[i] = "{token}";
        }

        return "/" + string.Join("/", segments);
    }

    private DetectionContribution NeutralContribution(string reason) => new()
    {
        DetectorName = Name,
        Category = "SessionVector",
        ConfidenceDelta = 0.0,
        Weight = 1.0,
        Reason = reason
    };

    private DetectionContribution BotContribution(double confidence, string reason, BotType botType) => new()
    {
        DetectorName = Name,
        Category = "SessionVector",
        ConfidenceDelta = confidence,
        Weight = WeightBotSignal,
        Reason = reason,
        BotType = botType.ToString()
    };

    private DetectionContribution HumanContribution(double confidence, string reason) => new()
    {
        DetectorName = Name,
        Category = "SessionVector",
        ConfidenceDelta = -confidence,
        Weight = WeightHumanSignal,
        Reason = reason
    };
}
