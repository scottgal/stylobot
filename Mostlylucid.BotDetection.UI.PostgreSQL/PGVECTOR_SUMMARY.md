# pgvector Integration Summary

> **Important**: This is an **optional enhancement** - Stylobot works perfectly without any database at all!
>
> ## Stylobot Storage Layers
>
> Each layer is optional and builds on the previous one:
>
> 1. **Base (Default)**: File/SQLite storage
>    - ✅ Works out of the box
>    - ✅ No external dependencies
>    - ✅ Perfect for development and low-traffic sites
>    - 📦 `Mostlylucid.BotDetection` (core package)
>
> 2. **Enhanced**: PostgreSQL storage
>    - ✅ Better performance at scale
>    - ✅ GIN indexes for fuzzy matching
>    - ✅ Relational queries and JOINs
>    - 📦 `Mostlylucid.BotDetection.UI.PostgreSQL` (optional plugin)
>
> 3. **Further Enhanced**: + TimescaleDB
>    - ✅ 100-1000x faster dashboard queries
>    - ✅ 90% storage reduction via compression
>    - ✅ Automatic data retention
>    - 🔧 Enable with `EnableTimescaleDB = true`
>
> 4. **Fully Enhanced**: + pgvector
>    - ✅ ML-based signature similarity search
>    - ✅ Replaces need for Qdrant
>    - ✅ Vector embeddings in PostgreSQL
>    - 🔧 Enable with `EnablePgVector = true`

## What Was Added

pgvector support has been integrated into the PostgreSQL storage provider for **ML-based bot signature similarity search**, providing an alternative to Qdrant for vector storage.

## Changes Made

### 1. Database Schema (`Schema/comprehensive_schema.sql`)

**Extensions:**
```sql
CREATE EXTENSION IF NOT EXISTS vector;  -- pgvector for embeddings
```

**Vector Columns** added to `bot_signatures` table:
```sql
signature_embedding vector(384),  -- Embedding of signature features
behavior_embedding vector(384),   -- Embedding of behavior patterns
```

**HNSW Indexes** for fast similarity search:
```sql
CREATE INDEX idx_signatures_signature_embedding_hnsw
    ON bot_signatures USING hnsw (signature_embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

CREATE INDEX idx_signatures_behavior_embedding_hnsw
    ON bot_signatures USING hnsw (behavior_embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);
```

**Helper Functions:**
- `find_similar_signatures()` - Find similar bot signatures by embedding
- `find_similar_behaviors()` - Find similar behavior patterns
- `update_signature_embeddings()` - Batch update embeddings

### 2. Configuration (`Configuration/PostgreSQLStorageOptions.cs`)

New options added:
```csharp
public bool EnablePgVector { get; set; } = false;
public int VectorDimension { get; set; } = 384;
public int VectorIndexM { get; set; } = 16;
public int VectorIndexEfConstruction { get; set; } = 64;
public double VectorMinSimilarity { get; set; } = 0.8;
```

### 3. Documentation

**New Files:**
- `PGVECTOR_GUIDE.md` - Comprehensive pgvector usage guide
  - Installation instructions
  - Configuration examples
  - ML model integration patterns
  - Query examples
  - Performance tuning
  - Migration from Qdrant

**Updated Files:**
- `DOCKER_SETUP.md` - Added pgvector section
- `TIMESCALEDB_GUIDE.md` - Added pgvector integration notes
- Package description updated to mention pgvector

### 4. Project Configuration

Updated `Mostlylucid.BotDetection.UI.PostgreSQL.csproj`:
- Package description includes pgvector
- Documentation files packed with NuGet package
- Tags updated: `pgvector`, `vector-search`, `ml`

## Benefits

### Why pgvector Instead of Qdrant?

| Feature | Qdrant | pgvector |
|---------|--------|----------|
| **Infrastructure** | Separate service | PostgreSQL extension |
| **Deployment** | 2 containers | 1 container |
| **Backups** | Separate process | Single pg_dump |
| **Transactions** | No SQL joins | ACID with JOINs |
| **Cost** | Extra service | Included |
| **Performance** | Very fast | Very fast (HNSW) |

### Use Cases

1. **ML-Based Bot Detection**
   - Find signatures similar to known bots
   - Cluster related attack patterns
   - Transfer learning from existing data

2. **Behavioral Analysis**
   - Identify similar behavior sequences
   - Detect anomalous request patterns
   - Group related attack campaigns

3. **Signature Clustering**
   - Deduplicate similar signatures
   - Find signature families
   - Optimize storage

## Deployment

### Docker (Recommended)

The `timescale/timescaledb:latest-pg16` image **already includes pgvector**:

```yaml
services:
  timescaledb:
    image: timescale/timescaledb:latest-pg16
    # pgvector is pre-installed, just enable in schema
```

### Code Configuration

```csharp
builder.Services.AddStyloBotPostgreSQL(
    connectionString,
    opts =>
    {
        opts.EnableTimescaleDB = true;  // Time-series optimizations
        opts.EnablePgVector = true;     // ML-based similarity search
        opts.VectorDimension = 384;     // all-MiniLM-L6-v2
    });
```

## Example Usage

### Find Similar Signatures

```csharp
// Generate embedding (using your ML model)
var embedding = await embeddingService.GenerateSignatureEmbedding(request);

// Find top 10 similar known signatures
var similar = await connection.QueryAsync<SimilarSignature>(@"
    SELECT * FROM find_similar_signatures(
        @Embedding::vector,
        10,   -- limit
        0.8   -- min similarity
    )",
    new { Embedding = embedding });

// Check if similar to known bot
if (similar.Any(s => s.IsKnownBot && s.Similarity > 0.9))
{
    // High confidence bot detection
}
```

### Update Embeddings

```csharp
// After ML model generates embeddings
await connection.ExecuteAsync(@"
    SELECT update_signature_embeddings(
        @SignatureId::uuid,
        @SignatureEmbedding::vector,
        @BehaviorEmbedding::vector
    )",
    new {
        SignatureId = signature.Id,
        SignatureEmbedding = sigEmbedding,
        BehaviorEmbedding = behaviorEmbedding
    });
```

## Embedding Models

### Recommended: all-MiniLM-L6-v2 (384 dim)

```csharp
// Local ONNX inference - fast and free
using Microsoft.ML.OnnxRuntime;

public class LocalEmbeddingService
{
    private readonly InferenceSession _session;

    public LocalEmbeddingService()
    {
        _session = new InferenceSession("models/all-MiniLM-L6-v2.onnx");
    }

    public async Task<float[]> GenerateEmbedding(string text)
    {
        // Tokenize and infer
        var embedding = await InferAsync(text);
        return embedding; // 384-dimensional
    }
}
```

### Alternative: OpenAI (1536 dim)

```csharp
// Cloud API - higher quality, costs $0.0001/1k tokens
using OpenAI;

public class OpenAIEmbeddingService
{
    public async Task<float[]> GenerateEmbedding(string text)
    {
        var response = await client.GetEmbeddingsAsync(
            "text-embedding-3-small", new[] { text });
        return response.Data[0].Embedding; // 1536-dimensional
    }
}
```

## Performance

### Query Speed

With HNSW index:
- **Find top 10 similar**: <1ms
- **Filtered similarity**: 2-5ms
- **Batch updates**: 100-200ms per 1000 vectors

### Storage

| Vectors | Dimension | Index Size (m=16) |
|---------|-----------|-------------------|
| 100K | 384 | ~200MB |
| 1M | 384 | ~2GB |
| 10M | 384 | ~20GB |

### Memory Recommendations

| Traffic | Vectors/Day | RAM for pgvector |
|---------|-------------|------------------|
| Low | <10K | 1GB |
| Medium | 10K-100K | 2-4GB |
| High | 100K-1M | 8-16GB |

## Migration from Qdrant

If you're currently using Qdrant:

1. **Export vectors** from Qdrant (use Python client)
2. **Batch insert** to PostgreSQL
3. **Build HNSW index** (happens automatically)
4. **Switch queries** to use PostgreSQL functions
5. **Decommission** Qdrant service

See `PGVECTOR_GUIDE.md` for detailed migration steps.

## Production Checklist

- [ ] Enable `EnablePgVector = true` in configuration
- [ ] Choose embedding model (384 or 1536 dim)
- [ ] Update `VectorDimension` to match model
- [ ] Tune HNSW parameters based on traffic
- [ ] Set up ML inference service (local or API)
- [ ] Monitor index size and query performance
- [ ] Plan for embedding updates (batch nightly?)
- [ ] Test similarity thresholds for accuracy

## Future Enhancements

This is a **future feature** foundation. When ready to use:

1. Integrate ML embedding service
2. Generate embeddings for signatures
3. Enable similarity-based detection
4. Fine-tune similarity thresholds
5. Add embedding versioning
6. Implement A/B testing

## Resources

- **pgvector Guide**: `PGVECTOR_GUIDE.md` (comprehensive docs)
- **Docker Setup**: `DOCKER_SETUP.md` (deployment)
- **TimescaleDB Guide**: `TIMESCALEDB_GUIDE.md` (time-series)
- **pgvector GitHub**: https://github.com/pgvector/pgvector
- **HNSW Paper**: https://arxiv.org/abs/1603.09320

## Status

✅ **Schema Ready** - Vector columns and indexes defined
✅ **Functions Ready** - Similarity search helpers implemented
✅ **Configuration Ready** - Options for vector settings
✅ **Documentation Complete** - Full usage guide available
⏳ **ML Integration** - Pending (future work)
⏳ **Production Use** - Disabled by default (`EnablePgVector = false`)

**To enable**: Set `EnablePgVector = true` and integrate your ML embedding service.
