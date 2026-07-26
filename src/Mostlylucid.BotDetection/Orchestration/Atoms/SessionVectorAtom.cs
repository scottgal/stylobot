using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that compresses per-request Markov
///     transitions into a fixed-dimension session vector and detects
///     inter-session behavioural drift, velocity anomalies, frequency
///     periodicity, HNSW voidness, and trajectory-toward-attack-clusters.
///     Priority 30.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>SessionVectorContributor</c>. Rich vectors
///         (SessionVelocityVector, SessionDriftVector, SessionFrequencyFingerprint)
///         stay on <see cref="HttpContext.Items"/> under
///         <see cref="VelocityVectorKey"/> / <see cref="DriftVectorKey"/> /
///         <see cref="FrequencyFingerprintKey"/>. Sink receives only
///         scalars / booleans / short strings.
///     </para>
/// </remarks>
public sealed class SessionVectorAtom : DetectorAtomBase
{
    public const string VelocityVectorKey = "stylobot.session.velocity_vector";
    public const string DriftVectorKey = "stylobot.session.drift_vector";
    public const string FrequencyFingerprintKey = "stylobot.session.frequency_fingerprint";
    private const int SequenceMinPosition = 3;

    private readonly ILogger<SessionVectorAtom> _logger;
    private readonly SessionStore _sessionStore;
    private readonly ISessionVectorSearch? _vectorSearch;
    private readonly SessionEscalationService? _escalationService;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public SessionVectorAtom(
        ILogger<SessionVectorAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        SessionStore sessionStore,
        ISessionVectorSearch? vectorSearch = null,
        SessionEscalationService? escalationService = null)
        : base(name: "SessionVector", category: "SessionVector")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _sessionStore = sessionStore;
        _vectorSearch = vectorSearch;
        _escalationService = escalationService;
    }

    public override int Priority => 30;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.PrimarySignature };

    private double P(string name, double fallback) => _configProvider.GetParameter(Name, name, fallback);
    private int P(string name, int fallback) => _configProvider.GetParameter(Name, name, fallback);
    private float PF(string name, double fallback) => (float)_configProvider.GetParameter(Name, name, fallback);
    private double WeightBotSignal => _configProvider.GetDefaults(Name).Weights.BotSignal;
    private double WeightHumanSignal => _configProvider.GetDefaults(Name).Weights.HumanSignal;

    private int MinSessionRequests => P("min_session_requests", 5);
    private float MinMaturityForScoring => PF("min_maturity_for_scoring", 0.3);
    private float VelocityAnomalyThreshold => PF("velocity_anomaly_threshold", 0.6);
    private float GapNormalizedVelocityThreshold => PF("gap_normalized_velocity_threshold", 0.4);
    private float FingerprintRotationThreshold => PF("fingerprint_rotation_threshold", 0.15);
    private float AccelerationThreshold => PF("acceleration_threshold", 0.25);
    private float DissimilarityBotThreshold => PF("dissimilarity_bot_threshold", 0.3);
    private double HighVelocityConfidence => P("high_velocity_confidence", 0.5);
    private double GapNormalizedVelocityConfidence => P("gap_normalized_velocity_confidence", 0.45);
    private double FingerprintRotationConfidence => P("fingerprint_rotation_confidence", 0.55);
    private double AccelerationConfidence => P("acceleration_confidence", 0.35);
    private double DissimilarSessionConfidence => P("dissimilar_session_confidence", 0.4);
    private double ConsistentSessionHumanConfidence => P("consistent_session_human_confidence", -0.2);
    private double StableVelocityHumanConfidence => P("stable_velocity_human_confidence", -0.15);
    private int PartialChainMinRequests => P("partial_chain_min_requests", 3);
    private float PartialChainSimilarityThreshold => PF("partial_chain_similarity_threshold", 0.6);
    private float PartialChainMaxConfidence => PF("partial_chain_max_confidence", 0.35);
    private float PeriodicityBotThreshold => PF("periodicity_bot_threshold", 0.5);
    private double PeriodicityBotConfidence => P("periodicity_bot_confidence", 0.35);
    private float FrequencyRhythmSimilarityThreshold => PF("frequency_rhythm_similarity_threshold", 0.85);
    private float TrajectoryAttackClusterThreshold => PF("trajectory_attack_cluster_threshold", 0.72);
    private double TrajectoryAttackClusterConfidence => P("trajectory_attack_cluster_confidence", 0.45);
    private float TrajectoryProjectionSteps => PF("trajectory_projection_steps", 1.5);
    private float VoidDetectionMinSimilarity => PF("void_detection_min_similarity", 0.40);
    private int VoidDetectionTopK => P("void_detection_top_k", 5);
    private int VoidMinIndexSize => P("void_min_index_size", 50);
    private double VoidBotConfidence => P("void_bot_confidence", 0.30);
    private float VoidNearVoidThreshold => PF("void_near_void_threshold", 0.50);
    private double VoidNearVoidMaxConfidence => P("void_near_void_max_confidence", 0.15);
    private float VoidMahalanobisThreshold => PF("void_mahalanobis_threshold", 3.0);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        if (!ShouldRunUnderSequenceGuard(sink)) return None();

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var contributions = new List<DetectionContribution>();

        try
        {
            var signature = sink.ReadHint(SignalKeys.PrimarySignature);
            if (string.IsNullOrEmpty(signature))
                return Single(Neutral("No waveform signature available"));

            var requestState = RequestMarkovClassifier.Classify(context, sink);
            var statusCode = context.Response.StatusCode;
            var path = TemplatizePath(context.Request.Path.Value ?? "/");
            var fromUpstream = sink.ReadBoolHint(SignalKeys.ResponseFromUpstream, fallback: true);
            var enforcementMode = sink.ReadHint(SignalKeys.EnforcementMode);
            var policyRevision = sink.ReadHint(SignalKeys.PolicyRevision);
            var shed = sink.ReadHint(SignalKeys.Shed) is not null
                ? sink.ReadBoolHint(SignalKeys.Shed)
                : context.Items.ContainsKey(BotDetectionMiddleware.BotDetectionShedKey);

            var sessionRequest = new SessionRequest(
                requestState, DateTimeOffset.UtcNow, path,
                statusCode > 0 ? statusCode : 200,
                FromUpstream: fromUpstream, Shed: shed,
                EnforcementMode: enforcementMode,
                PolicyRevision: policyRevision,
                IsDocumentNavigation: ContentSequenceAtom.IsDocumentRequest(context.Request, sink));

            var fpContext = BuildFingerprintContext(sink);

            // Learning-suppressed requests (bypass key with DisableLearningWrites, or
            // impersonation) score against existing session history but must NOT record
            // this request's Markov transition into the store. RecordRequestAsync +
            // SetHeaderHashes are learning writes -- skip them; the reads below
            // (GetCurrentSession / GetHistory) still drive scoring off prior knowledge.
            var learningSuppressed = sink.Detect(SignalKeys.LearningSuppressed);

            var completedSession = learningSuppressed
                ? null
                : await _sessionStore.RecordRequestAsync(signature, sessionRequest, fpContext).ConfigureAwait(false);
            var currentSession = _sessionStore.GetCurrentSession(signature);

            if (!learningSuppressed && currentSession is { Count: 1 })
            {
                var headerHashesJson = sink.ReadHint(SignalKeys.HeaderHashes);
                if (!string.IsNullOrEmpty(headerHashesJson))
                    _sessionStore.SetHeaderHashes(signature, headerHashesJson);
            }

            var sessionHistory = _sessionStore.GetHistory(signature);

            sink.Raise($"{SignalKeys.SessionRequestCount}:{currentSession?.Count ?? 1}", sessionId);
            sink.Raise($"{SignalKeys.SessionHistoryCount}:{sessionHistory.Count}", sessionId);
            sink.Raise($"{SignalKeys.SessionCurrentState}:{requestState}", sessionId);

            if (completedSession is not null)
            {
                sink.Raise($"{SignalKeys.SessionBoundaryDetected}:true", sessionId);
                sink.Raise($"{SignalKeys.SessionCompletedMaturity}:{completedSession.Maturity.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                sink.Raise($"{SignalKeys.SessionCompletedRequestCount}:{completedSession.RequestCount}", sessionId);
                sink.Raise($"{SignalKeys.SessionDominantState}:{completedSession.DominantState}", sessionId);
                _logger.LogDebug("Session boundary detected for {Signature}: {Count} requests, maturity={Maturity:F2}",
                    signature, completedSession.RequestCount, completedSession.Maturity);
            }

            if (sink.ReadBoolHint(SignalKeys.GatewayWarmup))
            {
                if (contributions.Count == 0)
                    contributions.Add(Neutral($"Gateway warmup active; session tracking accumulating ({currentSession?.Count ?? 0} requests, {sessionHistory.Count} prior sessions)"));
                return contributions;
            }

            if (currentSession is not null
                && currentSession.Count >= PartialChainMinRequests
                && currentSession.Count < MinSessionRequests)
                AnalyzePartialChain(sink, sessionId, currentSession, fpContext, contributions);

            if (currentSession is not null && currentSession.Count >= MinSessionRequests)
            {
                var currentVector = SessionVectorizer.Encode(currentSession, fpContext);
                var currentMaturity = SessionVectorizer.ComputeMaturity(currentSession);
                sink.Raise($"{SignalKeys.SessionVectorMaturity}:{currentMaturity.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                if (currentMaturity >= MinMaturityForScoring)
                    AnalyzeCurrentSession(sink, sessionId, signature, currentVector, contributions);
            }

            if (sessionHistory.Count >= 2)
                AnalyzeInterSessionVelocity(context, sink, sessionId, sessionHistory, contributions);

            if (currentSession is not null && currentSession.Count >= 5)
            {
                AnalyzeFrequencyRhythm(context, sink, sessionId, currentSession, contributions);
                if (sessionHistory.Count >= 2)
                    AnalyzeCrossSessionRhythm(currentSession, sessionHistory, contributions);
            }

            if (_vectorSearch is not null && currentSession is not null && currentSession.Count >= MinSessionRequests)
            {
                var currentVector = SessionVectorizer.Encode(currentSession, BuildFingerprintContext(sink));
                await AnalyzeVoidnessAsync(sink, sessionId, signature, currentVector, contributions, ct).ConfigureAwait(false);
                if (sessionHistory.Count >= 3)
                    await AnalyzeTrajectoryAsync(context, sink, sessionId, signature, currentVector, sessionHistory, contributions, ct).ConfigureAwait(false);
            }

            if (contributions.Count == 0)
                contributions.Add(Neutral($"Session tracking active ({currentSession?.Count ?? 0} requests, {sessionHistory.Count} prior sessions)"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in session vector analysis");
            contributions.Add(Neutral("Session vector analysis error"));
        }

        return contributions;
    }

    private static bool ShouldRunUnderSequenceGuard(SignalSink sink)
    {
        var positionHint = sink.ReadHint(SignalKeys.SequencePosition);
        if (positionHint is null) return true;
        if (!sink.ReadBoolHint(SignalKeys.SequenceOnTrack, fallback: true)) return true;
        if (sink.ReadBoolHint(SignalKeys.SequenceDiverged)) return true;
        return int.TryParse(positionHint, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos)
               && pos >= SequenceMinPosition;
    }

private void AnalyzeCurrentSession(SignalSink sink, string sessionId, string signature, float[] currentVector, List<DetectionContribution> contributions)
    {
        var history = _sessionStore.GetHistory(signature);
        if (history.Count == 0) return;

        var similarities = history
            .Where(h => h.Maturity >= MinMaturityForScoring)
            .Select(h => SessionVectorizer.CosineSimilarity(currentVector, h.Vector))
            .ToList();
        if (similarities.Count == 0) return;

        var avgSimilarity = similarities.Average();
        sink.Raise($"{SignalKeys.SessionSelfSimilarity}:{avgSimilarity.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        if (avgSimilarity < DissimilarityBotThreshold)
            contributions.Add(Bot(DissimilarSessionConfidence,
                $"Current session behavior diverges from client history (similarity={avgSimilarity:F2})",
                BotType.Scraper));
        else if (avgSimilarity > 0.7f)
            contributions.Add(Human(Math.Abs(ConsistentSessionHumanConfidence),
                $"Session behavior consistent with client history (similarity={avgSimilarity:F2})"));
    }

    private void AnalyzeInterSessionVelocity(HttpContext context, SignalSink sink, string sessionId,
        IReadOnlyList<SessionSnapshot> history, List<DetectionContribution> contributions)
    {
        var recent = history
            .Where(h => h.Maturity >= MinMaturityForScoring)
            .OrderByDescending(h => h.EndedAt)
            .Take(3)
            .ToList();
        if (recent.Count < 2) return;

        var v1 = SessionVectorizer.ComputeVelocity(recent[0].Vector, recent[1].Vector);
        var magnitude = SessionVectorizer.VelocityMagnitude(v1);
        var decomp = SessionVectorizer.DecomposeVelocity(v1);
        var gap = recent[0].EndedAt - recent[1].EndedAt;
        var gapNormMag = SessionVectorizer.GapNormalizedMagnitude(v1, gap);

        // Rich velocity vector -> HttpContext.Items (atom-holds-rich-state).
        context.Items[VelocityVectorKey] = v1;

        sink.Raise($"{SignalKeys.SessionVelocityMagnitude}:{magnitude.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.SessionVelocityGapNormalized}:{gapNormMag.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.SessionVelocityMarkovMagnitude}:{decomp.MarkovMagnitude.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.SessionVelocityTemporalMagnitude}:{decomp.TemporalMagnitude.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.SessionVelocityFingerprintMagnitude}:{decomp.FingerprintMagnitude.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.SessionVelocityIsFingerprintRotation}:{(decomp.IsFingerprintDominant ? "true" : "false")}", sessionId);

        if (recent.Count == 3)
        {
            var v2 = SessionVectorizer.ComputeVelocity(recent[1].Vector, recent[2].Vector);
            var acceleration = SessionVectorizer.ComputeAcceleration(v1, v2);
            var accMag = SessionVectorizer.VelocityMagnitude(acceleration);
            sink.Raise($"{SignalKeys.SessionVelocityAcceleration}:{accMag.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

            if (magnitude > 0.15f && accMag < AccelerationThreshold * magnitude)
                contributions.Add(Bot(AccelerationConfidence,
                    $"Constant rotation rate across 3 sessions (velocity={magnitude:F2}, accel={accMag:F2})",
                    BotType.Scraper));
        }

        if (decomp.IsFingerprintDominant && decomp.FingerprintMagnitude > FingerprintRotationThreshold)
            contributions.Add(Bot(FingerprintRotationConfidence,
                $"Fingerprint rotation trail: behavior stable but transport fingerprint shifted (fp_delta={decomp.FingerprintMagnitude:F2})",
                BotType.Scraper));

        if (gapNormMag > GapNormalizedVelocityThreshold)
            contributions.Add(Bot(GapNormalizedVelocityConfidence,
                $"Fast behavioral rotation (gap-normalized velocity={gapNormMag:F2}, gap={gap.TotalMinutes:F0}m)",
                BotType.Scraper));
        else if (magnitude > VelocityAnomalyThreshold)
            contributions.Add(Bot(HighVelocityConfidence,
                $"Sudden behavioral shift between sessions (velocity={magnitude:F2})",
                BotType.Scraper));

        if (magnitude < 0.15f && history.Count >= 3)
            contributions.Add(Human(Math.Abs(StableVelocityHumanConfidence),
                $"Stable behavior across {history.Count} sessions (velocity={magnitude:F2})"));
    }

    private void AnalyzeFrequencyRhythm(HttpContext context, SignalSink sink, string sessionId,
        IReadOnlyList<SessionRequest> currentSession, List<DetectionContribution> contributions)
    {
        var fingerprint = FrequencyFingerprintEncoder.Encode(currentSession);
        var periodicityScore = FrequencyFingerprintEncoder.PeriodicityScore(fingerprint);
        var dominantLag = FrequencyFingerprintEncoder.DominantLagIndex(fingerprint);

        // Rich frequency-fingerprint array -> HttpContext.Items.
        context.Items[FrequencyFingerprintKey] = fingerprint;
        sink.Raise($"{SignalKeys.SessionFrequencyPeriodicityScore}:{periodicityScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.SessionFrequencyDominantLag}:{dominantLag}", sessionId);

        if (periodicityScore < PeriodicityBotThreshold) return;

        var lagDesc = dominantLag >= 0 ? $"{FrequencyFingerprintEncoder.LagSeconds[dominantLag]}s" : "unknown";
        contributions.Add(Bot(PeriodicityBotConfidence,
            $"Periodic request rhythm detected (score={periodicityScore:F2}, dominant_lag={lagDesc})",
            BotType.Scraper));
    }

    private void AnalyzeCrossSessionRhythm(
        IReadOnlyList<SessionRequest> currentSession, IReadOnlyList<SessionSnapshot> sessionHistory,
        List<DetectionContribution> contributions)
    {
        var currentFingerprint = FrequencyFingerprintEncoder.Encode(currentSession);
        var currentPeriodicity = FrequencyFingerprintEncoder.PeriodicityScore(currentFingerprint);
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

        contributions.Add(Bot(PeriodicityBotConfidence + 0.1,
            $"Rhythm-preserving rotation detected: periodicity preserved across sessions (similarity={maxSimilarity:F2})",
            BotType.Scraper));
    }

    private async Task AnalyzeVoidnessAsync(SignalSink sink, string sessionId, string signature,
        float[] currentVector, List<DetectionContribution> contributions, CancellationToken ct)
    {
        if (_vectorSearch is null) return;
        try
        {
            var similar = await _vectorSearch.FindSimilarAsync(
                currentVector, topK: VoidDetectionTopK, minSimilarity: VoidDetectionMinSimilarity).ConfigureAwait(false);
            var topSimilarity = similar.Count > 0 ? similar[0].Similarity : 0f;
            var isVoid = similar.Count == 0;

            var mahalanobisNearest = float.MaxValue;
            try
            {
                var mahalMatches = await _vectorSearch.FindSimilarMahalanobisAsync(
                    currentVector, topK: 1, maxDistance: 5.0f).ConfigureAwait(false);
                if (mahalMatches.Count > 0)
                    mahalanobisNearest = 1f - mahalMatches[0].Similarity;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mahalanobis search failed (non-fatal)");
            }

            sink.Raise($"{SignalKeys.SessionIsVoid}:{(isVoid ? "true" : "false")}", sessionId);
            sink.Raise($"{SignalKeys.SessionTopSimilarity}:{topSimilarity.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
            sink.Raise($"{SignalKeys.SessionMahalanobisNearestDistance}:{mahalanobisNearest.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

            if (isVoid)
            {
                _logger.LogDebug("Void detection: session in empty shape-space for {Signature}", signature);
                _escalationService?.FlagForEscalation(signature);
            }

            var indexSize = _vectorSearch.Count;
            if (indexSize >= VoidMinIndexSize)
            {
                if (isVoid && mahalanobisNearest >= VoidMahalanobisThreshold)
                    contributions.Add(Bot(VoidBotConfidence,
                        "Session has no behavioral neighbors in any known shape-space (double-void)",
                        BotType.Scraper));
                else if (topSimilarity < VoidNearVoidThreshold)
                {
                    var scale = (VoidNearVoidThreshold - topSimilarity) / VoidNearVoidThreshold;
                    var confidence = VoidNearVoidMaxConfidence * scale;
                    contributions.Add(Bot(confidence,
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

    private async Task AnalyzeTrajectoryAsync(HttpContext context, SignalSink sink, string sessionId, string signature,
        float[] currentVector, IReadOnlyList<SessionSnapshot> sessionHistory,
        List<DetectionContribution> contributions, CancellationToken ct)
    {
        if (_vectorSearch is null) return;
        try
        {
            var recent = sessionHistory
                .Where(h => h.Maturity >= MinMaturityForScoring)
                .OrderBy(h => h.EndedAt)
                .TakeLast(8)
                .ToList();
            if (recent.Count < 3) return;

            var driftVector = SessionVectorizer.ComputeDriftVector(recent);
            var driftMagnitude = SessionVectorizer.VelocityMagnitude(driftVector);
            context.Items[DriftVectorKey] = driftVector;

            if (driftMagnitude < 0.05f) return;

            var predicted = SessionVectorizer.ProjectForward(currentVector, driftVector, TrajectoryProjectionSteps);
            var nearPredicted = await _vectorSearch.FindSimilarAsync(
                predicted, topK: 3, minSimilarity: TrajectoryAttackClusterThreshold).ConfigureAwait(false);
            if (nearPredicted.Count == 0) return;

            var top = nearPredicted[0];
            var allVectors = _vectorSearch.GetAllVectorsSnapshot();
            var topMeta = allVectors.FirstOrDefault(v => v.Metadata.Signature == top.Signature).Metadata;
            if (topMeta is null || !topMeta.IsBot) return;

            var similarity = top.Similarity;
            sink.Raise($"{SignalKeys.SessionTrajectoryClusterSimilarity}:{similarity.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
            sink.Raise($"{SignalKeys.SessionTrajectoryInAttackCluster}:true", sessionId);

            contributions.Add(Bot(TrajectoryAttackClusterConfidence,
                $"Behavioral trajectory points toward known attack cluster (predicted_similarity={similarity:F2}, drift_magnitude={driftMagnitude:F2})",
                BotType.Scraper));
            _logger.LogDebug("Trajectory alert: {Signature} drifting toward bot cluster (predicted_sim={Sim:F2})",
                signature, similarity);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Trajectory analysis failed");
        }
    }

    private void AnalyzePartialChain(SignalSink sink, string sessionId,
        IReadOnlyList<SessionRequest> currentSession, FingerprintContext fpContext,
        List<DetectionContribution> contributions)
    {
        var fullVector = SessionVectorizer.Encode(currentSession, fpContext);
        var stateCount = Enum.GetValues<RequestState>().Length;
        var transitionDims = stateCount * stateCount;
        var partialVector = new float[transitionDims];
        Array.Copy(fullVector, partialVector, Math.Min(fullVector.Length, transitionDims));

        float norm = 0;
        for (var i = 0; i < partialVector.Length; i++) norm += partialVector[i] * partialVector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (var i = 0; i < partialVector.Length; i++) partialVector[i] /= norm;
        else return;

        var bestSimilarity = 0f;
        MarkovArchetype? bestMatch = null;
        foreach (var archetype in MarkovArchetypes.All)
        {
            var similarity = CosineSimilarity100(partialVector, archetype.PartialVector);
            if (similarity > bestSimilarity) { bestSimilarity = similarity; bestMatch = archetype; }
        }

        if (bestMatch is null || bestSimilarity < PartialChainSimilarityThreshold) return;

        var scaledConfidence = Math.Min(PartialChainMaxConfidence,
            Math.Abs(bestMatch.DefaultConfidence) * bestSimilarity);

        sink.Raise($"{SignalKeys.SessionPartialChainMatch}:{bestMatch.Name}", sessionId);
        sink.Raise($"{SignalKeys.SessionPartialChainSimilarity}:{bestSimilarity.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.SessionPartialChainConfidence}:{scaledConfidence.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        if (bestMatch.IsHuman)
            contributions.Add(Human(scaledConfidence,
                $"Partial chain ({currentSession.Count} requests) matches '{bestMatch.Name}' archetype (similarity={bestSimilarity:F2})"));
        else
            contributions.Add(Bot(scaledConfidence,
                $"Partial chain ({currentSession.Count} requests) matches '{bestMatch.Name}' archetype (similarity={bestSimilarity:F2})",
                bestMatch.DefaultBotType ?? BotType.Scraper));

        _logger.LogDebug("Partial chain match: {Archetype} (similarity={Similarity:F2}, confidence={Confidence:F2}, requests={Count})",
            bestMatch.Name, bestSimilarity, scaledConfidence, currentSession.Count);
    }

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

    private static FingerprintContext BuildFingerprintContext(SignalSink sink)
    {
        var tlsProtocol = sink.ReadHint(SignalKeys.TlsProtocol) ?? string.Empty;
        var tlsVersion = tlsProtocol switch
        {
            _ when tlsProtocol.Contains("1.3") => 1.0f,
            _ when tlsProtocol.Contains("1.2") => 0.5f,
            _ when tlsProtocol.Length > 0 => 0.2f,
            _ => 0f
        };

        var h2 = sink.ReadHint(SignalKeys.H2Protocol);
        var h3 = sink.ReadHint(SignalKeys.H3Protocol);
        var httpProtocol = h3 is not null ? 1.0f : h2 is not null ? 0.5f : 0f;

        var clientType = sink.ReadHint(SignalKeys.H2ClientType) ?? sink.ReadHint(SignalKeys.H3ClientType) ?? string.Empty;
        var protocolClientType = clientType.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
                                 || clientType.Contains("Firefox", StringComparison.OrdinalIgnoreCase)
                                 || clientType.Contains("Safari", StringComparison.OrdinalIgnoreCase) ? 1.0f
            : clientType.Contains("Bot", StringComparison.OrdinalIgnoreCase) ? 0.2f
            : clientType.Length > 0 ? 0.5f : 0f;

        var tcpOs = sink.ReadHint(SignalKeys.TcpOsHint) ?? sink.ReadHint(SignalKeys.TcpOsHintTtl) ?? string.Empty;
        var uaOs = sink.ReadHint(SignalKeys.UserAgentOs) ?? string.Empty;
        float tcpOsConsistency;
        if (tcpOs.Length == 0 || uaOs.Length == 0) tcpOsConsistency = 0.5f;
        else tcpOsConsistency = tcpOs.Contains(uaOs, StringComparison.OrdinalIgnoreCase)
                                || uaOs.Contains(tcpOs, StringComparison.OrdinalIgnoreCase) ? 1.0f : 0f;

        var zeroRtt = sink.ReadBoolHint(SignalKeys.H3ZeroRtt);
        var fpIntegrity = sink.ReadDoubleHint(SignalKeys.FingerprintIntegrityScore);
        var headless = sink.ReadDoubleHint(SignalKeys.FingerprintHeadlessScore);
        var isDatacenter = sink.ReadBoolHint(SignalKeys.IpIsDatacenter);

        return new FingerprintContext
        {
            TlsVersion = tlsVersion,
            HttpProtocol = httpProtocol,
            ProtocolClientType = protocolClientType,
            TcpOsConsistency = tcpOsConsistency,
            QuicZeroRtt = zeroRtt ? 1f : 0f,
            ClientFingerprintIntegrity = (float)Math.Min(1.0, fpIntegrity),
            HeadlessScore = (float)Math.Min(1.0, headless),
            IsDatacenter = isDatacenter ? 1f : 0f
        };
    }

    private static string TemplatizePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (long.TryParse(segments[i], out _)) segments[i] = "{id}";
            else if (Guid.TryParse(segments[i], out _)) segments[i] = "{guid}";
            else if (segments[i].Length > 20
                     && segments[i].All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '='))
                segments[i] = "{token}";
        }
        return "/" + string.Join("/", segments);
    }

    private DetectionContribution Neutral(string reason) => DetectionContribution.Info(Name, Category, reason);

    private DetectionContribution Bot(double confidence, string reason, BotType botType) => new()
    {
        DetectorName = Name, Category = Category,
        ConfidenceDelta = confidence, Weight = WeightBotSignal,
        Reason = reason, BotType = botType.ToString()
    };

    private DetectionContribution Human(double confidence, string reason) => new()
    {
        DetectorName = Name, Category = Category,
        ConfidenceDelta = -confidence, Weight = WeightHumanSignal, Reason = reason
    };
}
