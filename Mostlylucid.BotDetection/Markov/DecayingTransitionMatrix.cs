namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     A space-bounded, exponentially-decayed first-order Markov transition matrix.
///     Tracks transitions between normalized route templates with automatic pruning
///     of low-weight edges (top-K per source node).
/// </summary>
public sealed class DecayingTransitionMatrix
{
    private readonly int _maxK;
    private readonly TimeSpan _halfLife;
    private readonly Dictionary<string, NodeStats> _nodes = new();
    private readonly Dictionary<string, Dictionary<string, EdgeEntry>> _edges = new();
    private readonly object _lock = new();
    private long _totalTransitions;
    private DateTime _lastUpdate;

    public DecayingTransitionMatrix(TimeSpan halfLife, int maxK = 20)
    {
        _halfLife = halfLife;
        _maxK = maxK;
        _lastUpdate = DateTime.UtcNow;
    }

    public long TotalTransitions => _totalTransitions;
    public DateTime LastUpdate => _lastUpdate;
    public int NodeCount { get { lock (_lock) return _nodes.Count; } }
    public int EdgeCount { get { lock (_lock) return _edges.Values.Sum(e => e.Count); } }

    /// <summary>
    ///     Record a transition from one route to another.
    /// </summary>
    public void RecordTransition(string from, string to, DateTime timestamp)
    {
        lock (_lock)
        {
            _totalTransitions++;
            _lastUpdate = timestamp;

            // Update source node stats
            if (!_nodes.TryGetValue(from, out var fromStats))
            {
                fromStats = new NodeStats();
                _nodes[from] = fromStats;
            }
            fromStats.VisitCount.IncrementWithDecay(1.0, timestamp, _halfLife);

            // Update target node stats
            if (!_nodes.TryGetValue(to, out var toStats))
            {
                toStats = new NodeStats();
                _nodes[to] = toStats;
            }
            toStats.VisitCount.IncrementWithDecay(1.0, timestamp, _halfLife);

            // Update edge
            if (!_edges.TryGetValue(from, out var targets))
            {
                targets = new Dictionary<string, EdgeEntry>();
                _edges[from] = targets;
            }

            if (targets.TryGetValue(to, out var edge))
            {
                edge.Weight.IncrementWithDecay(1.0, timestamp, _halfLife);
                edge.LastSeen = timestamp;
            }
            else
            {
                targets[to] = new EdgeEntry
                {
                    Target = to,
                    Weight = new DecayingCounter(1.0, timestamp),
                    LastSeen = timestamp
                };
            }

            // Prune to top-K if needed
            if (targets.Count > _maxK * 2) // Prune at 2x to amortize
                PruneEdges(targets, timestamp);
        }
    }

    /// <summary>
    ///     Get the transition probability from source to target.
    /// </summary>
    public double GetTransitionProbability(string from, string to, DateTime now)
    {
        lock (_lock)
        {
            if (!_edges.TryGetValue(from, out var targets))
                return 0;

            var totalWeight = 0.0;
            var targetWeight = 0.0;

            foreach (var (key, edge) in targets)
            {
                var w = edge.Weight.Decayed(now, _halfLife);
                totalWeight += w;
                if (key == to) targetWeight = w;
            }

            return totalWeight > 0 ? targetWeight / totalWeight : 0;
        }
    }

    /// <summary>
    ///     Get the probability distribution from a source node.
    ///     Returns normalized (target → probability) pairs.
    /// </summary>
    public Dictionary<string, double> GetDistribution(string from, DateTime now)
    {
        lock (_lock)
        {
            var result = new Dictionary<string, double>();
            if (!_edges.TryGetValue(from, out var targets))
                return result;

            var totalWeight = 0.0;
            var weights = new Dictionary<string, double>();

            foreach (var (key, edge) in targets)
            {
                var w = edge.Weight.Decayed(now, _halfLife);
                if (w < 1e-6) continue; // Skip effectively-zero edges
                weights[key] = w;
                totalWeight += w;
            }

            if (totalWeight > 0)
                foreach (var (key, w) in weights)
                    result[key] = w / totalWeight;

            return result;
        }
    }

    /// <summary>
    ///     Get the full transition probability matrix (for divergence computation).
    ///     Returns all source nodes with their outgoing distributions.
    /// </summary>
    public Dictionary<string, Dictionary<string, double>> GetFullDistribution(DateTime now)
    {
        lock (_lock)
        {
            var result = new Dictionary<string, Dictionary<string, double>>();
            foreach (var from in _edges.Keys)
            {
                var dist = GetDistributionUnsafe(from, now);
                if (dist.Count > 0)
                    result[from] = dist;
            }
            return result;
        }
    }

    /// <summary>
    ///     Internal non-locking version of GetDistribution (caller must hold _lock).
    /// </summary>
    private Dictionary<string, double> GetDistributionUnsafe(string from, DateTime now)
    {
        var result = new Dictionary<string, double>();
        if (!_edges.TryGetValue(from, out var targets))
            return result;

        var totalWeight = 0.0;
        var weights = new Dictionary<string, double>();

        foreach (var (key, edge) in targets)
        {
            var w = edge.Weight.Decayed(now, _halfLife);
            if (w < 1e-6) continue;
            weights[key] = w;
            totalWeight += w;
        }

        if (totalWeight > 0)
            foreach (var (key, w) in weights)
                result[key] = w / totalWeight;

        return result;
    }

    /// <summary>
    ///     Calculate Shannon entropy of the overall path distribution.
    /// </summary>
    public double GetPathEntropy(DateTime now)
    {
        lock (_lock)
        {
            var totalVisits = 0.0;
            var nodeWeights = new Dictionary<string, double>();

            foreach (var (key, stats) in _nodes)
            {
                var w = stats.VisitCount.Decayed(now, _halfLife);
                if (w < 1e-6) continue;
                nodeWeights[key] = w;
                totalVisits += w;
            }

            if (totalVisits <= 0) return 0;

            var entropy = 0.0;
            foreach (var w in nodeWeights.Values)
            {
                var p = w / totalVisits;
                if (p > 0) entropy -= p * Math.Log2(p);
            }
            return entropy;
        }
    }

    /// <summary>
    ///     Check if a transition exists in this matrix (with non-negligible weight).
    /// </summary>
    public bool HasEdge(string from, string to, DateTime now)
    {
        lock (_lock)
        {
            if (!_edges.TryGetValue(from, out var targets))
                return false;
            if (!targets.TryGetValue(to, out var edge))
                return false;
            return edge.Weight.Decayed(now, _halfLife) > 1e-6;
        }
    }

    /// <summary>
    ///     Get total number of distinct transitions with non-negligible weight.
    /// </summary>
    public int GetActiveEdgeCount(DateTime now)
    {
        lock (_lock)
        {
            var count = 0;
            foreach (var targets in _edges.Values)
                foreach (var edge in targets.Values)
                    if (edge.Weight.Decayed(now, _halfLife) > 1e-6)
                        count++;
            return count;
        }
    }

    /// <summary>
    ///     Merge another matrix into this one (for cohort aggregation).
    /// </summary>
    public void MergeFrom(DecayingTransitionMatrix other, DateTime now)
    {
        lock (_lock)
        {
        foreach (var (from, targets) in other._edges)
        {
            if (!_edges.TryGetValue(from, out var myTargets))
            {
                myTargets = new Dictionary<string, EdgeEntry>();
                _edges[from] = myTargets;
            }

            foreach (var (to, edge) in targets)
            {
                var weight = edge.Weight.Decayed(now, other._halfLife);
                if (weight < 1e-6) continue;

                if (myTargets.TryGetValue(to, out var existing))
                {
                    existing.Weight.IncrementWithDecay(weight, now, _halfLife);
                    if (edge.LastSeen > existing.LastSeen)
                        existing.LastSeen = edge.LastSeen;
                }
                else
                {
                    myTargets[to] = new EdgeEntry
                    {
                        Target = to,
                        Weight = new DecayingCounter(weight, now),
                        LastSeen = edge.LastSeen
                    };
                }
            }

            if (myTargets.Count > _maxK * 2)
                PruneEdges(myTargets, now);
        }

        foreach (var (key, stats) in other._nodes)
        {
            var weight = stats.VisitCount.Decayed(now, other._halfLife);
            if (weight < 1e-6) continue;

            if (!_nodes.TryGetValue(key, out var myStats))
            {
                myStats = new NodeStats();
                _nodes[key] = myStats;
            }
            myStats.VisitCount.IncrementWithDecay(weight, now, _halfLife);
        }

        _totalTransitions += other._totalTransitions;
        if (other._lastUpdate > _lastUpdate)
            _lastUpdate = other._lastUpdate;
        }
    }

    private void PruneEdges(Dictionary<string, EdgeEntry> targets, DateTime now)
    {
        if (targets.Count <= _maxK) return;

        // Sort by decayed weight, keep top K
        var sorted = targets
            .OrderByDescending(kvp => kvp.Value.Weight.Decayed(now, _halfLife))
            .ToList();

        targets.Clear();
        foreach (var kvp in sorted.Take(_maxK))
            targets[kvp.Key] = kvp.Value;
    }
}

/// <summary>
///     Statistics for a single node (route template).
/// </summary>
public sealed class NodeStats
{
    public DecayingCounter VisitCount;
}

/// <summary>
///     A weighted, timestamped edge in the transition graph.
/// </summary>
public sealed class EdgeEntry
{
    public required string Target;
    public DecayingCounter Weight;
    public DateTime LastSeen;
}
