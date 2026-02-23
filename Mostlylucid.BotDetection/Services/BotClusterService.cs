using System.Collections.Frozen;
using System.Numerics.Tensors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Clustering;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that periodically clusters signatures from the SignatureCoordinator's
///     sliding window. Discovers bot products (same bot software) and bot networks (coordinated
///     campaigns). Uses FFT-based spectral analysis to detect shared timing patterns.
///
///     Supports two clustering algorithms (configurable):
///     - Leiden (default): CPM-based community detection with refinement step for guaranteed
///       well-connected communities. Better quality than Label Propagation.
///     - Label Propagation (fallback): Fast, simple algorithm for compatibility.
///
///     Optionally blends semantic embeddings (ONNX all-MiniLM-L6-v2) with heuristic features
///     for improved similarity scoring.
///
///     After clustering completes, fires an event for background LLM description generation
///     (never blocks the request pipeline).
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
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly MarkovTracker? _markovTracker;
    private readonly AdaptiveSimilarityWeighter? _adaptiveWeighter;

    // Event-driven trigger: counts new bot detections since last clustering run
    private int _botDetectionsSinceLastRun;
    private readonly SemaphoreSlim _triggerSignal = new(0, 1);

    /// <summary>
    ///     Fired after each clustering run with the new clusters.
    ///     Used by BotClusterDescriptionService to generate LLM descriptions in background.
    /// </summary>
    public event Action<IReadOnlyList<BotCluster>, IReadOnlyList<SignatureBehavior>>? ClustersUpdated;

    public BotClusterService(
        ILogger<BotClusterService> logger,
        IOptions<BotDetectionOptions> options,
        SignatureCoordinator signatureCoordinator,
        IEmbeddingProvider? embeddingProvider = null,
        MarkovTracker? markovTracker = null,
        AdaptiveSimilarityWeighter? adaptiveWeighter = null)
    {
        _logger = logger;
        _options = options.Value.Cluster;
        _signatureCoordinator = signatureCoordinator;
        _embeddingProvider = embeddingProvider;
        _markovTracker = markovTracker;
        _adaptiveWeighter = adaptiveWeighter;

        if (_options.EnableSemanticEmbeddings && _embeddingProvider != null)
            _logger.LogInformation(
                "Semantic embeddings enabled for clustering (dimension={Dim}, weight={Weight:F2})",
                _embeddingProvider.Dimension, _options.SemanticWeight);
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

    /// <summary>
    ///     Update a cluster's description (called by BotClusterDescriptionService after LLM generates it).
    ///     Atomically replaces the cluster in the snapshot with the updated description.
    /// </summary>
    public void UpdateClusterDescription(string clusterId, string label, string description)
    {
        var snapshot = _snapshot;
        if (!snapshot.Clusters.TryGetValue(clusterId, out var existing))
            return;

        var updated = existing with
        {
            Label = label,
            Description = description
        };

        // Rebuild the clusters dictionary with the updated cluster
        var newClusters = snapshot.Clusters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        newClusters[clusterId] = updated;

        _snapshot = new ClusterSnapshot(
            newClusters.ToFrozenDictionary(),
            snapshot.SignatureToCluster,
            snapshot.SpectralCache);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var algorithm = _options.Algorithm.Equals("leiden", StringComparison.OrdinalIgnoreCase)
            ? "Leiden" : "LabelPropagation";

        _logger.LogInformation(
            "BotClusterService started (algorithm={Algorithm}, interval={Interval}s, threshold={Threshold}, trigger={Trigger} detections)",
            algorithm,
            _options.ClusterIntervalSeconds,
            _options.SimilarityThreshold,
            _options.MinBotDetectionsToTrigger);

        // Wait a bit before first run to let the system warm up
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

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

        // Cluster ALL traffic — Leiden naturally forms communities for bots AND humans
        var behaviors = allBehaviors.ToList();

        if (behaviors.Count < _options.MinClusterSize)
        {
            _logger.LogInformation(
                "Clustering: {Count} signatures below MinClusterSize={Min}. " +
                "Top bot probs: [{TopProbs}]",
                behaviors.Count, _options.MinClusterSize,
                string.Join(", ", allBehaviors
                    .OrderByDescending(b => b.AverageBotProbability)
                    .Take(5)
                    .Select(b => $"{b.AverageBotProbability:F2}")));
            return;
        }

        // 2. Build feature vectors (includes spectral analysis + optional semantic embeddings)
        var features = BuildFeatureVectors(behaviors);

        // 2.5. Build spectral cache
        var spectralBuilder = new Dictionary<string, SpectralFeatures>();
        foreach (var f in features)
        {
            if (f.Spectral is { HasSufficientData: true })
                spectralBuilder[f.Signature] = f.Spectral;
        }

        // 2.6. Compute adaptive weights for this cycle
        if (_adaptiveWeighter != null && features.Count >= 3)
        {
            _currentWeights = _adaptiveWeighter.ComputeWeights(features);
            _logger.LogDebug("Adaptive weights computed for {Count} features, top: {Top}",
                _currentWeights.Count,
                string.Join(", ", _currentWeights
                    .OrderByDescending(w => w.Value)
                    .Take(3)
                    .Select(w => $"{w.Key}={w.Value:P1}")));
        }
        else
        {
            _currentWeights = null; // Fall back to defaults
        }

        // 3. Compute similarity matrix and build graph
        var adjacency = BuildSimilarityGraph(features);

        // Log graph density for diagnostics
        var edgeCount = adjacency.Values.Sum(e => e.Count) / 2;
        var maxEdges = features.Count * (features.Count - 1) / 2;
        _logger.LogInformation(
            "Clustering graph: {Nodes} nodes, {Edges}/{MaxEdges} edges (density={Density:P1}), threshold={Threshold}",
            features.Count, edgeCount, maxEdges,
            maxEdges > 0 ? (double)edgeCount / maxEdges : 0,
            _options.SimilarityThreshold);

        // 4. Run community detection algorithm
        int[] labels;
        var useLeiden = _options.Algorithm.Equals("leiden", StringComparison.OrdinalIgnoreCase);

        if (useLeiden)
        {
            labels = LeidenClustering.FindCommunities(
                adjacency, features.Count,
                _options.LeidenResolution,
                _options.MaxIterations);
            _logger.LogDebug("Leiden clustering produced {Communities} communities from {Nodes} nodes",
                labels.Distinct().Count(), features.Count);
        }
        else
        {
            labels = LabelPropagation(adjacency, features.Count);
        }

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

        var productCount = newClusters.Values.Count(c => c.Type == BotClusterType.BotProduct);
        var networkCount = newClusters.Values.Count(c => c.Type == BotClusterType.BotNetwork);
        var emergentCount = newClusters.Values.Count(c => c.Type == BotClusterType.Emergent);
        var humanCount = newClusters.Values.Count(c => c.Type == BotClusterType.HumanTraffic);
        var mixedCount = newClusters.Values.Count(c => c.Type == BotClusterType.Mixed);
        _logger.LogInformation(
            "BotClusterService: discovered {Total} clusters ({Product} product, {Network} network, {Emergent} emergent, {Human} human, {Mixed} mixed) from {Signatures} signatures using {Algorithm}",
            newClusters.Count, productCount, networkCount, emergentCount, humanCount, mixedCount,
            behaviors.Count, useLeiden ? "Leiden" : "LabelPropagation");

        // Always fire event — even when clusters dissolve, so callbacks can update
        ClustersUpdated?.Invoke(newClusters.Values.ToList(), behaviors);
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

        /// <summary>384-dim semantic embedding from ONNX model, or null if unavailable.</summary>
        public float[]? SemanticEmbedding { get; init; }

        // Enriched geo fields
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public string? ContinentCode { get; init; }
        public string? RegionCode { get; init; }
        public bool IsVpn { get; init; }

        // Markov drift signals
        public double SelfDrift { get; init; }
        public double HumanDrift { get; init; }
        public double TransitionNovelty { get; init; }
        public double EntropyDelta { get; init; }
        public double LoopScore { get; init; }
        public double SequenceSurprise { get; init; }

        // Intent / Threat signals (from IntentContributor)
        public string? IntentCategory { get; init; }
        public double ThreatScore { get; init; }
    }

    internal List<FeatureVector> BuildFeatureVectors(IReadOnlyList<SignatureBehavior> behaviors)
    {
        var useEmbeddings = _options.EnableSemanticEmbeddings
                            && _embeddingProvider is { IsAvailable: true };

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

            // Generate semantic embedding from behavioral text (privacy-safe: no raw IP/UA)
            float[]? embedding = null;
            if (useEmbeddings)
            {
                var topPaths = string.Join(",",
                    b.Requests.GroupBy(r => r.Path)
                        .OrderByDescending(g => g.Count())
                        .Take(5)
                        .Select(g => g.Key));

                // Derive path-based intent heuristics from request history
                var intentPart = "";
                var probePaths = b.Requests.Count(r =>
                    r.Path.Contains(".env", StringComparison.OrdinalIgnoreCase) ||
                    r.Path.Contains("wp-login", StringComparison.OrdinalIgnoreCase) ||
                    r.Path.Contains("/admin", StringComparison.OrdinalIgnoreCase) ||
                    r.Path.Contains("/.git", StringComparison.OrdinalIgnoreCase) ||
                    r.Path.Contains("phpmyadmin", StringComparison.OrdinalIgnoreCase));
                if (probePaths > 0)
                    intentPart += $" | PROBE_PATHS:{probePaths}";
                if (b.AverageBotProbability > 0.7)
                    intentPart += $" | HIGH_BOT_PROB:{b.AverageBotProbability:F2}";
                if (b.IsAberrant)
                    intentPart += " | ABERRANT";

                var embeddingText =
                    $"RATE:{requestRate:F1}/min | PATHS:{topPaths} | " +
                    $"ENTROPY:{b.PathEntropy:F2} | TIMING_CV:{b.TimingCoefficient:F2} | " +
                    $"COUNTRY:{b.CountryCode ?? "?"} | ASN:{b.Asn ?? "?"} | " +
                    $"DC:{b.IsDatacenter} | BOT_PROB:{b.AverageBotProbability:F2}" +
                    intentPart;

                embedding = _embeddingProvider!.GenerateEmbedding(embeddingText);
            }

            // Get Markov drift signals (single call per signature)
            var drift = _markovTracker?.GetDriftSignals(
                b.Signature, b.IsDatacenter, false) ?? Markov.DriftSignals.Empty;

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
                Intervals = intervals,
                SemanticEmbedding = embedding,
                // Enriched geo
                Latitude = b.Latitude,
                Longitude = b.Longitude,
                ContinentCode = b.ContinentCode,
                RegionCode = b.RegionCode,
                IsVpn = b.IsVpn,
                // Markov drift (populated by MarkovTracker if available)
                SelfDrift = drift.SelfDrift,
                HumanDrift = drift.HumanDrift,
                TransitionNovelty = drift.TransitionNovelty,
                EntropyDelta = drift.EntropyDelta,
                LoopScore = drift.LoopScore,
                SequenceSurprise = drift.SequenceSurprise,
                // Intent / Threat signals (populated when intent HNSW has data for this signature)
                IntentCategory = null,
                ThreatScore = 0.0
            };
        }).ToList();
    }

    #endregion

    #region Similarity

    /// <summary>
    ///     Cached adaptive weights for current clustering cycle.
    ///     Recomputed each RunClustering() call.
    /// </summary>
    private Dictionary<string, double>? _currentWeights;

    internal double ComputeBlendedSimilarity(FeatureVector a, FeatureVector b)
    {
        var heuristicSim = ComputeSimilarity(a, b, _currentWeights);

        // If both have semantic embeddings, blend with heuristic
        if (a.SemanticEmbedding != null && b.SemanticEmbedding != null)
        {
            var cosineSim = CosineSimilarity(a.SemanticEmbedding, b.SemanticEmbedding);
            // Blend: (1-w)*heuristic + w*semantic
            var w = _options.SemanticWeight;
            return (1.0 - w) * heuristicSim + w * cosineSim;
        }

        return heuristicSim;
    }

    internal static double ComputeSimilarity(FeatureVector a, FeatureVector b,
        Dictionary<string, double>? weights = null)
    {
        weights ??= AdaptiveSimilarityWeighter.GetDefaultWeights();

        // Continuous features: normalized absolute difference
        var timingSim = 1.0 - NormalizedDiff(a.TimingRegularity, b.TimingRegularity);
        var rateSim = 1.0 - NormalizedDiff(a.RequestRate, b.RequestRate);
        var pathDivSim = 1.0 - NormalizedDiff(a.PathDiversity, b.PathDiversity);
        var entropySim = 1.0 - NormalizedDiff(a.PathEntropy, b.PathEntropy);
        var botProbSim = 1.0 - NormalizedDiff(a.AvgBotProbability, b.AvgBotProbability);

        // Geographic proximity: hierarchical scoring instead of binary country match
        var geoSim = ComputeGeoSimilarity(a, b);

        // Categorical features
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

        // Markov drift signals: similarity = 1 - normalized diff
        var selfDriftSim = 1.0 - NormalizedDiff(a.SelfDrift, b.SelfDrift);
        var humanDriftSim = 1.0 - NormalizedDiff(a.HumanDrift, b.HumanDrift);
        var loopScoreSim = 1.0 - NormalizedDiff(a.LoopScore, b.LoopScore);
        var surpriseSim = 1.0 - NormalizedDiff(a.SequenceSurprise, b.SequenceSurprise);
        var noveltySim = 1.0 - NormalizedDiff(a.TransitionNovelty, b.TransitionNovelty);
        var entropyDeltaSim = 1.0 - NormalizedDiff(Math.Abs(a.EntropyDelta), Math.Abs(b.EntropyDelta));

        // Weighted sum using adaptive weights (sum to ~1.0)
        return timingSim * weights.GetValueOrDefault("timing", 0.08) +
               rateSim * weights.GetValueOrDefault("rate", 0.07) +
               pathDivSim * weights.GetValueOrDefault("pathDiv", 0.05) +
               entropySim * weights.GetValueOrDefault("entropy", 0.05) +
               botProbSim * weights.GetValueOrDefault("botProb", 0.08) +
               geoSim * weights.GetValueOrDefault("geo", 0.08) +
               datacenterSim * weights.GetValueOrDefault("datacenter", 0.05) +
               asnSim * weights.GetValueOrDefault("asn", 0.06) +
               spectralEntropySim * weights.GetValueOrDefault("spectralEntropy", 0.06) +
               harmonicSim * weights.GetValueOrDefault("harmonic", 0.04) +
               peakToAvgSim * weights.GetValueOrDefault("peakToAvg", 0.05) +
               dominantFreqSim * weights.GetValueOrDefault("dominantFreq", 0.03) +
               selfDriftSim * weights.GetValueOrDefault("selfDrift", 0.06) +
               humanDriftSim * weights.GetValueOrDefault("humanDrift", 0.06) +
               loopScoreSim * weights.GetValueOrDefault("loopScore", 0.05) +
               surpriseSim * weights.GetValueOrDefault("surprise", 0.05) +
               noveltySim * weights.GetValueOrDefault("novelty", 0.04) +
               entropyDeltaSim * weights.GetValueOrDefault("entropyDelta", 0.04);
    }

    /// <summary>
    ///     Hierarchical geographic proximity scoring using Haversine distance.
    ///     Same city ≈ 1.0, same region ≈ 0.85, same country ≈ 0.7,
    ///     within 500km ≈ 0.6, same continent ≈ 0.4, distant ≈ 0.0.
    /// </summary>
    internal static double ComputeGeoSimilarity(FeatureVector a, FeatureVector b)
    {
        // If both have lat/lon, use Haversine distance for fine-grained proximity
        if (a.Latitude.HasValue && a.Longitude.HasValue &&
            b.Latitude.HasValue && b.Longitude.HasValue)
        {
            var distanceKm = HaversineDistanceKm(
                a.Latitude.Value, a.Longitude.Value,
                b.Latitude.Value, b.Longitude.Value);

            // Same city (< 50km)
            if (distanceKm < 50) return 1.0;
            // Same metro area (< 150km)
            if (distanceKm < 150) return 0.9;
            // Same region (< 300km)
            if (distanceKm < 300) return 0.85;
            // Nearby (< 500km)
            if (distanceKm < 500) return 0.7;
            // Same time zone range (< 1500km)
            if (distanceKm < 1500) return 0.5;
            // Distant (< 5000km)
            if (distanceKm < 5000) return 0.3;
            // Very distant
            return 0.1;
        }

        // Fallback to hierarchical categorical matching (no lat/lon available)
        var sameCountry = !string.IsNullOrEmpty(a.CountryCode) &&
                          string.Equals(a.CountryCode, b.CountryCode, StringComparison.OrdinalIgnoreCase);

        if (sameCountry)
        {
            // Same country + same region = strong match
            if (!string.IsNullOrEmpty(a.RegionCode) && !string.IsNullOrEmpty(b.RegionCode) &&
                string.Equals(a.RegionCode, b.RegionCode, StringComparison.OrdinalIgnoreCase))
                return 1.0;

            // Same country, no region data or different regions
            // When neither has lat/lon, country is our best data — score high
            var neitherHasLatLon = !a.Latitude.HasValue && !b.Latitude.HasValue;
            return neitherHasLatLon ? 1.0 : 0.7;
        }

        if (!string.IsNullOrEmpty(a.ContinentCode) &&
            string.Equals(a.ContinentCode, b.ContinentCode, StringComparison.OrdinalIgnoreCase))
            return 0.4;

        // No geo data available for either — neutral (no penalty, no bonus)
        if (string.IsNullOrEmpty(a.CountryCode) && string.IsNullOrEmpty(b.CountryCode))
            return 1.0;

        // One has geo, other doesn't — slight penalty
        return 0.3;
    }

    /// <summary>
    ///     Haversine formula for great-circle distance between two lat/lon points.
    ///     Returns distance in kilometers.
    /// </summary>
    private static double HaversineDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusKm * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double CosineSimilarity(float[] a, float[] b)
    {
        var dot = TensorPrimitives.Dot(a, b);
        // Embeddings are already L2-normalized by OnnxEmbeddingProvider
        // so cosine similarity = dot product, mapped from [-1,1] to [0,1]
        return (dot + 1.0) / 2.0;
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
    ///     When semantic embeddings are available, similarity is blended (configurable weight).
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
                var sim = ComputeBlendedSimilarity(features[i], features[j]);

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

    #region Label Propagation (fallback)

    /// <summary>
    ///     Label Propagation algorithm: each node adopts the most frequent label
    ///     among its neighbors (weighted by similarity). O(V+E) per iteration.
    ///     Used as fallback when Leiden is not selected.
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

        // Average similarity across connected pairs only (not all possible pairs).
        // Dividing by all-pairs dilutes the average because non-edges are below the
        // similarity threshold, not zero-similarity — using connected pairs gives
        // a truthful representation of intra-cluster cohesion.
        var totalPossiblePairs = memberIndices.Count * (memberIndices.Count - 1) / 2;
        var avgSimilarity = simCount > 0 ? totalSim / simCount : 0;
        var connectedness = totalPossiblePairs > 0 ? (double)simCount / totalPossiblePairs : 0;

        // Compute temporal density: what fraction of members were active in the same 5-minute window?
        var temporalDensity = ComputeTemporalDensity(members);

        // Average bot probability
        var avgBotProb = members.Average(m => m.AverageBotProbability);

        // Determine cluster type based on average bot probability and behavioral signals
        var uniqueSignatures = new HashSet<string>(members.Select(m => m.Signature));

        BotClusterType clusterType;
        if (avgBotProb < 0.3)
        {
            // Predominantly human traffic
            clusterType = BotClusterType.HumanTraffic;
        }
        else if (avgBotProb < 0.5)
        {
            // Mixed — some borderline, some human
            clusterType = BotClusterType.Mixed;
        }
        else if (avgSimilarity >= _options.ProductSimilarityThreshold)
        {
            // High behavioral similarity + botlike = same bot software (Bot Product)
            clusterType = BotClusterType.BotProduct;
        }
        else if (temporalDensity >= _options.NetworkTemporalDensityThreshold &&
                 avgSimilarity >= 0.5)
        {
            // Temporally correlated with moderate similarity = coordinated campaign (Bot Network)
            clusterType = BotClusterType.BotNetwork;
        }
        else
        {
            // Emergent cluster: community detection grouped these together but they
            // don't yet meet strict BotProduct/BotNetwork thresholds. Still valuable —
            // these may be evolving campaigns, new bot software, or DDoS participants.
            clusterType = BotClusterType.Emergent;
        }

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

        // Compute dominant intent and average threat score from feature vectors
        var intentCategories = features
            .Where(f => f.IntentCategory != null)
            .GroupBy(f => f.IntentCategory)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;
        var avgThreatScore = features
            .Where(f => f.ThreatScore > 0)
            .Select(f => f.ThreatScore)
            .DefaultIfEmpty(0.0)
            .Average();

        // Generate heuristic label (LLM description applied asynchronously later)
        var label = GenerateHeuristicLabel(clusterType, avgSimilarity, temporalDensity, avgBotProb, members);

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
            Connectedness = connectedness,
            TemporalDensity = temporalDensity,
            DominantCountry = dominantCountry,
            DominantAsn = dominantAsn,
            Label = label,
            FirstSeen = new DateTimeOffset(members.Min(m => m.FirstSeen), TimeSpan.Zero),
            LastSeen = new DateTimeOffset(members.Max(m => m.LastSeen), TimeSpan.Zero),
            DominantIntent = intentCategories,
            AverageThreatScore = avgThreatScore
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

    /// <summary>
    ///     Heuristic label generation used as default and fallback when LLM is unavailable.
    ///     Renamed from GenerateLabel to clarify this is the non-LLM path.
    /// </summary>
    internal static string GenerateHeuristicLabel(
        BotClusterType type, double similarity, double temporalDensity,
        double avgBotProb, List<SignatureBehavior> members)
    {
        var avgInterval = members.Average(m => m.AverageInterval);
        var avgEntropy = members.Average(m => m.PathEntropy);

        return type switch
        {
            BotClusterType.HumanTraffic => InferHumanLabel(members),
            BotClusterType.Mixed => "Mixed-Traffic",
            BotClusterType.BotProduct => InferBotProductLabel(members, avgInterval, avgEntropy),
            BotClusterType.BotNetwork when temporalDensity > 0.8 => "Burst-Campaign",
            BotClusterType.BotNetwork when members.Count > 10 => "Large-Botnet",
            BotClusterType.BotNetwork => "Coordinated-Campaign",
            BotClusterType.Emergent when avgInterval < 2.0 => "Emerging-Rapid-Pattern",
            BotClusterType.Emergent when avgBotProb > 0.8 => "High-Confidence-Group",
            BotClusterType.Emergent => "Emerging-Pattern",
            _ => "Unknown-Cluster"
        };
    }

    /// <summary>
    ///     Infer a human-readable label for human traffic clusters from UA family, country, and paths.
    /// </summary>
    private static string InferHumanLabel(List<SignatureBehavior> members)
    {
        // Extract dominant UA family from request signals
        var uaFamily = members
            .SelectMany(m => m.Requests)
            .Select(r => r.Signals.TryGetValue("ua.family", out var f) ? f?.ToString() : null)
            .Where(f => !string.IsNullOrEmpty(f))
            .GroupBy(f => f!)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        // Dominant country
        var country = members
            .Where(m => !string.IsNullOrEmpty(m.CountryCode))
            .GroupBy(m => m.CountryCode!)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        // Check if predominantly mobile
        var mobileCount = members
            .SelectMany(m => m.Requests)
            .Count(r => r.Signals.TryGetValue("ua.is_mobile", out var mob) && mob is true or "true" or "True");
        var isMobile = mobileCount > members.Sum(m => m.RequestCount) / 2;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(country))
            parts.Add(country);
        if (!string.IsNullOrEmpty(uaFamily))
            parts.Add(uaFamily);
        if (isMobile)
            parts.Add("Mobile");
        parts.Add("Users");

        return string.Join(" ", parts);
    }

    /// <summary>
    ///     Infer a label for bot product clusters from known bot name signals or UA family.
    /// </summary>
    private static string InferBotProductLabel(List<SignatureBehavior> members, double avgInterval, double avgEntropy)
    {
        // Try to get bot name from signals
        var botName = members
            .SelectMany(m => m.Requests)
            .Select(r => r.Signals.TryGetValue("ua.bot_name", out var n) ? n?.ToString() : null)
            .Where(n => !string.IsNullOrEmpty(n))
            .GroupBy(n => n!)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        if (!string.IsNullOrEmpty(botName))
            return $"{botName} Cluster";

        // Fallback to UA family
        var uaFamily = members
            .SelectMany(m => m.Requests)
            .Select(r => r.Signals.TryGetValue("ua.family", out var f) ? f?.ToString() : null)
            .Where(f => !string.IsNullOrEmpty(f))
            .GroupBy(f => f!)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        if (!string.IsNullOrEmpty(uaFamily))
            return $"{uaFamily} Bot Cluster";

        // Final fallback to behavioral heuristics
        if (avgInterval < 2.0) return "Rapid-Scraper";
        if (avgEntropy > 3.0) return "Deep-Crawler";
        if (avgEntropy < 1.0) return "Targeted-Scanner";
        return "Bot-Software";
    }

    // Keep backward-compatible static method
    internal static string GenerateLabel(
        BotClusterType type, double similarity, double temporalDensity,
        double avgBotProb, List<SignatureBehavior> members)
        => GenerateHeuristicLabel(type, similarity, temporalDensity, avgBotProb, members);

    internal static string ComputeClusterId(List<string> sortedSignatures)
    {
        var combined = string.Join("|", sortedSignatures);
        return $"cluster-{Data.PatternNormalization.ComputeHash(combined)}";
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
