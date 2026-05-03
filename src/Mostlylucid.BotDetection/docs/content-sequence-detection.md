# Content Sequence Detection

Wave: 0 (Fast Path)
Priority: 4
Trigger: No prerequisites — runs on every request

## Purpose

Tracks the request sequence produced by each fingerprint and scores divergence from the expected browser page-load pattern. Real browsers follow a predictable rhythm: a document navigation request is followed immediately by a burst of static asset loads (CSS, JS, images), then API calls settle in, and finally streaming connections (SignalR, WebSocket) open if the page requires them. Bots routinely break this rhythm — hitting APIs directly after a document, loading no assets at all, or firing requests at machine speed.

`ContentSequenceContributor` maintains this per-fingerprint sequence state, classifies each incoming request against the current phase window, and writes divergence signals that deferred detectors (SessionVector, BehavioralWaveform, ResourceWaterfall, Periodicity, CacheBehavior) consume to decide whether to run.

## How It Works

### Document requests and sequence reset

A request is treated as a document navigation when any of the following is true:

- `Sec-Fetch-Mode: navigate` is present
- `Accept` header includes `text/html` and the method is GET
- The `transport.protocol_class` signal from `TransportProtocolContributor` is `document`

When a document request is received, the contributor resets the sequence context to position 0 for that fingerprint and loads the best available chain:

- **Tier 2 (centroid-specific):** a chain built from the human-cluster centroid for the path, rebuilt after each Leiden clustering run. Only used when the cluster sample size meets `min_centroid_sample_size` (default 20) and the centroid is not stale.
- **Tier 1 (global fallback):** the default human page-load sequence (document → static asset burst → API calls → optional streaming) used when no Tier 2 chain exists.

The document path is stored in the sequence context so continuation requests can correlate against the same path's chain without relying on the ephemeral per-request blackboard.

### Continuation requests

Every request after the document is a continuation. The contributor:

1. Classifies the request into a Markov state (PageView, ApiCall, StaticAsset, WebSocket, SignalR, ServerSentEvent, FormSubmit, AuthAttempt, NotFound, Search).
2. Advances the sequence position counter.
3. Determines the current phase window based on elapsed time since the document request.
4. Computes a divergence score from three sub-signals (see Divergence Scoring below).
5. Checks whether the next expected state in the centroid chain is SignalR and, if so, emits `sequence.signalr_expected`.
6. Writes all sequence signals to the blackboard.

### Phase windows (set-based)

Phase windows are defined by elapsed time since the document request, not by request position. Each window defines the set of request states that a real browser would legitimately produce during that interval. Divergence is assessed by checking whether the observed state is a member of the expected set.

| Phase | Elapsed time | Expected states |
|---|---|---|
| Critical | 0 – 500 ms | StaticAsset, PageView |
| Mid | 500 ms – 2 s | StaticAsset, ApiCall, PageView |
| Late | 2 s – 30 s | ApiCall, SignalR, WebSocket, ServerSentEvent |
| Settled | 30 s+ | ApiCall, SignalR, ServerSentEvent |

### Divergence scoring

The divergence score is a sum of three additive components, clamped to 1.0. When the score meets or exceeds `divergence_threshold` (default 0.4), the request is marked as diverged.

| Condition | Score contribution | Default |
|---|---|---|
| Inter-request gap below `machine_speed_threshold_ms` | `machine_speed_score` | 0.4 |
| Observed state not in expected set for current phase | `unexpected_state_score` | 0.5 |
| Request count in window exceeds `high_request_count_threshold` | `high_request_count_score` | 0.3 |

The unexpected-state contribution has one exception: an ApiCall during the Critical window (0–500 ms) is not penalised when `sequence.cache_warm` is true, because a warm-cache visitor's browser skips static asset fetches and moves directly to data requests.

### Prefetch handling

Requests carrying any of the following are recorded in the observed state set for the phase window but excluded from divergence scoring entirely:

- `Purpose: prefetch` header
- `Sec-Purpose: prefetch` header
- `Sec-Fetch-Mode: no-cors` combined with `Sec-Fetch-Dest: document`

This prevents speculative prefetch from inflating divergence scores on real users. The `sequence.prefetch_detected` signal is written so downstream detectors can inspect prefetch activity independently.

### Cache-warm detection

When the Critical phase window (0–500 ms) closes with no StaticAsset observed, the contributor marks the session as cache-warm and writes `sequence.cache_warm = true`. This is the normal pattern for repeat visitors whose browser already holds all static resources. `CacheBehaviorContributor` reads this signal and returns a neutral contribution instead of flagging the absence of cache validation headers.

### No active sequence (API-only access)

When a fingerprint has never issued a document request — an extremely common pattern for API-targeting bots — the contributor writes no sequence signals at all. Deferred detectors that use `SequenceGuardTrigger.Default` treat the absence of `sequence.position` as a trigger condition (see Deferred Detector Integration below), so these fingerprints are always analysed in full.

## State Management

### SequenceContextStore

Per-fingerprint sequence state is held in a `ConcurrentDictionary` (`SequenceContextStore`). Each entry records:

- The current sequence position
- The document request timestamp (for phase window calculation)
- The elapsed time of the last request (for machine-speed detection)
- The observed state set per phase (for set-based divergence)
- The content path from the triggering document request

A 30-minute inactivity gap (configurable via `session_gap_minutes`) causes the next document request to start a fresh chain rather than continuing the old one. A background sweep removes entries that have been inactive for 5 minutes, capped at `max_tracked_positions` (default 20) entries per fingerprint. State loss on process restart is acceptable — fingerprints simply start fresh from their next document navigation.

### CentroidSequenceStore

Expected chains are persisted to SQLite, keyed by cluster ID.

**Tier 2 chains** are built from `BotCluster` data after each Leiden clustering run. Human clusters produce human chains; Bot and Emergent clusters produce bot chains. Chains are rebuilt asynchronously by `CentroidSequenceRebuildHostedService` whenever `BotClusterService.ClustersUpdated` fires.

**Tier 1 (global fallback)** represents the typical human page-load sequence and is used when no cluster chain exists for a path or the sample size is below `min_centroid_sample_size`.

**Staleness:** `MarkEndpointStale(path)` suppresses divergence scoring for a path for 1 hour. This is called by the centroid freshness system when a content change is detected — a divergence spike following a site redesign reflects changed centroid expectations, not bot behaviour, so divergence scoring is suppressed until centroids are rebuilt.

## Signals Written

| Signal | Type | Written when | Meaning |
|---|---|---|---|
| `sequence.position` | int | Sequence active | 0 = document, 1+ = continuation |
| `sequence.on_track` | bool | Sequence active | true when divergence score is below threshold |
| `sequence.diverged` | bool | Sequence active | true when divergence score meets or exceeds threshold |
| `sequence.divergence_score` | double | Sequence active | Cumulative divergence score, 0.0–1.0 |
| `sequence.chain_id` | string | Sequence active | UUID of the current centroid chain |
| `sequence.centroid_type` | string | Sequence active | Human, Bot, or Unknown |
| `sequence.content_path` | string | Document requests | Path of the triggering document (e.g. `/`, `/products`) |
| `sequence.cache_warm` | bool | Continuation requests | true when Critical window closed with no StaticAsset observed |
| `sequence.signalr_expected` | bool | When applicable | Next centroid step is SignalR — suppresses StreamAbuse false positives |
| `sequence.prefetch_detected` | bool | When applicable | Request is a browser prefetch; excluded from divergence |
| `sequence.divergence_at_position` | int | On first divergence | Sequence position where divergence first occurred |
| `sequence.centroid_stale` | bool | When stale | Centroid is marked stale; divergence scoring suppressed |
| `asset.content_changed` | bool | Document requests | A content change was detected for the associated path |

## Deferred Detector Integration

Five detectors that produce expensive or meaningful-only-with-enough-data analysis are gated behind `SequenceGuardTrigger.Default`:

- `SessionVectorContributor`
- `PeriodicityContributor`
- `BehavioralWaveformContributor`
- `ResourceWaterfallContributor`
- `CacheBehaviorContributor`

The guard triggers a deferred detector when **any** of the following conditions is true:

| Condition | Rationale |
|---|---|
| `sequence.position` does not exist | No document request seen — API-only access pattern, always analyse |
| `sequence.on_track = false` | Request is already off the expected path |
| `sequence.diverged = true` | Divergence threshold crossed |
| `sequence.position >= 3` | Enough continuation requests exist for reliable signals |

This means:
- **API-only bots** (condition 1) are always subjected to the full deferred detector set regardless of sequence state.
- **On-track real users at positions 0–2** skip the expensive deferred detectors, reducing per-request cost for the majority of legitimate traffic.
- **Bots that do load a document** but then deviate are caught at the first divergence event.

## YAML Configuration

File: `Orchestration/Manifests/detectors/contentsequence.detector.yaml`

```yaml
name: ContentSequenceContributor
priority: 4
enabled: true
scope: request
taxonomy:
  category: behavioral
  subcategory: sequence

defaults:
  parameters:
    divergence_threshold: 0.4
    timing_tolerance_multiplier: 3.0
    min_centroid_sample_size: 20
    session_gap_minutes: 30
    max_tracked_positions: 20
    machine_speed_threshold_ms: 20.0
    machine_speed_score: 0.4
    unexpected_state_score: 0.5
    high_request_count_score: 0.3
    high_request_count_threshold: 50
```

### Parameter reference

| Parameter | Default | Description |
|---|---|---|
| `divergence_threshold` | 0.4 | Cumulative score at which a request is flagged as diverged |
| `timing_tolerance_multiplier` | 3.0 | Reserved for chain-based timing checks; unused in current set-based scoring |
| `min_centroid_sample_size` | 20 | Minimum cluster member count to use a Tier 2 chain; smaller clusters fall back to Tier 1 |
| `session_gap_minutes` | 30 | Inactivity gap that causes a new document request to start a fresh chain |
| `max_tracked_positions` | 20 | Maximum continuation positions tracked per session before the session is considered complete |
| `machine_speed_threshold_ms` | 20.0 | Inter-request gap (ms) below which machine-speed scoring applies |
| `machine_speed_score` | 0.4 | Divergence score contribution for machine-speed timing |
| `unexpected_state_score` | 0.5 | Divergence score contribution for an out-of-phase request state |
| `high_request_count_score` | 0.3 | Divergence score contribution when request count exceeds threshold |
| `high_request_count_threshold` | 50 | Request count within the phase window that triggers high-volume scoring |

## appsettings.json Override

Parameters can be overridden at runtime without redeployment:

```json
{
  "BotDetection": {
    "Detectors": {
      "ContentSequenceContributor": {
        "Enabled": true,
        "Parameters": {
          "divergence_threshold": 0.35,
          "machine_speed_threshold_ms": 15.0,
          "session_gap_minutes": 20
        }
      }
    }
  }
}
```

Lowering `divergence_threshold` increases sensitivity and will flag more borderline cases — useful on high-value endpoints with low tolerance for automated access. Raising it reduces false positives on sites with non-standard page-load patterns (e.g., single-page applications that issue many API calls immediately after navigation).

## Reading Signals in Custom Detectors

Custom detectors can read sequence signals from the blackboard state to adjust their own scoring:

```csharp
// Check whether a sequence is active and how far along it is
var position = state.GetSignal<int>(SignalKeys.SequencePosition);
var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
var onTrack = state.GetSignal<bool>(SignalKeys.SequenceOnTrack);
var cacheWarm = state.GetSignal<bool>(SignalKeys.SequenceCacheWarm);
var centroidStale = state.GetSignal<bool>(SignalKeys.SequenceCentroidStale);
var centroidType = state.GetSignal<string>(SignalKeys.SequenceCentroidType);

// Example: only emit a cache-miss penalty when the centroid is fresh
// and the session is not cache-warm (i.e., this is a cold visit)
if (!cacheWarm && !centroidStale && assetCount == 0)
{
    score += GetParam<double>("no_cache_hit_score", 0.3);
}

// Example: suppress an alert when the visitor is on track in a human-centroid chain
if (onTrack && centroidType == "Human")
{
    return NeutralContribution("On-track human sequence");
}
```

The `sequence.signalr_expected` signal follows a similar pattern — `StreamAbuseContributor` skips its analysis when this signal is true because opening a SignalR connection after a document load is a normal browser action, not stream abuse.

## Interaction with Other Detectors

### CacheBehaviorContributor

Reads `sequence.cache_warm`. When true, returns a neutral contribution rather than flagging the absence of `If-None-Match` / `If-Modified-Since` cache validation headers. This prevents false positives on repeat visitors whose browsers skip conditional requests because the resource is already fresh in the local cache.

### StreamAbuseContributor

Reads `sequence.signalr_expected`. When true, skips its analysis for SignalR connection attempts. A SignalR open that follows a document request and appears in the centroid chain's next expected state is not stream abuse.

### SessionVectorContributor, BehavioralWaveformContributor, PeriodicityContributor, ResourceWaterfallContributor

All four use `SequenceGuardTrigger.Default` to decide whether to run. See Deferred Detector Integration above.

### TransportProtocolContributor (Priority 5)

`ContentSequenceContributor` consumes the `transport.protocol_class` signal emitted by `TransportProtocolContributor` to classify document requests. Because `ContentSequenceContributor` runs at priority 4 and `TransportProtocolContributor` at priority 5, this signal is not yet available on the first pass. The fallback logic (`Sec-Fetch-Mode: navigate` or `Accept: text/html + GET`) handles document detection independently for the priority-4 wave. Transport classification is used for continuation request classification where priority ordering is not a constraint.

### CentroidSequenceRebuildHostedService

Listens to `BotClusterService.ClustersUpdated` and rebuilds all Tier 2 chains in `CentroidSequenceStore` asynchronously. During the rebuild window, affected chains are temporarily marked stale (`sequence.centroid_stale = true`), suppressing divergence scoring to avoid false positives from an outdated chain.

## Tuning Notes

**Single-page applications.** SPAs issue many API calls immediately after the initial navigation, which overlaps with the Critical phase window. If your application exhibits this pattern and you see elevated false-positive rates, raise `divergence_threshold` to 0.5–0.6 or lower `unexpected_state_score`. Alternatively, ensure the SPA's initial API burst is reflected in the Tier 1 global chain by setting `min_centroid_sample_size` low enough that a representative Tier 2 chain is built quickly.

**High-traffic static endpoints.** Paths that serve only assets (e.g., `/cdn-cgi/`) never trigger a document reset and are always continuation requests without a sequence context. These paths produce no sequence signals and deferred detectors run in full via the no-active-sequence condition.

**Aggressive bots with real browsers.** Bots that use full headless browsers (Puppeteer, Playwright) and load all assets may appear on-track for positions 0–2. The `machine_speed_threshold_ms` parameter is the primary discriminator here: even headless browsers operating at machine speed will trigger machine-speed scoring. Lowering the threshold to 10–15 ms catches faster automation while preserving tolerance for fast real user connections.

**Centroid freshness after site redesign.** A significant structural change to page layout (new CSS bundles, changed JS entry points, rearranged API calls) will cause divergence spikes as the Tier 2 centroids become stale. Call `MarkEndpointStale(path)` for affected paths during a deployment to suppress divergence scoring for the 1-hour suppression window while `CentroidSequenceRebuildHostedService` rebuilds the chains from post-deployment traffic.

**Session gap tuning.** The default 30-minute gap matches the session boundary used by `SessionVectorContributor`. Keeping both values aligned ensures that a sequence chain reset and a new session vector start together. If you customise `session_gap_minutes`, apply the same value to `SessionVectorContributor`'s session gap parameter to avoid the two subsystems diverging on session identity.

## Performance

Typical execution: <1 ms per request (in-memory `ConcurrentDictionary` lookup + phase window arithmetic).

The `CentroidSequenceStore` SQLite reads occur only on document requests (sequence reset), not on every continuation. The store uses a write-through cache so steady-state continuation requests never touch SQLite.

The background sweep of `SequenceContextStore` runs every 5 minutes and removes entries inactive for more than 5 minutes, keeping memory consumption bounded under sustained attack.
