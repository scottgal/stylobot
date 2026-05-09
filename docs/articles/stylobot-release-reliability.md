# StyloBot Release Series: From Unbounded Vector Indexes to Self-Maintaining Memory

*How a periodic reliability review led to a complete architectural rethink of StyloBot's vector similarity layer - and what "self-maintenance" actually means for a bot detection product you can leave running on a Pi4.*

---

## A Reliability Review Exposes the Boundaries

As StyloBot grows, I periodically step back from feature work and review the reliability envelope of the system. A recent 6.x change to session compression made the memory shape of the vector layer much more visible during one of those reviews, and the `Mostlylucid.BotDetection.Demo` process showed a 20 GB resident set size while handling synthetic test traffic. That number should not be sustainable.

A few `dotnet-counters` commands later:

```
dotnet.gc.last_collection.heap.size[loh]   13,393,217,096 bytes (13.4 GB)
dotnet.gc.heap.total_allocated              97 MB/sec
dotnet.exceptions[SqliteException]          4/sec
```

The Large Object Heap was 13.4 GB and growing at nearly 100 MB/sec. Something was allocating enormous objects continuously, and the Gen2 GC couldn't keep up with them.

In .NET, any object larger than 85 KB is allocated directly on the Large Object Heap rather than the standard generational heap. LOH objects are only collected during Gen2 GC, which is expensive and infrequent. Once the LOH is fragmented, you lose that memory until the process restarts.

The review surfaced three classes that were safe at small scale but no longer matched the long-running profile I wanted for StyloBot.

## Three Unbounded HNSW Indices

StyloBot performs three kinds of vector similarity search during detection:

1. **Signature similarity**: given this request's 64-dimensional feature vector, find similar past requests to boost bot confidence based on past outcomes
2. **Session similarity**: given a 129-dimensional Markov chain behavioral vector, find similar past sessions for entity resolution and drift detection
3. **Intent classification**: given a 36-dimensional intent vector, find similar past intent patterns so future sessions don't need LLM re-classification

For all three, I'd implemented HNSW (Hierarchical Navigable Small World) graphs the algorithm underlying most production vector databases. HNSW gives sub-millisecond approximate nearest-neighbour search. It's excellent technology.

The algorithm was not the issue. The issue was the fit between the data structure and the runtime pattern.

Each HNSW index had a field like this:

```csharp
private readonly List<float[]> _graphVectors = new();
```

The learning handler that fed these indices subscribed to `LearningEventType.FullDetection`, which fires on **every HTTP request**. So every request added a vector. Every request. There was no eviction.

The main LOH pressure came from the autosave timer:

```csharp
private readonly TimeSpan AutoSaveInterval = TimeSpan.FromMinutes(5);
```

Every five minutes, each index serialized its entire graph to JSON and wrote it to disk. At demo scale, `signatures.vectors.json` was 104 MB. In production I found `intent.meta.json` at 70 MB and `intent.vectors.json` at 51 MB. JSON serialization creates a contiguous string in memory at 100+ MB, and that string goes straight to the LOH. Three indices, every five minutes, continuously growing.

## The Architectural Boundary

The real conclusion from the review was simple: HNSW was being used for a caching problem.

HNSW is the right algorithm for indexing a **stable or slowly-changing corpus** and querying it for approximate nearest neighbours. It's what Pinecone, Weaviate, and pgvector use internally. It's designed to index millions of vectors and search them in microseconds.

What I actually needed was: *"for each active fingerprint, keep a small window of recent behavioral vectors in memory so detection can compare the current request against past behavior from similar fingerprints."*

That's a cache. Specifically, it's a frequency-aware bounded cache where bot fingerprints (which are repetitive) should stay hot, and human fingerprints (which are one-off) should evict quickly.

HNSW has no eviction. That's not a bug in HNSW - eviction is outside its design scope. The review clarified that a bounded cache was the right abstraction for this part of the product.

The SQLite vector store is a pragmatic bridge, not the end state. Long term, I want the hot path to stay simple enough that the system does not need a separate VSS-style vector store just to stay stable.

## The Refinement: Bounded Hot Cache + SQLite Centroid Store

The replacement architecture has two layers:

**Hot layer:** A bounded `BoundedVectorCache<TEntry>` - a thin wrapper around `ConcurrentDictionary` with access-frequency priority eviction. The retention scorer gives bot-classified entries a 2x survival weight, so the cache self-organizes to keep exactly what's useful for detection:

```csharp
retentionScorer: (_, entry) => entry.WasBot ? 2.0 : 1.0
```

Bots are repetitive - same fingerprint, same behavior, high access count. Humans are one-off - low access count, evict quickly. No manual tuning needed; the traffic pattern does the work.

**Persistent layer:** Three new SQLite tables (`signature_centroids`, `session_centroids`, `intent_centroids`) storing compressed centroids from the nightly `VectorCompactionService`. Vectors are stored as raw float32 blobs:

```csharp
internal static byte[] PackFloats(float[] v) =>
    MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
```

Compact binary serialization. No 100+ MB JSON strings. No LOH.

**Similarity search on the persistent layer** is brute-force cosine over all rows. At compressed centroid scale (hundreds to a few thousand rows after L1/L2 compaction), loading all blobs and scanning with SIMD takes ~1-2 ms on a Pi4. That's fine: this only runs post-request in background handlers, never on the detection fast path.

## Miss Semantics

The detection fast path is synchronous. When a request comes in, the similarity search does a `TryGet` - a non-blocking synchronous dictionary lookup:

```csharp
if (!_cache.TryGet(signatureId, out var entry))
    return null; // no signal this request - other 48 detectors still run
```

Cache miss means *no similarity signal this request*. The other 48 detectors still run. After the request completes, a background learning event handler queries SQLite and warms the cache for next time. The fast path never blocks on a database query.

This is the correct model. Similarity search is a confidence booster, not a gate. A miss is fine.

## The Full Accumulator Audit

As part of the same review, I audited every singleton accumulator in the codebase. Some were already well-bounded:

- `EphemeralPatternReputationCache`: hard cap at 10,000 entries with background decay and LRU eviction
- `BehavioralPatternAnalyzer`: IMemoryCache with per-identity limits (50 paths, 100 timings, 15-min TTL)
- `DriftDetectionHandler`: 10,000 patterns × 50 samples, TTL-pruned
- `SessionEscalationService`: 35-minute TTL with timer-driven eviction

One needed attention beyond the HNSW classes: `MarkovTracker._cohortBaselines`. The Markov tracker maintains per-cohort baseline transition matrices (separate from per-signature chains, which already had LRU eviction at `MaxTrackedSignatures`). The cohort baselines one per traffic cohort like "datacenter-new", "residential-returning", or by cluster ID had no eviction at all. The refinement is to evict the cold cohorts (fewest total transitions) when the dictionary exceeds `SelfMaintenanceOptions.MarkovCohortSize`.

## SelfMaintenanceOptions: Configurable Bounds for Everything

All limits are now configurable under `BotDetection:SelfMaintenance` in appsettings.json. The defaults work for a standard server. For Pi4 or other constrained hardware, there's a `LowMemory` static preset:

```csharp
public static SelfMaintenanceOptions LowMemory => new()
{
    SignatureCacheSize  = 1_000,
    SessionCacheSize    = 500,
    IntentCacheSize     = 300,
    MarkovCohortSize    = 2_000,
    CacheSlidingExpiration = TimeSpan.FromHours(1),
};
```

Wire it up:

```csharp
builder.Services.AddBotDetection(opts =>
{
    opts.SelfMaintenance = SelfMaintenanceOptions.LowMemory;
});
```

Or configure via appsettings.json for environment-specific tuning:

```json
{
  "BotDetection": {
    "SelfMaintenance": {
      "SignatureCacheSize": 1000,
      "SessionCacheSize": 500,
      "IntentCacheSize": 300,
      "CentroidRetentionDays": 14
    }
  }
}
```

## Memory Envelope: Before and After

| Component | Before | After (LowMemory preset) |
|---|---|---|
| Signature HNSW index | Unbounded LOH | ~256 KB hot cache |
| Session HNSW index | Unbounded LOH | ~258 KB hot cache |
| Intent HNSW index | Unbounded LOH | ~43 KB hot cache |
| JSON autosave buffers | 100-500 MB LOH every 5 min | 0 |
| Markov cohort baselines | Unbounded | ~1 MB (2K cap) |
| Total vector layer | **13+ GB LOH** | **<6 MB** |

The detection model is unchanged. What changed is where similarity evidence lives and when it is allowed to affect the fast path. The centroids are preserved in SQLite and survive restarts. The nightly compaction still computes L1/L2 centroids. The difference is that none of this requires unbounded memory growth.

## What This Means for Long-Running Deployments

The original architecture would eventually exhaust memory on any machine, given enough traffic and time. The new architecture has a predictable memory envelope determined entirely by the configured cache sizes. Once the cache fills with hot entries, new entries evict cold ones. Memory usage reaches steady-state and stays there.

On a Pi4 with the LowMemory preset: the process should use well under 500 MB RSS after warmup, regardless of how long it runs or how much traffic it has seen. The nightly compaction prunes old centroid data from SQLite, so the database doesn't grow without bound either.

That's what "self-maintenance" means: you configure it once, point it at hardware, and it runs. It doesn't require a restart every week to reclaim memory. It doesn't require operator intervention when a bot campaign spikes traffic. It just works.

That is the product intent: StyloBot should be boring in production. Leave it running on a Pi, and it should take care of itself.

## The Learning

The LOH problem was architectural, not algorithmic. HNSW is excellent; it's widely used in production vector search systems. The key lesson was choosing it for an indexing problem only where it truly fits, and using a bounded cache where the runtime pattern calls for one.

When you see unbounded memory growth in a production system, the question to ask is not "how do we make this structure more efficient?" It's "is this the right data structure for what we're actually doing?" In this case, the answer was no, and the replacement was simpler than the original.

The hardest part of this refinement wasn't the code - it was resisting the urge to add a cap. Adding a cap would have bounded the memory without changing the architecture. The JSON autosave would still have created LOH strings every five minutes. The structure would still have been wrong. The cap would have hidden the problem while leaving the root cause intact.

Cap the hot path. Compress the history. Fix the shape, not the symptom.

---

*The implementation plan for this work is at `docs/superpowers/plans/2026-05-09-self-maintenance-memory.md`. The spec is at `docs/superpowers/specs/2026-05-09-self-maintenance-memory-design.md`.*
