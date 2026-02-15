using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Dual-vector similarity search combining heuristic (64-dim) and semantic (384-dim) vectors.
///     Stores two named vectors per point in Qdrant:
///       - "heuristic": from FeatureVectorizer (always available, &lt;0.1ms)
///       - "semantic": from ONNX all-MiniLM-L6-v2 (when available, ~1-3ms)
///     Search merges results from both vectors by weighted score.
///     Falls back to heuristic-only if embeddings are unavailable.
/// </summary>
public sealed class DualVectorSimilaritySearch : ISignatureSimilaritySearch
{
    private const string HeuristicVectorName = "heuristic";
    private const string SemanticVectorName = "semantic";
    private const float HeuristicWeight = 0.6f;
    private const float SemanticWeight = 0.4f;

    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly FeatureVectorizer _vectorizer;
    private readonly IEmbeddingProvider _embedder;
    private readonly int _heuristicDim;
    private readonly int _semanticDim;
    private readonly string? _databasePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public DualVectorSimilaritySearch(
        QdrantOptions options,
        FeatureVectorizer vectorizer,
        IEmbeddingProvider embedder,
        string? databasePath,
        ILogger logger)
    {
        _collectionName = options.CollectionName;
        _heuristicDim = options.VectorDimension;
        _semanticDim = options.EmbeddingDimension;
        _databasePath = databasePath;
        _vectorizer = vectorizer;
        _embedder = embedder;
        _logger = logger;

        var uri = new Uri(options.Endpoint);
        _client = new QdrantClient(uri.Host, uri.Port);
    }

    public int Count
    {
        get
        {
            try
            {
                EnsureInitializedAsync().GetAwaiter().GetResult();
                var info = _client.GetCollectionInfoAsync(_collectionName).GetAwaiter().GetResult();
                return (int)info.PointsCount;
            }
            catch
            {
                return 0;
            }
        }
    }

    public async Task<IReadOnlyList<SimilarSignature>> FindSimilarAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.80f, string? embeddingContext = null)
    {
        await EnsureInitializedAsync();

        try
        {
            // Search heuristic vector (always available, <0.1ms)
            var heuristicResults = await _client.SearchAsync(
                _collectionName,
                vector,
                vectorName: HeuristicVectorName,
                limit: (ulong)(topK * 2), // Over-fetch for merging
                scoreThreshold: minSimilarity * 0.9f); // Slightly lower for merge

            var merged = heuristicResults
                .ToDictionary(
                    r => r.Id.Uuid,
                    r => (
                        Score: (float)r.Score * HeuristicWeight,
                        SignatureId: r.Payload.TryGetValue("signatureId", out var sid) ? sid.StringValue : r.Id.Uuid,
                        WasBot: r.Payload.TryGetValue("wasBot", out var wb) && wb.BoolValue,
                        Confidence: r.Payload.TryGetValue("confidence", out var conf) ? conf.DoubleValue : 0.5));

            // Search semantic vector if embedder is available AND we have embedding context
            if (_embedder.IsAvailable && !string.IsNullOrEmpty(embeddingContext))
            {
                var semanticVector = _embedder.GenerateEmbedding(embeddingContext);
                if (semanticVector != null)
                {
                    var semanticResults = await _client.SearchAsync(
                        _collectionName,
                        semanticVector,
                        vectorName: SemanticVectorName,
                        limit: (ulong)(topK * 2),
                        scoreThreshold: minSimilarity * 0.7f);

                    foreach (var r in semanticResults)
                    {
                        var id = r.Id.Uuid;
                        var sigId = r.Payload.TryGetValue("signatureId", out var sid) ? sid.StringValue : id;
                        var wasBot = r.Payload.TryGetValue("wasBot", out var wb) && wb.BoolValue;
                        var conf = r.Payload.TryGetValue("confidence", out var c) ? c.DoubleValue : 0.5;

                        if (merged.TryGetValue(id, out var existing))
                            merged[id] = (existing.Score + (float)r.Score * SemanticWeight, existing.SignatureId, existing.WasBot, existing.Confidence);
                        else
                            merged[id] = ((float)r.Score * SemanticWeight, sigId, wasBot, conf);
                    }
                }
            }

            var combinedWeight = _embedder.IsAvailable && !string.IsNullOrEmpty(embeddingContext)
                ? HeuristicWeight + SemanticWeight
                : HeuristicWeight;

            return merged.Values
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .Where(r => r.Score / combinedWeight >= minSimilarity)
                .Select(r => new SimilarSignature(
                    r.SignatureId,
                    1.0f - r.Score / combinedWeight,
                    r.WasBot,
                    r.Confidence))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dual-vector search failed");
            return Array.Empty<SimilarSignature>();
        }
    }

    public async Task AddAsync(float[] vector, string signatureId, bool wasBot, double confidence, string? embeddingContext = null)
    {
        await EnsureInitializedAsync();

        try
        {
            var pointId = ToGuid(signatureId);

            // Build named vectors dictionary
            var namedVectors = new Dictionary<string, float[]>
            {
                [HeuristicVectorName] = vector
            };

            // Generate semantic embedding from request context (UA, headers, etc.)
            if (_embedder.IsAvailable && !string.IsNullOrEmpty(embeddingContext))
            {
                var semanticVector = _embedder.GenerateEmbedding(embeddingContext);
                if (semanticVector != null)
                    namedVectors[SemanticVectorName] = semanticVector;
            }

            var point = new PointStruct
            {
                Id = pointId,
                Vectors = namedVectors,
                Payload =
                {
                    ["signatureId"] = signatureId,
                    ["wasBot"] = wasBot,
                    ["confidence"] = confidence,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["hasEmbedding"] = _embedder.IsAvailable
                }
            };

            await _client.UpsertAsync(_collectionName, [point]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add dual vector to Qdrant");
        }
    }

    public Task SaveAsync() => Task.CompletedTask;
    public Task LoadAsync() => EnsureInitializedAsync();

    /// <summary>
    ///     Search using both heuristic vector and semantic text.
    ///     This is the preferred search method when embedding text is available.
    /// </summary>
    public async Task<IReadOnlyList<SimilarSignature>> FindSimilarWithEmbeddingAsync(
        float[] heuristicVector, string embeddingText, int topK = 5, float minSimilarity = 0.80f)
    {
        await EnsureInitializedAsync();

        try
        {
            // Search heuristic
            var heuristicResults = await _client.SearchAsync(
                _collectionName,
                heuristicVector,
                vectorName: HeuristicVectorName,
                limit: (ulong)(topK * 2),
                scoreThreshold: minSimilarity * 0.8f);

            var merged = heuristicResults
                .ToDictionary(
                    r => r.Id.Uuid,
                    r => (
                        Score: (float)r.Score * HeuristicWeight,
                        SignatureId: r.Payload.TryGetValue("signatureId", out var sid) ? sid.StringValue : r.Id.Uuid,
                        WasBot: r.Payload.TryGetValue("wasBot", out var wb) && wb.BoolValue,
                        Confidence: r.Payload.TryGetValue("confidence", out var conf) ? conf.DoubleValue : 0.5));

            // Search semantic if embedder available
            if (_embedder.IsAvailable)
            {
                var semanticVector = _embedder.GenerateEmbedding(embeddingText);
                if (semanticVector != null)
                {
                    var semanticResults = await _client.SearchAsync(
                        _collectionName,
                        semanticVector,
                        vectorName: SemanticVectorName,
                        limit: (ulong)(topK * 2),
                        scoreThreshold: minSimilarity * 0.7f);

                    foreach (var r in semanticResults)
                    {
                        var id = r.Id.Uuid;
                        var sigId = r.Payload.TryGetValue("signatureId", out var sid) ? sid.StringValue : id;
                        var wasBot = r.Payload.TryGetValue("wasBot", out var wb) && wb.BoolValue;
                        var conf = r.Payload.TryGetValue("confidence", out var c) ? c.DoubleValue : 0.5;

                        if (merged.TryGetValue(id, out var existing))
                            merged[id] = (existing.Score + (float)r.Score * SemanticWeight, existing.SignatureId, existing.WasBot, existing.Confidence);
                        else
                            merged[id] = ((float)r.Score * SemanticWeight, sigId, wasBot, conf);
                    }
                }
            }

            var combinedWeight = _embedder.IsAvailable ? HeuristicWeight + SemanticWeight : HeuristicWeight;

            return merged.Values
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .Where(r => r.Score / combinedWeight >= minSimilarity)
                .Select(r => new SimilarSignature(
                    r.SignatureId,
                    1.0f - r.Score / combinedWeight,
                    r.WasBot,
                    r.Confidence))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dual-vector search with embedding failed");
            return Array.Empty<SimilarSignature>();
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            var collections = await _client.ListCollectionsAsync();
            if (!collections.Any(c => c == _collectionName))
            {
                _logger.LogInformation(
                    "Creating dual-vector Qdrant collection '{Collection}' (heuristic={HDim}, semantic={SDim})",
                    _collectionName, _heuristicDim, _semanticDim);

                var vectorsConfig = new VectorParamsMap();
                vectorsConfig.Map.Add(HeuristicVectorName, new VectorParams
                {
                    Size = (ulong)_heuristicDim,
                    Distance = Distance.Cosine
                });
                vectorsConfig.Map.Add(SemanticVectorName, new VectorParams
                {
                    Size = (ulong)_semanticDim,
                    Distance = Distance.Cosine
                });

                await _client.CreateCollectionAsync(_collectionName, vectorsConfig);
            }

            _initialized = true;
            _logger.LogInformation("Dual-vector similarity search initialized: collection={Collection}", _collectionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize dual-vector Qdrant collection");
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static Guid ToGuid(string signatureId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signatureId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
