# Session Analytics: Temporal Compression and Behavioral Intelligence

StyloBot compresses per-request data into session-level behavioral vectors and applies four
complementary analytics layers to detect bots, identify rotation patterns, and flag genuinely
novel behavior for escalation.

## Architecture overview

```
Requests → SessionStore (ring buffer) → FinalizeSession
                                               │
                    ┌──────────────────────────┼──────────────────────────┐
                    │                          │                          │
           FrequencyFingerprint          DriftVector               SessionVector
           (8D autocorrelation)       (OLS regression          (129-dim Markov chain
                                        slope over N             + fingerprint)
                                         sessions)
                    │                          │                          │
                    └──────────────────────────┼──────────────────────────┘
                                               │
                                         SQLite sessions table
                                         (frequency_fingerprint BLOB,
                                          drift_vector BLOB)
                                               │
                                         HNSW index (in memory,
                                          file-backed on disk)
                                         SessionVectorMetadata:
                                          - VelocityVector
                                          - FrequencyFingerprint
                                          - DriftVector
                                          - VarianceVector (after compaction)
```

---

## Feature 1: Frequency fingerprinting

**What it does:** Encodes the temporal rhythm of a session as an 8-dimensional autocorrelation
vector at lag scales `[1s, 3s, 10s, 30s, 1m, 3m, 10m, 30m]`.

**Why it matters:** Human browsing is aperiodic (broadband spectrum). Bot traffic has clean
periodicity: scraper retry loops produce 30s spikes, credential stuffers have sub-second bursts,
crawlers run at fixed intervals. The periodicity score is the RMS deviation from the flat
(broadband) baseline -- high score = bot-like timing regularity.

**Cross-session rhythm matching:** If the current session is periodic AND prior sessions for
the same signature share the same dominant rhythm (cosine similarity > threshold), the bot
changed what it requests but kept the same mechanical timing loop. This is flagged as a
rhythm-preserving rotation -- a strong evasion signal.

**Key signals emitted:**

| Signal key | Type | Description |
|---|---|---|
| `session.frequency_fingerprint` | `float[8]` | Autocorrelation at 8 lag scales |
| `session.frequency_periodicity_score` | `float [0,1]` | Regularity score; high = bot rhythm |
| `session.frequency_dominant_lag` | `int` | Index of dominant lag bin; -1 if aperiodic |

**Thresholds (YAML-configurable):**

| Parameter | Default | Effect |
|---|---|---|
| `periodicity_bot_threshold` | 0.5 | Score above which bot rhythm is flagged |
| `periodicity_bot_confidence` | 0.35 | Confidence delta for periodic detection |
| `frequency_rhythm_similarity_threshold` | 0.85 | Cross-session rhythm match threshold |

**Persistence:** The 8D fingerprint is serialized to the `frequency_fingerprint BLOB` column
in `sessions`. Loaded back into HNSW metadata on warmup. Preserved through compaction as
a per-signature centroid (L1) and per-cluster centroid (L2).

---

## Feature 2: Drift vector (trajectory modeling)

**What it does:** Fits an OLS linear regression over the most recent N session vectors (up to 8)
to compute the per-dimension slope -- the direction and rate the entity's behavior is changing.

**Why it matters:** Bots rotate. Between sessions they change IP, user-agent, or request paths.
The drift vector captures this rotation in behavioral space. A signature that keeps drifting in
the same direction is a continuing campaign, not a new visitor.

**Pre-crime detection:** The current vector is projected forward by `trajectory_projection_steps`
normalized time units using `ProjectForward`. The HNSW index is searched near the predicted
future position. If the nearest neighbor is a known bot signature, the detection fires before
the current session exhibits overt bot behavior.

**Key signals emitted:**

| Signal key | Type | Description |
|---|---|---|
| `session.drift_vector` | `float[129]` | OLS regression slope (zero if < 3 sessions) |
| `session.trajectory_cluster_similarity` | `float` | Similarity to nearest neighbor at predicted position |
| `session.trajectory_in_attack_cluster` | `bool` | Pre-crime flag |

**Thresholds:**

| Parameter | Default | Effect |
|---|---|---|
| `trajectory_attack_cluster_threshold` | 0.72 | Min similarity to flag trajectory hit |
| `trajectory_attack_cluster_confidence` | 0.45 | Confidence delta for pre-crime detection |
| `trajectory_projection_steps` | 1.5 | Normalized time units to project forward |

**Persistence:** Serialized to `drift_vector BLOB` in `sessions`. Loaded into HNSW metadata
on warmup. Requires >= 3 completed prior sessions to compute; null otherwise.

---

## Feature 3: Void detection

**What it does:** After finalizing a session vector, the HNSW index is searched for nearby
neighbors. If no neighbor meets the minimum similarity threshold, the session is in empty
behavioral shape-space -- genuinely novel behavior.

**Two-pass void detection:**
1. Cosine search (`FindSimilarAsync`): standard ANN search with minimum similarity threshold
2. Mahalanobis search (`FindSimilarMahalanobisAsync`): variance-aware search against L1/L2
   centroids with known variance envelopes

A **deep void** is a session that registers as void under both passes. This is the
highest-priority alert: the behavior doesn't look like any known human baseline or any known
bot campaign. Deep voids are automatically flagged for LLM escalation at session end via
`SessionEscalationService.FlagForEscalation`.

A **shallow void** (cosine void but Mahalanobis match found) means the session falls within
a known centroid's variance envelope -- it resembles a known campaign's spread, just not any
specific session. This is a softer signal.

**Key signals emitted:**

| Signal key | Type | Description |
|---|---|---|
| `session.is_void` | `bool` | No cosine neighbors above threshold |
| `session.top_similarity` | `float` | Best cosine similarity (0.0 when void) |
| `session.mahalanobis_nearest_distance` | `float` | Distance to nearest centroid (`float.MaxValue` if no centroids) |

**IntentContributor features (derived):**

| Feature key | Description |
|---|---|
| `session:is_void` | Raw void flag |
| `session:is_deep_void` | Void under both cosine and Mahalanobis (distance > 3.0) |
| `session:novelty` | `1 - top_similarity`; 1.0 when void |
| `session:mahalanobis_distance` | Normalized distance [0,1] |

**Thresholds:**

| Parameter | Default | Effect |
|---|---|---|
| `void_detection_min_similarity` | 0.40 | Minimum similarity to NOT be void |
| `void_detection_top_k` | 5 | Neighbors to search |

---

## Feature 4: Mahalanobis distance (variance-aware ghost matching)

**What it does:** Standard cosine similarity treats all vector dimensions equally. Mahalanobis
distance weights each dimension by its inverse variance: dimensions that are stable across a
campaign (e.g., TLS fingerprint dims for bots that always use the same TLS stack) are
discriminative -- deviations are anomalous. Dimensions with high variance (e.g., session
timing dims for human browsing) are noisy -- deviations are expected.

**How it works in StyloBot:**
- `ComputeVarianceVector` is called during Phase 3 HNSW compaction when collapsing a
  signature's multiple L0 entries into an L1 centroid
- The resulting `VarianceVector` is stored in `SessionVectorMetadata.VarianceVector`
- `FindSimilarMahalanobisAsync` uses Mahalanobis distance for L1/L2 entries with a variance
  vector; falls back to cosine distance (scaled) for plain L0 entries
- Results are ordered by ascending distance (closest match first)

**When it fires:** Only meaningful after the first nightly compaction (Phase 3) runs and L1
centroids exist with variance envelopes. Before that, all entries are L0 and Mahalanobis
falls back to cosine.

---

## Data flow: persistence and warmup

### Per-session write path

```
FinalizeSession()
  → FrequencyFingerprintEncoder.Encode(requests) → snapshot.FrequencyFingerprint
  → SessionVectorizer.ComputeDriftVector(priorHistory) → snapshot.DriftVector
  → SessionFinalized event → SessionPersistenceService
      → SerializeVector(FrequencyFingerprint) → PersistedSession.FrequencyFingerprintBlob
      → SerializeVector(DriftVector)          → PersistedSession.DriftVectorBlob
      → SqliteSessionStore.AddSessionAsync()  → sessions table (BLOBs)
      → AddToVectorSearchAsync()
          → DeserializeVector(FrequencyFingerprintBlob) → HNSW AddAsync(frequencyFingerprint:)
          → DeserializeVector(DriftVectorBlob)          → HNSW AddAsync(driftVector:)
```

### Startup warmup

`SessionVectorWarmupService` runs once if the HNSW graph files are absent:
1. Loads up to 5000 recent sessions from SQLite (including `FrequencyFingerprintBlob` and `DriftVectorBlob`)
2. Deserializes and passes all supplementary vectors to `AddAsync`
3. Saves the rebuilt index to disk

If graph files are present (normal operation after first run), warmup is skipped and the
full index with all metadata is loaded from disk in < 1 second.

### Nightly compaction (VectorCompactionService)

**Phase 2 - SQLite session compaction** (triggers when a signature exceeds `MaxSessionsPerSignature`):
- Reads all session rows including `frequency_fingerprint`
- Computes `FrequencyCentroid` (mean of non-null fingerprints)
- Returns `CompactionResult` with `BehavioralCentroid`, `VelocityCentroid`, `FrequencyCentroid`
- Updates HNSW L1 entry with all three centroids

**Phase 3 - HNSW LOD compaction** (triggers when total vectors exceed `HnswLevel1Threshold`):
- L1: collapse multiple same-signature vectors to one centroid; computes `VarianceVector`,
  `FrequencyFingerprint` centroid, `VelocityVector` centroid
- L2: collapse low-priority L1 entries in the same cluster to a single cluster centroid

**In-memory ring buffer compaction** (`CompactHistory` in `SessionStore`):
When the per-signature snapshot ring exceeds `max_snapshots_per_signature`, old snapshots
are merged into a root snapshot. The root snapshot carries `FrequencyFingerprint` as the
mean of compacted fingerprints, so cross-session rhythm detection continues to work across
the compaction boundary.

---

## HNSW index versioning and resilience

The on-disk HNSW index is fixed-dimension by construction: a serialized graph built for
129-float vectors cannot load vectors of a different length without corrupting the graph.
Two mechanisms protect against this:

### IndexManifest

Every save writes `{hnsw-index}/index.manifest.json` alongside the graph files:

```json
{ "SchemaVersion": 1, "Dimension": 129 }
```

`LoadAsync` reads this manifest before deserializing anything.

### Dimension growth (zero-padding, no data loss)

When new feature dimensions are **appended** to `SessionVectorizer` (e.g., adding 3 more
transition timing slots), the saved `Dimension` will be less than `SessionVectorizer.Dimensions`.
`LoadAsync` zero-pads all loaded vectors to the live size, deletes the serialized graph, and
triggers a rebuild. Existing behavioral data is preserved; the graph is reconstructed on startup.

This is the expected upgrade path. No version bump required.

### Breaking schema changes (discard and rebuild)

When dimensions are **reordered, removed, or semantically reassigned**, cosine similarity
results would be meaningless against old vectors. In this case:

1. Increment `CurrentSchemaVersion` in `HnswFileSimilaritySearch.cs` (currently `1`)
2. On next startup, `LoadAsync` detects `manifest.SchemaVersion != CurrentSchemaVersion`,
   deletes all index files, and starts accumulating from scratch

Data is lost but correctness is preserved. The HNSW index is a performance cache over the
behavioral signal: the source of truth is in the SQLite `sessions` table and the warmup
service (`SessionVectorWarmupService`) will rebuild from SQLite within a few minutes.

### Legacy indices (no manifest)

Indices written before the manifest was introduced have no `index.manifest.json`. `LoadAsync`
infers the saved dimension from the first loaded vector and proceeds as if `SchemaVersion = 1`.
If the inferred dimension matches the live size, the existing graph is used unchanged.

### Summary

| Scenario | Manifest state | Action |
|---|---|---|
| Normal load | `SchemaVersion` matches, `Dimension` matches | Deserialize graph, done |
| Dimension grew | `Dimension < SessionVectorizer.Dimensions` | Zero-pad vectors, rebuild graph |
| Dimension shrank | `Dimension > current vector length` | Discard, start fresh |
| Breaking schema change | `SchemaVersion` mismatch | Discard all files, start fresh |
| No manifest (legacy) | Absent | Infer dim from vectors, use graph if present |

### Key file

`Mostlylucid.BotDetection/Similarity/HnswFileSimilaritySearch.cs` -- `CurrentSchemaVersion`
constant controls when a breaking discard is triggered.

---

## Signal reference

All signals are emitted by `SessionVectorContributor` (priority 30, after behavioral analysis).
Override thresholds via `appsettings.json → BotDetection:Detectors:SessionVectorContributor:*`.

```
session.frequency_fingerprint          float[8]   Autocorrelation rhythm vector
session.frequency_periodicity_score    float      Periodicity score [0,1]
session.frequency_dominant_lag         int        Dominant lag index; -1 if aperiodic
session.drift_vector                   float[129] OLS regression slope
session.trajectory_cluster_similarity  float      Similarity at predicted future position
session.trajectory_in_attack_cluster   bool       Pre-crime flag
session.is_void                        bool       No cosine neighbors above threshold
session.top_similarity                 float      Best cosine similarity (0 when void)
session.mahalanobis_nearest_distance   float      Nearest Mahalanobis distance to centroid
session.self_similarity                float      Cosine similarity vs own history
session.velocity_magnitude             float      Behavioral shift magnitude between sessions
session.request_count                  int        Requests in current session
session.boundary_detected              bool       Session boundary just detected
```

**Downstream consumers:**

| Consumer | Signals used |
|---|---|
| `SessionVectorContributor` | Emits all session.* signals |
| `IntentContributor` | Reads all session.* signals as intent features |
| `SessionEscalationService` | `FlagForEscalation` called on deep void detection |
| `EntityResolutionService` | Uses session vectors for merge/split/convergence decisions |

---

## Detector contribution logic

The contributor fires multiple analysis passes in sequence. Each pass may add a
`DetectionContribution` to the result list. Contributions are additive; the orchestrator
combines them into a final probability score.

| Analysis | Trigger condition | Bot contribution |
|---|---|---|
| Inter-session velocity | >= 2 prior sessions | High velocity or fingerprint rotation |
| Frequency rhythm | >= 5 requests in current session | Periodicity score > threshold |
| Cross-session rhythm | >= 5 requests + >= 2 prior sessions with fingerprints | Same rhythm, different path |
| Void detection | `_vectorSearch` available | Signal only (no hard contribution); escalates to LLM |
| Trajectory | >= 3 mature prior sessions + `_vectorSearch` | Pre-crime: predicted position in bot cluster |

Void detection intentionally emits a signal rather than a bot contribution. Novel behavior
could be a legitimate new service or user population. The LLM escalation path handles
the decision with full context at session end.
