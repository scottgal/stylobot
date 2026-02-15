using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Qdrant-backed similarity search implementation.
///     Uses gRPC for vector operations with cosine distance.
///     Collection is auto-created on first use. Data migrated from HNSW on first init.
/// </summary>
public sealed class QdrantSimilaritySearch : ISignatureSimilaritySearch
{
    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly int _vectorDimension;
    private readonly string? _databasePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public QdrantSimilaritySearch(
        QdrantOptions options,
        string? databasePath,
        ILogger logger)
    {
        _collectionName = options.CollectionName;
        _vectorDimension = options.VectorDimension;
        _databasePath = databasePath;
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
            var results = await _client.SearchAsync(
                _collectionName,
                vector,
                limit: (ulong)topK,
                scoreThreshold: minSimilarity);

            return results
                .Select(r => new SimilarSignature(
                    SignatureId: r.Payload.TryGetValue("signatureId", out var sid) ? sid.StringValue : r.Id.Uuid,
                    Distance: 1.0f - (float)r.Score, // Convert similarity to distance
                    WasBot: r.Payload.TryGetValue("wasBot", out var wb) && wb.BoolValue,
                    Confidence: r.Payload.TryGetValue("confidence", out var conf) ? conf.DoubleValue : 0.5))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant search failed");
            return Array.Empty<SimilarSignature>();
        }
    }

    public async Task AddAsync(float[] vector, string signatureId, bool wasBot, double confidence, string? embeddingContext = null)
    {
        await EnsureInitializedAsync();

        try
        {
            // Deterministic UUID from signatureId to avoid duplicates
            var pointId = ToGuid(signatureId);

            var point = new PointStruct
            {
                Id = pointId,
                Vectors = vector,
                Payload =
                {
                    ["signatureId"] = signatureId,
                    ["wasBot"] = wasBot,
                    ["confidence"] = confidence,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            };

            await _client.UpsertAsync(_collectionName, [point]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add vector to Qdrant");
        }
    }

    public Task SaveAsync() => Task.CompletedTask; // Qdrant persists automatically

    public Task LoadAsync() => EnsureInitializedAsync();

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            // Check if collection exists, create if not
            var collections = await _client.ListCollectionsAsync();
            if (!collections.Any(c => c == _collectionName))
            {
                _logger.LogInformation("Creating Qdrant collection '{Collection}' with dimension {Dim}",
                    _collectionName, _vectorDimension);

                await _client.CreateCollectionAsync(
                    _collectionName,
                    new VectorParams
                    {
                        Size = (ulong)_vectorDimension,
                        Distance = Distance.Cosine
                    });
            }

            // Attempt HNSW migration
            await MigrateFromHnswAsync();

            _initialized = true;
            _logger.LogInformation("Qdrant similarity search initialized: collection={Collection}", _collectionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Qdrant collection");
            _initialized = true; // Don't retry on every call
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Migrate existing HNSW data files into Qdrant on first startup.
    /// </summary>
    private async Task MigrateFromHnswAsync()
    {
        if (string.IsNullOrEmpty(_databasePath)) return;

        var metaFile = Path.Combine(_databasePath, "signatures.meta.json");
        var vectorsFile = Path.Combine(_databasePath, "signatures.vectors.json");

        if (!File.Exists(metaFile) || !File.Exists(vectorsFile)) return;

        try
        {
            _logger.LogInformation("Found HNSW data files, migrating to Qdrant...");

            var metaJson = await File.ReadAllTextAsync(metaFile);
            var vectorsJson = await File.ReadAllTextAsync(vectorsFile);

            var metadata = JsonSerializer.Deserialize<List<HnswMetadata>>(metaJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var vectors = JsonSerializer.Deserialize<List<float[]>>(vectorsJson);

            if (metadata == null || vectors == null || metadata.Count != vectors.Count)
            {
                _logger.LogWarning("HNSW migration: metadata/vectors mismatch, skipping");
                return;
            }

            // Bulk upsert in batches
            const int batchSize = 100;
            var totalMigrated = 0;

            for (var i = 0; i < metadata.Count; i += batchSize)
            {
                var batch = new List<PointStruct>();
                var end = Math.Min(i + batchSize, metadata.Count);

                for (var j = i; j < end; j++)
                {
                    var meta = metadata[j];
                    var vector = vectors[j];

                    if (vector.Length != _vectorDimension)
                        continue;

                    batch.Add(new PointStruct
                    {
                        Id = ToGuid(meta.SignatureId),
                        Vectors = vector,
                        Payload =
                        {
                            ["signatureId"] = meta.SignatureId,
                            ["wasBot"] = meta.WasBot,
                            ["confidence"] = meta.Confidence,
                            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["migrated"] = true
                        }
                    });
                }

                if (batch.Count > 0)
                {
                    await _client.UpsertAsync(_collectionName, batch);
                    totalMigrated += batch.Count;
                }
            }

            _logger.LogInformation("Migrated {Count} vectors from HNSW to Qdrant", totalMigrated);

            // Rename files to prevent re-import
            File.Move(metaFile, metaFile + ".migrated", overwrite: true);
            File.Move(vectorsFile, vectorsFile + ".migrated", overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate HNSW data to Qdrant");
        }
    }

    /// <summary>
    ///     Convert a signatureId string to a deterministic GUID for Qdrant point ID.
    /// </summary>
    private static Guid ToGuid(string signatureId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signatureId));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed class HnswMetadata
    {
        public string SignatureId { get; set; } = "";
        public bool WasBot { get; set; }
        public double Confidence { get; set; }
    }
}
