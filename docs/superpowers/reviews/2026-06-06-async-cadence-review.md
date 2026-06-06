# Async cadence review

> Read-only inventory and drift analysis of every background service in `src/`. Companion to `docs/superpowers/specs/2026-06-06-async-cadence-review.md` and `docs/superpowers/plans/2026-06-06-verdict-cache-undrift.md`.

## TL;DR

Forty-five distinct hosted services + timer-driven stores were inventoried. Most are correctly time-driven for what they do (24h external-feed refreshes, hourly retention prunes, sub-second drainer loops). **Five services do tick on their own clock when the blackboard already has the signal that should wake them**, and three of those produce state the request path gates on. The orchestrator exposes a per-key signal subscription surface (`IDetectionSignalBus.Subscribe(...)`) that today has exactly one consumer (the observability log bridge). That is the under-used coordination primitive; almost every drift candidate below could be fixed by subscribing to an existing key instead of polling for its consequences.

## 1. Inventory

Column convention: **Trigger** = time / queue / signal / hybrid. **Interval** = effective period (config key in italics where one exists). **Gates request path** = "yes" (with the request-path consumer named) or "no" (out-of-band).

### Identity + learning core

| Service | File | Trigger | Interval / signal | Produces | Gates request path |
|---|---|---|---|---|---|
| FingerprintDriftService | `src/Mostlylucid.BotDetection/Identity/FingerprintDriftService.cs` | time | *Identity.Drift.DriftCheckIntervalSeconds* (default 5s, floor 1s) | bumps `fingerprints.cached_score_updated_at`, logs drift | **yes**; verdict cache lookup gated on `cached_score_updated_at` (fixed in `2026-06-06-verdict-cache-undrift.md`; now log-only) |
| FingerprintAbsorptionService | `src/Mostlylucid.BotDetection/Identity/FingerprintAbsorptionService.cs` | time | *Identity.Drift.DriftCheckIntervalSeconds* (floor 1s) | folds observations into `fingerprints.centroid`, `centroid_maturity`, `weights`, `inferred_client_type`; emits type-drift events | **yes**; Pass-2 confirm + display name read `centroid_maturity`, weights, type |
| FingerprintModeAbsorptionService | `src/.../Identity/BrowserModes/FingerprintModeAbsorptionService.cs` | time | same key as above, *DrainMaxRowsPerTick* batch cap | drains `fingerprint_mode_observations` → `fingerprint_browser_modes` (per-mode centroids) | yes (only when `BrowserMode.RollupEnabled`) |
| FingerprintRollupRecomputeService | `src/.../Identity/BrowserModes/FingerprintRollupRecomputeService.cs` | time | *Identity.BrowserMode.RollupRecomputeIntervalSeconds* (floor 10s) | recomputes parent `fingerprints.centroid` from per-mode centroids | yes (rollup-enabled only); dry-runs math when flag off |
| IdentityWeightCalibrationService | `src/Mostlylucid.BotDetection/Identity/IdentityWeightCalibrationService.cs` | time | *Identity.Calibration.CalibrationIntervalMinutes* (floor 1m) | writes `identity_dimension_weights`, refines `identity_archetypes.centroid` | yes; matcher composes global weights via `IdentityGlobalWeightsCache` per request |
| IdentityGlobalWeightsCache | `src/Mostlylucid.BotDetection/Identity/IdentityGlobalWeightsCache.cs` | time | *Identity.Weights.GlobalRefreshSeconds* (floor 1s) | refreshes `_current` float[] from `IFingerprintStore.GetGlobalWeightsAsync()` | yes; matcher reads `.Compose(...)` on every request |
| IdentityProcessingCoordinator | `src/Mostlylucid.BotDetection/Identity/IdentityProcessingCoordinator.cs` | signal | per-fp queue + breaker; worker pool `Identity.Coordinator.WorkerCount` | in-mem inflight / queue state, diagnostics counters | yes; request path calls `RunAsync<T>()` |
| SessionPersistenceService | `src/Mostlylucid.BotDetection/Data/SessionPersistenceService.cs` | queue | producer = `SessionStore.SessionFinalized` event (channel cap 500, DropOldest) | `sessions`, `signatures`, entity_resolution rows | yes; dashboard drill-in, downstream entity resolution |
| LearningBackgroundService | `src/Mostlylucid.BotDetection/Services/LearningBackgroundService.cs` | queue | producer = `ILearningEventBus.TryPublish()`, dispatches to `ILearningEventHandler`s | per-handler (no direct write) | indirect; handlers update reputation, HNSW, etc. |
| BoundedChannelLearningBus | `src/Mostlylucid.BotDetection/Services/BoundedChannelLearningBus.cs` | queue | front-end bounded channel when `SelfMaintenance.HighPerformanceMode = true` | none | indirect; decouples hot-path publish from handler invocation |
| ReputationMaintenanceService | `src/Mostlylucid.BotDetection/Services/ReputationMaintenanceService.cs` | hybrid | time (decay *Reputation.DecaySweepIntervalMinutes*, GC 24h, persist 5m) + signal (`ILearningEventBus`) | `IPatternReputationCache` + persist | yes; reputation contributors |
| BackgroundEnrichmentService | `src/Mostlylucid.BotDetection/Services/BackgroundEnrichmentService.cs` | queue | producer = request-path `TryEnqueue(EnrichmentRequest)`, capacity *BackgroundEnrichment.ChannelCapacity* | reputation cache updates | yes; next request from same IP reads cache |
| AnomalySaverService | `src/Mostlylucid.BotDetection/Persistence/AnomalySaverService.cs` | hybrid | signal subscription on `ILearningEventBus` + time flush *AnomalySaver.FlushInterval* | rolling NDJSON files | no; operator-only |
| EntityResolutionService | `src/Mostlylucid.BotDetection/Services/EntityResolutionService.cs` | time | 60s loop, adaptive via `PipelineLoadSensor` | `entities.velocity_variance/factor_count/confidence_level/rotation_cadence_seconds`, Converge edges | yes; `/api/v1/entity/{id}` reads; convergence flag |
| SignatureConvergenceService | `src/Mostlylucid.BotDetection/Services/SignatureConvergenceService.cs` | time | *SignatureConvergence.EvaluationIntervalSeconds* + adaptive cap | in-mem `SignatureFamily` registrations via `SignatureCoordinator` | yes; request path reads families |
| IntentClassificationCoordinator | `src/Mostlylucid.BotDetection/Services/IntentClassificationCoordinator.cs` | queue | producer = request-path `TryEnqueue`, capacity 100 | intent HNSW index, reputation, `IntentClassified` learning event | yes (next request) |
| LlmClassificationCoordinator | `src/Mostlylucid.BotDetection/Services/LlmClassificationCoordinator.cs` | queue | producer = request-path `TryEnqueue`, capacity *LlmCoordinator.ChannelCapacity*, adaptive sample rate | reputation updates, drift events, SignalR broadcast | yes (next request) |
| SignatureDescriptionService | `src/Mostlylucid.BotDetection/Services/SignatureDescriptionService.cs` | hybrid | request-path activity tracking + hourly cleanup loop; threshold *SignatureDescriptionThreshold* | enqueues to LLM description coordinator on threshold crossing | indirect |
| SessionAtomizerService | `src/Mostlylucid.BotDetection/Services/SessionAtomizerService.cs` | time | *Retention.AtomizerRunInterval* loop, gap-based splitting | `sessions` table + `request_to_session` | yes; dashboard drill-in |
| VectorCompactionService | `src/Mostlylucid.BotDetection/Services/VectorCompactionService.cs` | time | nightly at *Retention.CompactionHourUtc* | bucket prune, session compaction, HNSW L1→L2 LOD, centroid prune | yes (HNSW reads) |
| DeploymentNormCalibrationService | `src/Mostlylucid.BotDetection/Services/DeploymentNormCalibrationService.cs` | time | 1s poll on `DeploymentNormTracker.IsWarmingUp` | log only | indirect; exposes warm-up state for operators |
| PopulationMarkovService | `src/Mostlylucid.BotDetection/Markov/PopulationMarkovService.cs` | time | *Markov.CohortFlushIntervalSeconds* / *SnapshotIntervalSeconds* | in-mem `MarkovTracker` baseline; TBD TimescaleDB | yes; request path reads baseline |
| CentroidSequenceRebuildHostedService | `src/Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs` | signal | `BotClusterService.ClustersUpdated` + startup `RelearnGlobalAsync(minSessions: 50)` | `centroid_sequences` SQLite | yes; content-sequence detector reads |
| SessionVectorWarmupService | `src/Mostlylucid.BotDetection/Services/SessionVectorWarmupService.cs` | time | one-shot startup (3s delay) | warms HNSW (`_signatureSearch`, `_sessionSearch`, `_intentSearch`) | yes; request path queries warm indices |
| SignatureCoordinatorWarmupService | `src/Mostlylucid.BotDetection/Services/SignatureCoordinatorWarmupService.cs` | time | one-shot startup | replays persisted requests into in-mem coordinator | yes; clustering, family merging |

### Dashboard, observability, threat intel

| Service | File | Trigger | Interval / signal | Produces | Gates request path |
|---|---|---|---|---|---|
| DashboardSummaryBroadcaster | `src/Mostlylucid.BotDetection.UI/Services/DashboardSummaryBroadcaster.cs` | time | *SummaryBroadcastIntervalSeconds* + *AggregateCacheIdleSkipSeconds* gate + hourly prune | `DashboardAggregateCache` (Countries/Endpoints/UA/Summary/Detections/TimeSeries/TopBots/Threats) | yes; `/api/v1/summary` etc. read snapshot |
| SignatureAggregateCacheWarmupService | `src/Mostlylucid.BotDetection.UI/Services/SignatureAggregateCacheWarmupService.cs` | time | one-shot 2s startup | `SignatureAggregateCache` | yes; dashboard widgets |
| VisitorCacheWarmupService | `src/Mostlylucid.BotDetection.UI/Services/VisitorCacheWarmupService.cs` | time | one-shot 2s startup | `VisitorListCache` | yes; visitor / top-bots endpoints |
| RouteNameStoreInitializer | `src/.../UI/Services/Routes/RouteNameStoreInitializer.cs` | time | one-shot startup | `route_names` schema | yes; gating reads |
| RemoteMetricCollector | `src/Mostlylucid.BotDetection.UI/Services/RemoteMetricCollector.cs` | time | injected `_pollInterval` (no public key) | `IMetricSnapshotStore` rows | yes (remote-mode dashboards) |
| MeterListenerService | `src/Mostlylucid.BotDetection/MonitoringPacks/MeterListenerService.cs` | hybrid | min(pack `CollectionInterval`)≈60s + `IPackRuntimeController.PackChanged` event | `IMetricSnapshotStore` | yes (remote collectors) |
| GatewayMeterAccumulator | `src/Mostlylucid.BotDetection/MonitoringPacks/GatewayMeterAccumulator.cs` | signal | `MeterListener` callbacks | in-mem snapshot | yes; `/metrics` |
| ConfigurationWatcher | `src/Mostlylucid.BotDetection/Orchestration/Manifests/ConfigurationWatcher.cs` | signal | `IConfigurationOverrideSource.WatchAsync()` async streams | invalidates `IDetectorConfigProvider` cache | yes; every detection read |
| ThreatIntelEnrichmentQueue | `src/Mostlylucid.BotDetection/ThreatIntel/ThreatIntelEnrichmentQueue.cs` | queue | producer = detector cache-miss `TryEnqueue`, capacity 500 DropOldest | provider-backed threat cache | yes; next request reads cache |
| ThreatIntelRefreshService | `src/Mostlylucid.BotDetection/ThreatIntel/ThreatIntelRefreshService.cs` | time | per-provider `provider.RefreshInterval`, staggered, *BlockStartupOnFirstFetch* | provider cache hydration | yes; detection reads cache |
| SignalRBeaconRelay | `src/Stylobot.Ui/SignalRBeaconRelay.cs` | signal | gateway hub `BroadcastInvalidation` / `BroadcastAttackArc` | none; relay only | indirect (browser refresh) |
| HoneypotReporter | `src/Mostlylucid.BotDetection.ApiHolodeck/Services/HoneypotReporter.cs` | hybrid | `ILearningEventBus` subscription + 1m flush | queued reports (currently log-only) | no; out-of-band |

### Peripheral + timer-driven stores

| Service | File | Trigger | Interval / signal | Produces | Gates request path |
|---|---|---|---|---|---|
| HeartbeatService | `src/Mostlylucid.BotDetection.Console/Services/HeartbeatService.cs` | time | 5m hardcoded | log only | no |
| LiveDetectionTableService | `src/Mostlylucid.BotDetection.Console/Services/LiveDetectionTable.cs` | queue | `Channel<DetectionEntry>` + 500ms render poll | TUI display | no |
| OpenApiStartupSeederService | `src/Mostlylucid.BotDetection.OpenApi/OpenApiStartupSeederService.cs` | time | one-shot startup | `IOpenApiCatalog` | yes; OpenAPI middleware |
| BotListUpdateService | `src/Mostlylucid.BotDetection/Services/BotListUpdateService.cs` | time | 60m check loop, ~24h actual update (cron) | `IBotListDatabase`, `ICompiledPatternCache` | yes; pattern detectors |
| BrowserVersionService | `src/Mostlylucid.BotDetection/Services/BrowserVersionService.cs` | time | *UpdateIntervalHours* (default 24h) | in-mem version map | yes; `VersionAgeDetector` |
| BotClusterService | `src/Mostlylucid.BotDetection/Services/BotClusterService.cs` | hybrid | timer *ClusterIntervalSeconds* + semaphore release on `MinBotDetectionsToTrigger` | `ClusterSnapshot` + `SqliteClusterStore` | yes; `ClusterMembershipLookup` + `ClustersUpdated` event |
| CommonUserAgentService | `src/Mostlylucid.BotDetection/Services/CommonUserAgentService.cs` | time | *UpdateIntervalHours* (24h) | UA prevalence map | yes; prevalence detectors |
| VerifiedBotRegistry | `src/Mostlylucid.BotDetection/Services/VerifiedBotRegistry.cs` | time | *IpRangeRefreshHours* (24h) + DNS TTL caches | IP-range dict, DNS cache | yes; `VerifiedBotContributor` |
| LicenseStateRefreshService | `src/Mostlylucid.BotDetection/Licensing/LicenseStateRefreshService.cs` | time | `PeriodicTimer` 60s | `ILicenseState` snapshot | yes; `LearningFrozen` etc. |
| PeriodicUpdateService | `src/Mostlylucid.Common/Services/PeriodicUpdateService.cs` | time | abstract `UpdateInterval` | subclass-defined | n/a |
| GeoLite2UpdateService | `src/Mostlylucid.GeoDetection/Services/GeoLite2UpdateService.cs` | time | *UpdateCheckInterval* (~24h) | `.mmdb` file + reload | yes; every request |
| ProfileAnalysisWorker | `src/Stylobot.Gateway/Services/ProfileAnalysisWorker.cs` | queue | `ProfileAnalysisChannel`, semaphore concurrency | `ProfileCalibrationStore` | no; learning mode only |
| SqliteWeightStore | `src/Mostlylucid.BotDetection/Data/WeightStore.cs` | hybrid | 500ms drain timer + decay timer | `learned_weights` + LRU MemoryCache | yes; detectors call `GetWeightAsync` |
| AssetHashStore | `src/Mostlylucid.BotDetection/Services/AssetHashStore.cs` | time | 1h eviction loop | `asset_hashes` SQLite + recent-change dict | yes; `ContentSequenceContributor` reads `IsRecentlyChanged` |
| PipelineLoadSensor | `src/Mostlylucid.BotDetection/Services/PipelineLoadSensor.cs` | time | 1s sample loop, EMA α=0.3 | `_smoothedRps`, `LoadBand` | yes; `BotClusterService` / `SignatureConvergenceService` adaptive intervals |
| SequenceContextStore | `src/Mostlylucid.BotDetection/Services/SequenceContextStore.cs` | time | 5m TTL sweep | in-mem context dict | yes; `ContentSequenceContributor` |
| SessionEscalationService | `src/Mostlylucid.BotDetection/Services/SessionEscalationService.cs` | hybrid | 5m cleanup + `SessionStore.SessionFinalized` | reputation updates, in-mem escalation flags | no (escalation is out-of-band) |
| YarpSignatureWriter | `src/Mostlylucid.BotDetection/Yarp/YarpSignatureWriter.cs` | time | 10s auto-flush + buffer-full trigger | rotated JSON / JSONL | no; learning mode write |
| DegradationAtom | `src/Mostlylucid.BotDetection/RateLimit/DegradationAtom.cs` | time | 5s EMA decay (~60s effective window) | rate / latency EMAs | yes; adaptive rate-limit tier |
| WriteBehindLfuStore (base) | `src/Mostlylucid.BotDetection/Storage/WriteBehindLfuStore.cs` | queue | bounded `Channel<TWriteOp>` + `_drainInterval` partial-batch flush | subclass-defined; hot dict is source of truth | yes; subclasses sit on detection hot path |

`Mostlylucid.StyloSpam.Incoming/*` (Gmail / IMAP / SMTP polling services) is a separate subsystem; noted and excluded.

## 2. Drift candidates

Time-driven services whose output gates a request-path feature, where the blackboard could wake them more directly. Worst → best:

**A. `FingerprintAbsorptionService` + `FingerprintModeAbsorptionService`** (*Identity.Drift.DriftCheckIntervalSeconds*, default 5s). Absorption folds the newest observation into the centroid + bumps maturity; Pass-2 confirm + display name read centroid maturity. A hot fingerprint waits up to 5s for the next tick before its just-appended observation is absorbed. The matcher already calls `SqliteFingerprintStore.AppendObservationAsync` on the request path; source comments on both services flag this for "migration to schedule coordinator". Closest analogue to the verdict-cache fix.

**B. `IdentityGlobalWeightsCache`** (*Identity.Weights.GlobalRefreshSeconds*, floor 1s). One upstream writer: `IdentityWeightCalibrationService`. There's no race, just polling; the fix is a single `weights_updated` event the calibration service raises on commit. The 1s floor exists only because the periodic loop is the only refresh mechanism today.

**C. `EntityResolutionService`** (60s loop, adaptive via `PipelineLoadSensor`). Oscillation / rotation / convergence are session-boundary phenomena; the trigger should be `SessionStore.SessionFinalized` (already used by `SessionPersistenceService` + `SessionEscalationService`), batched per signature. A freshly-finalized session today waits up to a minute before its entity facts update; when convergence detection is the whole point.

**D. `SignatureConvergenceService`** (*SignatureConvergence.EvaluationIntervalSeconds*). Same shape as (C). Evaluated against session vectors that only change on `SessionFinalized`. Walks all candidates every tick whether anything changed or not.

**E. `FingerprintDriftService`** (5s tick). Already half-drained: the `cached_score_updated_at` write moved into the request path (`2026-06-06-verdict-cache-undrift.md`). What remains is the L2 verifier that detects when a confirmed-human verdict diverges from its identity shape. The natural wake signal is the existing `identity.ambiguity_persistence` (`SignalKeys.IdentityAmbiguityPersistence`, see §4). Today this signal is emitted, persisted into observations, and read by nothing.

`IdentityWeightCalibrationService`'s minute-scale cadence is appropriate (Fisher discriminant ratio across the whole fingerprint table is expensive and slowly-changing). Cadence is fine; what's missing is the *downstream signal* it should raise (see B).

## 3. Coordination opportunities

- **Two services scanning the fingerprints table on independent clocks.** `FingerprintDriftService` (5s, stale `cached_score_updated_at`) and `FingerprintAbsorptionService` (5s, new `fingerprint_observations`) share a config key and a floor and walk overlapping subsets of the same table. Either consolidate to one maintenance pass with a single cursor, or; preferred; drive both off the matcher's per-request signals.
- **Type-drift not propagated to weights composition.** `FingerprintAbsorptionService` flips `inferred_client_type` (Chrome→Headless, say); the global weight vector calibrated for the old archetype is no longer the best composition. Today this waits for the 1s cache tick + the minute calibration tick. The composer should at least invalidate the cache keyed on the affected archetype.
- **`SessionStore.SessionFinalized` has four would-be subscribers running on independent clocks.** `SessionPersistenceService` (queue-driven) and `SessionEscalationService` are subscribed correctly. `SessionAtomizerService` walks unatomized requests on `Retention.AtomizerRunInterval`; but atomization is literally "fold finalized session into the canonical table". `EntityResolutionService` and `SignatureConvergenceService` ought to subscribe too (§2 C/D). One event, four subscribers, three missing.
- **`BotClusterService.ClustersUpdated` is well-wired**; `CentroidSequenceRebuildHostedService` subscribes correctly. Reference pattern worth documenting.
- **`DashboardSummaryBroadcaster`'s `LastHitAtUtc`-based idle-skip** is the right model for any time-driven service whose output exists only to be read on demand. `RemoteMetricCollector` is the natural next adopter; idle remote dashboards shouldn't poll the gateway.

## 4. Signals already on the blackboard but no async subscriber

Searched `SignalKeys` and `IDetectionSignalBus`. The orchestrator's per-key subscription surface (`IDetectionOrchestrator.SubscribeToSignals(listener)`) has exactly **one subscriber across the entire repo**: `BlackboardSignalLogBridge` (observability logging). Every other coordination flows through `ILearningEventBus`, which is downstream of the blackboard and lossy by design (HP mode drops oldest on full).

Concrete signals defined and emitted but with no async consumer:

- **`identity.ambiguity_persistence`** (`SignalKeys.IdentityAmbiguityPersistence`); float, fraction of recent verdicts that landed in the ambiguity zone (Pass-2 correction, rotation candidate). This is precisely the "drift verifier should wake" signal. Nothing subscribes; the drift service polls SQLite for stale rows instead.
- **`identity.ambiguity_probing`** (`SignalKeys.IdentityAmbiguityProbing`); bool, ambiguity_persistence above threshold. Same story.
- **`intent.ambiguous`** (`SignalKeys.IntentAmbiguous`); bool, intent score in 0.3-0.7 zone. The intent classification coordinator already takes a request-path enqueue, but it could subscribe directly to this signal instead of relying on the contributor to call `TryEnqueue`.

The threshold-crossing signals the spec hypothesizes (`signature.observation_count.crossed_N`, `centroid.maturity.threshold_reached`, `signature.first_seen`) do **not** exist as signal keys; they are SQLite columns on `fingerprints` / `fingerprint_keys`. Adding them as derived blackboard signals (emitted by the foundation `FingerprintMatchContributor` when the row it just upserted crosses a threshold) is the cleanest way to wake the services in §2 candidates A/B/E.

## 5. Recommendations

Punch list, ordered by latency / correctness win × implementation cost:

1. **Migrate `FingerprintAbsorptionService` to observation-append subscription.** Add `ObservationAppended(fingerprintId)` on `SqliteFingerprintStore` (or surface `signature.observation_count.crossed_N` as a foundation signal); absorb on signal arrival, debounced per fingerprint. Keep a slow time-driven sweep as crash-recovery backstop. Largest blast radius; the matcher reads the maturity-bumped centroid on the very next request from the same visitor.
2. **Add `weights_updated` event on `IdentityWeightCalibrationService` commit; have `IdentityGlobalWeightsCache` subscribe.** Delete the 1s polling floor; keep a startup re-prime.
3. **Wire `EntityResolutionService` to `SessionStore.SessionFinalized`.** Batch + debounce per signature. Remove the 60s base loop; the `PipelineLoadSensor` cap stays as a queue-depth ceiling, not a tick floor.
4. **Same change for `SignatureConvergenceService`.** Schedule pairwise checks only against the signature whose session just finalized.
5. **Surface fingerprint identity threshold events as foundation signals.** Add `SignalKeys.FingerprintFirstSeen`, `FingerprintMaturityThreshold`, `FingerprintObservationCountCrossed`. Emit from `FingerprintMatchContributor` (foundation, runs unconditionally). Subscribe `FingerprintDriftService`'s L2 verification to `identity.ambiguity_persistence` and / or the new threshold signals; keep the periodic sweep only as a safety net at ≥ 5m.
6. **Make `SessionAtomizerService` subscribe to `SessionFinalized`** instead of walking the unatomized batch on its own clock. Forced shutdown flush stays.
7. **Consolidate the two fingerprint-table scans.** If (1) and (5) ship, run any residual periodic backstop once and let both consumers share the cursor; one query, two effects.
8. **Adopt `DashboardAggregateCache.LastHitAtUtc` idle-skip in `RemoteMetricCollector`.** Park polling when no remote dashboard is connected; resume on first relay.
9. **Document `BotClusterService.ClustersUpdated` → `CentroidSequenceRebuildHostedService`** in `docs/architecture/signal-contracts.md` as the reference signal-driven coordination pattern.

## 6. Out of scope

These are time-driven for genuine reasons; don't touch them:

- **External-feed refreshes** (`GeoLite2UpdateService`, `BotListUpdateService`, `BrowserVersionService`, `CommonUserAgentService`, `VerifiedBotRegistry`); upstream publishes on a slow human cadence (~weekly), 24h cap is correct.
- **License refresh** (`LicenseStateRefreshService`, 60s `PeriodicTimer`); token expiry / grace transitions are time-based by nature; the upstream grace window is 30 days, 60s polling is conservative-correct.
- **Retention / compaction** (`VectorCompactionService` nightly, `DashboardSummaryBroadcaster` hourly prune); large multi-table ops bounded to off-peak; not signal-driven by design.
- **Operator telemetry** (`HeartbeatService`, `LiveDetectionTableService`, `DeploymentNormCalibrationService`); log / TUI surfaces, no request-path consumer.
- **`PipelineLoadSensor` 1s tick**; load measurement is the signal *itself*; making it signal-driven is circular. The 1s sample EMA is the right primitive.
- **`DegradationAtom` 5s decay**; same reasoning; the decay rate *is* the signal semantics.
- **`WriteBehindLfuStore` drainer's `_drainInterval` partial-batch flush**; this is bounded latency for partial batches, not drift. Drainer wakes immediately on enqueue; the interval bounds *worst-case staleness for partial batches*, exactly the right knob.
- **Distributed-deploy coordination** (per spec).
