using System.Text.Json;
using HNSW.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     File-backed HNSW similarity search implementation.
///     Uses the curiosity-ai HNSW library with SIMD-accelerated cosine distance.
///     Thread-safe: reads snapshot under lock, writes use a Lock.
/// </summary>
public sealed class HnswFileSimilaritySearch : ISignatureSimilaritySearch, IDisposable
{
    private const int DefaultM = 16;
    private const int MinVectorsForGraph = 5;
    private const int RebuildThreshold = 50;
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromMinutes(5);

    private static readonly SmallWorld<float[], float>.Parameters GraphParameters = new()
    {
        M = DefaultM,
        LevelLambda = 1.0 / Math.Log(DefaultM),
        NeighbourHeuristic = NeighbourSelectionHeuristic.SelectSimple
    };

    private readonly string _databasePath;
    private readonly ILogger<HnswFileSimilaritySearch> _logger;
    private readonly object _writeLock = new();
    private readonly Timer _autoSaveTimer;

    // Pending vectors not yet in the HNSW graph (added since last rebuild)
    private readonly List<float[]> _pendingVectors = [];
    private readonly List<SignatureMetadata> _pendingMetadata = [];

    // Current HNSW graph (rebuilt when pending vectors accumulate)
    private SmallWorld<float[], float>? _graph;
    private List<float[]> _graphVectors = [];
    private List<SignatureMetadata> _metadata = [];
    private bool _dirty;

    public HnswFileSimilaritySearch(
        ILogger<HnswFileSimilaritySearch> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _databasePath = options.Value.DatabasePath
                        ?? Path.Combine(AppContext.BaseDirectory, "botdetection-data");

        Directory.CreateDirectory(_databasePath);

        _autoSaveTimer = new Timer(_ => AutoSave(), null, AutoSaveInterval, AutoSaveInterval);

        // Load existing data on startup
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load HNSW index on startup");
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

    public Task<IReadOnlyList<SimilarSignature>> FindSimilarAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.80f)
    {
        var results = new List<SimilarSignature>();

        // Cosine distance = 1 - cosine_similarity
        var maxDistance = 1.0f - minSimilarity;

        SmallWorld<float[], float>? graph;
        List<SignatureMetadata> metadata;
        List<float[]> pendingVectors;
        List<SignatureMetadata> pendingMeta;

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

                    // KNNSearchResult.Id is the item index in the graph
                    var idx = result.Id;
                    if (idx >= 0 && idx < metadata.Count)
                    {
                        var meta = metadata[idx];
                        results.Add(new SimilarSignature(
                            meta.SignatureId,
                            result.Distance,
                            meta.WasBot,
                            meta.Confidence));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HNSW search failed");
            }
        }

        // Brute-force search on pending vectors (small set, SIMD distance)
        for (var i = 0; i < pendingVectors.Count; i++)
        {
            var distance = CosineDistance.SIMD(vector, pendingVectors[i]);
            if (distance <= maxDistance)
            {
                var meta = pendingMeta[i];
                results.Add(new SimilarSignature(
                    meta.SignatureId,
                    distance,
                    meta.WasBot,
                    meta.Confidence));
            }
        }

        // Sort by distance (closest first) and trim
        results.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        if (results.Count > topK)
            results.RemoveRange(topK, results.Count - topK);

        return Task.FromResult<IReadOnlyList<SimilarSignature>>(results);
    }

    public Task AddAsync(float[] vector, string signatureId, bool wasBot, double confidence)
    {
        var meta = new SignatureMetadata
        {
            SignatureId = signatureId,
            WasBot = wasBot,
            Confidence = confidence,
            Timestamp = DateTimeOffset.UtcNow
        };

        lock (_writeLock)
        {
            _pendingVectors.Add(vector);
            _pendingMetadata.Add(meta);
            _dirty = true;

            // Rebuild when pending vectors accumulate
            if (_pendingVectors.Count >= RebuildThreshold)
                RebuildGraphLocked();
        }

        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        List<float[]> allVectors;
        List<SignatureMetadata> allMetadata;
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
            var metaPath = Path.Combine(_databasePath, "signatures.meta.json");
            var graphPath = Path.Combine(_databasePath, "signatures.hnsw");
            var vectorPath = Path.Combine(_databasePath, "signatures.vectors.json");

            // Save metadata + vectors
            var metaJson = JsonSerializer.Serialize(allMetadata, MetadataJsonContext.Default.ListSignatureMetadata);
            var vectorsJson = JsonSerializer.Serialize(allVectors, VectorJsonContext.Default.ListSingleArray);

            await Task.WhenAll(
                File.WriteAllTextAsync(metaPath, metaJson),
                File.WriteAllTextAsync(vectorPath, vectorsJson));

            // Save HNSW graph via stream
            if (graph is not null)
            {
                await using var fs = File.Create(graphPath);
                graph.SerializeGraph(fs);
            }

            _logger.LogDebug("Saved HNSW index with {Count} vectors", allVectors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save HNSW index");
        }
    }

    public async Task LoadAsync()
    {
        var metaPath = Path.Combine(_databasePath, "signatures.meta.json");
        var graphPath = Path.Combine(_databasePath, "signatures.hnsw");
        var vectorPath = Path.Combine(_databasePath, "signatures.vectors.json");

        if (!File.Exists(metaPath) || !File.Exists(vectorPath))
        {
            _logger.LogDebug("No existing HNSW index found at {Path}", _databasePath);
            return;
        }

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaPath);
            var loadedMetadata = JsonSerializer.Deserialize(metaJson, MetadataJsonContext.Default.ListSignatureMetadata);

            var vectorsJson = await File.ReadAllTextAsync(vectorPath);
            var loadedVectors = JsonSerializer.Deserialize(vectorsJson, VectorJsonContext.Default.ListSingleArray);

            if (loadedMetadata is null || loadedVectors is null ||
                loadedMetadata.Count != loadedVectors.Count)
            {
                _logger.LogWarning("HNSW index data is corrupted (count mismatch)");
                return;
            }

            lock (_writeLock)
            {
                _graphVectors = loadedVectors;
                _metadata = loadedMetadata;

                if (_graphVectors.Count < MinVectorsForGraph)
                {
                    _logger.LogDebug("Loaded {Count} vectors (below threshold for graph build)",
                        _graphVectors.Count);
                    return;
                }

                // Try to deserialize the saved graph (fast path)
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
                            "Loaded HNSW index with {Count} vectors from serialized graph",
                            _graphVectors.Count);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize HNSW graph, rebuilding");
                    }
                }

                // Rebuild from vectors (slow path)
                BuildGraphFromVectorsLocked();
                _logger.LogInformation("Rebuilt HNSW index with {Count} vectors", _graphVectors.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load HNSW index from {Path}", _databasePath);
        }
    }

    public void Dispose()
    {
        _autoSaveTimer.Dispose();
        try
        {
            SaveAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save HNSW index on shutdown");
        }
    }

    /// <summary>
    ///     Merge pending vectors and rebuild the graph. Must be called under _writeLock.
    /// </summary>
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

    /// <summary>
    ///     Build an HNSW graph from _graphVectors. Must be called under _writeLock.
    /// </summary>
    private void BuildGraphFromVectorsLocked()
    {
        try
        {
            var graph = new SmallWorld<float[], float>(
                CosineDistance.SIMD,
                DefaultRandomGenerator.Instance,
                GraphParameters,
                threadSafe: true);

            graph.AddItems(_graphVectors, progressReporter: null);
            _graph = graph;

            _logger.LogDebug("Built HNSW graph with {Count} vectors", _graphVectors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build HNSW graph");
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
                SaveAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-save of HNSW index failed");
        }
    }
}

/// <summary>
///     Metadata stored alongside each vector in the HNSW index.
/// </summary>
public class SignatureMetadata
{
    public string SignatureId { get; set; } = string.Empty;
    public bool WasBot { get; set; }
    public double Confidence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
///     Default random generator for HNSW graph construction.
/// </summary>
internal sealed class DefaultRandomGenerator : IProvideRandomValues
{
    public static readonly DefaultRandomGenerator Instance = new();
    private readonly Random _random = new(42);

    public bool IsThreadSafe => false;
    public int Next(int minValue, int maxValue) => _random.Next(minValue, maxValue);
    public float NextFloat() => _random.NextSingle();
    public void NextFloats(Span<float> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = _random.NextSingle();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<SignatureMetadata>))]
internal partial class MetadataJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

[System.Text.Json.Serialization.JsonSerializable(typeof(List<float[]>))]
internal partial class VectorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
