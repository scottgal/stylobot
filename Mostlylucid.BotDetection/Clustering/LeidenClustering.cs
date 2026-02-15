namespace Mostlylucid.BotDetection.Clustering;

/// <summary>
///     Native C# implementation of the Leiden community detection algorithm.
///     Uses the Constant Potts Model (CPM) quality function with a resolution parameter.
///     Optimized for small-medium graphs (&lt;1000 nodes) typical in bot clustering.
///
///     Key advantages over Label Propagation:
///     - Resolution parameter controls cluster granularity
///     - Guaranteed well-connected communities (refinement step)
///     - Deterministic with stable convergence
///
///     Reference: Traag, Waltman, van Eck (2019). "From Louvain to Leiden"
/// </summary>
public static class LeidenClustering
{
    /// <summary>
    ///     Run Leiden community detection on a weighted adjacency graph.
    /// </summary>
    /// <param name="adjacency">Adjacency list: node → [(neighbor, weight)]</param>
    /// <param name="nodeCount">Total number of nodes</param>
    /// <param name="resolution">CPM resolution parameter (higher = more/smaller clusters). Default: 1.0</param>
    /// <param name="maxIterations">Maximum number of outer iterations. Default: 10</param>
    /// <returns>Community labels: labels[i] = community ID for node i</returns>
    public static int[] FindCommunities(
        Dictionary<int, List<(int Neighbor, double Weight)>> adjacency,
        int nodeCount,
        double resolution = 1.0,
        int maxIterations = 10)
    {
        // Initialize: each node in its own community
        var communities = new int[nodeCount];
        for (var i = 0; i < nodeCount; i++)
            communities[i] = i;

        // Pre-compute total edge weight for normalization
        var totalWeight = 0.0;
        foreach (var (_, neighbors) in adjacency)
            foreach (var (_, w) in neighbors)
                totalWeight += w;
        totalWeight /= 2.0; // Each edge counted twice

        if (totalWeight < 1e-9)
            return communities;

        var rng = new Random(42); // Deterministic

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var improved = false;

            // Phase 1: Local moving — try moving each node to best neighboring community
            improved |= LocalMovingPhase(adjacency, communities, nodeCount, resolution, totalWeight, rng);

            // Phase 2: Refinement — ensure communities are well-connected
            RefinePhase(adjacency, communities, nodeCount, resolution);

            if (!improved)
                break;
        }

        // Compact community labels to [0, N-1]
        return CompactLabels(communities);
    }

    /// <summary>
    ///     Local moving phase: iterate through nodes in random order,
    ///     move each to the neighboring community that maximizes CPM quality gain.
    /// </summary>
    private static bool LocalMovingPhase(
        Dictionary<int, List<(int Neighbor, double Weight)>> adjacency,
        int[] communities,
        int nodeCount,
        double resolution,
        double totalWeight,
        Random rng)
    {
        var improved = false;
        var order = Enumerable.Range(0, nodeCount).ToArray();

        // Shuffle for fairness
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        // Pre-compute community sizes
        var commSizes = new Dictionary<int, int>();
        foreach (var c in communities)
        {
            commSizes.TryGetValue(c, out var count);
            commSizes[c] = count + 1;
        }

        // Pre-compute sum of weights within each community
        var commInternalWeight = new Dictionary<int, double>();
        for (var i = 0; i < nodeCount; i++)
        {
            var ci = communities[i];
            if (!adjacency.TryGetValue(i, out var neighbors))
                continue;

            foreach (var (neighbor, weight) in neighbors)
            {
                if (communities[neighbor] == ci && neighbor > i) // Count each internal edge once
                {
                    commInternalWeight.TryGetValue(ci, out var current);
                    commInternalWeight[ci] = current + weight;
                }
            }
        }

        foreach (var node in order)
        {
            if (!adjacency.TryGetValue(node, out var neighbors) || neighbors.Count == 0)
                continue;

            var currentComm = communities[node];

            // Calculate edge weight from node to each neighboring community
            var neighborCommWeights = new Dictionary<int, double>();
            foreach (var (neighbor, weight) in neighbors)
            {
                var neighborComm = communities[neighbor];
                neighborCommWeights.TryGetValue(neighborComm, out var current);
                neighborCommWeights[neighborComm] = current + weight;
            }

            // Weight from node to its own community (internal edges)
            neighborCommWeights.TryGetValue(currentComm, out var weightToOwn);

            var bestComm = currentComm;
            var bestGain = 0.0;

            // Try each neighboring community
            foreach (var (candidateComm, weightToCandidate) in neighborCommWeights)
            {
                if (candidateComm == currentComm)
                    continue;

                // CPM quality gain for moving node from currentComm to candidateComm:
                // gain = (weight_to_candidate - weight_to_current) - resolution * (size_candidate - size_current + 1)
                commSizes.TryGetValue(candidateComm, out var candidateSize);
                commSizes.TryGetValue(currentComm, out var currentSize);

                var gain = (weightToCandidate - weightToOwn)
                           - resolution * (candidateSize - (currentSize - 1));

                if (gain > bestGain)
                {
                    bestGain = gain;
                    bestComm = candidateComm;
                }
            }

            if (bestComm != currentComm)
            {
                // Move node to best community
                communities[node] = bestComm;

                // Update community sizes
                commSizes[currentComm] = commSizes.GetValueOrDefault(currentComm) - 1;
                commSizes[bestComm] = commSizes.GetValueOrDefault(bestComm) + 1;

                if (commSizes[currentComm] <= 0)
                    commSizes.Remove(currentComm);

                improved = true;
            }
        }

        return improved;
    }

    /// <summary>
    ///     Refinement phase: check each community for connectedness.
    ///     If a community has disconnected subgraphs, split them into separate communities.
    ///     This is the key improvement of Leiden over Louvain.
    /// </summary>
    private static void RefinePhase(
        Dictionary<int, List<(int Neighbor, double Weight)>> adjacency,
        int[] communities,
        int nodeCount,
        double resolution)
    {
        // Group nodes by community
        var communityNodes = new Dictionary<int, List<int>>();
        for (var i = 0; i < nodeCount; i++)
        {
            var c = communities[i];
            if (!communityNodes.TryGetValue(c, out var list))
            {
                list = [];
                communityNodes[c] = list;
            }
            list.Add(i);
        }

        var nextCommunityId = communities.Length > 0 ? communities.Max() + 1 : 0;

        foreach (var (commId, nodes) in communityNodes)
        {
            if (nodes.Count <= 1)
                continue;

            // BFS to find connected components within this community
            var visited = new HashSet<int>();
            var components = new List<List<int>>();

            foreach (var startNode in nodes)
            {
                if (visited.Contains(startNode))
                    continue;

                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(startNode);
                visited.Add(startNode);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    component.Add(current);

                    if (!adjacency.TryGetValue(current, out var neighbors))
                        continue;

                    foreach (var (neighbor, _) in neighbors)
                    {
                        if (communities[neighbor] == commId && visited.Add(neighbor))
                            queue.Enqueue(neighbor);
                    }
                }

                components.Add(component);
            }

            // If community is already connected (single component), no split needed
            if (components.Count <= 1)
                continue;

            // Keep the largest component with the original ID, assign new IDs to others
            components.Sort((a, b) => b.Count.CompareTo(a.Count));
            for (var i = 1; i < components.Count; i++)
            {
                foreach (var node in components[i])
                    communities[node] = nextCommunityId;
                nextCommunityId++;
            }
        }
    }

    /// <summary>
    ///     Compact community labels to contiguous range [0, N-1].
    /// </summary>
    private static int[] CompactLabels(int[] communities)
    {
        var labelMap = new Dictionary<int, int>();
        var nextLabel = 0;
        var result = new int[communities.Length];

        for (var i = 0; i < communities.Length; i++)
        {
            if (!labelMap.TryGetValue(communities[i], out var mapped))
            {
                mapped = nextLabel++;
                labelMap[communities[i]] = mapped;
            }
            result[i] = mapped;
        }

        return result;
    }
}
