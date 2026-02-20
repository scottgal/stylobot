using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     Tracks per-signature Markov chains and cohort baselines.
///     Returns drift signals comparing individual behavior against population norms.
///     Thread-safe: each signature chain is locked independently.
/// </summary>
public sealed class MarkovTracker
{
    private readonly ILogger<MarkovTracker> _logger;
    private readonly MarkovOptions _options;

    // Per-signature chains (keyed by hashed signature)
    private readonly ConcurrentDictionary<string, SignatureChainState> _signatureChains = new();

    // Cohort baselines (keyed by cohort name: "datacenter-new", "residential-returning", or cluster ID)
    private readonly ConcurrentDictionary<string, DecayingTransitionMatrix> _cohortBaselines = new();

    // Global baseline (all human traffic)
    private readonly DecayingTransitionMatrix _globalBaseline;

    // Recent transitions buffer per signature (for drift computation)
    private readonly ConcurrentDictionary<string, RecentTransitionBuffer> _recentTransitions = new();

    // Pending cohort updates (batched)
    private readonly ConcurrentQueue<CohortUpdate> _pendingCohortUpdates = new();

    public MarkovTracker(
        ILogger<MarkovTracker> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value.Markov;
        _globalBaseline = new DecayingTransitionMatrix(
            TimeSpan.FromHours(_options.GlobalHalfLifeHours),
            _options.MaxEdgesPerNode);
    }

    /// <summary>
    ///     Record a transition and return drift signals.
    ///     Called on every request after detection completes.
    ///     Returns Empty if insufficient data.
    /// </summary>
    public DriftSignals RecordTransition(
        string signature,
        string rawPath,
        DateTime timestamp,
        bool isBot,
        bool isDatacenter,
        bool isReturning,
        string? clusterId = null)
    {
        var normalizedPath = PathNormalizer.Normalize(rawPath);

        // Get or create signature chain state
        var state = _signatureChains.GetOrAdd(signature, _ => new SignatureChainState(
            new DecayingTransitionMatrix(
                TimeSpan.FromHours(_options.SignatureHalfLifeHours),
                _options.MaxEdgesPerNode)));

        // Get recent transitions buffer
        var recentBuffer = _recentTransitions.GetOrAdd(signature,
            _ => new RecentTransitionBuffer(_options.RecentTransitionWindowSize));

        DriftSignals drift;
        lock (state.Lock)
        {
            // Record in per-signature chain
            if (state.LastPath != null)
            {
                var transition = (state.LastPath, normalizedPath);
                state.Chain.RecordTransition(state.LastPath, normalizedPath, timestamp);
                recentBuffer.Add(transition);

                // Queue cohort update (non-blocking)
                var cohortKey = GetCohortKey(isDatacenter, isReturning, clusterId);
                _pendingCohortUpdates.Enqueue(new CohortUpdate(
                    cohortKey, state.LastPath, normalizedPath, timestamp, !isBot));
            }

            state.LastPath = normalizedPath;
            state.TransitionCount++;

            // Compute drift signals if we have enough data
            drift = state.TransitionCount >= _options.MinTransitionsForDrift
                ? ComputeDriftSignals(signature, state, recentBuffer, timestamp,
                    isDatacenter, isReturning, clusterId)
                : DriftSignals.Empty;
        }

        return drift;
    }

    /// <summary>
    ///     Process pending cohort updates. Called by PopulationMarkovService on a timer.
    /// </summary>
    public void FlushCohortUpdates()
    {
        var now = DateTime.UtcNow;
        var processed = 0;

        while (_pendingCohortUpdates.TryDequeue(out var update))
        {
            // Only human traffic goes into baselines
            if (!update.IsHuman) continue;

            var baseline = _cohortBaselines.GetOrAdd(update.CohortKey,
                _ => new DecayingTransitionMatrix(
                    TimeSpan.FromHours(_options.CohortHalfLifeHours),
                    _options.MaxEdgesPerNode));

            baseline.RecordTransition(update.From, update.To, update.Timestamp);

            // Also update global baseline
            _globalBaseline.RecordTransition(update.From, update.To, update.Timestamp);

            processed++;
        }

        if (processed > 0)
            _logger.LogDebug("Flushed {Count} cohort updates to {Cohorts} cohorts + global baseline",
                processed, _cohortBaselines.Count);
    }

    /// <summary>
    ///     Get the drift signals for a signature without recording a new transition.
    /// </summary>
    public DriftSignals GetDriftSignals(string signature, bool isDatacenter, bool isReturning,
        string? clusterId = null)
    {
        if (!_signatureChains.TryGetValue(signature, out var state))
            return DriftSignals.Empty;

        if (!_recentTransitions.TryGetValue(signature, out var recentBuffer))
            return DriftSignals.Empty;

        lock (state.Lock)
        {
            return state.TransitionCount >= _options.MinTransitionsForDrift
                ? ComputeDriftSignals(signature, state, recentBuffer, DateTime.UtcNow,
                    isDatacenter, isReturning, clusterId)
                : DriftSignals.Empty;
        }
    }

    /// <summary>
    ///     Get all cohort baselines (for snapshotting to storage).
    /// </summary>
    public IReadOnlyDictionary<string, DecayingTransitionMatrix> GetCohortBaselines() => _cohortBaselines;

    /// <summary>
    ///     Get the global baseline (for snapshotting).
    /// </summary>
    public DecayingTransitionMatrix GetGlobalBaseline() => _globalBaseline;

    /// <summary>
    ///     Restore a cohort baseline from a snapshot.
    /// </summary>
    public void RestoreCohortBaseline(string cohortKey, DecayingTransitionMatrix matrix)
    {
        _cohortBaselines[cohortKey] = matrix;
    }

    /// <summary>
    ///     Get summary statistics for monitoring.
    /// </summary>
    public MarkovStats GetStats()
    {
        var now = DateTime.UtcNow;
        return new MarkovStats
        {
            ActiveSignatures = _signatureChains.Count,
            CohortCount = _cohortBaselines.Count,
            GlobalBaselineNodes = _globalBaseline.NodeCount,
            GlobalBaselineEdges = _globalBaseline.GetActiveEdgeCount(now),
            PendingCohortUpdates = _pendingCohortUpdates.Count
        };
    }

    private DriftSignals ComputeDriftSignals(
        string signature,
        SignatureChainState state,
        RecentTransitionBuffer recentBuffer,
        DateTime now,
        bool isDatacenter,
        bool isReturning,
        string? clusterId)
    {
        var recentTransitions = recentBuffer.GetRecent();
        if (recentTransitions.Count < 3) return DriftSignals.Empty;

        // Get the appropriate cohort baseline (cluster > specific cohort > global)
        var cohortKey = GetCohortKey(isDatacenter, isReturning, clusterId);
        var baseline = GetBestBaseline(cohortKey);

        // 1. Self-drift: recent vs historical chain
        var recentDist = BuildDistributionFromRecent(recentTransitions);
        var historicalDist = FlattenDistribution(state.Chain.GetFullDistribution(now));
        var selfDrift = DivergenceMetrics.JensenShannonDivergence(recentDist, historicalDist);

        // 2. Human-drift: signature's chain vs cohort baseline
        var baselineDist = FlattenDistribution(baseline.GetFullDistribution(now));
        var humanDrift = DivergenceMetrics.JensenShannonDivergence(historicalDist, baselineDist);

        // 3. Transition novelty
        var novelty = DivergenceMetrics.ComputeTransitionNovelty(recentTransitions, state.Chain, now);

        // 4. Entropy delta
        var currentEntropy = state.Chain.GetPathEntropy(now);
        var entropyDelta = currentEntropy - (state.LastEntropy ?? currentEntropy);
        state.LastEntropy = currentEntropy;

        // 5. Loop score
        var loopScore = DivergenceMetrics.ComputeLoopScore(recentTransitions);

        // 6. Sequence surprise
        var surprise = DivergenceMetrics.AverageTransitionSurprise(recentTransitions, baseline, now);

        return new DriftSignals
        {
            SelfDrift = selfDrift,
            HumanDrift = humanDrift,
            TransitionNovelty = novelty,
            EntropyDelta = entropyDelta,
            LoopScore = loopScore,
            SequenceSurprise = surprise
        };
    }

    private DecayingTransitionMatrix GetBestBaseline(string cohortKey)
    {
        // Try cluster-specific first, then cohort, then global
        if (_cohortBaselines.TryGetValue(cohortKey, out var baseline) && baseline.TotalTransitions >= 50)
            return baseline;

        // Fall back to generic cohort (without cluster)
        var genericKey = cohortKey.Split(':')[0]; // "datacenter-new" from "datacenter-new:cluster123"
        if (genericKey != cohortKey &&
            _cohortBaselines.TryGetValue(genericKey, out var generic) && generic.TotalTransitions >= 50)
            return generic;

        return _globalBaseline;
    }

    private static string GetCohortKey(bool isDatacenter, bool isReturning, string? clusterId)
    {
        var infraType = isDatacenter ? "datacenter" : "residential";
        var visitType = isReturning ? "returning" : "new";
        var key = $"{infraType}-{visitType}";
        return clusterId != null ? $"{key}:{clusterId}" : key;
    }

    /// <summary>
    ///     Flatten a per-source distribution into a global edge distribution.
    /// </summary>
    private static Dictionary<string, double> FlattenDistribution(
        Dictionary<string, Dictionary<string, double>> fullDist)
    {
        var flat = new Dictionary<string, double>();
        var totalWeight = 0.0;

        foreach (var (from, targets) in fullDist)
        {
            foreach (var (to, prob) in targets)
            {
                var edgeKey = $"{from}->{to}";
                flat[edgeKey] = flat.GetValueOrDefault(edgeKey) + prob;
                totalWeight += prob;
            }
        }

        // Normalize
        if (totalWeight > 0)
            foreach (var key in flat.Keys.ToList())
                flat[key] /= totalWeight;

        return flat;
    }

    /// <summary>
    ///     Build a distribution from recent transitions (for self-drift comparison).
    /// </summary>
    private static Dictionary<string, double> BuildDistributionFromRecent(
        IReadOnlyList<(string From, string To)> transitions)
    {
        var counts = new Dictionary<string, double>();
        foreach (var (from, to) in transitions)
        {
            var key = $"{from}->{to}";
            counts[key] = counts.GetValueOrDefault(key) + 1.0;
        }

        var total = transitions.Count;
        foreach (var key in counts.Keys.ToList())
            counts[key] /= total;

        return counts;
    }

    private sealed class SignatureChainState
    {
        public readonly DecayingTransitionMatrix Chain;
        public readonly object Lock = new();
        public string? LastPath;
        public int TransitionCount;
        public double? LastEntropy;

        public SignatureChainState(DecayingTransitionMatrix chain)
        {
            Chain = chain;
        }
    }

    private readonly record struct CohortUpdate(
        string CohortKey,
        string From,
        string To,
        DateTime Timestamp,
        bool IsHuman);
}

/// <summary>
///     Ring buffer for recent transitions (fixed size, FIFO).
/// </summary>
public sealed class RecentTransitionBuffer
{
    private readonly (string From, string To)[] _buffer;
    private int _writeIndex;
    private int _count;

    public RecentTransitionBuffer(int capacity)
    {
        _buffer = new (string, string)[capacity];
    }

    public void Add((string From, string To) transition)
    {
        _buffer[_writeIndex] = transition;
        _writeIndex = (_writeIndex + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    public IReadOnlyList<(string From, string To)> GetRecent()
    {
        if (_count == 0) return [];

        var result = new (string, string)[_count];
        var start = _count < _buffer.Length ? 0 : _writeIndex;
        for (var i = 0; i < _count; i++)
            result[i] = _buffer[(start + i) % _buffer.Length];
        return result;
    }
}

/// <summary>
///     Monitoring statistics for MarkovTracker.
/// </summary>
public sealed record MarkovStats
{
    public int ActiveSignatures { get; init; }
    public int CohortCount { get; init; }
    public int GlobalBaselineNodes { get; init; }
    public int GlobalBaselineEdges { get; init; }
    public int PendingCohortUpdates { get; init; }
}
