# Changelog

All notable changes to StyloBot are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased] - 6.4.7

### Removed — ONNX text embeddings; clustering uses metastable centroids

The `OnnxEmbeddingProvider` (and its `IEmbeddingProvider` interface, `OnnxSetupResource`, and `EmbeddingOptions` config) is gone. It existed as a workaround for not having a real behavioural vector — embedded a hand-summarised text string (`RATE:42/min | PATHS:/wp-login,/.env | COUNTRY:RU | ...`) through `all-MiniLM-L6-v2` to fake similarity over numeric features we already had natively. With the metastable identity layer landing in this release, that workaround is strictly worse than the alternative: the per-fingerprint centroid is the actual learned shape, weighted by per-fp + global Fisher.

- **`BotClusterService`** now reads `fingerprints.centroid` via the new `SqliteFingerprintStore.GetCentroidsBySignaturesAsync` (single round-trip per cluster pass) and feeds the cosine of the centroid into the cluster similarity blend at the same weight the prior text-embedding axis used. Same Leiden algorithm, same blend formula — better vector. Falls back to heuristic-only similarity when Identity is disabled or a signature has no resolved fingerprint binding.
- **`ClusterOptions.EnableSemanticEmbeddings` → `EnableBehaviouralVectorAxis`**, **`SemanticWeight` → `BehaviouralVectorWeight`**. Defaults preserved (true / 0.4). `BotDetection:Embedding:*` config block is silently ignored — operators with old `Embedding` entries can delete them.
- **Packages dropped:** `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`. Native binary footprint reduction across rids; AOT path improves (these packages had known AOT trim issues).
- **Operator action:** if you'd downloaded the `all-MiniLM-L6-v2.onnx` model file (~90 MB), you can delete it. Cluster output may shift slightly because the input vector changed from text-embedding-of-summary to learned-behavioural-shape — this is an upgrade in fidelity, not a regression. UA-family clustering is preserved via the existing `UaFamily` categorical match boost (heuristically parsed from UA string).


### Added — Metastable fingerprint identity

A new identity layer that treats each visitor as a *shape* (a learned vector centroid + per-fingerprint weight vector + observation cloud) rather than a single hash. Replaces the load-bearing role of `PrimarySignature` (HMAC of IP + UA) for visitors whose IP or UA rotates. Reads `PrimarySignature` first as a fast L1 point lookup; falls back to a vector cosine search (L2) when the rotation guarantee doesn't hold. Dormant by default; flip on with `BotDetection:Identity:Enabled = true`.

The full design and contracts live in [`docs/architecture/fingerprint-match.md`](docs/architecture/fingerprint-match.md). User-facing reader version at [`identity-fingerprint-match.md`](src/Mostlylucid.BotDetection/docs/identity-fingerprint-match.md).

- **Two-pass match** — Pass 1 looks up `fingerprint_keys[primary_signature]` and runs a quick weighted-cosine confirm against the candidate's centroid (fast-path; humans pay microseconds). Pass 2 runs `IIdentityAnchorIndex.SearchAsync` over the centroid + observation set when L1 misses or fails confirm (slow-path; bots pay it). Pass 2 disagreement triggers a *correction*: per-fp weights nudge toward dims that distinguished the new winner, and `fingerprint_keys` re-binds.
- **Per-fingerprint weight learning** — every fingerprint carries its own dim-weight vector. Two learning signals: corrections (sharp edits when L1 was wrong) and stability (gentler nudges every absorption, based on per-dim deviation from centroid).
- **Centroid absorption (`FingerprintAbsorptionService`)** — folds detailed observations into the centroid via a maturity-weighted mean (`new = (centroid * maturity + obs) / (maturity + 1)`) so a year-old visitor's shape is preserved while detail compresses. Recomputes inferred client type against the archetype registry on every absorption; emits a structured drift log when classification flips.
- **Drift verifier (`FingerprintDriftService`)** — re-checks L1-confirmed fingerprints whose `cached_score_updated_at` is older than `CachedScoreTtlSeconds`. Closes the "L1 still observes" guarantee — a "passes-as-human" fast-path verdict cannot persist indefinitely without L2 agreement on the latest observation.
- **Calibration (`IdentityWeightCalibrationService`)** — periodically computes a global per-dim weight vector via the Fisher discriminant ratio (between-cluster variance / within-cluster variance) over fingerprints grouped by inferred client type. High-discriminating dims get amplified; noise dims suppressed. Same tick refines each archetype centroid by blending in the mean of its descendants (cap-bounded by `ArchetypeRefinementCap` so an archetype can never drift more than half its identity per cycle).
- **Global weights cache (`IdentityGlobalWeightsCache`)** — hosted singleton that reads the calibrated weights on every `GlobalRefreshSeconds` tick. The matcher composes them multiplicatively with per-fp weights at confirm + Pass 2 time. `Volatile.Write` atomic swap; live matching never sees a torn vector.
- **Archetype registry** — nine starter archetypes loaded from embedded YAML at `src/Mostlylucid.BotDetection/Definitions/IdentityArchetypes/*.yaml`. Used as cold-start templates for new fingerprints and as cluster labels for calibration. Self-refining — descendants pull their archetype's centroid toward the population mean.
- **`IFoundationContributor` wave** — `IdentityVectorContributor` (priority 5) composes the request vector from upstream signals and raw headers; `FingerprintMatchContributor` (priority 6) runs the two-pass match. Both are foundation: they run unconditionally under any policy, never gated by classifier filters.
- **Signal contract** — `identity.fingerprint_id`, `identity.match_score`, `identity.is_new_fingerprint`, `identity.is_correction`, `identity.rotation_candidate`, `identity.client_type`, `identity.client_type_confidence`, `identity.client_type_origin`, `identity.cached_bot_probability`, `identity.cached_risk_band`. All emitted by `FingerprintMatchContributor`; consumed by downstream display, the BDF rig, and `IdentityVerdictLookup` (the verdict-cache composition path).
- **Verdict cache composition** — when Identity is enabled, `SignatureVerdictGate` reads both the per-signature aggregate (sliding window, scoped to IP+UA) and the per-fingerprint cached verdict (scoped to the metastable identity, survives rotation). Fresher source wins. Skip-path responses set `X-StyloBot-VerdictSource: identity-cache` (vs. plain `cache`) when the fingerprint cache was the winner, and emit `X-StyloBot-IdentityFingerprint` with the resolved fingerprint id. Returning visitors whose IP+UA has changed inherit their prior verdict instead of paying for a fresh pipeline pass.
- **Dashboard "Identities" tab** — new tab listing every metastable fingerprint with the surface an operator needs to triage drift candidates: fingerprint id (short), inferred client type + confidence, total observation count, **unabsorbed observation count** (the freshness budget the next absorption tick will fold), correction count, cached bot probability + risk band, last verified, last seen, archetype origin. Sorted by unabsorbed-count desc so drift candidates float to the top. Two per-row actions: **Re-verify** posts to `POST /api/identities/{id}/reverify` and runs `FingerprintDriftService.VerifyOneAsync` on demand (skips the `CachedScoreTtlSeconds` gate, bumps `cached_score_updated_at`, returns the row HTML for HTMX in-place swap); **Run AI** posts to `POST /api/identities/{id}/run-ai` and invokes `IdentityAiOpinionService` (see below). Empty-state copy when `Identity:Enabled = false`.
- **`SqliteVecIdentityAnchorIndex` — vec0 perf path with brute-force fallback** — when [sqlite-vec](https://github.com/asg017/sqlite-vec) (`vec0.dylib`/`vec0.so`/`vec0.dll`) is available on the OS library search path (or at the path specified by `BotDetection:Identity:Engine:SqliteVecExtensionPath`), `SqliteFingerprintStore` auto-loads it at init, creates the `fingerprints_vec` and `observations_vec` virtual tables (centroid keyed by `fingerprint_id` TEXT primary key; observations keyed by integer `observation_id` with `+fingerprint_id` as a queryable aux column), and mirrors every `InsertFingerprintAsync` / `RecordObservationAsync` / `AbsorbObservationAsync` write into the matching vec0 row in the same transaction. KNN searches dispatch via `WHERE col MATCH ? AND k = ?` and translate vec0's L2 distance back to cosine (`1 - distance² / 2` for L2-normalised vectors) so scores stay parity with the brute-force engine. When the extension isn't installed, isn't loadable, or errors mid-flight, the index falls through to `BruteForceIdentityAnchorIndex` per-call — the FOSS package ships zero native dependencies and operators opt into the perf path by installing the binary themselves.
- **`IdentityAiOpinionService` — operator-triggered classifier on demand** — given a fingerprint id, builds a prompt summarising the fingerprint's metadata (inferred client type + confidence, observation count, centroid maturity, correction count, age, current cached verdict, archetype origin), sends it synchronously to the registered `ILlmProvider` (resolved by reflection so core takes no hard dependency on the optional Llm packages), parses the JSON reply, and writes the verdict back to `fingerprints.cached_*`. Returns a structured `IdentityAiOpinionResult` with one of `ok`, `identity-disabled`, `not-found`, `no-llm-provider`, `llm-not-ready`, `llm-error`, or `parse-error` so the dashboard can show exactly why a click was a no-op. The middleware forwards the status as `X-StyloBot-AiOpinion-Status`, the bot probability as `X-StyloBot-AiOpinion-Probability`, and the error detail (CR/LF-stripped, capped at 200 chars) as `X-StyloBot-AiOpinion-Detail`.
- **SQLite schema** — seven core tables in `fingerprints.db` (separate file from the main detection DB): `fingerprints`, `fingerprint_keys`, `fingerprint_observations`, `fingerprint_corrections`, `identity_dimension_weights`, `identity_archetypes`, `identity_vector_layout`. Vector layout version is fixed at deployment; mismatched layouts on startup fail loud rather than silently corrupt data.
- **Test coverage** — 19 identity unit tests (Fisher math, weight composition, drift verifier, calibration end-to-end, global weights cache) plus 17 BDF replay scenarios that probe the metastable contract: every request emits `identity.fingerprint_id`, the last request of each scenario doesn't allocate a new fingerprint.

### Added — Verdict cache (rolled forward from 6.4.6)

Wires the per-signature reputation aggregate the product has been computing into the live request path as a verdict cache. Known fingerprints reuse their verdict; only unknown, stale, or verifiably-changed fingerprints run the full detector pipeline. The verdict source is the existing ephemeral sliding window in `SignatureCoordinator`, not a parallel cache. Four gate outcomes emerge per policy:

- **Skip**: live signature state meets `SkipMinConfidence` (direction-agnostic: sure-bot AND sure-human qualify) AND was observed within `SkipMaxAgeSeconds`. The variance watchdog confirms nothing variant. The request bypasses the heavy detector pipeline; the cached verdict is enforced. Sliding-window observation is still recorded so clustering and drift detection see the request. Emits `X-StyloBot-VerdictSource: cache`.
- **Watchdog-trip**: Skip-eligible cache hit BUT the variance watchdog detected an unusual signal (IP rotation, rate spike). The cached verdict is invalidated for this request; full pipeline runs. Emits `X-StyloBot-WatchdogTrip: <reason>`.
- **Bias**: cache hit meets `BiasMinConfidence` but is too low-confidence or too stale for Skip. Pipeline runs with the cached verdict injected as a Wave 0 prior contribution. The posterior is pulled toward the prior in proportion to prior confidence and linear age decay.
- **Miss**: no usable cache, below `BiasMinConfidence`, or older than `BiasMaxAgeSeconds`. Full pipeline runs from scratch.

Also fixes a latent persistence bug: the `signatures.bot_probability` upsert was MAX-prob (so a one-off 0.95 false positive pinned the signature at 0.95 forever); it is now a proper EWMA that decays toward benign observations.

### Added

- **`SignatureCoordinator.TryGetVerdictAsync(signature)`** -snapshot accessor over the existing in-process sliding window. Returns `SignatureVerdict?` with the per-signature aggregate (probability, confidence, request count, last-seen, risk band, threat score). The window is bounded by `MaxSignaturesInWindow` (default 1000, platform-tunable) with LRU + TTL eviction so hot signatures retain their slot.
- **`SignatureVerdictGate`** -decides Skip / Bias / Miss per request based on the policy's `SignatureCacheOptions`. `SkipSamplingRate` (default 5 percent) forces a fraction of Skip-eligible requests to refresh the cache.
- **`VarianceWatchdog`** -cheap per-signature checks (IP rotation within window, rate spike vs rolling baseline, path-family divergence vs bounded per-signature memory) that veto a Skip and force a pipeline run. Has its own per-signature observation history kept current by every request (Skip, Bias, or Miss).
- **`FingerprintPriorContributor`** (Wave 0, priority 4) -reads `fingerprint.prior.*` signals written by the gate on a Bias decision and emits a single calibrated contribution. Effective weight is `prior_confidence * multiplier * linear-age-decay`, so old priors lose all weight and very-recent confident priors strongly anchor the posterior.
- **`SignatureCacheOptions`** on `DetectionPolicy` -per-policy thresholds (`SkipMinConfidence`, `SkipMaxAgeSeconds`, `BiasMinConfidence`, `BiasMaxAgeSeconds`, `SkipSamplingRate`, `Enabled`) plus a nested `Watchdog` of type `VarianceWatchdogOptions`. JSON-bindable via the existing `DetectionPolicyConfiguration`.
- **`AggregatedEvidence.PriorProbability` and `.RequestContributionDelta`** -let downstream consumers display the per-request contribution to the fingerprint score instead of the absolute per-request probability. Computed during orchestrator aggregation by reverse-mapping the `FingerprintPrior` contribution. The Skip path's synthetic evidence reports the cached value as both prior and posterior, with delta zero.
- **`SignatureCoordinator.NotifyObservationAsync`** -lightweight per-signature observation hook called on the Skip path. Records signature, timestamp, path, and last-known probability into the sliding window so clustering, drift detection, and the dashboard's per-signature stats see the full traffic, NOT a hole where the cached fingerprint flew through.
- **`BotDetectionOptions.SignatureEwmaAlpha`** (default 0.15) -tunable EWMA weight for the newest observation when updating a signature's persisted `bot_probability`. Smaller values mean stronger memory; larger values react more quickly to changes.
- **`signatures.last_updated_utc`** column (with migration) so the verdict gate can apply freshness thresholds.
- **CLI dashboard** -feed rows now display the per-request contribution delta (signed percentage points moved against the fingerprint's prior) instead of the absolute Bot%. Cache-served rows are marked with a dim asterisk in the time column. Sidebar Top Fingerprints shows the fingerprint's EWMA-smoothed posterior with an 8-sample sparkline of recent observations so volatility is visible as trend, not as a row-by-row hysterical number. Bullet colour reflects the EWMA (stable verdict), not the latest spike.
- **`docs/fingerprint-verdict-cache.md`** -reference doc covering the scaling thesis, the four gate outcomes, per-policy configuration, the EWMA upsert fix, direction-agnostic Skip, the sliding window as core (not a parallel cache), Skip-path sliding-window observation, the per-request contribution delta, performance posture, tuning patterns, and follow-ups.

### Changed

- **`BotDetectionMiddleware`** -consults `SignatureVerdictGate` at request intake. Skip enforces the cached verdict and bypasses the heavy pipeline (with watchdog veto check). Bias writes `fingerprint.prior.*` signals to `context.Items` so the prior contributor injects them in Wave 0. Miss runs the pipeline normally. `ComputeAndStoreSignature` is now called BEFORE the gate (moved from post-orchestrator) so the gate can find the primary signature on the first request; the call is idempotent so the existing post-orchestrator call is a no-op when the signature is already populated.

### Fixed

- **`signatures.bot_probability` upsert was MAX, now EWMA**. A signature that scored 0.95 once was pinned at 0.95 forever, regardless of subsequent benign observations. The upsert now blends `(1 - alpha) * prior + alpha * observation` with `alpha = 0.15` (configurable via `BotDetectionOptions.SignatureEwmaAlpha`). Old high-risk priors now decay toward benign observations as the entity continues to behave.

### Tests Added

Total: 24 new unit tests across:

- `SignatureUpsertEwmaTests` (3): literal first observation, EWMA decay on repeated benign observations, `last_updated_utc` recording.
- `SignatureCoordinatorVerdictTests` (3): unknown signature returns null, after-record-request returns snapshot, multiple-requests reflects latest aggregate.
- `VarianceWatchdogTests` (5): no change does not trip, IP rotation within window trips, IP rotation disabled does not trip, rate spike trips, disabled never trips.
- `SignatureVerdictGateTests` (8): no signature, no cache, fresh confident hit (Skip), low confidence (Bias), very low (Miss), disabled, sampling refresh (Skip downgraded to Bias), stale (Bias not Skip).
- `FingerprintPriorContributorTests` (4): no prior, human prior, bot prior, old prior decayed.
- `BotDetectionOptions.SignatureEwmaAlpha` exposed as a tunable knob with default 0.15.

Full suite after this work: 2040 tests pass across `Mostlylucid.BotDetection.Test`, `Mostlylucid.BotDetection.Api.Tests`, `Mostlylucid.BotDetection.Demo.Tests`, `Stylobot.Gateway.Tests`, and `Mostlylucid.BotDetection.Orchestration.Tests` (Puppeteer integration tests excluded).

### Verified

- **AOT compatibility**: `Mostlylucid.BotDetection.Console` publishes cleanly under `PublishAot=true` for `osx-arm64`; zero IL2026/IL3050 warnings from any of the added files (`SignatureVerdict`, `SignatureVerdictGate`, `VarianceWatchdog`, `FingerprintPriorContributor`, `SignatureCacheOptions`, `VarianceWatchdogOptions`).
- **Build**: 0 errors across the solution after the work. 0 new warnings introduced.

### Performance and hot-path quality

A second review pass tightened allocations along the Skip path before merge:

- **`SignatureCoordinator.NotifyObservationAsync`** dropped from ~6 heap allocations per Skip request (Guid string, Dictionary, HashSet, `SignatureGeoContext`, `SignatureUpdateRequest`, LINQ prune) down to a single `SignatureRequest` by calling the atom directly with shared static empties. The keyed-sequential dispatch and shadow-index work are correctly bypassed for Skip; they still run on Miss / Bias via the unchanged `RecordRequestAsync`.
- **`VarianceWatchdog`** is now bounded: `MaxFingerprints = 10_000` with TTL + LRU prune (single-flight via `Interlocked`); per-fingerprint observation queue capped at 600 entries. Was previously unbounded.
- **`VarianceWatchdog.Check`** is sync (was fake-async with no awaits). `WatchdogResult` is a `readonly record struct` so checks allocate nothing on the heap.
- **`Slash24`** uses `Span<byte>` + `string.Create` to avoid intermediate string allocation.
- **`FingerprintPriorContributor`** caches the empty-result `Task<IReadOnlyList<DetectionContribution>>` so the Miss path allocates nothing.
- **`DetectionLedgerExtensions`** reads `PriorProbability` directly from signals instead of reverse-mapping the `FingerprintPrior` contribution.
- **SQL EWMA upsert** dropped a redundant `COALESCE` wrapper.
- **Stringly-typed context keys** `"BotDetection:Signature"` and `"BotDetection.Signatures"` were duplicated across 14 call sites in 12 files. Now centralised as `BotDetectionMiddleware.PrimarySignatureKey` and `.SignatureSetKey`; all callers route through the constants.

### BenchmarkDotNet

- **`VerdictCacheBenchmarks`** added with `[MemoryDiagnoser]` covering: gate Skip/Bias/Miss paths, watchdog Check (tripped and not), watchdog RecordObservation, coordinator `NotifyObservationAsync`, and prior-contributor Miss vs Bias. Lets allocation regressions surface immediately.

### Dashboard wiring follow-up

- **`DashboardSummary.BotFingerprints` / `.HumanFingerprints` / `.HighRiskFingerprints`** new fields populated from the `signatures` table (one row per unique fingerprint, with EWMA-blended `bot_probability` and latest `risk_band`). The previous `BotRequests` / `HumanRequests` were request counts (one detection row per request); the dashboard's "X ok / Y bots / Z high" banner was effectively saying "this many *requests*", which dominated whenever a single fingerprint hit repeatedly. The new fields say "this many distinct *actors*". `BotRequests`/`HumanRequests` remain for traffic-volume displays.
- **`SqliteDashboardEventStore.GetSummaryAsync`** now issues a second query against `signatures` to compute the fingerprint-level counts plus a fingerprint-level `RiskBandCounts` distribution. The previous implementation always returned an empty `RiskBandCounts` dict.
- **CLI banner** (`RemoteDashboardTui`) renders the new fingerprint-level numbers: `✓ N ok  ✗ N bots  ⚠ N high  · N sigs`. CLI's `RemoteStats` carries the three new fields end-to-end.
- **`SbWidgetBatchMiddleware.BuildSummaryContextAsync`** exposes `bot_fingerprints`, `human_fingerprints`, `high_risk_fingerprints` to the widget Liquid context.

### Entity-resolution merge-prior inheritance

- **`SignatureCoordinator.TryGetVerdictAsync`** now falls through to the family's canonical signature when the requesting signature has no atom of its own. A rotating fingerprint that has been merged into a family inherits its sibling's verdict and skips a fresh pipeline pass. The verdict is reported under the *requested* `SignatureId` so the gate, cache header, and dashboard see continuous identity. Forgetting is implicit via the existing sliding-window TTL (cold canonical atoms evict naturally) and via split events that drop the `_signatureToFamily` entry. No new aggregation policy or invalidation channel was needed.

### Follow-up batch

- **`SignatureVerdict.RiskBand` and `.ThreatScore`** are now populated on Skip-served verdicts. The tracking atom captures the latest pipeline-observed `intent.threat_score` and confirmed-bad flag from incoming signals (Skip observations leave the cached values untouched, so the band stays representative of the most recent full pass). `TryGetVerdictAsync` then routes those plus `BotProbability` / `Confidence` / `RequestCount` through `DetectionLedgerExtensions.DetermineRiskBand(aiRan: false)` so a cached verdict carries the same band a fresh pipeline pass would.
- **Watchdog `CheckPathCentroid` is implemented.** Each `FingerprintHistory` keeps a bounded fixed-size (8 slot) memory of recently-observed path families (first non-empty path segment, lowercased). The check activates once at least three distinct families have been seen, then trips on a never-before-seen family. Tunable per policy via the existing `VarianceWatchdogOptions.CheckPathCentroid` flag.
- **`BoundedChannelLearningBusTests.TryPublish_WhenQueueFull_DropsOldestAndAcceptsNew`** flake is fixed: replaced `await foreach + ReadAllAsync` with a bounded `WaitToReadAsync` loop and a 30-second deadline, matching the fix used in 6.4.2 for the sibling HP-mode test.
- **CLI dashboard tunnel-dropout detection.** `RemoteDashboardService` now categorises connection failures (timeout / HTTP status / socket / WebSocket error code) and tracks consecutive failure count. After three back-to-back misses (~15s at the existing 5s backoff) the status line flips to `Tunnel down? (<reason>, N misses)` so a vanished anonymous Cloudflare tunnel is visible without grepping logs.
- **`Helpers.DeterministicBucket`** consolidates the Knuth-multiplicative-hash `bucket < rate` decision used by both `SignatureVerdictGate.ShouldRefresh` and `LoadShedDecision.ShouldShed`.
- **`Helpers.NetworkHelper.GetIPv4Slash24`** extracts the `/24` helper previously private to `VarianceWatchdog`.
- **`Helpers.Ewma.Update`** consolidates the canonical `(1 - alpha) * previous + alpha * observation` blend, applied in `PipelineLoadSensor`, `PatternReputation`, and `WeightStore`. `UaProfileStore` is intentionally left alone with a comment noting its inverted-alpha convention.

### Not Done

Nothing outstanding from the original Not Done list. Future ideas (not blockers): surfacing the per-request contribution delta on the web dashboard the way the CLI already does (`PriorProbability` / `RequestContributionDelta` are populated on the Skip path and consumed by the CLI; the web dashboard widgets currently still display the absolute Bot%).
- Cloudflare anonymous quick tunnels time out (typically a few hours); the CLI dashboard's sidebar shows the tunnel URL but does not yet detect when it goes dead. Separate small follow-up.

---

## [6.4.2] - 2026-05-12

Test-only release. Fixes a flaky concurrency test that intermittently failed the 6.4.1 publish workflow on slow GitHub Actions runners.

### Fixed

- **`BoundedChannelLearningBusTests.TryPublish_WhenHpModeOn_ReturnsImmediately_InnerBusReceivesLater`** -replaced the `TaskCompletionSource + Task.Run` polling shim with a direct `WaitToReadAsync` on the inner reader and bumped the deadline from 5s to 30s. The test now fails clearly on a genuine deadlock but no longer flakes under normal CI scheduler variance.

No production code changes. No runtime behaviour changes.

---

## [6.4.1] - 2026-05-12

Policy-system consolidation. Two genuine additions (`FailureMode`, `LoadShed`) plus a deprecation pass that surfaces and documents the existing duplication between `BotDetectionOptions` and `DetectionPolicy`. No breaking changes; existing customers continue to work unchanged.

### Added

- **`DetectionPolicy.OnFailure` (FailureMode enum)** -policy-level behaviour when detection itself fails. Three values: `FailOpen` (default), `FailClosed` (HTTP 503), `LogOnly` (allow + emit `X-StyloBot-Failed` header). Honoured by `BotDetectionMiddleware` (via a new try-catch around `DetectWithPolicyAsync` that previously was missing, so unhandled detector exceptions used to crash with HTTP 500) and `SidecarBotDetectionMiddleware` (via `SidecarClientOptions.OnFailure`).
- **`DetectionPolicy.LoadShed` (LoadShedOptions)** -per-policy load shedding at request intake. `DropFractionAtHigh` and `DropFractionAtCritical` (default 0.0) drop the configured fraction of requests when `PipelineLoadSensor.CurrentBand` reports `High` or `Critical`. Sheds emit `X-StyloBot-Shed: 1` for observability. Decision is deterministic by request seed (Connection.Id hash) so retries land identically.
- **`LoadShedDecision`** service and **`ILoadBandSource`** interface -wraps `PipelineLoadSensor` so the shed decision is unit-testable.
- **`HoneypotPack`** -discoverable static factory for `SimulationPack`, disambiguating the type name from `ReactionPack` / `CompliancePack` / `MonitoringPack`. Existing `SimulationPack` code continues to work.
- **`docs/policy-system.md`** -new reference doc covering all four policy-shaped concepts (DetectionPolicy, ActionPolicy, FailureMode, LoadShed), the four "pack" types, threshold precedence, and existing capabilities customers commonly ask for (per-detector timeout, the existing circuit breaker, sidecar mode, FastPathDecider sampling).

### Changed

- **`BotDetectionMiddleware.DetectWithPolicyAsync` calls** -both call sites are now wrapped in try-catch that applies the policy's `OnFailure`. Previously an unhandled detector exception crashed the request with HTTP 500. The change preserves the existing UA-override finally semantics in `RunDetectionWithOverriddenUaAsync`.
- **`SidecarBotDetectionMiddleware`** -previously hardcoded fail-open on RPC error; now reads `SidecarClientOptions.OnFailure`.

### Deprecated

Nine fields on `BotDetectionOptions` duplicate per-policy `DetectionPolicy` properties. All are now `[Obsolete]` (warning only) with the corresponding replacement in the message. Scheduled for removal in a future major release. Internal callsites are suppressed via `#pragma warning disable CS0618` so the build remains clean while the consolidation lands incrementally; customer code that references these fields will emit a build warning naming the replacement.

- `BotThreshold` -use `DetectionPolicy.ImmediateBlockThreshold` / `EarlyExitThreshold`
- `MinConfidenceToBlock` -use `DetectionPolicy.MinConfidence`
- `BlockDetectedBots` -use per-policy `ActionPolicyName` / `Transitions`
- `AllowVerifiedSearchEngines` -use `DetectionPolicy.AllowVerifiedBots` (or a `Transitions` rule)
- `EnableUserAgentDetection`, `EnableHeaderAnalysis`, `EnableIpDetection`, `EnableBehavioralAnalysis`, `EnableLlmDetection` -use `DetectionPolicy.{FastPathDetectors, SlowPathDetectors, AiPathDetectors, ExcludedDetectors}`

### Configuration

New per-policy JSON shape (via `DetectionPolicyConfiguration`):

```json
"Policies": {
  "admin": {
    "OnFailure": "FailClosed",
    "LoadShed": { "DropFractionAtHigh": 0.0, "DropFractionAtCritical": 0.05 }
  }
}
```

Unrecognised `OnFailure` values fall back to `FailOpen`. Absent `LoadShed` defaults to a zero-fraction (no shedding).

### Tests Added

- `FailureModeTests` -3 facts on enum default + init + value set
- `BotDetectionMiddlewareFailureTests` -4 facts: FailOpen / FailClosed / LogOnly applier behaviour + LoadShed policy-options integration
- `SidecarMiddlewareFailureTests` -2 facts on `SidecarClientOptions.OnFailure` default + setter
- `LoadShedDecisionTests` -6 facts covering Low / Normal / High / Critical bands and 0.0 / 0.5 / 1.0 drop fractions
- `PolicyConfigurationBindingTests` -3 facts for JSON binding of `OnFailure` (valid, default, unrecognised) and `LoadShed`

Total new tests: 18. Full BotDetection.Test suite: 1520 passed, 0 failed, 10 pre-existing skips (Ollama integration).

### Not Done

- No `PerformanceMode` enum was added. The existing scattered controls (`UseFastPath`, `FastPathDetectors`, `FastPathDecider.IsAlwaysFullPath`, `ForceSlowPath`, and the seven built-in policies `default` / `strict` / `relaxed` / `static` / `learning` / `monitor` / `api`) already cover the concept; adding a new enum would have been an 8th surface for the same idea. The new `docs/policy-system.md` makes the existing capabilities discoverable.
- No new circuit-breaker was added. The existing `CircuitState` in `BlackboardOrchestrator` already opens after `CircuitBreakerThreshold` failures (default 5) and half-opens after `CircuitBreakerResetTime` (default 60s). Documented in `policy-system.md`.
- Internal callsites that read deprecated `BotDetectionOptions` fields were NOT migrated to `DetectionPolicy`. That migration is a larger design decision (some of those callsites are public DI extension methods like `AddSimpleBotDetection`) and is deferred to a future major release.

---

## [6.4.0] - 2026-05-12

False-positive reduction in `ContentSequenceContributor`. The previous flat unexpected-state score (0.5) tripped divergence on routine browser noise (a single unexpected static asset crossed the 0.4 threshold and cascaded to five deferred detectors). The global baseline was a hardcoded human-with-SignalR template that diverged for any site that did not match it, especially on cold-start before clusters formed. This release replaces both with per-state weights and a site-learned baseline, plus several supporting fixes that prevent heavy SPAs and returning visitors from being misclassified.

### Added

#### Per-state divergence weights

- **`StateDivergenceWeights`** (`Mostlylucid.BotDetection/Services/StateDivergenceWeights.cs`) -immutable per-`RequestState` weight map; `Default` is a `FrozenDictionary` populated for all 10 enum values; `FromParameters(resolve)` factory for YAML overrides
- **YAML weight knobs** in `contentsequence.detector.yaml`: `unexpected_weight_static_asset` (0.05), `unexpected_weight_page_view` (0.10), `unexpected_weight_api_call` (0.25), `unexpected_weight_signalr` / `unexpected_weight_websocket` / `unexpected_weight_server_sent_event` (0.20), `unexpected_weight_form_submit` (0.40), `unexpected_weight_auth_attempt` (0.60), `unexpected_weight_not_found` (0.50), `unexpected_weight_search` (0.40)
- **Drift-guard test** (`YamlKeyFor_HasMappingForEveryRequestState`) -iterates `Enum.GetValues<RequestState>()` via reflection and asserts every state has a YAML key; future enum additions fail loudly instead of silently aliasing to ApiCall

#### Learned global baseline

- **`CentroidSequenceStore.RelearnGlobalAsync(minSessions, ct)`** -learns the global chain from a broad sample of confirmed-human sessions via the new `ClusterSessionLoader` delegate; falls back to template only when sessions are below `learned_global_min_sessions` (default 50)
- **`CentroidSequenceStore.IsGlobalReady`** -read by `ContentSequenceContributor` to suppress divergence scoring entirely while warming up (`scoringAllowed = ctx.CentroidType != Unknown || _centroidStore.IsGlobalReady`)
- **Learned global persistence** -`"global"` row in `centroid_sequences` table; `LoadFromDatabaseAsync` restores both `_globalChain` and `IsGlobalReady` on startup
- **`CentroidSequenceRebuildHostedService`** -kicks initial `RelearnGlobalAsync(50)` in `StartAsync`; runs again after every cluster rebuild so the learned global re-converges as the human cluster grows
- **YAML knob** `learned_global_min_sessions: 50` in `contentsequence.detector.yaml`

#### Real centroid computation from session data

- **`SessionChainAggregator`** (`Mostlylucid.BotDetection/Services/SessionChainAggregator.cs`) -aggregates per-cluster session `TransitionCountsJson` into a Markov transition matrix and greedy-walks the expected chain from the modal `DominantState`; gated by `minTotalTransitions` floor (10); truncates when a state has no outbound transitions
- **`CentroidSequenceStore.ClusterSessionLoader`** delegate -optional constructor argument; when present, `RebuildAsync` aggregates real session paths into modal chains instead of mapping cluster type to a hardcoded template
- **DI factory wiring** in `ServiceCollectionExtensions.cs` -builds the loader closure over `SqliteSessionStore`; empty signatures list calls `GetRecentSessionsAsync(perSig, isBot: false, ct)` for the learned-global broad sample

#### Cookie-aware cache-warm in the critical window

- A `Cookie` header on the first non-static continuation request now flips `sequence.cache_warm = true` immediately, suppressing the unexpected-ApiCall penalty for returning visitors whose browser already has warm assets; the original `phaseIndex > 0 && !observedSet.Contains(StaticAsset)` trigger is preserved

#### Idle-reset on the request-count window

- **`RequestCountIdleResetSeconds`** parameter (default 60s) -inter-request gap above this resets `RequestCountInWindow`, `WindowStartTime`, `ObservedStateSet`, and `CacheWarm` so a long-lived heavy SPA does not accumulate a permanent `HighRequestCountScore` penalty
- Idle-reset locals are now computed BEFORE `ComputeDivergenceScore`, so the first request after an idle gap is scored against a fresh window (previous code left a one-request residual penalty and mis-categorised the phase)

### Changed

- **`DivergenceThreshold`** -default raised from 0.4 to 0.6 so a single unexpected state cannot trip divergence
- **`HighRequestCountThreshold`** -default raised from 50 to 200 (modern dashboards with polling and telemetry routinely exceed 50 requests per session)
- **`MachineSpeedScore`** -default lowered from 0.4 to 0.3
- **`HighRequestCountScore`** -default lowered from 0.3 to 0.2
- **`YamlKeyFor`** -default switch arm now throws `ArgumentOutOfRangeException` (was silently aliasing unknown states to ApiCall)
- **`CentroidSequenceStore.SetGlobalChain`** -also sets `IsGlobalReady = true`; an explicit caller asserting a baseline is available

### Removed

- **`UnexpectedStateScore`** property on `ContentSequenceContributor` -replaced by per-state lookup via `GetWeights().For(requestState)`
- **`unexpected_state_score`** YAML key -superseded by the 10 `unexpected_weight_*` keys
- **Hardcoded `_globalChain` template** -`[StaticAsset, StaticAsset, StaticAsset, ApiCall, SignalR]` is now the fallback only when the learned global has not converged

### Fixed

- **First request after idle no longer carries residual high-count penalty** -the reset locals are computed before `ComputeDivergenceScore`, not after
- **Phase mis-categorisation after idle reset** -`elapsedMs` is computed against `effectiveWindowStart` (the post-reset value), so the first request post-idle lands in the critical phase as intended, not the settled phase (index 3)
- **`MachineSpeedRequest_SubTwentyMs_WritesDivergedTrue` renamed to `MachineSpeedPlusNotFound_TripsDivergence`** -test name now reflects the new policy: machine-speed alone (0.3) intentionally cannot trip the 0.6 threshold; combined with NotFound (0.50) it does

### Performance

- **Lazy `_weights` cache** on `ContentSequenceContributor` (commit `1ccb36e`) -Wave 0 hot path: matches the base class `ConfiguredContributorBase.Config` caching pattern; avoids 10 GetParam calls per request inside the sidecar p99 detection budget

### Tests Added

- **`StateDivergenceWeightsTests`** -4 facts on the type's Default values and FromParameters override behaviour
- **`SessionChainAggregatorTests`** -10 facts covering greedy walk, modal start, truncation on no outbound, min-total floor, JSON parser edge cases (unknown states, malformed input)
- **`CentroidSequenceStoreTests`** -learned-global ready/not-ready/persistence-across-init tests; loader fallback to template; uses temp-file SQLite paths with `IDisposable` cleanup of `.db` and WAL/SHM sidecars
- **`ContentSequenceContributorTests`** -8 new facts: per-state weight scoring, AuthAttempt trips divergence, StaticAsset does not, cookie-aware cache-warm, idle reset, no residual penalty after idle, phase detection uses fresh window, drift-guard for `YamlKeyFor`, global-warming-up suppresses scoring, YAML weight override flows through

### Documentation

- `src/Mostlylucid.BotDetection/docs/centroid-freshness.md` -new section "Learned global baseline and per-state weights" covering all four mechanisms (weights, cookie-warm, idle reset, learned global)
- `src/Mostlylucid.BotDetection/docs/content-sequence-detection.md` -divergence-scoring section rewritten with the new per-state weight table; YAML config block updated to match the current manifest

### Verified

- **AOT compatibility** -`Mostlylucid.BotDetection.Console` (`PublishAot=true`) publishes cleanly for `osx-arm64`; zero new IL2026/IL3050 warnings introduced by `StateDivergenceWeights`, `SessionChainAggregator`, `CentroidSequenceStore`, `ContentSequenceContributor`, or `CentroidSequenceRebuildHostedService`. The published binary starts and serves requests through the full detection pipeline.
- **Sidecar pattern** -end-to-end review confirmed all changes work correctly in the gRPC sidecar deployment: Cookie header round-trips through `SyntheticHttpContext.FromDetectRequest`; `SequenceContextStore`, `CentroidSequenceStore`, and `CentroidSequenceRebuildHostedService` are all singletons in the sidecar's DI graph; `ISessionStore` is registered so the learned-global loader closure activates identically to the gateway pattern; Caddy plugin only forwards to the gRPC `Detect` RPC with no shadow sequence tracking

---

## [6.2.0] - 2026-05-06

### Added

#### Endpoint Pinning and Honeypot Path Management

- **`IPinnedEndpointStore`** -interface for operator-pinned endpoints; `PinnedEndpoint` sealed record (`Id`, `Method`, `Path`, `IsHoneypot`, `Note`, `CreatedAt`)
- **`SqlitePinnedEndpointStore`** -SQLite-backed implementation writing to `sessions.db` (`pinned_endpoints` table); unique index on `(method, path)` with `ON CONFLICT DO NOTHING` + re-SELECT upsert; semaphore write lock; registered automatically by `AddStyloBotDashboard()`
- **Dashboard pin API** -three routes handled by `StyloBotDashboardMiddleware`: `GET /_stylobot/api/endpoint-pins`, `POST /_stylobot/api/endpoint-pins` (JSON or form body), `DELETE /_stylobot/api/endpoint-pins/{id}`; method validated against allowlist (`ANY`, `GET`, `POST`, `PUT`, `DELETE`, `PATCH`, `HEAD`, `OPTIONS`)
- **Endpoint detail protection section** -replaces the old Bot Policy section; shows active policy badge, reaction pack coverage rows (pack name, scope, level, policy), and pin/unpin controls with HTMX inline form
- **Pin Endpoint inline form** -in the Endpoints tab header; method dropdown, path input (required, must start with `/`), honeypot checkbox, optional note; submits via HTMX, replaces the endpoint list on success
- **Pin and honeypot icons** -pin icon (`bx-pin`) and warning icon (`bx-bug`) shown in the path cell of both the sortable full endpoints list and the compact view; zero-traffic pinned paths are merged into the endpoint data at query time
- **`DashboardEndpointStats`** -added `IsPinned`, `IsHoneypot`, `PinId` fields
- **`EndpointDetailModel`** -added `PolicyName`, `PackCoverage` (`IReadOnlyList<EndpointPackCoverage>`), `IsPinned`, `IsHoneypot`, `PinId` fields
- **`EndpointPackCoverage`** record -`(PackName, Scope, CurrentLevel, CurrentPolicy)` for reaction pack coverage display
- **Docs**: `src/Mostlylucid.BotDetection/docs/endpoint-pinning.md` -feature overview, dashboard API routes, programmatic usage, curl examples, what pinning does and does not do

#### Simulation Packs, Holodeck, and Custom Pack Authoring Documentation

- **Docs**: `src/Mostlylucid.BotDetection/docs/simulation-packs.md` -pack architecture (all record types), WordPress pack detail (11 paths, 8 CVE modules), path matching, template types, timing profiles, emitted signals
- **Docs**: `src/Mostlylucid.BotDetection/docs/holodeck.md` -three-layer architecture (`HoneypotPathTagger`, `HolodeckCoordinator`, `SimulationPackResponder`), FOSS vs LLM tiers, beacon/canary lifecycle, `IHolodeckResponder` interface, engagement slot management, testing headers
- **Docs**: `src/Mostlylucid.BotDetection/docs/custom-pack-authoring.md` -complete YAML schema reference, all template placeholders (`{{nonce}}`, `{{token}}`, `{{api_key}}`), LLM response hints, three registration approaches, minimal worked example (PHP admin panel pack)

#### Adblocker Detection

- **`AdBlockerDetectionTagHelper`** -client-side TagHelper that injects a probe element to detect adblockers without fingerprint; result written to `no-fingerprint` channel for `ClientSideContributor` to consume

#### Node SDK

- Simplified Express sample server and Playwright integration tests; removed redundant test scaffolding

### Documentation

- **README**: fixed all dead doc links (missing `src/` prefix on every path); expanded documentation section from 9 links to all 87 docs organized into six categories (Getting Started, Detection and Policies, Detectors, Features, Dashboard and API, Infrastructure and Ops, Architecture Reference)
- **Reaction Packs design spec and implementation plan** added to `docs/superpowers/`
- **Endpoint config view design spec and implementation plan** added to `docs/superpowers/`

---

## [6.1.2] - 2026-05-03

### Added

- **`SignatureCoordinatorWarmupService`** -replays recently persisted requests into the in-memory `SignatureCoordinator` on startup, preventing clustering from starting from zero after a restart; runs as `BackgroundService` (post-startup, does not block host readiness)
- **`ISessionStore.GetRecentRequestsAsync`** -fetches the N most-recent persisted requests within a time window, newest-first internally then returned oldest-first for chronological replay
- **`LearningEventBus.Subscribe`** -independent subscriber streams for fan-out event delivery; each subscriber receives a copy of every published event without consuming from the primary reader. `AnomalySaverService` migrated to subscriber pattern
- **`SignatureCoordinator.RecordRequestAsync`** `timestampUtc` optional parameter -allows replaying historical requests with their original timestamps
- **`LeidenClustering`** -CPM penalty now scales by `averageEdgeWeight * 0.5` instead of raw `totalWeight`, fixing unmergeable graphs when edge weights are normalized similarities in [0, 1]
- **`BotClusterService`** -pre-filters signatures by `MinBotProbabilityForClustering` before applying worst-offender cap, focusing CPU on genuinely suspect signatures
- Startup error handling in `CentroidSequenceRebuildHostedService` and `AssetHashInitHostedService` -initialization failures degrade gracefully instead of crashing the host

### Changed

- **`BlackboardOrchestrator`** -`primarySignature` is now included in learning event metadata, enabling `SimilarityLearningHandler` and `IntentLearningHandler` to use stable visitor signatures instead of per-request IDs
- **`SimilarityLearningHandler`** -prefers `primarySignature` from event metadata over `RequestId` to prevent index filling with one-off IDs
- **`IntentLearningHandler`** -same stable-signature preference for attack event attribution
- **`EphemeralDetectionOrchestrator`** -feature extraction now runs for uncertain events (confidence < 0.6, probability 0.3-0.8) in addition to high-confidence detections
- **`HeuristicFeatureExtractor`** -single-pass rewrite of `ExtractDetectorResults` and `ExtractStatistics`: eliminates 6x `ToList()` allocations, two `GroupBy+ToDictionary` allocations, and inline LINQ in the hot path
- **`BlackboardOrchestrator`** -`KnownAiDetectors` promoted to static field (eliminates per-request `HashSet` allocation)

### Fixed

- **`SignatureCoordinator.GetAllBehaviors`** -cache miss on a signature no longer evicts its `_ipIndex` entry; only `PruneShadowIndexesIfNeeded` owns `_ipIndex` cleanup, preventing a race where a newly-registered signature was removed before its async atom was cached
- **CI** -removed `retentionScorer` named argument in `SlidingCacheAtom` constructor calls; parameter exists in local project reference but not in published NuGet 2.4.0, causing `CS1739` build failures on CI
- **CI** -removed `/tmp/local-nuget` source from `NuGet.Config` that caused `NU1301` on GitHub Actions hosts

### Tests Added

- `SignatureConvergenceServiceTests` -removed spurious `Task.Delay(50)` calls; tests now rely on synchronous `_ipIndex` population
- `LeidenClusteringTests` -verifies two disconnected clusters stay separated at default resolution
- `LearningEventBusTests` -verifies subscriber receives copy without consuming primary reader
- `SimilarityLearningHandlerTests` -verifies `primarySignature` metadata is used as the stable vector ID

---

## [6.0.4-rc0] - 2026-04-26

### Added

#### Click Fraud Detection (IAB SIVT)
- **`ClickFraudContributor`** (Priority 38) -scores paid-ad traffic for IAB Sophisticated Invalid Traffic (SIVT) patterns using 7 detection signals
  - Datacenter IP on paid landing (gclid/fbclid/msclkid/ttclid + UTM): +0.50
  - VPN/anonymizer on paid landing: +0.25
  - Open proxy on paid landing: +0.20
  - Referrer mismatch with click ID present (referrer spoofing): +0.40
  - Referrer mismatch on UTM-only paid landing: +0.25
  - Single-page session (immediate bounce on paid traffic): +0.20
  - Headless browser on paid landing: +0.40
  - All weights configurable via `clickfraud.detector.yaml` / appsettings.json `BotDetection:Detectors:ClickFraudContributor`
  - Writes `clickfraud.*` signals: `clickfraud.score`, `clickfraud.pattern`, `clickfraud.confidence`, `clickfraud.is_paid_traffic`, `clickfraud.checked`
  - Triggers on `utm.present` OR (`session.request_count` AND `ip.is_datacenter`)
- **`PiiQueryStringContributor`** (Priority 19) -extracts UTM parameters and click IDs from query strings, emits hashed signals pre-sanitization
  - Detects: `utm_source`, `utm_medium`, `utm_campaign`, `utm_term`, `utm_content`, `gclid`, `fbclid`, `msclkid`, `ttclid`
  - All values HMAC-SHA256 hashed via `PiiHasher` -raw ad parameters never on the blackboard
  - Referrer mismatch detection: click ID present but referrer absent or mismatched platform domain
  - Writes `utm.*` signals: `utm.present`, `utm.source_hash`, `utm.medium_hash`, `utm.campaign_hash`, `utm.click_id_hash`, `utm.has_gclid`, `utm.has_fbclid`, `utm.has_msclkid`, `utm.has_ttclid`, `utm.referrer_mismatch`, `utm.referrer_present`, `utm.source_platform`
- **`BotType.ClickFraud`** -new bot type classification for IAB SIVT
- **`QueryStringSanitizer.DetectAdTrafficParams`** -static method for UTM/click-ID extraction with HMAC-SHA256 hashing and referrer mismatch analysis; malformed percent-encoding is skipped rather than thrown
- **`AdTrafficDetectionResult`** record -carries all hashed ad signal values with `SourcePlatform` inference (google, meta, microsoft, tiktok, paid_other, organic)
- Click-fraud signals wired into `IntentContributor`, `HeuristicFeatureExtractor` (5 new ML features), and `ReputationBiasContributor` (paid-traffic bias multiplier)
- **Docs**: `Mostlylucid.BotDetection/docs/click-fraud-detection.md` -IAB IVT taxonomy, signal flow, detection pattern table, YAML configuration reference, custom filter examples

#### License Expiry Freeze
- **`ILicenseState`** interface + **`FossLicenseState`** (always-active FOSS implementation) + **`LicenseState`** (commercial JWT-based)
- **`LicenseStateRefreshService`** -60-second background refresh for commercial license tokens
- **`SqliteLicenseGraceStore`** -persists `grace_started_at` to `botdetection.db`; grace period survives restarts
- **`LicenseTokenParser`** + **`LicenseStateSnapshot`** state machine: `Active` → `Grace` (30-day) → `Expired` transitions
- **Freeze guards** in `ReputationMaintenance`, `LearningBackground`, `BotCluster`, `CentroidRebuild` -learning freezes on grace/expired; all services log freeze state at startup

### Changed

- **`ReputationBiasContributor`** -bias-only mode always active; paid-traffic amplifier multiplies bias score when `utm.present` is set
- **`PiiHasher.GetKey()`** -returns `(byte[])_key.Clone()` instead of direct array reference to prevent key mutation by callers

### Fixed

- **`QueryStringSanitizer.DetectAdTrafficParams`** -`utmPresent` check now includes `utm_medium` (previously utm_medium-only traffic was silently treated as organic)
- **`ClickFraudContributor`** -`isPaidTraffic` now `utmPresent || hasClickId` (previously required a click ID even when UTM-only present)
- Removed dead code: `UtmKeys`, `ClickIdKeys` static HashSets and `clickIdKey` variable (declared but never used)

### Accessibility

- **Full dark mode contrast pass** on dashboard: all low-contrast text updated to WCAG AA/AAA targets
  - `--sb-brand-muted` dark: `#6b7280` (4:1) → `#8b9eb8` (7.1:1)
  - `--sb-text-faint` dark: `#64748b` → `#8b9eb8`
  - Tailwind opacity floor overrides for `[data-theme="dark"]`: `/20`→45%, `/30`→52%, `/40`→60%, `/50`→68%
  - `bot-detection-details.css`: comprehensive `[data-theme="dark"]` block for `<bot-detection-details>` component
  - Detection ticker/bar: `#555` → `#8b9eb8`, `#888` → `#a0b2c8`
- **`sb-components.css`** was not imported anywhere -added `@import "./sb-components.css"` to `tailwind-input.css`

---

## [6.0.1-beta1] - 2026-04-23

### Added

#### Content Sequence Detection
- **`ContentSequenceContributor`** (Priority 4, Wave 0) -tracks each fingerprint's position in its page-load request sequence and writes `sequence.*` signals consumed by deferred detectors
  - Document requests (Sec-Fetch-Mode: navigate, Accept: text/html, or `transport.protocol_class=document`) reset the sequence at position 0
  - Continuation requests advance position and perform set-based phase-window divergence scoring across four phases: critical (0-500ms), mid (500ms-2s), late (2s-30s), settled (30s+)
  - Prefetch requests (Purpose/Sec-Purpose: prefetch) are tracked but excluded from divergence scoring
  - Fingerprints with no prior document request write no signals; deferred detectors fall back via SignalNotExistsTrigger
  - All thresholds configurable via `contentsequence.detector.yaml` / appsettings.json
- **`SequenceContextStore`** -per-fingerprint sequence state (ConcurrentDictionary, 30-min session gap, 5-min TTL sweep); loss on restart is acceptable
  - `SequenceContext` record: position, expected chain, observed state set (ImmutableHashSet), window timing, divergence count, cache-warm flag, content path
- **`CentroidSequenceStore`** -SQLite-backed expected request chains per cluster (Tier 2) with global fallback chain (Tier 1); rebuilt after each clustering run
  - `MarkEndpointStale` / `IsEndpointStale` / `ClearEndpointStale` -staleness window (1h) suppresses divergence scoring during content changes
- **`EndpointDivergenceTracker`** -rolling 1-hour per-path divergence rate tracking; marks centroid stale when ≥40% of sessions in window diverge (minimum 10 sessions); thread-safe via `ConcurrentDictionary.AddOrUpdate`
- **`AssetHashStore`** -ETag-first / Last-Modified+Content-Length fallback fingerprinting for static assets; SQLite-backed `asset_hashes` table; 24h in-memory change index with hourly eviction sweep
- **`AssetHashMiddleware`** -response-side middleware registered before detection; reads ETag/Last-Modified after `_next` returns and calls `AssetHashStore.RecordHashAsync` for static extensions (css, js, woff, woff2, png, jpg, svg, ico, and 6 others)
- **`CentroidSequenceRebuildHostedService`** -wires `BotClusterService.ClustersUpdated` → `CentroidSequenceStore.RebuildAsync`; initialises SQLite table on startup; errors from async rebuild are logged (not silently swallowed)
- **`AssetHashInitHostedService`** -creates `asset_hashes` table and loads recent change timestamps on startup
- **`SequenceGuardTrigger.Default`** -shared `AnyOfTrigger` extracted from 5 deferred detectors; run when: no sequence active, on_track=false, diverged=true, or position ≥ 3
- **3 new trigger types** in `IContributingDetector`:
  - `SignalNotExistsTrigger` -inverse of SignalExistsTrigger
  - `SignalValueTrigger<T>` -equality check on signal value
  - `SignalPredicateTrigger<T>` -predicate check on signal value
- **10 new signal keys** (`sequence.position`, `sequence.on_track`, `sequence.diverged`, `sequence.divergence_score`, `sequence.chain_id`, `sequence.centroid_type`, `sequence.content_path`, `sequence.signalr_expected`, `sequence.prefetch_detected`, `sequence.cache_warm`)
- **2 new signal keys** for centroid freshness: `sequence.centroid_stale`, `asset.content_changed`
- **4 BDF scenarios** -`sequence-human-browser`, `sequence-machine-speed-bot`, `sequence-api-only-bot`, `sequence-cache-warm`
- **`scripts/soak/run-sequence-bdf.sh`** -replays content-sequence scenarios against the running test site and reports per-request bot probability

#### Test Site Cleanup
- Removed 8 outdated Razor pages and static HTML files (BotTest, ComponentDemo, TagHelperDemo pages; proxy.html, test-client-side.html)
- `index.html` replaced with a minimal landing page clarifying this is a test/API-simulator site

### Changed

- **5 deferred detectors** (SessionVector, Periodicity, BehavioralWaveform, ResourceWaterfall, CacheBehavior) now use `SequenceGuardTrigger.Default` -skip early on-track sequences to avoid false positives before enough request data exists
- **`StreamAbuseContributor`** skips when `sequence.signalr_expected` is present, preventing false-positive flagging of expected SignalR upgrades on human-centroid chains
- **`ContentSequenceContributor.ComputeDivergenceScore`** -machine-speed threshold, score components, and request-count threshold moved from hardcoded values to YAML params (`machine_speed_threshold_ms`, `machine_speed_score`, `unexpected_state_score`, `high_request_count_score`, `high_request_count_threshold`)
- **`SequenceContext.ObservedStateSet`** changed from `HashSet<RequestState>` (mutable) to `ImmutableHashSet<RequestState>`

### Fixed

- **`SequenceContext.ContentPath`** -continuation requests now read the content path from `ctx.ContentPath` (populated during document request) instead of the per-request blackboard, which was always empty on non-document requests; divergence tracking and centroid staleness marking now function correctly
- **`CentroidSequenceRebuildHostedService`** -rebuild exceptions are now caught and logged via `ILogger.LogError` instead of silently swallowed in fire-and-forget

---

## [6.0.0-beta1] - 2026-04-22

### Added

#### Public API & SDK Ecosystem
- **`Mostlylucid.BotDetection.Api`** - Canonical REST API at `/api/v1/*` for all SDK clients
  - `POST /api/v1/detect` and `/detect/batch` - detection-as-a-service via synthetic HttpContext bridge
  - 10 read endpoints: detections, sessions, signatures, summary, timeseries, countries, endpoints, topbots, threats, me
  - Three auth tiers: proxy headers (zero-latency), API key (`X-SB-Api-Key`), OIDC bearer (commercial)
  - OpenAPI spec at `/api/v1/openapi.json`
- **`@stylobot/core`** (npm) - Zero-dep TypeScript types, `StyloBotClient`, header parser. Works in Node/Deno/Bun
- **`@stylobot/node`** (npm) - Express middleware (`styloBotMiddleware`), Fastify plugin (`styloBotPlugin`)
  - Two modes: `headers` (behind Gateway, zero-latency) or `api` (sidecar, calls detect endpoint)
- **Response header injection** - `X-StyloBot-IsBot`, `X-StyloBot-Probability`, `X-StyloBot-Confidence`, etc. (11 headers)

#### Holodeck Rearchitecture
- **`HoneypotPathTagger`** - Pre-detection middleware tags honeypot paths before any detector runs; fixes holodeck bypass caused by FastPathReputation early exit
- **`HolodeckCoordinator`** - Ephemeral keyed sequential slots: one holodeck engagement per fingerprint, global capacity cap (default 10)
- **`BeaconCanaryGenerator`** - HMAC-SHA256 deterministic canary generation per fingerprint+path
- **`BeaconStore`** - SQLite canary-to-fingerprint persistence for rotation tracking
- **`BeaconContributor`** - Priority 2 detector scans requests for canary values, writes `beacon.matched` + `beacon.original_fingerprint` signals for entity resolution
- **Signal-driven holodeck transitions** - `HoneypotTriggered`, `attack.detected`, `cve.probe.detected` signals trigger holodeck instead of score bands

#### LLM Holodeck Plugin
- **`Mostlylucid.BotDetection.Llm.Holodeck`** - In-process fake response generation using system's existing `ILlmProvider`
  - Replaces external MockLLMApi HTTP proxy with direct `ILlmProvider.CompleteAsync()` calls
  - `HolodeckPromptBuilder` - builds prompts from `ResponseHints` + canary embedding instructions
  - `HolodeckResponseCache` - per-fingerprint+path cache with TTL, avoids redundant LLM calls
  - Capability-aware: nodes without LLM serve static templates automatically
- **Core interfaces** - `IHolodeckResponder`, `ICanaryGenerator`, `IBeaconStore` defined in core for clean dependency boundaries
- **`SimulationPackResponder`** enhanced - dynamic LLM generation for `Dynamic = true` templates, static fallback with `{{nonce}}`/`{{api_key}}`/`{{token}}` canary placeholders

#### YAML-Driven Benchmark Harness
- **26 benchmark scenarios** in `Scenarios/*.benchmark.yaml` - define detector, request, signals, thresholds per file
- **`DetectorBenchmarkRunner`** - generic BenchmarkDotNet class, one benchmark per YAML via `[ParamsSource]`
- **`PipelineBenchmarkRunner`** - full orchestrator benchmarks
- **`RegressionChecker`** - post-run threshold validation for CI (`--regression` flag)
- CLI: `--filter`, `--list-scenarios`, `--regression`

### Changed

- **Detector tuning** - 3 KB/request saved across top 3 allocators:
  - IntentContributor: 6,104B to 5,448B (-11%) - pre-sized dict, span counting, OrdinalIgnoreCase
  - HeuristicFeatureExtractor: 3,472B to 2,488B (-28%) - eliminated ToLowerInvariant, pre-sized dict
  - BehavioralDetector: 2,688B to 2,112B (-21%) - stackalloc timing, span IP parsing, LINQ removal
- **`PromptPersonality`** added to `SimulationPack` model for LLM-driven pack personality

### Removed

- **3 dead projects** deleted: `Mostlylucid.GeoDetection.Demo` (79 days stale), `Mostlylucid.BotDetection.SignatureStore` (orphaned), `Mostlylucid.BotDetection.MinimalDemo` (documentation artifact)
- **`InMemoryDashboardEventStore`** (~565 lines) - replaced by SQLite/PostgreSQL stores
- **`InMemorySignatureLabelStore`** (~69 lines) - replaced by SQLite store
- **`SignatureTransitionEvent`** model - zero references
- **6 deprecated `BotDetectionOptions` properties** - `OllamaEndpoint`, `OllamaModel`, `LlmTimeoutMs`, `MaxConcurrentLlmRequests`, `UpdateIntervalHours`, `UpdateCheckIntervalMinutes`
- **`WaveformSignature`** constant - all code migrated to `PrimarySignature`
- **All `#pragma warning disable CS0618` blocks** in BotListUpdateService

### Fixed

- **API auth policy registration** - `RequireAuthorization("StyloBotApiKey")` was missing authorization policy, causing 500 on all `/api/v1/*` endpoints
- **Flaky cache eviction test** - `HolodeckResponseCache` used timestamp ordering (same-millisecond entries picked arbitrarily); replaced with monotonic counter

---

## [6.0.0-alpha] - 2026-04-17

### Added

#### Commercial Plugin Architecture
- **IConfigurationOverrideSource** - FOSS extension interface for commercial per-target config overrides (per-endpoint, per-user, per-API-key detector tuning)
- **IFleetReporter** - FOSS extension interface for commercial fleet telemetry reporting across multi-gateway deployments
- **IDetectionEventPublisher** - extension point for out-of-process dashboard UIs
- **FileSystemConfigurationOverrideSource** - FOSS hot-reload implementation for YAML-file-based config changes without restart
- **Signature labeling infrastructure** - groundwork for the upcoming detector weighting pass

#### Customer Portal (stylobot.net)
- **Keycloak OIDC integration** - portal auth scaffold with organization management
- **LicenseIssuer** - Ed25519-signed JWT license issuance with trial request, download, rotate, and revoke
- **Domain-based license entitlement** - DomainEntitlementValidator + cloud-pool host list; signed JWTs include `domains[]` claim
- **Team invites + audit log** - org member management with full audit trail UI
- **Personal API tokens** - `/api/v1/orgs/{slug}/licenses/current` for programmatic license access
- **BurstWorkUnitsPerMinute** mapping to StyloFlow licensing payload

#### Pipeline Coordination (spec)
- **Distributed-blackboard model** - chained YARP instances (edge - regional - app-side) avoid redundant detector execution via input-hash-per-detector deduplication
- **Layered action policies** - monotone-escalating policy cascade: `block` at an inner hop cannot be softened by an outer hop's `allow`

#### Dashboard Enhancements
- **Monaco YAML config editor** - in-dashboard configuration viewer (read-only in FOSS, live-edit in commercial)
- **FOSS licensing v1 wiring** - license status display in dashboard
- **World threat map** - jsVectorMap with countries colored by bot rate (green-amber-red gradient), 30s auto-refresh
- **Traffic-over-time chart** - ApexCharts area chart with Human/Bot series, 15s auto-refresh
- **Sessions in signature detail** - HTMX-loaded session timeline with Markov chain transition previews, path sequences, timing entropy
- **Behavioral shape radar chart** - 129-dim session vector projected into 8 interpretable radar axes with session stepping (prev/next)
- **Dashboard overview redesign** - Top Threats above fold, actionable intelligence first

#### Hardened Proof-of-Work Challenge
- **SHA-256 micro-puzzles** - Web Worker pool (up to `navigator.hardwareConcurrency`) solves puzzles in parallel
- **Blackboard-driven difficulty** - puzzle count and zeros scale with session velocity, cluster membership, reputation bias, threat score
- **Transport-aware** - API/SignalR/gRPC clients get 429 + JSON challenge, not HTML
- **Challenge-as-signal feedback loop** - ChallengeVerificationContributor reads solve metadata, emits human/bot signals based on timing characteristics
- **SqliteChallengeStore** - persistent challenge store (was in-memory)

#### Fingerprint Approval System
- **IFingerprintApprovalStore** - SQLite-backed approval with locked dimensions and audit trail
- **Locked dimensions** - behavioral contract: country, UA, IP CIDR constraints checked against live signals on every request
- **FingerprintApprovalContributor** - strong human signal (-0.4 delta) when approved with matching dimensions, strong bot signal (+0.3) on dimension mismatch (catches stolen credentials)
- **X-SB-Approval-Id header** - one-time approval token for borderline requests (opt-in)
- **Dashboard approval API** - full CRUD + token-based approval flow

#### Per-Transition Timing
- **3 new session vector dimensions** (126-128): impossible timing ratio, timing consistency score, fastest transition z-score
- Session vector now 129 dimensions (was 126/118)
- `CosineSimilarity` handles dimension mismatch via zero-padding for migration

#### Bot Naming
- **DeterministicBotNameSynthesizer** - generates names from signals without LLM: "Rapid Scraper", "Headless Python Bot", "Targeted Scanner"
- Replaces NoOpBotNameSynthesizer as default; LLM packages override via TryAddSingleton

#### Response Headers (opt-in)
- **X-SB-Reason** - top contributing detector reason (PII-free, 200 char max)
- **X-SB-Approval-Id** - one-time fingerprint approval token for borderline requests

### Changed

- Site repositioned as security product (not a tech demo)
- Detector-weights audit and benchmark artifact cleanup
- Bumped all dependencies to latest, cleared Dependabot alerts
- Pricing: $100/mo per domain (unlimited requests, no per-request metering)
- **BoundedCache** replaces raw ConcurrentDictionary across 6 lookup services (ASN, Honeypot, RDNS, CIDR, VerifiedBot DNS)
- Read-through caches on FingerprintApprovalStore and ChallengeStore (eliminates 50-500us/req SQLite hits)
- AccountTakeoverContributor eviction: O(N^2) -> O(N log N)
- GeoChangeContributor: two-phase pruning (expire + LRU)
- MarkovTracker: MaxTrackedSignatures with eviction
- SignatureCoordinator: shadow index pruning
- DriftDetectionHandler: bounded at 10K patterns/50 samples per pattern

### Fixed

- Five bugs found running the Phase 1 portal end-to-end
- Normalized em-dash characters to hyphens for consistent documentation style
- Synced reputation/decay tests to post-oscillation-fix behavior
- **IPv4-mapped IPv6 subnet classification** - `::ffff:x.x.x.x` addresses were grouped into `::ffff::/48` subnet, causing ALL IPv4-mapped addresses to share reputation. Fixed to extract IPv4 and use /24

### Security

- **CRITICAL-1**: HMAC token secret auto-generates cryptographically random secret (no guessable fallback)
- **CRITICAL-2**: returnUrl open redirect fixed (rejects absolute URLs, protocol-relative, scheme injection)
- **CRITICAL-3**: Token secret propagation fixed (EffectiveTokenSecret across requests)
- **HIGH-1**: TrainingEndpoints RequireApiKey defaults to true
- **HIGH-2**: Policy mutation endpoints require authorization
- **HIGH-3**: Dashboard defaults to deny when no auth configured (AllowUnauthenticatedAccess flag)
- **HIGH-4**: X-SB-Labeler header only honored when authenticated
- **MEDIUM-1**: PoW solution SeedIndex validated server-side
- **MEDIUM-2**: BDF replay header injection blocked (X-SB-*, X-Bot-*, Host, X-Forwarded-For)
- **MEDIUM-3**: Rate limiter dictionaries bounded at 10K entries
- **MEDIUM-4**: Raw HMAC token removed from verify JSON response

---

## [5.5.0] - 2026-03-15

### Added

#### Session Vector Architecture
- **SessionVectorizer** - per-request Markov chain transitions compressed into 118-dimensional normalized vectors (100 transition probabilities + 10 stationary distribution + 8 temporal features + 8 fingerprint features)
- **Retrogressive session boundary detection** - sessions defined by inter-request gaps (default 30min), detected when the NEXT request reveals the gap
- **Inter-session velocity analysis** - L2 magnitude of delta vectors between consecutive sessions detects sudden behavioral shifts (bot rotation, account takeover)
- **Snapshot compaction** - old session snapshots merge into maturity-weighted root vector, preserving behavioral baseline while discarding per-session detail
- **Unified fingerprint dimensions** - TLS/TCP/H2 fingerprints are vector dimensions in the same space as behavioral features; fingerprint mutation across sessions appears as velocity

#### SQLite Persistence
- **SqliteSessionStore** - zero-dependency session persistence (sessions, signatures, 1-minute counter buckets)
- **SessionPersistenceService** - background service bridging in-memory SessionStore events to SQLite
- ~100x compression vs per-request storage (200 sessions/day vs 10,000 requests/day)

#### Transport-Aware Detection
- **TransportProtocolContributor** (Priority 5) - classifies request transport context: document, API, SignalR, gRPC, static, WebSocket, SSE
- Seven existing detectors now consume transport context to suppress false positives on non-document traffic: HeuristicFeatureExtractor, InconsistencyDetector, MultiLayerCorrelation, ResponseBehavior, AdvancedBehavioral, Header, CacheBehavior

#### Oscillation Prevention
- **NonAiMaxProbability** (default 0.90) - configurable probability ceiling when AI hasn't run
- **State-aware reputation decay** - ConfirmedBad uses longer decay tau (12h vs 3h) and wider demotion hysteresis (0.5 vs 0.9) to prevent block/allow flapping
- **Browser attestation downgrade** - configurable via YAML (`browser_attestation_max_confidence`, `browser_attestation_weight`)

#### Dashboard
- **Sessions tab** - timeline with Markov chain previews, HTMX drill-in to session detail
- **Session detail view** - behavioral radar chart (ApexCharts), transition bar visualization, paths visited
- **Fail2ban-style escalating action policies** for persistent 404 abuse patterns

### Changed

- Dashboard detector count increased to 31 (SessionVector added to Wave 1)
- Session vector benchmarks added to benchmark suite

### Fixed

- ProcessingTimeMs nullable handling in PostgreSQL event store
- Nullable double coalescing in PostgreSQL event store

---

## [5.0.0] - 2026-02-22

### Added

#### Intent Classification and Threat Scoring
- **IntentContributor** - new Wave 3 detector that classifies request intent (reconnaissance, exploitation, scraping, benign, etc.) using HNSW-backed similarity search and cosine vectorization
- **Threat scoring orthogonal to bot probability** - a human probing `.env` files has low bot probability but high threat score; both dimensions are now independently surfaced
- **ThreatBand enum** - `None`, `Low`, `Elevated`, `High`, `Critical` with configurable score thresholds (0.15 / 0.35 / 0.55 / 0.80)
- **IntentClassificationCoordinator** - orchestrates intent vectorization, similarity search, and threat band assignment
- **HnswIntentSearch** - HNSW approximate nearest-neighbor index for real-time intent matching with configurable M/efConstruction/efSearch parameters
- **IntentVectorizer** - converts request features (path patterns, method, headers) into dense vectors for similarity search
- **IntentLearningHandler** - feeds confirmed intent classifications back into the HNSW index for adaptive improvement
- Intent signals: `intent.category`, `intent.threat_score`, `intent.threat_band`, `intent.confidence`, `intent.similarity_score`, `intent.nearest_label`

#### Dashboard Threat Visualization
- Threat badges on detection detail, "your detection" panel, visitor list rows, and cluster cards
- Cluster enrichment: `DominantIntent` (most common intent) and `AverageThreatScore` per cluster
- Narrative enhancement: threat qualifier prefix on bot narratives (`CRITICAL THREAT:`, `High-threat`, `Elevated-threat`)
- `intent.*` signals in dedicated "Intent / Threat" signal category with target icon
- `threatBandClass()` helper for DaisyUI badge coloring by threat band
- Threat data in all API endpoints: `/api/detections`, `/api/signatures`, `/api/topbots`, `/api/clusters`, `/api/me`, `/api/diagnostics`
- Threat data in CSV export
- Threat data in SignalR real-time broadcasts

#### Stream and Transport Detection
- **StreamAbuseContributor** - new Wave 1 detector that catches attackers hiding behind streaming traffic using per-signature sliding window tracking
- Stream abuse patterns: connection churn, payload flooding, protocol switching, rapid reconnection
- `stream-abuse.detector.yaml` manifest with configurable thresholds for all abuse patterns
- Enhanced **TransportProtocolContributor** - improved WebSocket, SSE, SignalR, gRPC, and GraphQL classification with `transport.is_streaming` signal for downstream consumption
- Five existing detectors now consume `transport.is_streaming` to suppress false positives on legitimate streaming traffic (CacheBehavior, BehavioralWaveform, AdvancedBehavioral, ResponseBehavior, MultiFactorSignature)
- Documentation: [`stream-transport-detection.md`](Mostlylucid.BotDetection/docs/stream-transport-detection.md)

#### Detection Accuracy Improvements
- Enhanced **BehavioralWaveformContributor** - stream-aware burst thresholds, excludes streaming requests from page rate calculations
- Enhanced **CacheBehaviorContributor** - skips cache validation for streaming requests entirely
- Enhanced **AdvancedBehavioralContributor** - skips path entropy, navigation pattern, and burst analysis for streaming
- Enhanced **ResponseBehaviorContributor** - new signals for response analysis
- Updated response behavior, transport protocol, and stream abuse detector YAML manifests
- **PolicyEvaluator** improvements - threat-aware policy evaluation
- **DetectionPolicy** updates - new policy fields for threat-based responses

#### Infrastructure
- New `HttpContext` extension methods for intent/threat access
- `BotCluster` enrichment with `DominantIntent` and `AverageThreatScore`
- `BotClusterService` computes cluster-level intent and threat aggregates
- `ILearningEventBus` extensions for intent learning feedback
- `DetectionLedgerExtensions` - threat band computation from aggregated evidence
- `DetectionContribution` - `ThreatBand` enum and threat fields on `AggregatedEvidence`
- Updated `BotDetectionOptions` with intent detection configuration
- Updated `ServiceCollectionExtensions` with intent detector registration

### Changed

- Dashboard now shows 30 detectors (was 29) - IntentContributor added to Wave 3
- Default `EnabledDetectorCount` increased to 30
- Cluster visualization includes threat percentage and dominant intent
- Bot narratives include threat qualifier prefix for elevated+ threats
- Diagnostics endpoint now includes `ThreatScore`/`ThreatBand` on detections, signatures, and top bots

### Fixed

- Missing `threatBandClass` function in inline Razor dashboard script (NuGet package users would have gotten a JS ReferenceError)
- Missing `Critical` threshold (>= 0.80) in cluster threat badge ternary
- Visitor row threat badge missing DaisyUI `badge` class (visual rendering was inconsistent)
- Removed dead `threatBandColor` function from dashboard.ts

### Documentation

- [`dashboard-threat-scoring.md`](Mostlylucid.BotDetection/docs/dashboard-threat-scoring.md) - full architecture, data flow, API endpoints, UI elements, security considerations
- [`stream-transport-detection.md`](Mostlylucid.BotDetection/docs/stream-transport-detection.md) - stream-aware detection architecture, transport classification, abuse patterns
- [`transport-protocol-detection.md`](Mostlylucid.BotDetection/docs/transport-protocol-detection.md) - updated with streaming classification
- Updated `SESSION_SUMMARY.md` with v5 section

---

## [4.0.0] - 2026-01-25

### Added

- Programmatic request attestation via `Sec-Fetch-Site` headers
- YARP API key passthrough for upstream services
- BDF (Bot Detection Format) export/replay system
- Standardized signal key usage across all contributors

## [3.0.0] - 2025-12-15

### Added

- Real-time dashboard with interactive world map
- Country analytics and reputation tracking
- Cluster visualization (Leiden algorithm)
- User agent breakdown with category badges
- Live signature feed with risk bands and sparklines
- SignalR-based live updates
- Server-side rendering for initial dashboard load

## [2.0.0] - 2025-10-01

### Added

- Wave-based detection pipeline (4 waves)
- Protocol-level fingerprinting (JA3/JA4, p0f, AKAMAI, QUIC)
- Heuristic AI model with ~50 features per request
- Action policies (block, throttle, challenge, redirect, logonly)
- Training data API for ML export
- PostgreSQL/TimescaleDB persistence layer

## [1.0.0] - 2025-07-01

### Added

- Initial release with 20 detectors
- Blackboard architecture via StyloFlow
- Zero-PII design with HMAC-SHA256 signatures
- YARP reverse proxy integration
- Basic dashboard