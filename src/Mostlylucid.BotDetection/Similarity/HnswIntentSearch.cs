using System.Text.Json;
using HNSW.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     File-backed HNSW similarity search for intent patterns.
///     Separate index from signature similarity - stores WHAT sessions do (intent vectors)
///     rather than WHO they are (identity vectors). Enables the learning loop where
///     LLM classifications get embedded for future fast-path matching.
/// </summary>
public sealed class HnswIntentSearch : IIntentSimilaritySearch, IDisposable
{
    private const int DefaultM = 16;
    private const int MinVectorsForGraph = 5;
    private const int RebuildThreshold = 50;
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromMinutes(5);

    private static readonly SmallWorldParameters GraphParameters = new()
    {
        M = DefaultM,
        LevelLambda = 1.0 / Math.Log(DefaultM),
        NeighbourHeuristic = NeighbourSelectionHeuristic.SelectSimple
    };

    private readonly string _databasePath;
    private readonly ILogger<HnswIntentSearch> _logger;
    private readonly object _writeLock = new();
    private readonly Timer _autoSaveTimer;
    private readonly Task _loadTask;

    // Pending vectors not yet in the HNSW graph (added since last rebuild)
    private readonly List<float[]> _pendingVectors = [];
    private readonly List<IntentMetadata> _pendingMetadata = [];

    // Current HNSW graph (rebuilt when pending vectors accumulate)
    private SmallWorld<float[], float>? _graph;
    private List<float[]> _graphVectors = [];
    private List<IntentMetadata> _metadata = [];
    private bool _dirty;
    private int _rebuildInProgress;

    public HnswIntentSearch(
        ILogger<HnswIntentSearch> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _databasePath = options.Value.DatabasePath
                        ?? Path.Combine(AppContext.BaseDirectory, "botdetection-data");

        Directory.CreateDirectory(_databasePath);

        _autoSaveTimer = new Timer(_ => AutoSave(), null, AutoSaveInterval, AutoSaveInterval);

        // Load existing data on startup
        _loadTask = Task.Run(async () =>
        {
            try
            {
                await LoadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load intent HNSW index on startup");
            }
        });
    }

    public int Count
    {
        get
        {
            lock (_writeLock)
                return _graphVectors.Count + _pendingVectors.Count;
        }
    }

    public async Task<IReadOnlyList<SimilarIntent>> FindSimilarAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.75f)
    {
        // Ensure startup load completes before first search
        await _loadTask.ConfigureAwait(false);

        if (vector.Length != IntentVectorizer.VectorDimension)
        {
            _logger.LogWarning(
                "Ignoring intent similarity search with incompatible vector length {Length}; expected {Expected}",
                vector.Length,
                IntentVectorizer.VectorDimension);
            return Array.Empty<SimilarIntent>();
        }

        var results = new List<SimilarIntent>();
        var maxDistance = 1.0f - minSimilarity;

        SmallWorld<float[], float>? graph;
        List<IntentMetadata> metadata;
        List<float[]> pendingVectors;
        List<IntentMetadata> pendingMeta;

        lock (_writeLock)
        {
            graph = _graph;
            metadata = _metadata;
            pendingVectors = [.. _pendingVectors];
            pendingMeta = [.. _pendingMetadata];
        }

        // Search the HNSW graph (SIMD-accelerated KNN)
        if (graph is not null && metadata.Count >= MinVectorsForGraph)
        {
            try
            {
                var knnResults = graph.KNNSearch(vector, topK);
                foreach (var result in knnResults)
                {
                    if (result.Distance > maxDistance)
                        continue;

                    var idx = result.Id;
                    if (idx >= 0 && idx < metadata.Count)
                    {
                        var meta = metadata[idx];
                        results.Add(new SimilarIntent(
                            meta.SignatureId,
                            result.Distance,
                            meta.ThreatScore,
                            meta.IntentCategory));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intent HNSW search failed");
            }
        }

        // Brute-force search on pending vectors (small set, SIMD distance)
        for (var i = 0; i < pendingVectors.Count; i++)
        {
            if (pendingVectors[i].Length != vector.Length)
                continue;

            var distance = CosineDistance.SIMD(vector, pendingVectors[i]);
            if (distance <= maxDistance)
            {
                var meta = pendingMeta[i];
                results.Add(new SimilarIntent(
                    meta.SignatureId,
                    distance,
                    meta.ThreatScore,
                    meta.IntentCategory));
            }
        }

        // Sort by distance (closest first) and trim
        results.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        if (results.Count > topK)
            results.RemoveRange(topK, results.Count - topK);

        return (IReadOnlyList<SimilarIntent>)results;
    }

    public Task AddAsync(float[] vector, string signatureId,
        double threatScore, string intentCategory, string? reasoning = null)
    {
        if (vector.Length != IntentVectorizer.VectorDimension)
        {
            _logger.LogWarning(
                "Skipping intent vector for {Signature}: incompatible length {Length}; expected {Expected}",
                signatureId,
                vector.Length,
                IntentVectorizer.VectorDimension);
            return Task.CompletedTask;
        }

        if (!IsValidVector(vector))
        {
            _logger.LogDebug("Skipping zero-norm intent vector for {Signature}", signatureId);
            return Task.CompletedTask;
        }

        var meta = new IntentMetadata
        {
            SignatureId = signatureId,
            ThreatScore = threatScore,
            IntentCategory = intentCategory,
            Reasoning = reasoning,
            Timestamp = DateTimeOffset.UtcNow,
            SchemaVersion = IntentVectorizer.SchemaVersion,
            VectorDimension = IntentVectorizer.VectorDimension
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
        List<IntentMetadata> allMetadata;
        SmallWorld<float[], float>? graph;

        lock (_writeLock)
        {
            if (!_dirty) return;

            if (_pendingVectors.Count > 0)
                RebuildGraphLocked();

            allVectors = [.. _graphVectors];
            allMetadata = [.. _metadata];
            graph = _graph;
            _dirty = false;
        }

        if (allVectors.Count == 0) return;

        try
        {
            var metaPath = Path.Combine(_databasePath, "intent.meta.json");
            var graphPath = Path.Combine(_databasePath, "intent.hnsw");
            var vectorPath = Path.Combine(_databasePath, "intent.vectors.json");

            var metaJson = JsonSerializer.Serialize(allMetadata, IntentMetadataJsonContext.Default.ListIntentMetadata);
            var vectorsJson = JsonSerializer.Serialize(allVectors, IntentVectorJsonContext.Default.ListSingleArray);

            await Task.WhenAll(
                File.WriteAllTextAsync(metaPath, metaJson),
                File.WriteAllTextAsync(vectorPath, vectorsJson));

            if (graph is not null)
            {
                await using var fs = File.Create(graphPath);
                graph.SerializeGraph(fs);
            }

            _logger.LogDebug("Saved intent HNSW index with {Count} vectors", allVectors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save intent HNSW index");
        }
    }

    public async Task LoadAsync()
    {
        var metaPath = Path.Combine(_databasePath, "intent.meta.json");
        var graphPath = Path.Combine(_databasePath, "intent.hnsw");
        var vectorPath = Path.Combine(_databasePath, "intent.vectors.json");

        if (!File.Exists(metaPath) || !File.Exists(vectorPath))
        {
            _logger.LogDebug("No existing intent HNSW index found at {Path}", _databasePath);
            return;
        }

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaPath);
            var loadedMetadata = JsonSerializer.Deserialize(metaJson, IntentMetadataJsonContext.Default.ListIntentMetadata);

            var vectorsJson = await File.ReadAllTextAsync(vectorPath);
            var loadedVectors = JsonSerializer.Deserialize(vectorsJson, IntentVectorJsonContext.Default.ListSingleArray);

            if (loadedMetadata is null || loadedVectors is null ||
                loadedMetadata.Count != loadedVectors.Count)
            {
                _logger.LogWarning("Intent HNSW index data is corrupted (count mismatch)");
                return;
            }

            var compatibleMetadata = new List<IntentMetadata>(loadedMetadata.Count);
            var compatibleVectors = new List<float[]>(loadedVectors.Count);
            var skippedVectors = 0;

            for (var i = 0; i < loadedMetadata.Count; i++)
            {
                var meta = loadedMetadata[i];
                var vector = loadedVectors[i];

                var schemaVersion = meta.SchemaVersion == 0 ? 1 : meta.SchemaVersion;
                var vectorDimension = meta.VectorDimension == 0 ? vector.Length : meta.VectorDimension;

                if (schemaVersion != IntentVectorizer.SchemaVersion ||
                    vectorDimension != IntentVectorizer.VectorDimension ||
                    vector.Length != IntentVectorizer.VectorDimension)
                {
                    skippedVectors++;
                    continue;
                }

                compatibleMetadata.Add(meta);
                compatibleVectors.Add(vector);
            }

            if (skippedVectors > 0)
            {
                _logger.LogInformation(
                    "Discarded {Count} incompatible intent vectors during load (schema={SchemaVersion}, dimension={Dimension})",
                    skippedVectors,
                    IntentVectorizer.SchemaVersion,
                    IntentVectorizer.VectorDimension);
            }

            lock (_writeLock)
            {
                _graphVectors = compatibleVectors;
                _metadata = compatibleMetadata;

                if (_graphVectors.Count < MinVectorsForGraph)
                {
                    _logger.LogDebug("Loaded {Count} intent vectors (below threshold for graph build)",
                        _graphVectors.Count);
                    return;
                }

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
                            "Loaded intent HNSW index with {Count} vectors from serialized graph",
                            _graphVectors.Count);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize intent HNSW graph, rebuilding");
                    }
                }

                BuildGraphFromVectorsLocked();
                _dirty = true;
                _logger.LogInformation("Rebuilt intent HNSW index with {Count} vectors", _graphVectors.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load intent HNSW index from {Path}", _databasePath);
        }
    }

    public void Dispose()
    {
        _autoSaveTimer.Dispose();
        try
        {
            if (!SaveAsync().Wait(TimeSpan.FromSeconds(5)))
                _logger.LogWarning("Intent HNSW index save timed out on shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save intent HNSW index on shutdown");
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
            _logger.LogWarning(ex, "Background intent HNSW rebuild failed");
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

    private void RebuildGraphLocked()
    {
        if (_pendingVectors.Count == 0) return;

        _graphVectors.AddRange(_pendingVectors);
        _metadata.AddRange(_pendingMetadata);
        _pendingVectors.Clear();
        _pendingMetadata.Clear();

        if (_graphVectors.Count < MinVectorsForGraph) return;

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

            _logger.LogDebug("Built intent HNSW graph with {Count} vectors", validVectors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build intent HNSW graph");
            _graph = null;
        }
    }

    private void AutoSave()
    {
        try
        {
            bool isDirty;
            lock (_writeLock)
                isDirty = _dirty;

            if (isDirty)
                _ = SaveSafeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-save of intent HNSW index failed");
        }
    }

    private async Task SaveSafeAsync()
    {
        try
        {
            await SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Async auto-save of intent HNSW index failed");
        }
    }
}

/// <summary>
///     Metadata stored alongside each vector in the intent HNSW index.
/// </summary>
public class IntentMetadata
{
    public string SignatureId { get; set; } = string.Empty;
    public double ThreatScore { get; set; }
    public string IntentCategory { get; set; } = string.Empty;
    public string? Reasoning { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public int SchemaVersion { get; set; }
    public int VectorDimension { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<IntentMetadata>))]
internal partial class IntentMetadataJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

[System.Text.Json.Serialization.JsonSerializable(typeof(List<float[]>))]
internal partial class IntentVectorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
