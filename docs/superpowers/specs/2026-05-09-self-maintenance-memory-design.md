# Self-Maintenance and Memory Constraint System Design

**Goal:** Replace three unbounded HNSW in-memory vector indices with bounded ephemeral caches + SQLite compressed centroid store, and cap every other accumulator in the system, so StyloBot runs indefinitely on a Raspberry Pi 4 without operator intervention.

**Architecture:** `SlidingCacheAtom<string, float[]>` as the bounded hot layer (frequency-driven eviction, no persistence). SQLite blob columns as the persistent compressed centroid store (brute-force cosine, zero new dependencies). `VectorCompactionService` Phase 3 rewired to write L1/L2 centroids to SQLite instead of HNSW. All other unbounded accumulators get hard caps with LRU/TTL eviction. Configurable via `SelfMaintenanceOptions` with a `LowMemory` preset targeting Pi4.

**Tech Stack:** C#/.NET 10, SQLite (existing), `SlidingCacheAtom` from `mostlylucid.ephemeral.atoms.slidingcache` (existing local ref), `System.Numerics.Vector<float>` for SIMD cosine similarity.

---

## Root Cause Summary

The immediate cause of the 13 GB LOH was:

1. `HnswFileSimilaritySearch._graphVectors: List<float[]>` grows on every HTTP request (SimilarityLearningHandler fires on `LearningEventType.FullDetection` - every request). No eviction.
2. `AutoSaveInterval = 5 minutes` serializes the full graph to JSON - 104 MB string at demo scale, >1 GB in production. Strings >= 85 KB go to LOH and are collected only on Gen2 GC.
3. `RebuildThreshold` copies the full `_graphVectors` list on rebuild - another LOH allocation.
4. All three indices (`HnswFileSimilaritySearch`, `HnswSessionVectorSearch`, `HnswIntentSearch`) share this pattern.

The architectural error: HNSW is the right data structure for an unbounded approximate nearest neighbour index. It is the wrong data structure for a sliding window "what did this fingerprint do recently" cache. The correct tool is a bounded frequency-aware cache.

---

## Part 1: Vector Similarity Layer Replacement

### Hot Layer: SlidingCacheAtom

Replace all three HNSW classes with thin wrappers around `SlidingCacheAtom<string, float[]>`:

```csharp
// Shared configuration shape (one per index type)
var cache = new SlidingCacheAtom<string, float[]>(
    maxSize: options.SelfMaintenance.SignatureCacheSize,    // default 5000
    slidingExpiration: TimeSpan.FromHours(2),
    absoluteExpiration: TimeSpan.FromHours(24),
    retentionScorer: (key, vec) => _botFlags.GetValueOrDefault(key, false) ? 2.0 : 1.0,
    workCoordinator: coordinator
);
```

The `retentionScorer` gives bot-classified vectors a 2x retention score. Bots are repetitive (high access count → stay hot). Humans are one-off (low access count → evict quickly). This is LFU semantics via the scorer hook.

**Key behavior differences from HNSW:**

| HNSW (removed) | SlidingCacheAtom (new) |
|---|---|
| Unbounded growth | Hard cap at maxSize |
| JSON autosave every 5 min (LOH) | No serialization at all |
| Full graph copy on rebuild | No rebuild |
| Synchronous similarity search | `TryGet` is synchronous + non-blocking |
| Graph loaded from disk at startup | Warmed from SQLite post-request |

### Detection Fast Path

All three interfaces (`ISignatureSimilaritySearch`, `ISessionVectorSearch`, `IIntentSimilaritySearch`) are preserved unchanged. The fast-path method on each becomes:

```csharp
public ValueTask<SimilarityResult?> FindSimilarAsync(float[] queryVector, ...)
{
    // TryGet is sync, non-blocking
    if (!_cache.TryGet(candidateKey, out var cachedVector))
        return ValueTask.FromResult<SimilarityResult?>(null); // miss = no signal

    var sim = CosineSimilarity(queryVector, cachedVector);
    return ValueTask.FromResult<SimilarityResult?>(new SimilarityResult(candidateKey, sim));
}
```

Miss semantics: **no signal this request**. The other 48 detectors still run. This is correct - similarity search is a confidence booster, not a gate.

### Post-Request Cache Warming

`SimilarityLearningHandler` currently calls `_search.AddAsync()` on every request (adding to HNSW). New behavior:

```csharp
// In SimilarityLearningHandler.HandleAsync
// 1. Update hot cache immediately (synchronous, bounded)
_cache.Set(signatureId, vector, metadata);

// 2. Queue async SQLite upsert (background, does not block)
_ = Task.Run(() => _centroidStore.UpsertAsync(signatureId, vector, wasBot, confidence));
```

On cache miss during detection: after the request completes, `ILearningEventBus` fires. The handler reads from SQLite centroids and warms the cache for the next request from this fingerprint.

### Persistent Layer: SQLite Centroid Tables

Three new tables in the existing SQLite database (created in `DatabaseMigrator`):

```sql
CREATE TABLE IF NOT EXISTS signature_centroids (
    signature_id TEXT PRIMARY KEY,
    vector       BLOB    NOT NULL,   -- float32 LE packed bytes
    was_bot      INTEGER NOT NULL DEFAULT 0,
    confidence   REAL    NOT NULL DEFAULT 0.5,
    access_count INTEGER NOT NULL DEFAULT 0,
    updated_at   INTEGER NOT NULL    -- Unix epoch seconds
);
CREATE INDEX IF NOT EXISTS idx_sigc_updated ON signature_centroids(updated_at);

CREATE TABLE IF NOT EXISTS session_centroids (
    signature_id TEXT PRIMARY KEY,
    vector       BLOB    NOT NULL,
    cluster_id   TEXT,
    updated_at   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sesc_updated ON session_centroids(updated_at);

CREATE TABLE IF NOT EXISTS intent_centroids (
    signature_id TEXT PRIMARY KEY,
    vector       BLOB    NOT NULL,
    intent_class INTEGER NOT NULL DEFAULT 0,
    updated_at   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_intc_updated ON intent_centroids(updated_at);
```

**Vector serialization:** `MemoryMarshal.AsBytes(vector.AsSpan())` → `byte[]`. Zero copy. `MemoryMarshal.Cast<byte, float>()` to deserialize.

**Brute-force cosine:** At compressed centroid scale (L1/L2: hundreds to a few thousand rows), load all blobs, deserialize, compute dot product via `System.Numerics.Vector<float>`. At 5,000 rows × 64 dims = 1.28 MB of data. SIMD scan completes in ~1-2 ms on Pi4. This runs post-request (async background), never on the detection fast path.

**Retention:** `VectorCompactionService` nightly prune deletes rows where `updated_at < now - CentroidRetentionDays`.

### VectorCompactionService Phase 3 Rewire

Current Phase 3 calls `ISessionVectorSearch.GetAllVectorsSnapshot()` then `ReplaceAllAsync()` to rebuild HNSW with L1/L2 centroids. New Phase 3:

1. Read L1 centroids from `VectorCompactionService` (unchanged computation - k-means over session snapshots)
2. Upsert centroids to `session_centroids` table via `ISessionCentroidStore`
3. Warm `SlidingCacheAtom` with top-N most-accessed centroids (bounded by cache size)
4. Delete raw session snapshots from `sessions` table older than `CompactionRetentionDays`

`GetAllVectorsSnapshot()` and `ReplaceAllAsync()` remain on `ISessionVectorSearch` but are now delegated to the SQLite store - `ReplaceAllAsync` upserts, `GetAllVectorsSnapshot` reads all blobs.

### Files Changed: Vector Layer

| Action | File |
|---|---|
| Delete | `Similarity/HnswFileSimilaritySearch.cs` |
| Delete | `Similarity/HnswSessionVectorSearch.cs` |
| Delete | `Similarity/HnswIntentSearch.cs` |
| Create | `Similarity/SlimSignatureSimilaritySearch.cs` |
| Create | `Similarity/SlimSessionVectorSearch.cs` |
| Create | `Similarity/SlimIntentSearch.cs` |
| Create | `Data/Contracts/ISignatureCentroidStore.cs` |
| Create | `Data/Contracts/ISessionCentroidStore.cs` |
| Create | `Data/Contracts/IIntentCentroidStore.cs` |
| Create | `Data/SqliteSignatureCentroidStore.cs` |
| Create | `Data/SqliteSessionCentroidStore.cs` |
| Create | `Data/SqliteIntentCentroidStore.cs` |
| Modify | `Services/VectorCompactionService.cs` (Phase 3 only) |
| Modify | `Services/SimilarityLearningHandler.cs` |
| Modify | `Extensions/ServiceCollectionExtensions.cs` |
| Modify | `Models/BotDetectionOptions.cs` (add SelfMaintenanceOptions, remove HnswOptions) |
| Modify | `Data/DatabaseMigrator.cs` (add 3 new tables) |

`hnsw-index/*.json` files can be deleted. Startup warmup reads from SQLite instead.

---

## Part 2: Other Accumulator Caps

### MarkovTracker._cohortBaselines (UNBOUNDED → CAPPED)

Currently `ConcurrentDictionary<string, CohortBaseline>` with no size limit. `_signatureChains` already has `MaxTrackedSignatures` LRU eviction - cohort baselines need the same treatment.

Fix: when `_cohortBaselines.Count >= SelfMaintenance.MarkovCohortSize`, evict the least-recently-updated cohort before inserting a new one. Track last-update timestamp per cohort in a parallel `ConcurrentDictionary<string, long>`.

Default: `MarkovCohortSize = 10_000`. Pi4 preset: `2_000`.

### CentroidSequenceStore._staleEndpoints (UNBOUNDED → CAPPED)

`ConcurrentDictionary<string, DateTimeOffset>` of asset paths with no eviction beyond the TTL check on read. In practice bounded by the number of deployed endpoints, but misconfigured or scanned sites can accumulate thousands of arbitrary paths.

Fix: when count exceeds `SelfMaintenance.StaleEndpointTrackerSize` (default 5,000), evict entries with `DateTimeOffset` older than `StalenessWindowHours` first. If still over limit, evict oldest entries.

### DeploymentNormTracker._buckets (SOFT-BOUNDED → HARD-CAPPED)

`ConcurrentDictionary<string, BucketState>` grows with `feature × bucket` combinations. The halving policy keeps values small but does not limit key count. A scanned or crawled site can introduce thousands of unique query param combinations as bucket keys.

Fix: hard cap at `SelfMaintenance.DeploymentNormMaxBuckets` (default 50,000). When at limit, log a warning and skip adding new bucket keys. Existing entries continue to function. Nightly prune via `VectorCompactionService` removes entries where `Total == 0`.

### MonitoringPacks Metrics Dictionaries (LOW RISK, ADD GUARD)

`_counters`, `_gauges`, `_histograms` dictionaries grow with metric names. In practice bounded by the number of registered .NET meters, but a bug could cause unbounded growth.

Fix: add a guard log at startup if any metrics dictionary exceeds 10,000 entries. No hard cap needed - this is an observability concern, not a runtime safety concern.

### SessionVector._activeSignatures (TTL-BOUNDED → HARD LIMIT)

`ConcurrentDictionary<string, FingerprintContext?>` relies on `SessionFinalized` events firing reliably. Under sustained bot traffic (thousands of unique fingerprints per minute), events may queue up.

Fix: add `SelfMaintenance.MaxActiveSessions` (default 10,000). When the dictionary size reaches this limit, evict entries with no `FingerprintContext` (null entries first), then by oldest last-access timestamp. Eviction runs on a 1-minute timer, not on every write (amortized cost).

---

## Part 3: SelfMaintenanceOptions Configuration

Replace the incorrectly-added `HnswOptions` in `BotDetectionOptions.cs` with:

```csharp
public sealed class SelfMaintenanceOptions
{
    // Vector similarity hot caches (SlidingCacheAtom max entries)
    public int SignatureCacheSize { get; set; } = 5_000;
    public int SessionCacheSize   { get; set; } = 2_000;
    public int IntentCacheSize    { get; set; } = 1_000;

    // SQLite centroid retention
    public int CentroidRetentionDays { get; set; } = 30;

    // Behavioral accumulator bounds
    public int MaxActiveSessions       { get; set; } = 10_000;
    public int MarkovCohortSize        { get; set; } = 10_000;
    public int StaleEndpointTrackerSize { get; set; } = 5_000;
    public int DeploymentNormMaxBuckets { get; set; } = 50_000;

    // Compaction schedule
    public TimeSpan CompactionInterval { get; set; } = TimeSpan.FromHours(12);

    /// Pi4 / low-memory preset. Set SelfMaintenance = SelfMaintenanceOptions.LowMemory in DI setup.
    public static SelfMaintenanceOptions LowMemory => new()
    {
        SignatureCacheSize      = 1_000,
        SessionCacheSize        = 500,
        IntentCacheSize         = 300,
        MaxActiveSessions       = 2_000,
        MarkovCohortSize        = 2_000,
        StaleEndpointTrackerSize = 1_000,
        DeploymentNormMaxBuckets = 10_000,
        CompactionInterval      = TimeSpan.FromHours(6),
    };
}
```

appsettings.json surface:

```json
{
  "BotDetection": {
    "SelfMaintenance": {
      "SignatureCacheSize": 1000,
      "SessionCacheSize": 500,
      "IntentCacheSize": 300,
      "CompactionInterval": "06:00:00"
    }
  }
}
```

---

## Part 4: Startup Warmup

On startup, `SessionVectorWarmupService` (already registered as `IHostedService`) reads the top-N most-recently-updated entries from each SQLite centroid table and pre-populates the corresponding `SlidingCacheAtom`. N = cache size. This replaces the current HNSW `LoadAsync()` file read.

```csharp
var rows = await _centroidStore.GetRecentAsync(limit: _options.SignatureCacheSize);
foreach (var (id, vector, wasBot) in rows)
    _cache.Set(id, vector, new VectorMeta { WasBot = wasBot });
```

No JSON files. No 104 MB LOH strings. Cold start time: SQLite read of N×64 floats - trivial.

---

## Part 5: Lifecycle Events Removed

The following are eliminated entirely:

- `AutoSaveInterval` timer in all three HNSW classes (JSON serialization)
- `SaveAsync()` / `LoadAsync()` with JSON file I/O
- `RebuildThreshold` full-copy rebuilds
- `hnsw-index/` directory and all `.meta.json` / `.vectors.json` files

The `ISimilarityIndex.SaveAsync()` / `LoadAsync()` methods become no-ops on the new implementations (retain on interface for backwards compatibility, call them from warmup/shutdown if needed for interface compliance, but they do nothing).

---

## Part 6: Expected Memory Envelope

**Pi4 target (LowMemory preset):**

| Component | Before | After |
|---|---|---|
| Signature similarity index | Unbounded (LOH) | ~1K entries × 64 floats × 4B = ~256 KB |
| Session similarity index | Unbounded (LOH) | ~500 entries × 129 floats × 4B = ~258 KB |
| Intent similarity index | Unbounded (LOH) | ~300 entries × 36 floats × 4B = ~43 KB |
| JSON autosave buffers | 104-500 MB LOH every 5 min | 0 |
| Total vector layer | 13+ GB LOH | <1 MB |
| Markov cohort baselines | Unbounded | ~2K entries × ~500B avg = ~1 MB |
| Session tracker | Unbounded | ~2K entries × ~2 KB avg = ~4 MB |
| **Total addressable** | **20+ GB** | **<50 MB** |

---

## What Does Not Change

- `ISignatureSimilaritySearch`, `ISessionVectorSearch`, `IIntentSimilaritySearch` - interfaces unchanged
- All 49 detector contributors - no changes
- `VectorCompactionService` Phase 1 (bucket pruning) and Phase 2 (session compaction) - unchanged
- `EphemeralPatternReputationCache` - already bounded (10K hard cap)
- `BehavioralPatternAnalyzer` - already bounded (IMemoryCache, 50 paths/100 timings per identity)
- `DriftDetectionHandler` - already bounded (10K patterns, 50 samples each)
- `SessionEscalationService` - already bounded (35-min TTL, timer eviction)
- `ReactiveSignalTracker` - already bounded (40 events per signature, 2h TTL)
- `AssetHashStore` - already bounded (24h TTL, hourly eviction)

---

## Out of Scope

- sqlite-vec native extension (upgrade path, not required at centroid scale)
- PostgreSQL centroid tables (commercial: native pgvector handles this already)
- Per-table SQLite WAL tuning for Pi4 write throughput
- Distributed cache coordination for multi-node deployments
