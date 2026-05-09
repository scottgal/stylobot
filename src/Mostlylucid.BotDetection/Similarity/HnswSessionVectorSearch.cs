using System.Text.Json.Serialization;
using HNSW.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using System.Text.Json;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     File-backed HNSW index for session behavioral vectors.
///     Mirrors HnswFileSimilaritySearch but targets the 129-dim Markov/fingerprint session vectors
///     produced by SessionVectorizer. Persisted as sessions.meta.json + sessions.vectors.json +
///     sessions.hnsw alongside the signature and intent indexes.
///
///     Graph parameters: M=16, cosine distance (SIMD), thread-safe.
///     Pending vectors accumulate until RebuildThreshold (100) then merge into the graph.
///     Auto-saves every 5 minutes; flushes synchronously on Dispose.
/// </summary>
public sealed class HnswSessionVectorSearch : ISessionVectorSearch, IDisposable
{
    private const int DefaultM = 16;
    private const int MinVectorsForGraph = 5;
    private const int RebuildThreshold = 100;
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromMinutes(5);

    private static readonly int ExpectedDimensions = SessionVectorizer.Dimensions;

    private static readonly SmallWorldParameters GraphParameters = new()
    {
        M = DefaultM,
        LevelLambda = 1.0 / Math.Log(DefaultM),
        NeighbourHeuristic = NeighbourSelectionHeuristic.SelectSimple
    };

    private readonly string _databasePath;
    private readonly ILogger<HnswSessionVectorSearch> _logger;
    private readonly object _writeLock = new();
    private readonly Timer _autoSaveTimer;
    private readonly Task _loadTask;
    private readonly int _maxVectors;

    private readonly List<float[]> _pendingVectors = [];
    private readonly List<SessionVectorMetadata> _pendingMetadata = [];

    private SmallWorld<float[], float>? _graph;
    private List<float[]> _graphVectors = [];
    private List<SessionVectorMetadata> _metadata = [];
    private bool _dirty;

    // 0 = idle, 1 = rebuild in progress; prevents concurrent rebuilds
    private int _rebuildInProgress;

    public HnswSessionVectorSearch(
        ILogger<HnswSessionVectorSearch> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _maxVectors = options.Value.SelfMaintenance.SessionCacheSize;
        _databasePath = options.Value.DatabasePath
                        ?? Path.Combine(AppContext.BaseDirectory, "botdetection-data");

        Directory.CreateDirectory(_databasePath);

        _autoSaveTimer = new Timer(_ => AutoSave(), null, AutoSaveInterval, AutoSaveInterval);

        _loadTask = Task.Run(async () =>
        {
            try { await LoadAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load session HNSW index on startup"); }
        });
    }

    public int Count
    {
        get { lock (_writeLock) return _graphVectors.Count + _pendingVectors.Count; }
    }

    public async Task<IReadOnlyList<SessionVectorMatch>> FindSimilarAsync(
        float[] vector, int topK = 10, float minSimilarity = 0.70f)
    {
        await _loadTask.ConfigureAwait(false);

        var maxDistance = 1.0f - minSimilarity;
        var results = new List<SessionVectorMatch>();

        SmallWorld<float[], float>? graph;
        List<SessionVectorMetadata> metadata;
        List<float[]> pendingVectors;
        List<SessionVectorMetadata> pendingMeta;

        lock (_writeLock)
        {
            graph = _graph;
            metadata = _metadata;
            pendingVectors = [.. _pendingVectors];
            pendingMeta = [.. _pendingMetadata];
        }

        if (graph is not null && metadata.Count >= MinVectorsForGraph)
        {
            try
            {
                var knnResults = graph.KNNSearch(vector, topK);
                foreach (var result in knnResults)
                {
                    if (result.Distance > maxDistance) continue;
                    var idx = result.Id;
                    if (idx >= 0 && idx < metadata.Count)
                        results.Add(new SessionVectorMatch(metadata[idx].Signature, 1.0f - result.Distance));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session HNSW search failed");
            }
        }

        // Brute-force scan over pending vectors (small set, bounded by RebuildThreshold)
        for (var i = 0; i < pendingVectors.Count; i++)
        {
            var distance = CosineDistance.SIMD(vector, pendingVectors[i]);
            if (distance <= maxDistance)
                results.Add(new SessionVectorMatch(pendingMeta[i].Signature, 1.0f - distance));
        }

        results.Sort(static (a, b) => b.Similarity.CompareTo(a.Similarity));
        if (results.Count > topK) results.RemoveRange(topK, results.Count - topK);

        return results;
    }

    public Task AddAsync(float[] vector, string signature, bool isBot, double botProbability,
        float[]? velocityVector = null,
        float[]? frequencyFingerprint = null,
        float[]? driftVector = null)
    {
        if (vector.Length != ExpectedDimensions)
        {
            _logger.LogDebug(
                "Skipping session vector with wrong dimension {Actual} (expected {Expected}) — schema mismatch",
                vector.Length, ExpectedDimensions);
            return Task.CompletedTask;
        }

        if (!IsValidVector(vector))
        {
            _logger.LogDebug("Skipping zero-norm session vector for {Signature}", signature);
            return Task.CompletedTask;
        }

        var meta = new SessionVectorMetadata
        {
            Signature = signature,
            IsBot = isBot,
            BotProbability = botProbability,
            Timestamp = DateTimeOffset.UtcNow,
            VelocityVector = velocityVector,
            VelocityMagnitude = velocityVector != null
                ? Analysis.SessionVectorizer.VelocityMagnitude(velocityVector)
                : 0f,
            FrequencyFingerprint = frequencyFingerprint,
            DriftVector = driftVector
        };

        bool triggerRebuild;
        lock (_writeLock)
        {
            _pendingVectors.Add(vector);
            _pendingMetadata.Add(meta);
            _dirty = true;
            triggerRebuild = _pendingVectors.Count >= RebuildThreshold;
        }
        if (triggerRebuild && Interlocked.CompareExchange(ref _rebuildInProgress, 1, 0) == 0)
            _ = Task.Run(RebuildGraphOffLock);
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        List<float[]> allVectors;
        List<SessionVectorMetadata> allMetadata;
        SmallWorld<float[], float>? graph;

        lock (_writeLock)
        {
            if (!_dirty) return;
            if (_pendingVectors.Count > 0) RebuildGraphLocked();
            allVectors = [.. _graphVectors];
            allMetadata = [.. _metadata];
            graph = _graph;
            _dirty = false;
        }

        if (allVectors.Count == 0) return;

        try
        {
            var metaPath = Path.Combine(_databasePath, "sessions.meta.json");
            var graphPath = Path.Combine(_databasePath, "sessions.hnsw");
            var vectorPath = Path.Combine(_databasePath, "sessions.vectors.json");

            var metaJson = JsonSerializer.Serialize(allMetadata, SessionMetadataJsonContext.Default.ListSessionVectorMetadata);
            var vectorsJson = JsonSerializer.Serialize(allVectors, VectorJsonContext.Default.ListSingleArray);

            await Task.WhenAll(
                File.WriteAllTextAsync(metaPath, metaJson),
                File.WriteAllTextAsync(vectorPath, vectorsJson));

            if (graph is not null)
            {
                await using var fs = File.Create(graphPath);
                graph.SerializeGraph(fs);
            }

            _logger.LogDebug("Saved session HNSW index: {Count} vectors", allVectors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save session HNSW index");
        }
    }

    public async Task LoadAsync()
    {
        var metaPath = Path.Combine(_databasePath, "sessions.meta.json");
        var graphPath = Path.Combine(_databasePath, "sessions.hnsw");
        var vectorPath = Path.Combine(_databasePath, "sessions.vectors.json");

        if (!File.Exists(metaPath) || !File.Exists(vectorPath))
        {
            _logger.LogDebug("No existing session HNSW index at {Path}", _databasePath);
            return;
        }

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaPath);
            var loadedMetadata = JsonSerializer.Deserialize(metaJson, SessionMetadataJsonContext.Default.ListSessionVectorMetadata);

            var vectorsJson = await File.ReadAllTextAsync(vectorPath);
            var loadedVectors = JsonSerializer.Deserialize(vectorsJson, VectorJsonContext.Default.ListSingleArray);

            if (loadedMetadata is null || loadedVectors is null || loadedMetadata.Count != loadedVectors.Count)
            {
                _logger.LogWarning("Session HNSW index data is corrupted (count mismatch); rebuilding from SQLite on next startup");
                return;
            }

            // Discard stale vectors from a different schema version
            var validVectors = new List<float[]>(loadedVectors.Count);
            var validMetadata = new List<SessionVectorMetadata>(loadedMetadata.Count);
            for (var i = 0; i < loadedVectors.Count; i++)
            {
                if (loadedVectors[i].Length == ExpectedDimensions)
                {
                    validVectors.Add(loadedVectors[i]);
                    validMetadata.Add(loadedMetadata[i]);
                }
            }

            if (validVectors.Count < loadedVectors.Count)
            {
                _logger.LogWarning(
                    "Discarded {Stale} session vectors with wrong dimension {Dim} (schema change from {Expected})",
                    loadedVectors.Count - validVectors.Count, loadedVectors[0].Length, ExpectedDimensions);
            }

            lock (_writeLock)
            {
                _graphVectors = validVectors;
                _metadata = validMetadata;

                if (_graphVectors.Count < MinVectorsForGraph)
                {
                    _logger.LogDebug("Loaded {Count} session vectors (below graph threshold)", _graphVectors.Count);
                    return;
                }

                // Fast path: deserialize saved HNSW binary
                if (File.Exists(graphPath))
                {
                    try
                    {
                        using var fs = File.OpenRead(graphPath);
                        var (graph, _) = SmallWorld<float[], float>.DeserializeGraph(
                            _graphVectors,
                            CosineDistance.SIMD,
                            DefaultRandomGenerator.Instance,
                            fs,
                            threadSafe: true);
                        _graph = graph;

                        _logger.LogInformation(
                            "Session HNSW index loaded: {Count} vectors (from serialized graph)",
                            _graphVectors.Count);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "Failed to deserialize session HNSW graph (stale format), rebuilding. Error: {Message}",
                            ex.Message);
                        try { File.Delete(graphPath); } catch { /* best-effort */ }
                    }
                }

                // Slow path: rebuild from vectors
                BuildGraphFromVectorsLocked();
                _dirty = true;
                _logger.LogInformation("Session HNSW index rebuilt: {Count} vectors", _graphVectors.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session HNSW index from {Path}", _databasePath);
        }
    }

    public async Task<IReadOnlyList<SessionVectorMatch>> FindSimilarMahalanobisAsync(
        float[] vector, int topK = 10, float maxDistance = 5.0f)
    {
        await _loadTask.ConfigureAwait(false);

        List<(float[] Vector, SessionVectorMetadata Metadata)> allEntries;
        lock (_writeLock)
        {
            allEntries = new List<(float[], SessionVectorMetadata)>(_graphVectors.Count + _pendingVectors.Count);
            for (var i = 0; i < _graphVectors.Count; i++) allEntries.Add((_graphVectors[i], _metadata[i]));
            for (var i = 0; i < _pendingVectors.Count; i++) allEntries.Add((_pendingVectors[i], _pendingMetadata[i]));
        }

        // Score each entry: use Mahalanobis for centroid entries (CompressionLevel >= 1 with variance),
        // cosine similarity converted to distance for L0 entries.
        var scored = new List<(float Distance, string Signature)>(allEntries.Count);
        foreach (var (v, meta) in allEntries)
        {
            float dist;
            if (meta.CompressionLevel >= 1 && meta.VarianceVector is { Length: > 0 })
            {
                dist = Analysis.SessionVectorizer.MahalanobisDistance(vector, v, meta.VarianceVector);
            }
            else
            {
                // Cosine distance for L0 entries
                dist = CosineDistance.SIMD(vector, v);
                // Convert to approximate Euclidean-like scale; cosine dist [0,2], Mahalanobis unbounded
                dist *= 3f; // Rough scale-matching so both can be compared with maxDistance
            }

            if (dist <= maxDistance)
                scored.Add((dist, meta.Signature));
        }

        scored.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        if (scored.Count > topK) scored.RemoveRange(topK, scored.Count - topK);

        // Convert distance back to similarity for the SessionVectorMatch interface
        return scored
            .Select(x => new SessionVectorMatch(x.Signature, Math.Max(0f, 1f - x.Distance / (maxDistance + 1f))))
            .ToList();
    }

    public Task<IReadOnlyList<GhostCentroidMatch>> FindGhostCentroidsAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.75f)
    {
        var snapshot = GetAllVectorsSnapshot();
        var results = new List<(float Sim, GhostCentroidMatch Match)>(snapshot.Count);

        foreach (var (v, meta) in snapshot)
        {
            if (meta.CompressionLevel < 1) continue; // L0 sessions are not ghost shapes
            var sim = 1f - CosineDistance.SIMD(vector, v);
            if (sim < minSimilarity) continue;
            results.Add((sim, new GhostCentroidMatch(
                FamilyId: meta.Signature,
                Similarity: sim,
                CompressionLevel: meta.CompressionLevel,
                IsBot: meta.IsBot,
                BotProbability: meta.BotProbability,
                VelocityVector: meta.VelocityVector,
                VarianceVector: meta.VarianceVector,
                FrequencyFingerprint: meta.FrequencyFingerprint)));
        }

        results.Sort(static (a, b) => b.Sim.CompareTo(a.Sim));
        if (results.Count > topK) results.RemoveRange(topK, results.Count - topK);

        return Task.FromResult<IReadOnlyList<GhostCentroidMatch>>(
            results.Select(r => r.Match).ToList());
    }

    public IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)> GetAllVectorsSnapshot()
    {
        lock (_writeLock)
        {
            var result = new List<(float[], SessionVectorMetadata)>(_graphVectors.Count + _pendingVectors.Count);
            for (var i = 0; i < _graphVectors.Count; i++)
                result.Add((_graphVectors[i], _metadata[i]));
            for (var i = 0; i < _pendingVectors.Count; i++)
                result.Add((_pendingVectors[i], _pendingMetadata[i]));
            return result;
        }
    }

    public async Task ReplaceAllAsync(IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items)
    {
        lock (_writeLock)
        {
            _pendingVectors.Clear();
            _pendingMetadata.Clear();
            _graphVectors = items.Select(x => x.Vector).ToList();
            _metadata = items.Select(x => x.Meta).ToList();
            _dirty = true;

            if (_graphVectors.Count >= MinVectorsForGraph)
                BuildGraphFromVectorsLocked();
            else
                _graph = null;
        }

        await SaveAsync().ConfigureAwait(false);
        _logger.LogInformation("Session HNSW index replaced with {Count} compacted vectors", items.Count);
    }

    public void Dispose()
    {
        _autoSaveTimer.Dispose();
        try
        {
            if (!SaveAsync().Wait(TimeSpan.FromSeconds(5)))
                _logger.LogWarning("Session HNSW index save timed out on shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save session HNSW index on shutdown");
        }
    }

    private void RebuildGraphOffLock()
    {
        try
        {
            List<float[]> allVectors;
            lock (_writeLock)
            {
                if (_pendingVectors.Count == 0) return;
                _graphVectors.AddRange(_pendingVectors);
                _metadata.AddRange(_pendingMetadata);
                _pendingVectors.Clear();
                _pendingMetadata.Clear();
                allVectors = [.. _graphVectors];
            }
            if (allVectors.Count < MinVectorsForGraph) return;
            var newGraph = new SmallWorld<float[], float>(
                CosineDistance.SIMD, DefaultRandomGenerator.Instance, GraphParameters, threadSafe: true);
            newGraph.AddItems(allVectors.Where(IsValidVector).ToList(), progressReporter: null);
            lock (_writeLock)
            {
                _graph = newGraph;
                _dirty = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background session HNSW rebuild failed");
        }
        finally { Interlocked.Exchange(ref _rebuildInProgress, 0); }
    }

    private static bool IsValidVector(float[] v)
    {
        if (v.Length == 0) return false;
        var normSq = 0f;
        foreach (var x in v) normSq += x * x;
        return normSq > 0f && !float.IsNaN(normSq) && !float.IsInfinity(normSq);
    }

    private void TrimToMaxLocked()
    {
        var excess = _graphVectors.Count - _maxVectors;
        if (excess <= 0) return;
        _graphVectors.RemoveRange(0, excess);
        _metadata.RemoveRange(0, excess);
        _logger.LogDebug("HNSW session index trimmed by {Excess} (cap={Max})", excess, _maxVectors);
    }

    private void RebuildGraphLocked()
    {
        if (_pendingVectors.Count == 0) return;

        _graphVectors.AddRange(_pendingVectors);
        _metadata.AddRange(_pendingMetadata);
        _pendingVectors.Clear();
        _pendingMetadata.Clear();
        TrimToMaxLocked();

        if (_graphVectors.Count >= MinVectorsForGraph)
            BuildGraphFromVectorsLocked();
    }

    private void BuildGraphFromVectorsLocked()
    {
        try
        {
            var graph = new SmallWorld<float[], float>(
                CosineDistance.SIMD,
                DefaultRandomGenerator.Instance,
                GraphParameters,
                threadSafe: true);

            var validVectors = _graphVectors.Where(IsValidVector).ToList();
            if (validVectors.Count < MinVectorsForGraph) return;
            graph.AddItems(validVectors, progressReporter: null);
            _graph = graph;

            _logger.LogDebug("Built session HNSW graph: {Count} vectors", validVectors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build session HNSW graph");
            _graph = null;
        }
    }

    private void AutoSave()
    {
        try
        {
            bool isDirty;
            lock (_writeLock) isDirty = _dirty;
            if (isDirty) _ = SaveSafeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session HNSW auto-save failed");
        }
    }

    private async Task SaveSafeAsync()
    {
        try { await SaveAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Async session HNSW auto-save failed"); }
    }
}

public class SessionVectorMetadata
{
    public string Signature { get; set; } = string.Empty;
    public bool IsBot { get; set; }
    public double BotProbability { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    ///     Velocity vector from the previous session for this signature (current - previous).
    ///     Null for first sessions or when previous session was unavailable.
    ///     Preserved through compaction as a velocity centroid (average drift direction).
    /// </summary>
    public float[]? VelocityVector { get; set; }

    /// <summary>Cached L2 magnitude of VelocityVector (0 when null).</summary>
    public float VelocityMagnitude { get; set; }

    /// <summary>Compression level: 0=full L0 session, 1=per-signature centroid, 2=per-cluster centroid.</summary>
    public int CompressionLevel { get; set; }

    /// <summary>Priority score used by VectorCompactionService to decide which entries to compress first.</summary>
    public double Priority { get; set; } = 1.0;

    /// <summary>Cluster ID if this entry belongs to a detected bot cluster (set during L2 compaction).</summary>
    public string? ClusterId { get; set; }

    /// <summary>
    ///     Per-dimension variance of the vectors that were compacted into this centroid.
    ///     Non-null only for L1/L2 centroid entries (CompressionLevel >= 1).
    ///     Used for Mahalanobis ghost matching: dimensions with low variance are
    ///     discriminative; deviations there are anomalous even if small.
    /// </summary>
    public float[]? VarianceVector { get; set; }

    /// <summary>
    ///     Frequency fingerprint: autocorrelation at 8 lag scales.
    ///     Captures temporal rhythm independent of behavioral path.
    ///     Two campaigns with the same crawl loop will score high similarity
    ///     here even if their Markov path has rotated.
    /// </summary>
    public float[]? FrequencyFingerprint { get; set; }

    /// <summary>
    ///     Drift vector: behavioral trajectory direction in 129-dim space.
    ///     Slope of linear regression over the most recent N session vectors.
    ///     Non-null when at least 3 sessions exist for this signature.
    /// </summary>
    public float[]? DriftVector { get; set; }
}

[JsonSerializable(typeof(List<SessionVectorMetadata>))]
internal partial class SessionMetadataJsonContext : JsonSerializerContext;
