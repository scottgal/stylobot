# pgvector Integration Guide

> **Note**: pgvector is an **optional enhancement** for the PostgreSQL storage provider. The bot detection system works perfectly with:
> - **Base**: File/SQLite storage (no database required)
> - **Enhanced**: PostgreSQL storage (better performance, relational queries)
> - **Further Enhanced**: + TimescaleDB (time-series optimizations)
> - **Fully Enhanced**: + pgvector (ML-based similarity search) ← You are here
>
> Each layer is optional and builds on the previous one. You can use Stylobot without PostgreSQL at all!

## Why pgvector?

**pgvector** is a PostgreSQL extension for vector similarity search. For Stylobot, it enables:

### Benefits Over Qdrant
- **Single Database** - No separate vector database needed
- **ACID Transactions** - Vectors and metadata in sync
- **Lower Ops Cost** - One service instead of two
- **Simple Backups** - Everything in PostgreSQL dumps
- **Relational Queries** - Join vectors with signatures, patterns, etc.

### Use Cases
- **ML-Based Bot Detection** - Find similar behavioral patterns
- **Signature Clustering** - Group related bot signatures
- **Anomaly Detection** - Identify outlier behaviors
- **Transfer Learning** - Reuse patterns from known bots

### Performance
- **HNSW Index** - Approximate nearest neighbor search
- **Sub-millisecond Queries** - Find top 10 similar in <1ms
- **Scales to Millions** - Handles 10M+ vectors efficiently

## Installation

### Option 1: TimescaleDB Image (Already Includes pgvector)

The `timescale/timescaledb:latest-pg16` Docker image includes pgvector by default:

```bash
docker run -d --name stylobot-timescale \
  -p 5432:5432 \
  -e POSTGRES_PASSWORD=yourpassword \
  timescale/timescaledb:latest-pg16
```

pgvector is ready to use - just enable the extension:
```sql
CREATE EXTENSION vector;
```

### Option 2: Self-Hosted PostgreSQL

```bash
# Ubuntu/Debian
sudo apt install postgresql-16-pgvector

# macOS (Homebrew)
brew install pgvector

# Enable in database
psql -U postgres -d stylobot -c "CREATE EXTENSION vector;"
```

### Option 3: Cloud Providers

#### AWS RDS
pgvector is available on PostgreSQL 15+ (supported on RDS).

#### Azure Database for PostgreSQL
pgvector is available on Flexible Server (PostgreSQL 15+).

#### Timescale Cloud
pgvector is pre-installed on all Timescale Cloud instances.

## Configuration

### Enable in Code

```csharp
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.PostgreSQL.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStyloBotDashboard(...);

builder.Services.AddStyloBotPostgreSQL(
    "Host=localhost;Database=stylobot;Username=postgres;Password=pass",
    opts =>
    {
        // Enable pgvector for ML-based similarity search
        opts.EnablePgVector = true;

        // Vector dimension (must match your embedding model)
        opts.VectorDimension = 384;  // all-MiniLM-L6-v2

        // HNSW index tuning
        opts.VectorIndexM = 16;              // Bi-directional links
        opts.VectorIndexEfConstruction = 64; // Build quality
        opts.VectorMinSimilarity = 0.8;      // 80% similarity threshold
    });
```

### Embedding Models

Choose embedding dimension based on your model:

| Model | Dimension | Speed | Quality | Use Case |
|-------|-----------|-------|---------|----------|
| all-MiniLM-L6-v2 | 384 | Very Fast | Good | Local deployment, real-time |
| OpenAI ada-002 | 768 | Fast | Better | Cloud API, cost-effective |
| text-embedding-3-small | 1536 | Medium | Best | High accuracy needs |
| text-embedding-3-large | 3072 | Slow | Excellent | Research/offline |

**Recommendation**: Start with **all-MiniLM-L6-v2** (384 dim) for local/fast inference.

## Schema

### Vector Columns

The `bot_signatures` table includes two vector columns:

```sql
-- Embedding of signature features (IP, UA, fingerprint)
signature_embedding vector(384)

-- Embedding of request behavior patterns (timing, paths, sequences)
behavior_embedding vector(384)
```

### Indexes

HNSW (Hierarchical Navigable Small World) indexes for fast similarity search:

```sql
CREATE INDEX idx_signatures_signature_embedding_hnsw
    ON bot_signatures USING hnsw (signature_embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

CREATE INDEX idx_signatures_behavior_embedding_hnsw
    ON bot_signatures USING hnsw (behavior_embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);
```

**Index Parameters:**
- `m`: Number of bi-directional links (default: 16)
  - Higher = more accurate, more memory
  - Range: 4-64
- `ef_construction`: Build quality (default: 64)
  - Higher = better index, slower build
  - Range: 10-500

## Usage

### Find Similar Signatures

```csharp
// Generate embedding for a new request (using your ML model)
var embedding = await embeddingService.GenerateSignatureEmbedding(request);

// Find similar known signatures
var similar = await connection.ExecuteAsync(@"
    SELECT * FROM find_similar_signatures(
        @Embedding::vector,
        @Limit,
        @MinSimilarity
    )",
    new {
        Embedding = embedding,
        Limit = 10,
        MinSimilarity = 0.8
    });
```

### Find Similar Behavior Patterns

```csharp
// Generate behavior embedding (request timing, path sequences, etc.)
var behaviorEmbedding = await embeddingService.GenerateBehaviorEmbedding(
    requestSequence, timingPatterns);

// Find similar behaviors
var similarBehaviors = await connection.ExecuteAsync(@"
    SELECT * FROM find_similar_behaviors(
        @Embedding::vector,
        @Limit,
        @MinSimilarity
    )",
    new {
        Embedding = behaviorEmbedding,
        Limit = 10,
        MinSimilarity = 0.85
    });
```

### Update Embeddings

```csharp
// After generating embeddings with your ML model
await connection.ExecuteAsync(@"
    SELECT update_signature_embeddings(
        @SignatureId::uuid,
        @SignatureEmbedding::vector,
        @BehaviorEmbedding::vector
    )",
    new {
        SignatureId = signatureId,
        SignatureEmbedding = sigEmbedding,
        BehaviorEmbedding = behaviorEmbedding
    });
```

## ML Model Integration

### Example: Local Embedding Service

```csharp
using Microsoft.ML.OnnxRuntime;

public class LocalEmbeddingService
{
    private readonly InferenceSession _session;

    public LocalEmbeddingService()
    {
        // Load ONNX model (all-MiniLM-L6-v2)
        _session = new InferenceSession("models/all-MiniLM-L6-v2.onnx");
    }

    public async Task<float[]> GenerateSignatureEmbedding(
        string primarySignature,
        string ipSignature,
        string uaSignature)
    {
        // Combine signatures into text
        var text = $"{primarySignature} {ipSignature} {uaSignature}";

        // Tokenize and run inference
        var tokens = await TokenizeAsync(text);
        var embedding = await InferAsync(tokens);

        return embedding; // 384-dimensional vector
    }

    public async Task<float[]> GenerateBehaviorEmbedding(
        List<string> requestPaths,
        List<double> requestTimings)
    {
        // Encode behavior sequence as text
        var behaviorText = EncodeBehaviorSequence(requestPaths, requestTimings);

        // Generate embedding
        var tokens = await TokenizeAsync(behaviorText);
        var embedding = await InferAsync(tokens);

        return embedding;
    }
}
```

### Example: OpenAI Embedding Service

```csharp
using OpenAI;

public class OpenAIEmbeddingService
{
    private readonly OpenAIClient _client;

    public OpenAIEmbeddingService(string apiKey)
    {
        _client = new OpenAIClient(apiKey);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        var response = await _client.GetEmbeddingsAsync(
            "text-embedding-3-small",
            new[] { text });

        return response.Data[0].Embedding; // 1536-dimensional
    }
}
```

## Querying

### Raw SQL Queries

```sql
-- Find signatures similar to a given embedding
SELECT
    id,
    primary_signature,
    1 - (signature_embedding <=> '[0.1,0.2,...]'::vector) AS similarity,
    reputation_score,
    is_known_bot
FROM bot_signatures
WHERE signature_embedding IS NOT NULL
ORDER BY signature_embedding <=> '[0.1,0.2,...]'::vector
LIMIT 10;

-- Combine with filters
SELECT
    id,
    primary_signature,
    1 - (signature_embedding <=> '[0.1,0.2,...]'::vector) AS similarity
FROM bot_signatures
WHERE signature_embedding IS NOT NULL
    AND is_known_bot = true
    AND reputation_score > 0.7
ORDER BY signature_embedding <=> '[0.1,0.2,...]'::vector
LIMIT 10;
```

### Distance Operators

pgvector provides three distance operators:

| Operator | Distance Type | Use Case |
|----------|---------------|----------|
| `<=>` | Cosine distance | Text embeddings (most common) |
| `<->` | Euclidean (L2) | Spatial data, image embeddings |
| `<#>` | Inner product | Pre-normalized vectors |

**Recommendation**: Use `<=>` (cosine) for text/behavior embeddings.

## Performance Tuning

### Index Parameters

| Parameter | Low Traffic | Medium Traffic | High Traffic |
|-----------|-------------|----------------|--------------|
| m | 8 | 16 | 32 |
| ef_construction | 32 | 64 | 128 |

### Query Performance

```sql
-- Check index usage
EXPLAIN ANALYZE
SELECT * FROM bot_signatures
ORDER BY signature_embedding <=> '[...]'::vector
LIMIT 10;

-- Should show "Index Scan using idx_signatures_signature_embedding_hnsw"
```

### Memory Usage

```sql
-- Check index size
SELECT
    schemaname,
    tablename,
    indexname,
    pg_size_pretty(pg_relation_size(indexrelid)) AS index_size
FROM pg_stat_user_indexes
WHERE indexname LIKE '%embedding%';
```

**Approximate Memory:**
- 384 dim, 1M vectors: ~2GB (m=16)
- 768 dim, 1M vectors: ~4GB (m=16)
- 1536 dim, 1M vectors: ~8GB (m=16)

## Migration from Qdrant

If you're currently using Qdrant for vector storage:

### 1. Export Vectors from Qdrant

```python
from qdrant_client import QdrantClient

client = QdrantClient(url="http://localhost:6333")

# Export collection
vectors = client.scroll(
    collection_name="bot_signatures",
    limit=10000,
    with_payload=True,
    with_vectors=True
)

# Save to JSON
import json
with open("vectors.json", "w") as f:
    json.dump(vectors, f)
```

### 2. Import to PostgreSQL

```csharp
// Read exported vectors
var vectors = JsonSerializer.Deserialize<List<QdrantVector>>(
    await File.ReadAllTextAsync("vectors.json"));

// Batch insert to PostgreSQL
foreach (var batch in vectors.Chunk(1000))
{
    await connection.ExecuteAsync(@"
        INSERT INTO bot_signatures (
            id, primary_signature, signature_embedding
        ) VALUES (
            @Id, @Signature, @Embedding::vector
        )
        ON CONFLICT (id) DO UPDATE
        SET signature_embedding = EXCLUDED.signature_embedding
        ",
        batch.Select(v => new {
            Id = v.Id,
            Signature = v.Payload["signature"],
            Embedding = v.Vector
        }));
}
```

### 3. Performance Comparison

| Operation | Qdrant | pgvector | Notes |
|-----------|--------|----------|-------|
| Insert (1k vectors) | ~200ms | ~150ms | pgvector batch insert faster |
| Search (top 10) | ~2ms | ~1ms | pgvector HNSW very fast |
| Filtered search | ~5ms | ~3ms | pgvector leverages SQL indexes |
| Backup | Complex | pg_dump | pgvector much simpler |

## Best Practices

1. **Normalize Vectors** - HNSW works best with unit vectors
2. **Batch Updates** - Use transactions for bulk embedding updates
3. **Monitor Index Size** - HNSW indexes can get large
4. **Combine with Filters** - Use SQL WHERE clauses with vector search
5. **Cache Embeddings** - Don't regenerate for every request
6. **Version Embeddings** - Track which model generated each embedding
7. **A/B Test Models** - Store embeddings from multiple models

## Troubleshooting

### Extension Not Found

```
ERROR: extension "vector" is not available
```

**Solution**: Install pgvector extension
```bash
sudo apt install postgresql-16-pgvector
```

### Index Build Fails

```
ERROR: index creation failed
```

**Solution**: Reduce ef_construction parameter
```sql
DROP INDEX idx_signatures_signature_embedding_hnsw;
CREATE INDEX idx_signatures_signature_embedding_hnsw
    ON bot_signatures USING hnsw (signature_embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 32);  -- Lower from 64
```

### Slow Queries

```sql
-- Check if index is being used
EXPLAIN ANALYZE
SELECT * FROM bot_signatures
ORDER BY signature_embedding <=> '[...]'::vector
LIMIT 10;
```

If not using index, check:
1. Ensure `signature_embedding IS NOT NULL` in WHERE clause
2. Verify index exists: `\di idx_signatures_signature_embedding_hnsw`
3. Run VACUUM ANALYZE: `VACUUM ANALYZE bot_signatures;`

### Dimension Mismatch

```
ERROR: vector dimension mismatch
```

**Solution**: Ensure embedding dimension matches column definition
```sql
-- Check column dimension
SELECT column_name, udt_name
FROM information_schema.columns
WHERE table_name = 'bot_signatures'
    AND column_name LIKE '%embedding%';

-- Alter if needed (requires recreating index)
ALTER TABLE bot_signatures
    ALTER COLUMN signature_embedding TYPE vector(1536);
```

## Further Reading

- [pgvector Documentation](https://github.com/pgvector/pgvector)
- [HNSW Algorithm](https://arxiv.org/abs/1603.09320)
- [PostgreSQL Vector Guide](https://www.postgresql.org/docs/current/pgvector.html)
- [all-MiniLM-L6-v2 Model](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2)
- [OpenAI Embeddings](https://platform.openai.com/docs/guides/embeddings)

## Example: Complete Bot Detection with Vectors

```csharp
public class VectorBasedBotDetector
{
    private readonly IDbConnection _connection;
    private readonly IEmbeddingService _embeddings;

    public async Task<BotDetectionResult> DetectAsync(HttpRequest request)
    {
        // 1. Generate signature
        var signature = GenerateSignature(request);

        // 2. Generate embedding
        var embedding = await _embeddings.GenerateSignatureEmbedding(
            signature.PrimarySignature,
            signature.IpSignature,
            signature.UaSignature);

        // 3. Find similar known signatures
        var similar = await _connection.QueryFirstOrDefaultAsync<SimilarSignature>(@"
            SELECT * FROM find_similar_signatures(
                @Embedding::vector,
                1,  -- Top 1 match
                0.9 -- High similarity threshold
            ) LIMIT 1",
            new { Embedding = embedding });

        // 4. If very similar to known bot, classify as bot
        if (similar?.Similarity > 0.95 && similar.IsKnownBot)
        {
            return new BotDetectionResult
            {
                IsBot = true,
                Confidence = similar.Similarity,
                Reason = $"Similar to known bot: {similar.BotName}",
                Method = "VectorSimilarity"
            };
        }

        // 5. Otherwise, run full detection pipeline
        return await RunFullDetectionAsync(request, signature, embedding);
    }
}
```
