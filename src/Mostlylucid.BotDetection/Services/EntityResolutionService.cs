using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service for entity resolution maintenance:
///     - Detects oscillation (split signal) in entity velocity history
///     - Detects post-merge divergence (rewind signal)
///     - Updates entity confidence levels (L0→L5 progression)
///     - Computes velocity variance (low variance = systematic rotation)
///
///     Runs every 60 seconds. Non-blocking, fire-and-forget.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(adaptiveInterval)</c> loop; now subscribes to
///         <see cref="TickCadence.Tick1m"/> and short-circuits when the
///         <see cref="PipelineLoadSensor"/> says we should back off (we skip
///         the tick if <c>now - lastRun</c> is shorter than the load-scaled
///         interval). The 30-second warm-up from the pre-Wave-2 shape is
///         dropped -- the first Tick1m fires roughly one minute after boot,
///         which exceeds the old warm-up window. See
///         <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
public sealed class EntityResolutionService : IDisposable
{
    private readonly ISessionStore _store;
    private readonly ILogger<EntityResolutionService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>Minimum sessions for split detection.</summary>
    private const int MinSessionsForSplit = 6;

    /// <summary>Autocorrelation lag-2 threshold for oscillation detection.</summary>
    private const double OscillationThreshold = 0.5;

    /// <summary>Velocity variance below this = systematic rotation.</summary>
    private const double RotationVarianceThreshold = 0.05;

    private readonly PipelineLoadSensor? _loadSensor;
    private readonly IDisposable _subscription;
    private DateTime _lastRunUtc = DateTime.MinValue;
    private int _disposed;

    public EntityResolutionService(
        ISessionStore store,
        ILogger<EntityResolutionService> logger,
        IScheduleCoordinator coordinator,
        PipelineLoadSensor? loadSensor = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _store = store;
        _logger = logger;
        _loadSensor = loadSensor;

        _subscription = coordinator.Subscribe(
            TickCadence.Tick1m,
            "EntityResolutionService",
            CostHint.Medium,
            OnTickAsync);
    }

    /// <summary>
    ///     The coordinator's tick handler. Fires every Tick1m; honours the
    ///     <see cref="PipelineLoadSensor"/> by skipping ticks when the
    ///     load-scaled interval hasn't yet elapsed since the last successful
    ///     analysis pass. Public so tests can drive a single beat
    ///     deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        var adaptiveInterval = _loadSensor?.GetAdaptiveInterval(Interval) ?? Interval;
        if (_lastRunUtc != DateTime.MinValue &&
            now.UtcDateTime - _lastRunUtc < adaptiveInterval)
        {
            return; // Load sensor says back off.
        }

        try
        {
            await AnalyzeEntitiesAsync(ct);
            _lastRunUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Entity resolution analysis failed");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
    }

    private async Task AnalyzeEntitiesAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var entityIds = await _store.GetActiveEntityIdsAsync(cutoff, 100, ct);

        // Per-entity analysis: velocity, oscillation, rotation, confidence
        foreach (var entityId in entityIds)
        {
            try
            {
                await AnalyzeEntityAsync(entityId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to analyze entity {EntityId}", entityId);
            }
        }

        // Cross-entity convergence detection: find entities with parallel behavioral vectors
        // This is the micro-level equivalent of Leiden clustering
        if (entityIds.Count >= 2)
            await DetectConvergenceAsync(entityIds, ct);
    }

    private async Task AnalyzeEntityAsync(string entityId, CancellationToken ct)
    {
        // Get all signatures linked to this entity
        var edges = await _store.GetEntityEdgesAsync(entityId, ct);
        var activeSignatures = edges.Where(e => e.IsActive).Select(e => e.Signature).Distinct().ToList();

        if (activeSignatures.Count == 0) return;

        // Collect all sessions across all signatures for this entity
        var allSessions = new List<PersistedSession>();
        foreach (var sig in activeSignatures)
        {
            var sessions = await _store.GetSessionsAsync(sig, 20, ct);
            allSessions.AddRange(sessions);
        }

        allSessions = allSessions.OrderBy(s => s.EndedAt).ToList();
        if (allSessions.Count < 3) return;

        // Compute inter-session velocities
        var velocities = ComputeVelocityHistory(allSessions);
        if (velocities.Count < MinSessionsForSplit) return;

        // Compute velocity statistics
        var meanVelocity = velocities.Average();
        var varianceVelocity = velocities.Select(v => (v - meanVelocity) * (v - meanVelocity)).Average();

        // Update entity with velocity stats
        var entity = await _store.GetEntityAsync(entityId, ct);
        if (entity == null) return;

        var factorCount = ComputeFactorCount(allSessions);
        var confidenceLevel = ComputeConfidenceLevel(allSessions.Count, factorCount, activeSignatures.Count);

        await _store.UpdateEntityAsync(entity with
        {
            VelocityVariance = varianceVelocity,
            FactorCount = factorCount,
            ConfidenceLevel = confidenceLevel,
            RotationCadenceSeconds = meanVelocity > 0.1 && varianceVelocity < RotationVarianceThreshold
                ? EstimateRotationCadence(allSessions)
                : null
        }, ct);

        // Check for oscillation → split signal
        if (velocities.Count >= MinSessionsForSplit)
        {
            var lag2Autocorrelation = ComputeAutocorrelation(velocities, lag: 2);
            if (lag2Autocorrelation > OscillationThreshold)
            {
                _logger.LogWarning(
                    "Oscillation detected in entity {EntityId}: lag-2 autocorrelation={AC:F3} " +
                    "(threshold={Threshold}). Two actors may share this identity.",
                    entityId, lag2Autocorrelation, OscillationThreshold);
                // TODO Phase 3: automatic split via k-means on session vectors
            }
        }

        // Check for rotation detection (low velocity variance + moderate mean)
        if (meanVelocity > 0.1 && varianceVelocity < RotationVarianceThreshold)
        {
            _logger.LogInformation(
                "Systematic rotation detected in entity {EntityId}: mean_velocity={Mean:F3}, " +
                "variance={Var:F4}, cadence≈{Cadence:F0}s",
                entityId, meanVelocity, varianceVelocity,
                EstimateRotationCadence(allSessions));
        }
    }

    private static List<double> ComputeVelocityHistory(List<PersistedSession> sessions)
    {
        var velocities = new List<double>();
        for (var i = 1; i < sessions.Count; i++)
        {
            var prev = sessions[i - 1].Vector is { Length: > 0 }
                ? SqliteSessionStore.DeserializeVector(sessions[i - 1].Vector) : null;
            var curr = sessions[i].Vector is { Length: > 0 }
                ? SqliteSessionStore.DeserializeVector(sessions[i].Vector) : null;

            if (prev != null && curr != null)
            {
                var velocity = SessionVectorizer.ComputeVelocity(curr, prev);
                velocities.Add(SessionVectorizer.VelocityMagnitude(velocity));
            }
        }
        return velocities;
    }

    /// <summary>
    ///     Compute autocorrelation at a given lag.
    ///     High autocorrelation at lag 2 = period-2 oscillation (two actors alternating).
    /// </summary>
    private static double ComputeAutocorrelation(List<double> values, int lag)
    {
        if (values.Count <= lag) return 0;

        var mean = values.Average();
        var n = values.Count - lag;

        var numerator = 0.0;
        var denominator = 0.0;

        for (var i = 0; i < values.Count; i++)
        {
            denominator += (values[i] - mean) * (values[i] - mean);
            if (i < n)
                numerator += (values[i] - mean) * (values[i + lag] - mean);
        }

        return denominator > 0 ? numerator / denominator : 0;
    }

    private static double EstimateRotationCadence(List<PersistedSession> sessions)
    {
        if (sessions.Count < 2) return 0;
        var gaps = new List<double>();
        for (var i = 1; i < sessions.Count; i++)
            gaps.Add((sessions[i].StartedAt - sessions[i - 1].EndedAt).TotalSeconds);
        return gaps.Count > 0 ? gaps.Average() : 0;
    }

    private static int ComputeFactorCount(List<PersistedSession> sessions)
    {
        // Count non-zero fingerprint dimensions across sessions
        var factors = 2; // IP + UA minimum (PrimarySignature)
        var latest = sessions.LastOrDefault();
        if (latest?.Vector is not { Length: > 0 }) return factors;

        var vector = SqliteSessionStore.DeserializeVector(latest.Vector);
        if (vector == null) return factors;

        // Fingerprint dims [118-125]: each non-zero = a resolved factor
        var fpOffset = 118;
        if (vector.Length > fpOffset + 7)
        {
            for (var i = 0; i < 8; i++)
                if (Math.Abs(vector[fpOffset + i]) > 0.01f) factors++;
        }

        return factors;
    }

    /// <summary>L0-L5 confidence level based on session count, factor count, and signature count.</summary>
    private static int ComputeConfidenceLevel(int sessionCount, int factorCount, int signatureCount)
    {
        if (sessionCount >= 5 && factorCount >= 8 && signatureCount >= 1) return 5; // Persistent Actor
        if (sessionCount >= 3 && factorCount >= 6) return 4; // Behavioral Identity
        if (factorCount >= 5) return 3; // Runtime Identity
        if (factorCount >= 3) return 2; // Transport Identity
        if (factorCount >= 2) return 1; // Browser Guess
        return 0; // Infrastructure
    }

    /// <summary>
    ///     Detect convergence: two entities with no fingerprint overlap but high
    ///     behavioral cosine similarity. Could be same actor on different devices,
    ///     or coordinated bots with identical behavior.
    ///     Creates Converge edges (flagged, not auto-merged - operator decides).
    /// </summary>
    private const float ConvergenceThreshold = 0.92f;

    private async Task DetectConvergenceAsync(List<string> entityIds, CancellationToken ct)
    {
        // Collect latest session vector per entity
        var entityVectors = new Dictionary<string, float[]>();
        foreach (var entityId in entityIds)
        {
            var edges = await _store.GetEntityEdgesAsync(entityId, ct);
            var sig = edges.FirstOrDefault(e => e.IsActive)?.Signature;
            if (sig == null) continue;

            var sessions = await _store.GetSessionsAsync(sig, 1, ct);
            if (sessions.Count == 0 || sessions[0].Vector is not { Length: > 0 }) continue;

            var vector = SqliteSessionStore.DeserializeVector(sessions[0].Vector);
            if (vector != null) entityVectors[entityId] = vector;
        }

        // Pairwise comparison - O(n²) but limited to 100 entities max
        var comparedPairs = new HashSet<string>();
        foreach (var (idA, vecA) in entityVectors)
        {
            foreach (var (idB, vecB) in entityVectors)
            {
                if (idA == idB) continue;
                var pairKey = string.Compare(idA, idB, StringComparison.Ordinal) < 0
                    ? $"{idA}:{idB}" : $"{idB}:{idA}";
                if (!comparedPairs.Add(pairKey)) continue;

                var similarity = SessionVectorizer.CosineSimilarity(vecA, vecB);
                if (similarity >= ConvergenceThreshold)
                {
                    _logger.LogInformation(
                        "Convergence detected: entities {A} and {B} have behavioral similarity={Sim:F3}. " +
                        "Possible same actor across devices or coordinated bots.",
                        idA, idB, similarity);

                    // Don't auto-merge - create Converge edge for operator review
                    // Check if this convergence was already flagged
                    var existingEdges = await _store.GetEntityEdgesAsync(idA, ct);
                    var alreadyFlagged = existingEdges.Any(e =>
                        e.EdgeType == EntityEdgeType.Converge && e.Reason?.Contains(idB) == true);

                    if (!alreadyFlagged)
                    {
                        await _store.MergeSignatureAsync(idA, $"converge:{idB}",
                            similarity,
                            $"Behavioral convergence: cosine={similarity:F3}, entity_b={idB}",
                            ct);
                    }
                }
            }
        }
    }
}
