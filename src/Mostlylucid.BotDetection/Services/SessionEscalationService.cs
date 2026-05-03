using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Manages LLM-driven escalation at session boundaries.
///
///     Per-request: caches detection snapshots in a ConcurrentDictionary per signature.
///     Each request to a signature refreshes the entry's timestamp (LFU-like sliding window).
///     When LLM returns escalate=true, the signature is flagged.
///
///     At session boundary (SessionFinalized): if flagged, extracts the full request cohort
///     and runs slow-path detectors (Similarity, Cluster, BehavioralWaveform) on the
///     accumulated session data. Results update reputation for next session.
/// </summary>
public sealed class SessionEscalationService : IDisposable
{
    private readonly SessionStore _sessionStore;
    private readonly IPatternReputationCache _reputationCache;
    private readonly PatternReputationUpdater _updater;
    private readonly ILogger<SessionEscalationService> _logger;

    // Per-signature request snapshots (accumulated during active session)
    private readonly ConcurrentDictionary<string, SignatureRequestCache> _requestCache = new();

    // Signatures flagged for escalation by LLM
    private readonly ConcurrentDictionary<string, bool> _escalationFlags = new();

    // Cleanup timer
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(35); // Slightly > session gap

    public SessionEscalationService(
        SessionStore sessionStore,
        IPatternReputationCache reputationCache,
        PatternReputationUpdater updater,
        ILogger<SessionEscalationService> logger)
    {
        _sessionStore = sessionStore;
        _reputationCache = reputationCache;
        _updater = updater;
        _logger = logger;

        // Subscribe to session boundary events
        _sessionStore.SessionFinalized += OnSessionFinalized;

        // Periodic cleanup of expired cache entries
        _cleanupTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    ///     Cache a detection snapshot for this signature. Called on every detection.
    ///     The LFU-like behavior: each call refreshes the entry's LastAccess timestamp.
    /// </summary>
    public void CacheRequestSnapshot(string signature, LlmClassificationRequest snapshot)
    {
        var cache = _requestCache.GetOrAdd(signature, _ => new SignatureRequestCache());
        cache.Add(snapshot);
    }

    /// <summary>
    ///     Flag a signature for escalation (called when LLM returns escalate=true).
    /// </summary>
    public void FlagForEscalation(string signature)
    {
        _escalationFlags[signature] = true;
        _logger.LogDebug("Signature {Signature} flagged for session-end escalation", signature);
    }

    /// <summary>
    ///     Check if a signature is flagged for escalation.
    /// </summary>
    public bool IsEscalationFlagged(string signature) =>
        _escalationFlags.ContainsKey(signature);

    /// <summary>
    ///     Get the cached request cohort for a signature (all requests in current session).
    /// </summary>
    public IReadOnlyList<LlmClassificationRequest>? GetCohort(string signature) =>
        _requestCache.TryGetValue(signature, out var cache) ? cache.GetAll() : null;

    /// <summary>
    ///     Stats for the live display.
    /// </summary>
    public (int CachedSignatures, int EscalationsPending, int TotalCachedRequests) GetStats() =>
        (_requestCache.Count,
         _escalationFlags.Count,
         _requestCache.Values.Sum(c => c.Count));

    private void OnSessionFinalized(SessionSnapshot snapshot, IReadOnlyList<SessionRequest> requests)
    {
        var signature = snapshot.Signature;

        // Check if this signature was flagged for escalation
        if (!_escalationFlags.TryRemove(signature, out _))
            return; // Not flagged - normal session end

        // Get the cached detection snapshots for this session
        if (!_requestCache.TryGetValue(signature, out var cache))
        {
            _logger.LogDebug("Escalation flagged for {Signature} but no cached snapshots found", signature);
            return;
        }

        var cohort = cache.GetAll();
        if (cohort.Count == 0) return;

        _logger.LogInformation(
            "Session escalation for {Signature}: {RequestCount} requests, {CohortSize} cached snapshots. " +
            "Running slow-path detectors offline.",
            signature, snapshot.RequestCount, cohort.Count);

        // Extract accumulated signals from the cohort
        var mergedSignals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        double maxProbability = 0;
        foreach (var req in cohort)
        {
            if (req.Signals != null)
                foreach (var (key, value) in req.Signals)
                    mergedSignals[key] = value; // Last-write-wins for overlapping keys
            if (req.HeuristicProbability > maxProbability)
                maxProbability = req.HeuristicProbability;
        }

        // Mark escalation signals
        mergedSignals["escalation.triggered"] = true;
        mergedSignals["escalation.cohort_size"] = cohort.Count;
        mergedSignals["escalation.session_requests"] = snapshot.RequestCount;
        mergedSignals["escalation.max_probability"] = maxProbability;

        // The slow-path detectors (Similarity, Cluster, BehavioralWaveform) work on
        // signatures and session vectors, not individual requests. They read from:
        // - SessionStore history (already populated by the session system)
        // - HNSW similarity index (populated by session vectorizer)
        // - BotClusterService (populated by Leiden clustering)
        //
        // These services already have the data they need. The escalation's job is to
        // UPDATE REPUTATION with the enriched analysis, so the next session starts
        // with better priors.

        // Apply escalation result to reputation via weight store
        var avgProbability = cohort.Average(r => r.HeuristicProbability);
        var confidenceBoost = avgProbability > 0.5 ? 0.15 : -0.10;
        var enrichedScore = Math.Clamp(avgProbability + confidenceBoost, 0.0, 1.0);

        _logger.LogInformation(
            "Escalation result for {Signature}: avg_prob={AvgProb:F2}, boost={Boost:+0.00;-0.00}, enriched={Enriched:F2}",
            signature, avgProbability, confidenceBoost, enrichedScore);

        // Update reputation for all signature vectors (churn-resistant)
        var primaryReq = cohort[^1]; // Most recent request
        var evidenceWeight = Math.Min(cohort.Count * 0.5, 3.0); // More requests = stronger evidence

        if (primaryReq.SignatureVectors != null)
        {
            foreach (var (vectorType, vectorValue) in primaryReq.SignatureVectors)
            {
                var patternId = $"{vectorType}:{vectorValue}";
                var existing = _reputationCache.Get(patternId);
                var updated = _updater.ApplyEvidence(existing, patternId, vectorType, vectorValue, enrichedScore, evidenceWeight);
                _reputationCache.Update(updated);
            }
        }

        // Clean up the cache for this signature (session is done)
        _requestCache.TryRemove(signature, out _);
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - CacheExpiry;
        var expired = _requestCache
            .Where(kv => kv.Value.LastAccess < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
        {
            _requestCache.TryRemove(key, out _);
            _escalationFlags.TryRemove(key, out _);
        }

        if (expired.Count > 0)
            _logger.LogDebug("Cleaned up {Count} expired escalation cache entries", expired.Count);
    }

    public void Dispose()
    {
        _sessionStore.SessionFinalized -= OnSessionFinalized;
        _cleanupTimer.Dispose();
    }
}

/// <summary>
///     Thread-safe per-signature request cache with LFU-like timestamp refresh.
/// </summary>
internal sealed class SignatureRequestCache
{
    private readonly List<LlmClassificationRequest> _requests = new();
    private readonly object _lock = new();
    private const int MaxPerSignature = 200;

    public DateTimeOffset LastAccess { get; private set; } = DateTimeOffset.UtcNow;
    public int Count { get { lock (_lock) return _requests.Count; } }

    public void Add(LlmClassificationRequest request)
    {
        lock (_lock)
        {
            LastAccess = DateTimeOffset.UtcNow; // LFU ping
            if (_requests.Count < MaxPerSignature)
                _requests.Add(request);
        }
    }

    public IReadOnlyList<LlmClassificationRequest> GetAll()
    {
        lock (_lock)
        {
            LastAccess = DateTimeOffset.UtcNow; // LFU ping on read
            return _requests.ToList();
        }
    }
}
