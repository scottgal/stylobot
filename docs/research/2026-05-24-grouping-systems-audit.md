# Grouping Systems Audit — Leiden Cost, HNSW Status, and Where Consolidation Actually Helps

**Date:** 2026-05-24
**Scope:** Every grouping / similarity / clustering / identity-matching system in the codebase. Three parallel deep-reads of 30+ files. File:line citations preserved.

## TL;DR

**Leiden is not the bottleneck.** Leiden's own loop is `O(maxIter × (N + E + N·D·K))` with `maxIter=10`. The cost is in the **N² similarity-graph construction** that feeds it (`BotClusterService.BuildSimilarityGraph`, lines 854-886). For N=500 signatures, graph build is ≈20M cosine ops per cluster cycle; Leiden itself is ≈400K. Graph build dominates by ~30×.

**HNSW was deliberately removed** from FOSS on 2026-05-09 (commit `048185f`). Reason in the commit message: "unbounded LOH growth", JSON-file-backed graph persistence, graph overhead. Replaced by `Slim*` brute-force-cosine-over-bounded-cache + SQLite centroids. The decision was sound for the in-cache similarity-search hot paths -- those see ≤5k vectors per query and brute-force SIMD wins at that scale.

**sqlite-vec IS in the codebase** (`SqliteVecIdentityAnchorIndex`), already proven for the identity-fingerprint anchor with graceful fallback. It is the right primitive to bring KNN back to the *cluster graph build* without resurrecting the old HNSW LOH-bloat impl.

**The duplication people perceive is mostly intentional.** Three centroid stores (signature / session / intent) are *orthogonal concerns*, not three versions of the same thing. Three similarity searches (session 129D / signature 64D / intent 36D) operate on *different vector spaces*. The actual duplication is at the **display-layer grouping** (visitor list, top-bots, sessions list each implement their own collapse rule). That's the real consolidation target.

**Two recommendations:**
1. Build the **BehaviouralGrouper** (display consolidation; uses existing analytical layers as inputs; bot-only gate; the plan in `docs/plans/2026-05-24-behavioural-grouper.md` stands).
2. Add a **KnnGraphBuilder** that feeds Leiden via sqlite-vec when available (analytical-layer speed-up; pattern from `SqliteVecIdentityAnchorIndex`; no HNSW comeback).

Don't merge centroid stores. Don't unify similarity searches. Don't bring back the old HNSW impl.

---

## 1. Inventory

Every system that produces or consumes a "group key" for signatures, mapped against its actual role.

### 1.1 Analytical-layer groupings (background, not on the request hot path)

| System | File | Purpose | Index | Output |
|---|---|---|---|---|
| Leiden clustering | `Clustering/LeidenClustering.cs` | Community detection on weighted graph (CPM, refinement step) | None — takes precomputed graph | Community ID per node |
| BotClusterService | `Services/BotClusterService.cs` | Background cycle: build graph → Leiden → classify → persist | N² brute-force pair similarity over features+centroids (lines 854-886) | `BotCluster` records with `ClusterId` |
| AdaptiveSimilarityWeighter | `Clustering/AdaptiveSimilarityWeighter.cs` | Per-cycle data-driven feature weights from CV / entropy | Trivial O(20·N) | Weights dict |
| SignatureConvergenceService | `Services/SignatureConvergenceService.cs` | Merge / split signature families based on entity-resolution evidence | Family-graph traversal | `signature_merges` table edges |
| VectorCompactionService | `Services/VectorCompactionService.cs` | Compresses old session vectors into centroids (L1/L2) | Reads SessionCentroidStore | Updated centroids with `CompressionLevel ≥ 1` |
| IdentityWeightCalibrationService | `Identity/IdentityWeightCalibrationService.cs` | Periodic global Fisher-discriminant weights | Group-by inferred client type | Global weight vector |

### 1.2 Per-request groupings (read-side, hot path)

| System | File | Purpose | Lookup | Cost |
|---|---|---|---|---|
| ClusterContributor | `Orchestration/ContributingDetectors/ClusterContributor.cs` | At request: is signature in a discovered cluster? | `_clusterService.FindCluster(signature)` — O(1) frozen dict; plus O(C) community affinity scan | Microseconds |
| FingerprintMatchContributor | `Orchestration/ContributingDetectors/FingerprintMatchContributor.cs` | Two-pass match: L1 point lookup, L2 KNN via `IIdentityAnchorIndex` | sqlite-vec when available, brute-force fallback | <1ms on warm cache; tens of ms on cold |
| SimilarityContributor | `Orchestration/ContributingDetectors/SimilarityContributor.cs` | Top-K=5 over `ISignatureSimilaritySearch` | Brute-force SIMD over bounded cache (~5k vectors) | <1ms typical |
| FastPathSignatureMatcher | `Orchestration/FastPathSignatureMatcher.cs` | Multi-factor signature match (IpSubnet/IP/UAFamily/Geo/Headers) | Per-factor comparison, no index | <100µs |

### 1.3 Indexes / stores

| Store | File | Indexed how | Used by |
|---|---|---|---|
| SqliteVecIdentityAnchorIndex | `Identity/SqliteVecIdentityAnchorIndex.cs` | **sqlite-vec (vec0) virtual tables** with L2 distance KNN | FingerprintMatchContributor Pass 2 |
| BruteForceIdentityAnchorIndex | `Identity/BruteForceIdentityAnchorIndex.cs` | Pure C# top-K heap scan | Fallback when vec0 unavailable |
| SqliteSignatureCentroidStore | `Data/SqliteSignatureCentroidStore.cs` | Temporal index only, no KNN | SlimSignatureSimilaritySearch persistence |
| SqliteSessionCentroidStore | `Data/SqliteSessionCentroidStore.cs` | Temporal index only | SlimSessionVectorSearch persistence; BotClusterService centroid reads |
| SqliteIntentCentroidStore | `Data/SqliteIntentCentroidStore.cs` | Temporal index only | SlimIntentSearch persistence |
| SqliteFingerprintStore | `Identity/SqliteFingerprintStore.cs` | Per-table indexes; vec0 mirror when available | FingerprintMatchContributor; absorption service; drift verifier |
| SqliteVectorCentroidStore | `Data/SqliteVectorCentroidStore.cs` | Temporal index only | Cross-system snapshot store (less hot) |

### 1.4 Similarity-search services (the in-process hot caches)

| Service | Dim | Default cache | Index | Caller |
|---|---|---|---|---|
| SlimSessionVectorSearch | 129 | 2k | `BoundedVectorCache` (LFU) + SIMD cosine | VectorCompactionService, dashboard |
| SlimSignatureSimilaritySearch | 64 | 5k | `BoundedVectorCache` (LFU) + SIMD cosine | SimilarityContributor hot path |
| SlimIntentSearch | 36 | 1k | `BoundedVectorCache` (LFU) + SIMD cosine | IntentContributor, IntentLearningHandler |

All three replaced HNSW implementations on 2026-05-09 (`048185f`).

### 1.5 Display-layer groupings (dashboard render)

| Surface | File | Group key | Why |
|---|---|---|---|
| VisitorListCache | `UI/Services/VisitorListCache.cs:282` | Bot name (when `IsGroupableIdentity`) or raw signature | Current rule — collapses Googlebot, Amazonbot etc |
| SbTopBots | `UI/Views/Shared/Components/SbTopBots/Default.cshtml:104` | Same as above (calls `IsGroupableIdentity`) | Same rule |
| Sessions list | `UI/Views/StyloBot/Dashboard/_InvestigateSignatures.cshtml` | Raw signature | No collapse |
| Clusters tab | `UI/Views/StyloBot/Dashboard/_ClustersList.cshtml` | `ClusterId` | Leiden community view |
| Identity tab | `UI/Views/StyloBot/Dashboard/_Identities.cshtml` (commercial) | `identity.fingerprint_id` | Metastable identity layer |

**The duplication:** the first three surfaces each implement their own collapse keying. None reads the rich behavioural data (cluster_id, fingerprint_id, vector cosine) that the analytical layer already provides. The Clusters tab and the Identity tab use those signals but only for their own native views.

---

## 2. Is Leiden actually slow?

No. Detailed numbers:

**Leiden's own complexity** (from `LeidenClustering.cs` audit):
- `LocalMovingPhase` (lines 79-191): `O(maxIter × (N + E + N·D·K))` where D = avg degree, K = avg neighbouring communities per node. With `maxIterations=10`.
- `RefinePhase` (lines 198-269): `O(N + E)` via BFS per community.
- Empirical for N=500, K≈3-5, D≈20-50: ≈400K-2.5M operations per cluster cycle. **Single-digit ms on modern CPUs.**

**Graph construction** (`BotClusterService.BuildSimilarityGraph` lines 854-886, calling `ComputeBlendedSimilarity` line 866):
- Double loop over all pairs: `O(N²)`.
- Per-pair cost: `ComputeSimilarity` evaluates 20 feature dimensions (≈50 ops) plus optionally a 110-dim cosine when both have centroids (≈220 ops via `TensorPrimitives.Dot`).
- For N=500: 124,750 pairs × ~80 ops = **10M ops minimum**, often 25M+ when centroids are present (`BehaviouralAxisActive` path).
- This runs **every 30-60 seconds** (`ClusterIntervalSeconds`, line 270), adaptive under load.

**Where the time goes** (estimate for N=500, behavioural axis on):
- Graph construction: **~95%**
- Leiden local-moving: **~3-4%**
- Leiden refinement: **<1%**
- Adaptive weight computation: **<1%**

Leiden as an algorithm is doing fine. The cycle cost is the **input** Leiden sees, not Leiden itself. This matches the cluster-service comment at `BotClusterService.cs:6`: "typical in bot clustering: <1000 nodes" — that's the operating assumption, and Leiden handles it comfortably.

What changes at N=5000:
- Graph construction: 25M → **2.5B** ops (still O(N²))
- Leiden: 2.5M → 25M ops (linear in N+E)
- Total cycle time: minutes, not seconds. **This is when Leiden's own loop becomes visible** — but the graph-build dominance only widens.

So the real question is: **how big does the operator's signature population get?** For most FOSS deployments on a single host: <500 signatures over the cluster window. For multi-tenant gateways or high-traffic sites: 5k-50k. At 50k, the graph build is the only thing that matters.

---

## 3. HNSW — why it was removed, when it'd help return

### Why it was removed (commit `048185f`, 2026-05-09):

> "delete HNSW implementation files and hnsw-index JSON; replaced by Slim* + SQLite centroids"

The three files deleted (`HnswFileSimilaritySearch`, `HnswSessionVectorSearch`, `HnswIntentSearch`) and their on-disk JSON manifests (`hnsw-index/*.json`) were replaced by `Slim*` services backed by SQLite centroid tables + `BoundedVectorCache`. The XML docs of the replacements name the specific failure modes:

- **"unbounded LOH growth"** — HnswSharp keeps full graph in managed memory; large objects don't compact, fragmenting the heap.
- **"HNSW graph overhead"** — graph metadata (edges, M-neighbours, level pointers) is ~2-3× the raw vector size.
- **"file-system [persistence]"** — JSON index files on disk; slow startup, no atomic durability, no concurrent-read story.

**The verdict was specifically against `HnswSharp + JSON files`, not HNSW-the-algorithm.** Reading the replacement code (`SlimSessionVectorSearch.cs:12`, `SlimSignatureSimilaritySearch.cs:10`), the wins are: bounded LFU cache, SQLite-backed centroids (atomic + crash-safe), brute-force SIMD cosine over a small set is faster than HNSW graph walks anyway at N≤5k.

This is correct for the **in-process similarity-search hot caches** — those exist precisely to serve top-K=5 queries on a small recent corpus. KNN graph indexing buys nothing when N≤5k and the cache is the index.

### Where HNSW (or sqlite-vec) WOULD help:

**Cluster graph construction.** Different workload: all-pairs similarity across N=500-50k signatures, every 30-60 seconds. A KNN index here lets the cluster service build a sparse K-NN graph in `O(N log N)` instead of `O(N²)`.

Algorithmic improvement: KNN-graph Leiden is well-studied; for community detection, using a K-NN graph instead of a full similarity graph usually produces **tighter, more interpretable clusters** (full-graph Leiden tends to over-merge at low resolution). So this isn't just a speed-up — it's likely a quality improvement.

**Identity anchor at scale.** Already covered by `SqliteVecIdentityAnchorIndex`. Validation that the pattern works.

### Why sqlite-vec, not HNSW resurrection:

| | Old HnswSharp impl (deleted) | sqlite-vec (proposed) |
|---|---|---|
| Memory | Unbounded LOH (managed) | Bounded; SQLite buffer-pool managed (native) |
| Persistence | JSON files on disk | SQLite virtual tables, transactional |
| Startup | Reload JSON, rebuild graph | Open connection, ready |
| Concurrency | Single-writer | SQLite WAL + per-conn |
| Available as | Internal C# library | Native extension; auto-fallback when not installed |
| Already proven in stylobot | Removed two weeks ago | `SqliteVecIdentityAnchorIndex` |

sqlite-vec gives us K-NN indexing with the failure modes of the old HNSW impl explicitly fixed by the native runtime. The codebase already has the graceful-fallback pattern (`SqliteFingerprintStore.cs:55-92`) that loads vec0 when available and degrades to brute-force otherwise. Same pattern applies cleanly here.

---

## 4. Where the duplication actually IS

Three centroid stores and three similarity searches are NOT duplication — they're orthogonal feature spaces:

**Centroid stores (intentional separation):**
- `SignatureCentroid` (64D) — "WHO is this request" — request shape, header pattern, UA family
- `SessionCentroid` (129D) — "HOW does this actor behave" — Markov chain, timing, velocity, variance
- `IntentCentroid` (36D) — "WHAT is this actor trying to do" — paths, response codes, attack signals

A unified table would merge unrelated features into one row with optional sparse columns. No win — three queries become one but each consumer would still pull only its own subset.

**Similarity searches (intentional separation):**
- SessionVectorSearch reads the 129D space
- SignatureSimilaritySearch reads the 64D space
- IntentSimilaritySearch reads the 36D space

Different dimensionality, different normalisation, different write paths, different consumers. Sharing the implementation (all three use `BoundedVectorCache` + SIMD cosine) is already the cleanest deduplication possible at the platform level.

**Where the actual duplication lives:**

### 4.1 Display-layer grouping (the real problem)

Three independent collapse rules across three surfaces, none consulting the rich analytical data:

- `VisitorListCache.CollapseGroupable` (line 282): key on bot name OR raw signature.
- `SbTopBots/Default.cshtml` (line 104): same `IsGroupableIdentity` call, independently.
- `_InvestigateSignatures.cshtml`: no collapse at all.

None of these reads `cluster_id`, `identity.fingerprint_id`, session-vector cosine, sequence centroid match, or `/24` rotation. The analytical layer computes all of those. The display layer ignores all of them.

**Fix: BehaviouralGrouper** (separate plan: `docs/plans/2026-05-24-behavioural-grouper.md`). Single grouper consulted by every display surface, ordered hierarchy reading the strongest available signal, **bot-only gate** to ensure humans never group.

### 4.2 cluster_id vs identity.fingerprint_id (a softer overlap)

Both express "this signature's behavioural group". Different lifecycles:
- `cluster_id` from Leiden: rebuilt every 30-60s, can shuffle as clusters reorganise.
- `identity.fingerprint_id` from metastable matcher: stable across IP/UA rotation, evolves slowly via centroid absorption.

These are *complementary*, not duplicates:
- Cluster is a community-detection answer ("here is a population of similar signatures").
- Identity is an entity answer ("this signature IS the same actor as that one").

The bridge that doesn't exist today: a signature in `cluster:c47` whose fingerprint identity is `fp:abc123` -- no surface tells you the cluster has 23 members AND fingerprint `abc123` has 47 observations across them.

The BehaviouralGrouper's hierarchy (Identity > Cluster > Vector > Sequence > Subnet > Name) is the bridge. Identity wins when present; cluster fills in when identity isn't yet stable.

### 4.3 What's not duplication but worth tightening

- `SignatureConvergenceService` (merge/split families) writes `signature_merges` rows. These are read by `SignatureCoordinator.TryGetVerdictAsync` for verdict-cache fall-through but **not by the grouper**. The grouper should consult the merge table as a hard "these signatures ARE the same actor" signal — higher than Cluster, equal-or-just-below Identity.

---

## 5. The HNSW-feeds-Leiden proposal in detail

### Current flow

```
BotClusterService cycle (every 30-60s):
  1. Pull behaviours from SignatureCoordinator
  2. Pull centroids from SqliteFingerprintStore
  3. Build FeatureVectors (40+ scalars + 110D centroid)
  4. BuildSimilarityGraph -- O(N^2) cosine + heuristic blend
  5. LeidenClustering.RunLeiden -- O(maxIter × (N + E + N·D·K))
  6. Classify + persist clusters
  7. NotifyClusterUpdate
```

Step 4 is the cost centre.

### Proposed flow

```
BotClusterService cycle:
  1. Pull behaviours
  2. Pull centroids
  3. Build FeatureVectors
  4. NEW: KnnGraphBuilder.BuildAsync(features, K=20)
     -> If sqlite-vec available: vec0 batched KNN query per node, returns top-K neighbours
     -> Otherwise: fall back to a SIMD-parallelised brute-force top-K scan per node
     Produces sparse adjacency list (N × K edges, not N × N)
  5. LeidenClustering.RunLeiden on the sparse graph
  6. Classify + persist
```

**Mechanics of step 4 with sqlite-vec:**

Each FeatureVector becomes a 110D centroid (or the cluster service's existing 20D feature vector). Insert all N vectors into a vec0 virtual table (or reuse the existing fingerprint store's centroids — same data). For each vector, query top-K=20 nearest. Emit edges `(i, j, similarity)` for each result.

- Insertion: `O(N log N)` amortised in vec0
- Query: `O(N × K × log N)` total
- Memory: SQLite buffer pool, bounded by `cache_size` pragma

For N=500: 500 × 20 × log(500) ≈ 90K ops, vs current 25M. **~270× speedup** at typical N. At N=5000: 500K vs 2.5B ≈ **5000× speedup**.

### Quality consideration

Leiden on a K-NN graph vs full similarity graph: empirically (e.g. Traag et al's KNN-graph experiments), KNN-graph Leiden produces tighter clusters because spurious low-weight edges don't pull communities together. Common settings: K=20-30 for community detection. We'd want a tuning param (`ClusterKnnK`) with default 20.

### What this DOESN'T break

- `ClusterContributor` (request hot path): still reads `_clusterService.FindCluster(signature)` — O(1). No change.
- `BotCluster` record: unchanged.
- Cluster persistence in `SqliteClusterStore`: unchanged.
- All the existing detector signals that depend on cluster metadata (`cluster.community_cluster_id`, etc): unchanged.

The change is entirely inside `BotClusterService.BuildSimilarityGraph` and a new `IKnnGraphBuilder` abstraction with `SqliteVecKnnGraphBuilder` + `BruteForceKnnGraphBuilder` (same pattern as `IIdentityAnchorIndex`).

---

## 6. Recommendations

In priority order:

### R1 — Build BehaviouralGrouper (display-layer consolidation)

Plan already written: `docs/plans/2026-05-24-behavioural-grouper.md`. **Bot-only gate** -- humans never group regardless of behavioural similarity. Hierarchy: Identity > Cluster > Vector cosine > Sequence centroid > Subnet rotation > Friendly name > raw signature. Every display surface consults one grouper.

This is the user-visible fix. ~2.5 days, mostly Razor + one new bridge service. Zero analytical-layer change.

### R2 — KnnGraphBuilder for cluster graph construction

New abstraction:
```csharp
public interface IKnnGraphBuilder
{
    Task<Dictionary<int, List<(int Neighbor, double Weight)>>>
        BuildAsync(IReadOnlyList<FeatureVector> features, int k, CancellationToken ct);
}
```

Two implementations:
- `SqliteVecKnnGraphBuilder` — uses vec0 batched KNN; the FOSS-by-default path when extension is installed.
- `BruteForceKnnGraphBuilder` — SIMD-batched top-K scan, fallback when vec0 unavailable.

`BotClusterService.BuildSimilarityGraph` becomes a 5-line call into the builder. Same pattern as `IIdentityAnchorIndex`. Quality should improve; speed will improve substantially at N > 500.

Estimated: 1-1.5 days. Includes benchmarks against the old brute-force impl to make sure quality (cluster compactness, modularity score) is at-least-equal.

### R3 — Bridge SignatureConvergenceService into the grouper

Family merge data already exists; grouper should consult it as a tier between Identity and Cluster ("these signatures were explicitly merged by entity resolution -- highest possible group-strength below the metastable layer"). Two-line change in `BehaviouralGrouper.GetGroupKeyAsync`.

### Don't:

- **Don't merge the three centroid stores.** Orthogonal concerns; merging trades a clean separation for one polymorphic table nobody queries cleanly.
- **Don't merge the three similarity searches.** Different dimensionality and normalisation; they already share the `BoundedVectorCache` implementation, which is the right level of dedup.
- **Don't bring back HnswSharp + JSON files.** That impl had real failure modes; the deletion was correct. Use sqlite-vec for new KNN needs.
- **Don't try to unify `cluster_id` and `identity.fingerprint_id`.** Different lifecycles; the grouper's hierarchy IS the unification.

---

## 7. Open questions for follow-up

1. **Production N today.** What's the actual signature-count distribution on stylobot.net's cluster cycles? If it's <300, R2's speedup is real-but-not-urgent. If >2000, R2 jumps in priority.
2. **sqlite-vec install story.** R2 assumes the extension is installable on the target deployment. Docker layer addition is trivial; bare-metal needs `apt install` or similar. Need to verify with the deployment pipeline what's already in the gateway image.
3. **Cluster quality benchmark.** Need a fixture set (BDF replay corpus would work) for A/B'ing brute-force-N² vs KNN-graph Leiden. Without it, the "quality should improve" claim is uncalibrated.
4. **Behavioural grouper test corpus.** Same need — a labelled fixture set where we know which signatures SHOULD group is needed for tier-hierarchy tests. The BDF replay scenarios may already provide this.

---

## 8. What I'm NOT recommending (and why)

| Pattern that looks like a fix | Why I'm not proposing it |
|---|---|
| Merge SignatureCentroid + SessionCentroid + IntentCentroid into one table | Three orthogonal feature vectors; merging trades schema clarity for nothing |
| Replace `SlimSessionVectorSearch` with HNSW again | The old impl had real bugs; the Slim impl is correct for in-process hot-cache top-K; HNSW would re-introduce LOH issues |
| Add a "master centroid" that combines all three | Loses the per-domain learning signal; muddies the contributors |
| Move VisitorListCache into the cluster service | Different lifecycles (every request vs every 30s); cache invalidation becomes coupled |
| Auto-group humans by behavioural similarity | Wrong product-wise; would hide real visitor counts; risks false merges on shared NAT |
| Make all dashboard surfaces read directly from cluster_id | Cluster_id is only one of seven possible group keys; reading it directly misses Identity / Vector / Sequence wins |

---

## Appendix A — Audit citations

All file:line citations preserved from the three parallel audits. Available in raw form on request; condensed here:

- Leiden complexity: `Clustering/LeidenClustering.cs:79-191` (LocalMoving), `198-269` (Refine).
- Graph build cost: `Services/BotClusterService.cs:854-886` (BuildSimilarityGraph), `651`, `676-750` (ComputeSimilarity inner).
- Cluster cycle interval: `BotClusterService.cs:228, 270-276`.
- Identity anchor index: `Identity/SqliteVecIdentityAnchorIndex.cs:40-94`, `Identity/BruteForceIdentityAnchorIndex.cs:14-88`, `Identity/IdentitySchema.cs:145-160` (vec0 schema), `Identity/SqliteFingerprintStore.cs:55-142` (vec0 init + fallback).
- Slim search caches: `Similarity/SlimSessionVectorSearch.cs:53-74`, `Similarity/SlimSignatureSimilaritySearch.cs:47-72`, `Similarity/SlimIntentSearch.cs:48-72`.
- BoundedVectorCache: `Similarity/BoundedVectorCache.cs:10-72`.
- Cache size config: `Models/BotDetectionOptions.cs:4006-4039`.
- Vectorizers: `Similarity/FeatureVectorizer.cs:21-129` (64D), `Similarity/IntentVectorizer.cs:22-130` (36D), `Analysis/SessionVector.cs:137-150` (129D).
- HNSW removal commit: `048185f`, 2026-05-09.
