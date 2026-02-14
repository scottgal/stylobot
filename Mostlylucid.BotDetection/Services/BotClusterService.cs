using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that periodically clusters signatures from the SignatureCoordinator's
///     sliding window using Label Propagation. Discovers bot products (same bot software) and
///     bot networks (coordinated campaigns). Uses FFT-based spectral analysis to detect
///     shared timing patterns (C2 heartbeats, cron schedules).
///
///     Clustering is triggered either by a timer or when enough new bot detections accumulate.
/// </summary>
public class BotClusterService : BackgroundService
{
    // Immutable snapshots swapped atomically via volatile reference
    private volatile ClusterSnapshot _snapshot = ClusterSnapshot.Empty;

    private readonly ILogger<BotClusterService> _logger;
    private readonly ClusterOptions _options;
    private readonly SignatureCoordinator _signatureCoordinator;

    // Event-driven trigger: counts new bot detections since last clustering run
    private int _botDetectionsSinceLastRun;
    private readonly SemaphoreSlim _triggerSignal = new(0, 1);

    public BotClusterService(
        ILogger<BotClusterService> logger,
        IOptions<BotDetectionOptions> options,
        SignatureCoordinator signatureCoordinator)
    {
        _logger = logger;
        _options = options.Value.Cluster;
        _signatureCoordinator = signatureCoordinator;
    }

    /// <summary>
    ///     Notify that a bot was detected, potentially triggering an early clustering run.
    ///     Thread-safe, called by the orchestrator after each detection.
    /// </summary>
    public void NotifyBotDetected()
    {
        var count = Interlocked.Increment(ref _botDetectionsSinceLastRun);
        if (count >= _options.MinBotDetectionsToTrigger)
        {
            // Signal the background loop to run clustering early
            try { _triggerSignal.Release(); } catch (SemaphoreFullException) { /* already signaled */ }
        }
    }

    /// <summary>
    ///     Find which cluster a signature belongs to, if any.
    /// </summary>
    public BotCluster? FindCluster(string signature)
    {
        var snapshot = _snapshot;
        if (snapshot.SignatureToCluster.TryGetValue(signature, out var clusterId) &&
            snapshot.Clusters.TryGetValue(clusterId, out var cluster))
            return cluster;
        return null;
    }

    /// <summary>
    ///     Get all discovered clusters.
    /// </summary>
    public IReadOnlyList<BotCluster> GetClusters()
    {
        return _snapshot.Clusters.Values.ToList();
    }

    /// <summary>
    ///     Get cached spectral features for a signature, if available.
    /// </summary>
    public SpectralFeatures? GetSpectralFeatures(string signature)
    {
        _snapshot.SpectralCache.TryGetValue(signature, out var features);
        return features;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BotClusterService started (interval={Interval}s, threshold={Threshold}, trigger={Trigger} detections)",
            _options.ClusterIntervalSeconds,
            _options.SimilarityThreshold,
            _options.MinBotDetectionsToTrigger);

        // Wait a bit before first run to let the system warm up
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunClustering();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during clustering run");
            }

            // Reset bot detection counter after each run
            Interlocked.Exchange(ref _botDetectionsSinceLastRun, 0);

            // Wait for either the timer or the trigger signal
            try
            {
                // WaitAsync with timeout = timer-based + event-driven
                await _triggerSignal.WaitAsync(
                    TimeSpan.FromSeconds(_options.ClusterIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal void RunClustering()
    {
        // 1. Snapshot all behaviors from the signature coordinator
        var allBehaviors = _signatureCoordinator.GetFamilyAwareBehaviors();

        // PRE-FILTER: Only cluster confirmed bots - never cluster humans
        var behaviors = allBehaviors
            .Where(b => b.AverageBotProbability >= _options.MinBotProbabilityForClustering)
            .ToList();

        if (behaviors.Count < _options.MinClusterSize)
        {
            _logger.LogDebug(
                "Too few bot signatures ({Count}) for clustering (filtered from {Total} total), need at least {Min}",
                behaviors.Count, allBehaviors.Count, _options.MinClusterSize);
            return;
        }

        // 2. Build feature vectors (includes spectral analysis)
        var features = BuildFeatureVectors(behaviors);

        // 2.5. Build spectral cache
        var spectralBuilder = new Dictionary<string, SpectralFeatures>();
        foreach (var f in features)
        {
            if (f.Spectral is { HasSufficientData: true })
                spectralBuilder[f.Signature] = f.Spectral;
        }

        // 3. Compute similarity matrix and build graph
        var adjacency = BuildSimilarityGraph(features);

        // 4. Run Label Propagation
        var labels = LabelPropagation(adjacency, features.Count);

        // 5. Group into clusters
        var rawClusters = labels
            .Select((label, idx) => (Label: label, Index: idx))
            .GroupBy(x => x.Label)
            .Where(g => g.Count() >= _options.MinClusterSize)
            .ToList();

        // 6. Classify and store clusters
        var newClusters = new Dictionary<string, BotCluster>();
        var newSignatureToCluster = new Dictionary<string, string>();

        foreach (var group in rawClusters)
        {
            var memberIndices = group.Select(x => x.Index).ToList();
            var memberBehaviors = memberIndices.Select(i => behaviors[i]).ToList();
            var memberFeatures = memberIndices.Select(i => features[i]).ToList();

            var cluster = ClassifyCluster(memberBehaviors, memberFeatures, adjacency, memberIndices);
            if (cluster == null)
                continue;

            newClusters[cluster.ClusterId] = cluster;
            foreach (var sig in cluster.MemberSignatures)
                newSignatureToCluster[sig] = cluster.ClusterId;
        }

        // Atomic swap: single volatile write replaces entire snapshot
        _snapshot = new ClusterSnapshot(
            newClusters.ToFrozenDictionary(),
            newSignatureToCluster.ToFrozenDictionary(),
            spectralBuilder.ToFrozenDictionary());

        if (newClusters.Count > 0)
        {
            var productCount = newClusters.Values.Count(c => c.Type == BotClusterType.BotProduct);
            var networkCount = newClusters.Values.Count(c => c.Type == BotClusterType.BotNetwork);
            _logger.LogInformation(
                "BotClusterService: discovered {Total} clusters ({Product} product, {Network} network) from {BotSignatures} bot signatures (filtered from {TotalSignatures} total)",
                newClusters.Count, productCount, networkCount, behaviors.Count, allBehaviors.Count);
        }
    }

    #region Feature Extraction

    internal record FeatureVector
    {
        public required string Signature { get; init; }
        public double TimingRegularity { get; init; }
        public double RequestRate { get; init; }
        public double PathDiversity { get; init; }
        public double PathEntropy { get; init; }
        public double AvgBotProbability { get; init; }
        public string? CountryCode { get; init; }
        public bool IsDatacenter { get; init; }
        public string? Asn { get; init; }
        public DateTime FirstSeen { get; init; }
        public DateTime LastSeen { get; init; }
        public SpectralFeatures? Spectral { get; init; }
        public double[]? Intervals { get; init; }
    }

    internal List<FeatureVector> BuildFeatureVectors(IReadOnlyList<SignatureBehavior> behaviors)
    {
        return behaviors.Select(b =>
        {
            var durationSeconds = (b.LastSeen - b.FirstSeen).TotalSeconds;
            var requestRate = durationSeconds > 0 ? b.RequestCount / (durationSeconds / 60.0) : 0;
            var uniquePaths = b.Requests.Select(r => r.Path).Distinct().Count();
            var pathDiversity = b.RequestCount > 0 ? (double)uniquePaths / b.RequestCount : 0;

            // Extract intervals and compute spectral features
            double[]? intervals = null;
            SpectralFeatures? spectral = null;
            if (b.Requests.Count >= 9)
            {
                intervals = new double[b.Requests.Count - 1];
                for (var i = 1; i < b.Requests.Count; i++)
                    intervals[i - 1] = (b.Requests[i].Timestamp - b.Requests[i - 1].Timestamp).TotalSeconds;
                spectral = SpectralFeatureExtractor.Extract(intervals);
            }

            return new FeatureVector
            {
                Signature = b.Signature,
                TimingRegularity = b.TimingCoefficient,
                RequestRate = requestRate,
                PathDiversity = pathDiversity,
                PathEntropy = b.PathEntropy,
                AvgBotProbability = b.AverageBotProbability,
                CountryCode = b.CountryCode,
                IsDatacenter = b.IsDatacenter,
                Asn = b.Asn,
                FirstSeen = b.FirstSeen,
                LastSeen = b.LastSeen,
                Spectral = spectral,
                Intervals = intervals
            };
        }).ToList();
    }

    #endregion

    #region Similarity

    internal static double ComputeSimilarity(FeatureVector a, FeatureVector b)
    {
        // Continuous features: normalized absolute difference
        var timingSim = 1.0 - NormalizedDiff(a.TimingRegularity, b.TimingRegularity);
        var rateSim = 1.0 - NormalizedDiff(a.RequestRate, b.RequestRate);
        var pathDivSim = 1.0 - NormalizedDiff(a.PathDiversity, b.PathDiversity);
        var entropySim = 1.0 - NormalizedDiff(a.PathEntropy, b.PathEntropy);
        var botProbSim = 1.0 - NormalizedDiff(a.AvgBotProbability, b.AvgBotProbability);

        // Categorical features: exact match
        var countrySim = string.Equals(a.CountryCode, b.CountryCode, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
        var datacenterSim = a.IsDatacenter == b.IsDatacenter ? 1.0 : 0.0;
        var asnSim = !string.IsNullOrEmpty(a.Asn) && string.Equals(a.Asn, b.Asn, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

        // Spectral features: neutral (0.5) when data is insufficient
        var spectralEntropySim = 0.5;
        var harmonicSim = 0.5;
        var peakToAvgSim = 0.5;
        var dominantFreqSim = 0.5;

        if (a.Spectral is { HasSufficientData: true } sa &&
            b.Spectral is { HasSufficientData: true } sb)
        {
            spectralEntropySim = 1.0 - Math.Abs(sa.SpectralEntropy - sb.SpectralEntropy);
            harmonicSim = 1.0 - Math.Abs(sa.HarmonicRatio - sb.HarmonicRatio);
            peakToAvgSim = 1.0 - Math.Abs(sa.PeakToAvgRatio - sb.PeakToAvgRatio);
            dominantFreqSim = 1.0 - NormalizedDiff(sa.DominantFrequency, sb.DominantFrequency);
        }

        // Weighted sum (weights sum to 1.0)
        return timingSim * 0.12 +
               rateSim * 0.10 +
               pathDivSim * 0.08 +
               entropySim * 0.08 +
               botProbSim * 0.12 +
               countrySim * 0.08 +
               datacenterSim * 0.07 +
               asnSim * 0.08 +
               spectralEntropySim * 0.09 +
               harmonicSim * 0.06 +
               peakToAvgSim * 0.07 +
               dominantFreqSim * 0.05;
    }

    private static double NormalizedDiff(double a, double b)
    {
        var maxVal = Math.Max(Math.Abs(a), Math.Abs(b));
        if (maxVal < 1e-9)
            return 0.0;
        return Math.Abs(a - b) / maxVal;
    }

    /// <summary>
    ///     Build adjacency list with similarity weights.
    ///     Edge exists if similarity exceeds threshold.
    ///     Cross-correlation boosts similarity for signatures with shared timing patterns.
    /// </summary>
    internal Dictionary<int, List<(int Neighbor, double Similarity)>> BuildSimilarityGraph(
        List<FeatureVector> features)
    {
        var adjacency = new Dictionary<int, List<(int, double)>>();

        for (var i = 0; i < features.Count; i++)
            adjacency[i] = new List<(int, double)>();

        for (var i = 0; i < features.Count; i++)
        {
            for (var j = i + 1; j < features.Count; j++)
            {
                var sim = ComputeSimilarity(features[i], features[j]);

                // If both have interval data, boost with temporal cross-correlation
                if (features[i].Intervals is { } intervalsI && features[j].Intervals is { } intervalsJ)
                {
                    var correlation = SpectralFeatureExtractor.ComputeTemporalCorrelation(
                        intervalsI, intervalsJ);
                    // Blend: 85% standard similarity + 15% temporal correlation
                    sim = sim * 0.85 + correlation * 0.15;
                }

                if (sim >= _options.SimilarityThreshold)
                {
                    adjacency[i].Add((j, sim));
                    adjacency[j].Add((i, sim));
                }
            }
        }

        return adjacency;
    }

    #endregion

    #region Label Propagation

    /// <summary>
    ///     Label Propagation algorithm: each node adopts the most frequent label
    ///     among its neighbors (weighted by similarity). O(V+E) per iteration.
    /// </summary>
    private int[] LabelPropagation(
        Dictionary<int, List<(int Neighbor, double Similarity)>> adjacency,
        int nodeCount)
    {
        // Initialize: each node is its own label
        var labels = new int[nodeCount];
        for (var i = 0; i < nodeCount; i++)
            labels[i] = i;

        var random = new Random(42); // Deterministic for reproducibility

        for (var iteration = 0; iteration < _options.MaxIterations; iteration++)
        {
            var changed = false;

            // Shuffle node order for fairness
            var order = Enumerable.Range(0, nodeCount).ToArray();
            for (var i = order.Length - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            foreach (var node in order)
            {
                var neighbors = adjacency[node];
                if (neighbors.Count == 0)
                    continue;

                // Weighted label votes
                var labelWeights = new Dictionary<int, double>();
                foreach (var (neighbor, similarity) in neighbors)
                {
                    var neighborLabel = labels[neighbor];
                    labelWeights.TryGetValue(neighborLabel, out var current);
                    labelWeights[neighborLabel] = current + similarity;
                }

                // Pick label with highest weight
                var bestLabel = labelWeights
                    .OrderByDescending(kvp => kvp.Value)
                    .First()
                    .Key;

                if (labels[node] != bestLabel)
                {
                    labels[node] = bestLabel;
                    changed = true;
                }
            }

            if (!changed)
            {
                _logger.LogDebug("Label propagation converged after {Iterations} iterations", iteration + 1);
                break;
            }
        }

        return labels;
    }

    #endregion

    #region Cluster Classification

    private BotCluster? ClassifyCluster(
        List<SignatureBehavior> members,
        List<FeatureVector> features,
        Dictionary<int, List<(int Neighbor, double Similarity)>> adjacency,
        List<int> memberIndices)
    {
        if (members.Count < _options.MinClusterSize)
            return null;

        // POST-FILTER: ALL members must be confirmed bots
        if (members.Any(m => m.AverageBotProbability < _options.MinBotProbabilityForClustering))
            return null;

        // Compute average intra-cluster similarity from pre-computed adjacency graph
        var memberSet = new HashSet<int>(memberIndices);
        var totalSim = 0.0;
        var simCount = 0;
        foreach (var idx in memberIndices)
        {
            foreach (var (neighbor, similarity) in adjacency[idx])
            {
                if (neighbor > idx && memberSet.Contains(neighbor))
                {
                    totalSim += similarity;
                    simCount++;
                }
            }
        }

        // For pairs not in the adjacency graph (below threshold), count as 0 similarity
        var totalPossiblePairs = memberIndices.Count * (memberIndices.Count - 1) / 2;
        var avgSimilarity = totalPossiblePairs > 0 ? totalSim / totalPossiblePairs : 0;

        // Compute temporal density: what fraction of members were active in the same 5-minute window?
        var temporalDensity = ComputeTemporalDensity(members);

        // Average bot probability
        var avgBotProb = members.Average(m => m.AverageBotProbability);

        // Determine cluster type
        var uniqueSignatures = new HashSet<string>(members.Select(m => m.Signature));

        BotClusterType clusterType;
        if (avgSimilarity >= _options.ProductSimilarityThreshold &&
            avgBotProb >= _options.MinBotProbabilityForClustering)
        {
            // High behavioral similarity + botlike = same bot software (Bot Product)
            clusterType = BotClusterType.BotProduct;
        }
        else if (temporalDensity >= _options.NetworkTemporalDensityThreshold &&
                 avgSimilarity >= 0.5 &&
                 avgBotProb >= _options.MinBotProbabilityForClustering)
        {
            // Temporally correlated with moderate similarity = coordinated campaign (Bot Network)
            clusterType = BotClusterType.BotNetwork;
        }
        else
        {
            // Not clearly a bot cluster
            clusterType = BotClusterType.Unknown;
        }

        // Only report bot clusters, not unknown ones
        if (clusterType == BotClusterType.Unknown)
            return null;

        // Determine dominant country/ASN
        var dominantCountry = features
            .Where(f => !string.IsNullOrEmpty(f.CountryCode))
            .GroupBy(f => f.CountryCode)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        var dominantAsn = features
            .Where(f => !string.IsNullOrEmpty(f.Asn))
            .GroupBy(f => f.Asn)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        // Generate label
        var label = GenerateLabel(clusterType, avgSimilarity, temporalDensity, avgBotProb, members);

        // Generate deterministic cluster ID from sorted member signatures
        var sortedSigs = uniqueSignatures.OrderBy(s => s).ToList();
        var clusterId = ComputeClusterId(sortedSigs);

        return new BotCluster
        {
            ClusterId = clusterId,
            Type = clusterType,
            MemberSignatures = sortedSigs,
            MemberCount = sortedSigs.Count,
            AverageBotProbability = avgBotProb,
            AverageSimilarity = avgSimilarity,
            TemporalDensity = temporalDensity,
            DominantCountry = dominantCountry,
            DominantAsn = dominantAsn,
            Label = label,
            FirstSeen = new DateTimeOffset(members.Min(m => m.FirstSeen), TimeSpan.Zero),
            LastSeen = new DateTimeOffset(members.Max(m => m.LastSeen), TimeSpan.Zero)
        };
    }

    private static double ComputeTemporalDensity(List<SignatureBehavior> members)
    {
        if (members.Count < 2)
            return 0;

        // Temporal density = fraction of member pairs active within the same 5-minute window
        const double windowMinutes = 5.0;
        var overlapping = 0;
        var totalPairs = 0;

        for (var i = 0; i < members.Count; i++)
        {
            for (var j = i + 1; j < members.Count; j++)
            {
                totalPairs++;
                var latestStart = members[i].FirstSeen > members[j].FirstSeen
                    ? members[i].FirstSeen : members[j].FirstSeen;
                var earliestEnd = members[i].LastSeen < members[j].LastSeen
                    ? members[i].LastSeen : members[j].LastSeen;
                var overlap = earliestEnd - latestStart;
                if (overlap.TotalMinutes >= -windowMinutes)
                    overlapping++;
            }
        }

        return totalPairs > 0 ? (double)overlapping / totalPairs : 0;
    }

    internal static string GenerateLabel(
        BotClusterType type, double similarity, double temporalDensity,
        double avgBotProb, List<SignatureBehavior> members)
    {
        var avgInterval = members.Average(m => m.AverageInterval);
        var avgEntropy = members.Average(m => m.PathEntropy);

        return type switch
        {
            BotClusterType.BotProduct when avgInterval < 2.0 => "Rapid-Scraper",
            BotClusterType.BotProduct when avgEntropy > 3.0 => "Deep-Crawler",
            BotClusterType.BotProduct when avgEntropy < 1.0 => "Targeted-Scanner",
            BotClusterType.BotProduct => "Bot-Software",
            BotClusterType.BotNetwork when temporalDensity > 0.8 => "Burst-Campaign",
            BotClusterType.BotNetwork when members.Count > 10 => "Large-Botnet",
            BotClusterType.BotNetwork => "Coordinated-Campaign",
            _ => "Unknown-Cluster"
        };
    }

    internal static string ComputeClusterId(List<string> sortedSignatures)
    {
        var combined = string.Join("|", sortedSignatures);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return $"cluster-{Convert.ToHexString(hash[..8]).ToLowerInvariant()}";
    }

    #endregion

    /// <summary>
    ///     Immutable snapshot of clustering state. Swapped atomically via volatile reference.
    /// </summary>
    private sealed record ClusterSnapshot(
        FrozenDictionary<string, BotCluster> Clusters,
        FrozenDictionary<string, string> SignatureToCluster,
        FrozenDictionary<string, SpectralFeatures> SpectralCache)
    {
        public static readonly ClusterSnapshot Empty = new(
            FrozenDictionary<string, BotCluster>.Empty,
            FrozenDictionary<string, string>.Empty,
            FrozenDictionary<string, SpectralFeatures>.Empty);
    }
}
