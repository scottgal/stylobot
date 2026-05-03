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
    private const int CurrentSchemaVersion = 1;
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromMinutes(5);

    private static readonly SmallWorldParameters GraphParameters = new()
    {
        M = DefaultM,
        LevelLambda = 1.0 / Math.Log(DefaultM),
        NeighbourHeuristic = NeighbourSelectionHeuristic.SelectSimple
    };

    private readonly string _databasePath;
    private readonly ILogger<HnswFileSimilaritySearch> _logger;
    private readonly object _writeLock = new();
    private readonly Timer _autoSaveTimer;
    private readonly Task _loadTask;

    // 0 = idle, 1 = rebuild in progress; prevents concurrent rebuilds
    private int _rebuildInProgress;

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
        var dbPath = options.Value.DatabasePath
                     ?? Path.Combine(BotDetectionOptions.ResolveDataDirectory(), "botdetection.db");
        var basePath = Path.GetDirectoryName(dbPath) ?? BotDetectionOptions.ResolveDataDirectory();
        _databasePath = Path.Combine(basePath, "hnsw-index");
        Directory.CreateDirectory(_databasePath);

        _autoSaveTimer = new Timer(_ => AutoSave(), null, AutoSaveInterval, AutoSaveInterval);

        // Load existing data on startup (tracked so we can await before first search)
        _loadTask = Task.Run(async () =>
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

    public async Task<IReadOnlyList<SimilarSignature>> FindSimilarAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.80f, string? embeddingContext = null)
    {
        // Ensure startup load completes before first search
        await _loadTask.ConfigureAwait(false);

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

        return (IReadOnlyList<SimilarSignature>)results;
    }

    public Task AddAsync(float[] vector, string signatureId, bool wasBot, double confidence, string? embeddingContext = null)
    {
        if (!IsValidVector(vector))
        {
            _logger.LogDebug("Skipping zero-norm signature vector for {Signature}", signatureId);
            return Task.CompletedTask;
        }

        var meta = new SignatureMetadata
        {
            SignatureId = signatureId,
            WasBot = wasBot,
            Confidence = confidence,
            Timestamp = DateTimeOffset.UtcNow
        };

        bool triggerRebuild;
        lock (_writeLock)
        {
            _pendingVectors.Add(vector);
            _pendingMetadata.Add(meta);
            _dirty = true;
            triggerRebuild = _pendingVectors.Count >= RebuildThreshold;
        }

        // Rebuild on a background thread — never block the hot path
        if (triggerRebuild && Interlocked.CompareExchange(ref _rebuildInProgress, 1, 0) == 0)
            _ = Task.Run(RebuildGraphOffLock);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Merges pending vectors and rebuilds the graph on a background thread.
    ///     The expensive AddItems call runs entirely off the write lock.
    /// </summary>
    private void RebuildGraphOffLock()
    {
        try
        {
            List<float[]> allVectors;
            List<SignatureMetadata> allMeta;

            lock (_writeLock)
            {
                if (_pendingVectors.Count == 0)
                    return;

                _graphVectors.AddRange(_pendingVectors);
                _metadata.AddRange(_pendingMetadata);
                _pendingVectors.Clear();
                _pendingMetadata.Clear();

                // Snapshot for building — lock released immediately after
                allVectors = [.. _graphVectors];
                allMeta = [.. _metadata];
            }

            if (allVectors.Count < MinVectorsForGraph)
                return;

            // CPU-intensive work runs completely off the lock
            var newGraph = new SmallWorld<float[], float>(
                CosineDistance.SIMD,
                DefaultRandomGenerator.Instance,
                GraphParameters,
                threadSafe: true);

            newGraph.AddItems(allVectors, progressReporter: null);

            lock (_writeLock)
            {
                _graph = newGraph;
                _dirty = true;
            }

            _logger.LogDebug("Rebuilt HNSW graph with {Count} vectors (background)", allVectors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build HNSW graph");
        }
        finally
        {
            Interlocked.Exchange(ref _rebuildInProgress, 0);
        }
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
            var manifestPath = Path.Combine(_databasePath, "index.manifest.json");

            var manifest = new IndexManifest
            {
                SchemaVersion = CurrentSchemaVersion,
                Dimension = allVectors[0].Length
            };

            var metaJson = JsonSerializer.Serialize(allMetadata, MetadataJsonContext.Default.ListSignatureMetadata);
            var vectorsJson = JsonSerializer.Serialize(allVectors, VectorJsonContext.Default.ListSingleArray);
            var manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.IndexManifest);

            await Task.WhenAll(
                File.WriteAllTextAsync(metaPath, metaJson),
                File.WriteAllTextAsync(vectorPath, vectorsJson),
                File.WriteAllTextAsync(manifestPath, manifestJson));

            if (graph is not null)
            {
                await using var fs = File.Create(graphPath);
                graph.SerializeGraph(fs);
            }

            _logger.LogDebug("Saved HNSW index with {Count} vectors (dim={Dim}, schema={Schema})",
                allVectors.Count, manifest.Dimension, manifest.SchemaVersion);
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
        var manifestPath = Path.Combine(_databasePath, "index.manifest.json");

        if (!File.Exists(metaPath) || !File.Exists(vectorPath))
        {
            _logger.LogDebug("No existing HNSW index found at {Path}", _databasePath);
            return;
        }

        try
        {
            // Read manifest (optional - pre-manifest indices treated as schema version 0)
            IndexManifest? manifest = null;
            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifestJson = await File.ReadAllTextAsync(manifestPath);
                    manifest = JsonSerializer.Deserialize(manifestJson, ManifestJsonContext.Default.IndexManifest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read HNSW manifest, treating as legacy index");
                }
            }

            // Schema version mismatch: discard everything and start fresh
            if (manifest is not null && manifest.SchemaVersion != CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "HNSW index schema version mismatch (saved={Saved}, current={Current}), discarding index",
                    manifest.SchemaVersion, CurrentSchemaVersion);
                DiscardIndex(metaPath, graphPath, vectorPath, manifestPath);
                return;
            }

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

            // Detect dimension growth: zero-pad old vectors to current size
            var savedDim = manifest?.Dimension ?? (loadedVectors.Count > 0 ? loadedVectors[0].Length : 0);
            var needsGraphRebuild = false;
            if (savedDim > 0 && loadedVectors.Count > 0)
            {
                var currentDim = loadedVectors[0].Length; // actual saved dim
                // We compare against what the live vectorizer would produce
                // If saved dim < current session vector dimensions, zero-pad
                if (savedDim < currentDim)
                {
                    // This shouldn't happen (manifest would have the save-time dim already)
                    // but guard anyway
                }
                else if (savedDim > currentDim)
                {
                    // Saved vectors have more dimensions than current build: discard
                    _logger.LogWarning(
                        "HNSW saved dimension ({Saved}) exceeds current dimension ({Current}), discarding index",
                        savedDim, currentDim);
                    DiscardIndex(metaPath, graphPath, vectorPath, manifestPath);
                    return;
                }
            }

            // Zero-pad if manifest records a smaller saved dim than current live dim
            // (manifest.Dimension is what was saved; if the live code now produces wider vectors,
            //  we pad so the existing data is still usable and the graph gets rebuilt)
            if (manifest is not null && loadedVectors.Count > 0)
            {
                var liveDim = Analysis.SessionVectorizer.Dimensions;
                if (manifest.Dimension < liveDim)
                {
                    _logger.LogInformation(
                        "HNSW dimension grew from {Old} to {New}, zero-padding {Count} vectors",
                        manifest.Dimension, liveDim, loadedVectors.Count);
                    loadedVectors = loadedVectors
                        .Select(v => ZeroPad(v, liveDim))
                        .ToList();
                    needsGraphRebuild = true;
                    // Delete stale serialized graph so LoadAsync rebuilds it
                    try { File.Delete(graphPath); } catch { /* best-effort */ }
                }
            }

            lock (_writeLock)
            {
                _graphVectors = loadedVectors;
                _metadata = loadedMetadata;
                _dirty = needsGraphRebuild; // trigger re-save with padded vectors

                if (_graphVectors.Count < MinVectorsForGraph)
                {
                    _logger.LogDebug("Loaded {Count} vectors (below threshold for graph build)",
                        _graphVectors.Count);
                    return;
                }

                if (!needsGraphRebuild && File.Exists(graphPath))
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
                        _logger.LogWarning("Failed to deserialize HNSW graph (stale format), rebuilding. Error: {Message}", ex.Message);
                        try { File.Delete(graphPath); } catch { /* best-effort */ }
                    }
                }

                BuildGraphFromVectorsLocked();
                _dirty = true; // ensure graph is serialized on next save so restart skips rebuild
                _logger.LogInformation("Rebuilt HNSW index with {Count} vectors", _graphVectors.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load HNSW index from {Path}", _databasePath);
        }
    }

    private static float[] ZeroPad(float[] v, int targetDim)
    {
        if (v.Length >= targetDim) return v;
        var padded = new float[targetDim];
        v.CopyTo(padded, 0);
        return padded;
    }

    private static bool IsValidVector(float[] v)
    {
        if (v.Length == 0) return false;
        var normSq = 0f;
        foreach (var x in v) normSq += x * x;
        return normSq > 0f && !float.IsNaN(normSq) && !float.IsInfinity(normSq);
    }

    private void DiscardIndex(params string[] paths)
    {
        foreach (var path in paths)
            try { File.Delete(path); } catch { /* best-effort */ }
    }

    public void Dispose()
    {
        _autoSaveTimer.Dispose();
        try
        {
            if (!SaveAsync().Wait(TimeSpan.FromSeconds(5)))
                _logger.LogWarning("HNSW index save timed out on shutdown");
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

            var validVectors = _graphVectors.Where(IsValidVector).ToList();
            if (validVectors.Count < MinVectorsForGraph) return;
            graph.AddItems(validVectors, progressReporter: null);
            _graph = graph;

            _logger.LogDebug("Built HNSW graph with {Count} vectors", validVectors.Count);
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
                _ = SaveSafeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-save of HNSW index failed");
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
            _logger.LogWarning(ex, "Async auto-save of HNSW index failed");
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
///     Sidecar file that records the vector schema at save time.
///     Used by LoadAsync to detect dimension growth (zero-pad) or
///     breaking schema changes (discard and rebuild).
/// </summary>
public class IndexManifest
{
    /// <summary>
    ///     Increment when dimensions are reordered, removed, or semantics change.
    ///     A mismatch causes the index to be discarded and rebuilt from scratch.
    ///     Dimension-only growth (new dims appended) does NOT require a version bump.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    ///     Number of floats per vector at save time.
    ///     If current Dimensions > saved Dimension, old vectors are zero-padded.
    /// </summary>
    public int Dimension { get; set; }
}

/// <summary>
///     Default random generator for HNSW graph construction.
/// </summary>
internal sealed class DefaultRandomGenerator : IProvideRandomValues
{
    public static readonly DefaultRandomGenerator Instance = new();

    [ThreadStatic] private static Random? t_random;
    private static Random ThreadRandom => t_random ??= Random.Shared;

    public bool IsThreadSafe => true;
    public int Next(int minValue, int maxValue) => ThreadRandom.Next(minValue, maxValue);
    public float NextFloat() => ThreadRandom.NextSingle();
    public void NextFloats(Span<float> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = ThreadRandom.NextSingle();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<SignatureMetadata>))]
internal partial class MetadataJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

[System.Text.Json.Serialization.JsonSerializable(typeof(List<float[]>))]
internal partial class VectorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

[System.Text.Json.Serialization.JsonSerializable(typeof(IndexManifest))]
internal partial class ManifestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
