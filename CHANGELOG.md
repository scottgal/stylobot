# Changelog

All notable changes to StyloBot are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [8.2.2] - 2026-07-22

### Fixed

- **Traffic `/summary` counter frozen across the window switcher on remote/thin-viewer hosts.** The gateway's `GET /api/v1/summary` served the warmed `DashboardAggregateCache` snapshot — a single fixed default-window materialisation — for *every* request, ignoring `since`/`until`, so the Traffic counter was byte-identical at 6h/24h/7d/30d (the sibling `/countries`, `/visitor-segments`, and `/domain-stats` reads all windowed correctly; only summary didn't). `HandleSummary` now binds `since`/`until`/`audience` and hits the store windowed when any is present, serving the cache only for the unqualified default view; and the dashboard middleware's summary fetches (KPI strip, `/summary` JSON, and the Traffic page-bundle fallback) now pass the resolved page window to `GetSummaryAsync` instead of reading the audience- and window-agnostic cache snapshot. The underlying `SqliteDashboardEventStore.GetSummaryAsync` already honoured the window; only the gateway handler and the callers dropped it. With this, the Traffic counter reconciles with the per-domain aggregate at the same window.

## [8.2.1] - 2026-07-22

### Added

- **`GET /api/v1/domain-stats`** — raw per-host domain rows (`Domain`, `Requests`, `Bots`, `IsInternal`) for **all** observed domains, one row each, ordered by request count descending and capped at `limit` (default 200). A thin/remote dashboard now gets true per-domain splits from the gateway rather than a domains filter the REST reads ignored. `IDashboardEventStore.GetDomainStatsAsync` is a default-empty interface method (SQLite and PostgreSQL override it; `RemoteDashboardEventStore` forwards to the endpoint, no local re-tally). Bot counts use the shared `Classification.BotFloor` so they reconcile with the Traffic counter, and internal self-traffic is **flagged, not excluded** — the licensed-vs-pool classification stays in the commercial overlay.

## [8.2.0] - 2026-07-22

8.2 is the **dashboard V2** release. The real-time dashboard is consolidated into a single FOSS RCL that the marketing site dogfoods (no commercial fork); the Visitors surface is rebuilt around an authoritative single-source segment aggregate; live SignalR delta updates are driven off the materialiser tick; and a set of empty FOSS extension slots let the commercial UI layer in license-gated. Alongside the dashboard work: operational-endpoint hardening, a NativeAOT hot-path fix, and a security-dependency sweep.

### Fixed

- **Visitors "all-zero" page.** The Visitors surface could render every counter, list, and shape at zero. The root cause was view composition, not data: an unguarded store-fetch at any of several call sites in `ServeDashboardPageAsync` / `SbVisitorListViewComponent` aborted the *entire* page render (hardcoded shapes included). Each fetch now degrades independently (the `Safe*` pattern already used for summary/countries/endpoints); the headline VISITORS / HUMAN / BOT counters read the authoritative visitor-segment aggregate rather than the capped row projection; the "req" line shows real request volume; the By-Country map init script was freed from a dead `@section` (nested partials silently drop `@section`); and the Signature-Patterns card gained a compact shape-only mode. Visitors also renders through the canonical `Visitors/Index.cshtml` on middleware-enabled hosts instead of the legacy list-only partial.
- **List verdict reflects RISK, not the always-`None` threat.** The visitor and Top-Bot list shields now show the risk band rather than a threat score that is `None` for most rows; a confirmed verification writes a durable `claim_status='verified'` so known good bots read `VeryHigh` instead of decaying to `Unknown`; Top Bots counts post-collapse so its badges match Visitors.
- **Mounted-host dashboard links.** Widget dashboard/signature links resolve from `ViewData["BasePath"]` (were hardcoded `/_stylobot`, 404ing on mounted hosts), and the breadcrumb "Dashboard" navigates to the dashboard rather than `history.back()`.
- **Live updates + build.** SignalR live-update broadcasts are restored from the materialiser tick and `DashboardMaterializerOptions` is appsettings-bindable; the gateway applies API-key context before detection; and a Console build break (a missing `MapPersistenceStatsEndpoints` `using` left by the operational-endpoint hardening) is fixed.
- **Verifier "fail-trips-bot" class — a failed or missing verification is no longer a spoof.** A verdict could be flipped to *spoofed*/bot by a verification that *couldn't run* (no reference loaded, or a transient failure) rather than a genuine refutation — "verifying against none." Three instances guarded: `VerifiedBotAtom` no longer brands `VerificationMethod = "none"` (no published IP ranges loaded **and** no rDNS channel) as `Spoofed-*`; `VerifyFcrDnsAsync` returns a tri-state so a transient DNS `SocketException`/timeout is treated as "couldn't verify" (retried, un-cached) instead of a false spoof, while a deterministic no-PTR/mismatch stays a refutation; and `FediverseDomainAtom` treats a failed NodeInfo lookup as unverified (neutral) rather than a `+0.3` "likely spoofed" push. A missing expected signal impacts *confidence*, it never asserts a spoof. A codebase-wide hunt for the class found no other instances.
- **Parasitic verdict store — Top Bots and the signature detail no longer diverge.** `SignatureAggregateCache` kept its own per-row copy of the verdict scalars, so any out-of-band update (AI opinion / drift) left Top Bots stale — the same signature could read `0.9 / VeryHigh / Unknown` on Top Bots but `0% / VeryLow / Human` on the detail page. The aggregate now reads the verdict *through* the fingerprint LFU at projection (the single source), exactly as it already did for the display name; `UpdateCachedVerdictAsync` is LFU-first (no DB-write-then-evict); and the catalogue bot-type is cached on the fingerprint so dashboard Internal-exclusion and the AI/search/tools filters resolve through that same source.
- **SignalR hub path.** The marketing live-preview SignalR negotiate 404'd because three FOSS hub-path defaults (`/dashboard/hub`, `/_stylobot/hub`) disagreed with the configured `HubPath`; all now default to the mapped `/stylobot/hub`, and a `STYLOBOT_HUB_PATH` override still wins.

### Added

- **One dogfooded FOSS dashboard.** The dashboard is a single FOSS RCL (`Mostlylucid.BotDetection.UI`) that the marketing site consumes directly rather than a commercial fork. `SignalRBeaconRelay` moved into FOSS and registers conditionally for remote-viewer mode; empty FOSS extension slots (`location-tree`, display variants — map / triangle / by-source, config-selectable visualization) let the commercial UI fill them license-gated; and an empty `IFingerprintNameActionSlot` seam is wired at all five name-render sites for the commercial per-fingerprint action controls.
- **Visitors overview + segment aggregate.** The Visitors page is rebuilt into a full overview from repurposed components, backed by a new authoritative visitor-segment aggregate (`GetVisitorSegmentCountsAsync`) exposed as `GET /api/v1/visitor-segments` with a remote-viewer implementation, so counts are single-sourced across local and remote hosts.
- **V2 sidebar dynamic pack nav.** The V2 sidebar renders every registered `IDashboardPack` from the registration (was a hardcoded two-entry array that silently excluded Settings, Domains, and any other pack); sub-row labels render from the registration, so a pack rename surfaces with zero FOSS edit.
- **Gateway YARP transform seam.** An optional YARP transform extension point for downstream request/response shaping.
- **RegistryClient archetype (increment 1).** Container-registry / OCI Distribution Spec clients (docker, containerd, podman, skopeo, buildkit, oras, Helm) are recognized by UA family *corroborated* by the registry v2 protocol (a `/v2/` path shape or a registry manifest `Accept` media type) and scored low-threat, so a customer's `docker pull` through the gateway is not tarpitted. The fix is in **detection** — a strong negative-delta contribution, never an early-exit or `/v2/*` allow bypass — so a spoofed `docker/*` UA that is not doing the protocol is still scored normally.

### Changed

- Demo mode moved out of FOSS; demo edit affordances and showcase controls are read-only in FOSS. `CachedVisitor` renamed to `ProjectedVisitor`.

### Security

- **Operational endpoints protected.** `/admin/persistence-stats` and the metrics snapshot endpoint are gated behind the same API-key / token policy rather than reachable unauthenticated, and dashboard-hub authorization parity is enforced (the hub no longer accepts connections the page auth would reject).
- **npm security sweep.** Patched all open advisories in the Node surfaces — `body-parser`, `@grpc/grpc-js`, `fast-uri`, and `protobufjs` — across `sdk/node` and the node-sidecar test app (`npm audit` clean).

### Performance

- **NativeAOT CountryCode accessor.** The compiled-delegate `CountryCode` accessor (`Expression.Compile`) is ~20× slower under NativeAOT than reflection because ILC falls back to the expression interpreter; it now uses a cached `PropertyInfo` under AOT, guarded by `RuntimeFeature.IsDynamicCodeSupported` so the JIT path is unchanged.

### Build

- `Mostlylucid.BotDetection.Api` directly references `Mostlylucid.BotDetection.OpenApi` and `Mostlylucid.Ephemeral.Atoms.Taxonomy` (the types it re-exports), fixing a `CS0012` for any project — notably the commercial test suite — that references `Api` directly and relied on transitive visibility that did not flow. `NU1605` resolved by promoting `mostlylucid.ephemeral` to a direct reference; `SignalR.Client` added as a FOSS-RCL dependency for the relay.

### CI / packaging

- **winget and Chocolatey now auto-publish.** Both moved from the `release:` trigger (which never fired for `GITHUB_TOKEN`-created releases) to `workflow_run` after the binaries build, so a tagged release publishes without a manual dispatch.
- **Self-serve SQLite-vs-Postgres soak runner** under `scripts/soak/` wraps the k6 plateau so the persistence-backend head-to-head can be run on demand.

## [8.1.0] - 2026-07-13

8.1 is the atom-orchestrator consolidation and durability-hardening release. Where v8 unified the classifier's *voice*, 8.1 rebuilds the *engine*: the detectors are now first-class `IDetectorAtom`s driven by a single stateless orchestrator (no more per-request re-wiring), the session and identity layers are bounded at the write so high-cardinality floods can no longer balloon memory, and every durable store gained a coalesced write-behind drain plus a commercial PostgreSQL sibling. New detection surfaces (Web Bot Auth, health-endpoint awareness, UA-rotation correlation) land on that foundation.

### Headline

- **Atom orchestrator (default-on).** The detector pipeline is now 64 native `IDetectorAtom` implementations under `Orchestration/Atoms/`, run by a stateless singleton orchestrator hoisted out of the per-request path (previously the full atom set was re-wired on every request). The old `IContributingDetector` model and hand-emitted `<name>.ran` signals are gone. The sub-ms fast path holds end to end (~100 µs/request).
- **Bounded, signals-native session + identity layers.** Session aggregation is decoupled from volatile caches and driven by signal-triggered two-phase eviction; the metastable-identity observation cloud is bounded *at the write* via adaptive-forgetting sampling (confirmatory observations are summarised, novel ones kept). Fixes unbounded memory growth under sustained high-cardinality traffic.
- **Data-guardian framework.** Discrete, single-purpose guardians on the shipped `IGuardian` / `GuardianService` framework bound each durable store: bucket retention, session and HNSW compaction, significance-weighted centroid retention (not blind-age TTL), and identity fingerprint observation-retention + eviction. The FOSS dashboard roster shows every guardian's last run.
- **Write-behind sample drain + store uniformity.** `WriteBehindLfuStore` gained a behavioural-sample drain (coalesce-by-key, DecisionNecessity coldness ordering), so a hot shape persists once per cycle instead of once per mutation. Every durable store now has a SQLite (FOSS) implementation with a PostgreSQL commercial sibling.
- **Web Bot Auth.** `WebBotAuthApprovalAtom` verifies RFC 9421 HTTP Message Signatures once per session window against a public-key registry; the dashboard surfaces the registered issuer keys.
- **Health-endpoint awareness.** Health and liveness probes are shape- and source-classified as `BotType.Internal` (never throttled, excluded from threat tallies); a source-aware endpoint policy stops real probes returning 429, and an active upstream-probe lane tracks upstream health.
- **New detection surfaces.** `HeaderCorrelation` (UA rotation via same-headers-different-signature), `Intent` (unified 0-1 threat score orthogonal to bot probability), and the foundation compute/match atoms (`RequestHydrator`, `IdentityVector`, `Time`, `FingerprintMatch`, `FingerprintPrior`) are now first-class.
- **Dashboard batch materialisation + delta updates.** A tick-driven materialiser warms a content cache out-of-request; the gateway composes multiple datasets in one round-trip; partial widget refreshes stream as deltas.
- **Operator-correction verdict seam.** An operator ground-truth label (Not-Bot / Bot + bot-type) on a fingerprint biases the verdict toward that label as a high-confidence prior in `SignatureRiskVerdictComposer` — never a decision-path override: the behaviour pins (confirmed-bad, hostile threat) still run on the biased probability, so a human-labelled fingerprint that later attacks is still caught. No bypass.
- **FOSS carries no licensing.** All licensing knowledge removed from FOSS and the capability verifier is generic. `BotDetectionPack` renamed to `BotDetectionOrchestrator`. Ephemeral atom packages to 2.9.1; Tailwind v4.

### Fixes

- **Boot + startup.** Fixed relative and bare-filename `DatabasePath` startup crashes; `DatabasePath = null` now fails loud rather than silently running in-memory. Idempotent guardian registration (double-wire boot crash), AOT-safe JSON for the SQLite detection archive, and non-loading manifests. **Restored two hosted services the contributor delete silently dropped:** the `IdentityProcessingCoordinator` (its worker loops never started, so `FingerprintMatchAtom` Pass-2 identity confirm degraded to L1-only and drift-verify was shed) and `SignatureCoordinatorWarmupService` (clustering cold-started from live traffic only after every restart). Both are now guarded by a hosted-registration test so they cannot silently recur.
- **Identity + absorption.** Fingerprint-centroid absorption folds are bounded and sequenced: the debounced fold routes through the (now-live) slow-path coordinator's worker loops instead of a fire-and-forget per-observation burst, and the absorbable working set is capped to the most-recently-seen fingerprints, so a high-cardinality flood can no longer contend the SQLite single writer.
- **Heuristic weight resolution.** `RunInference` now resolves a feature's weight as learned → tuned `DefaultWeights` baseline → unseen default, so known bot arms (honeypot, `ua:contains_bot`, 404-scan) score even before the async weight-hydration runs; genuinely-unseen features stay neutral (cold-start false-positive fix). The prior lookup skipped `DefaultWeights` entirely on an un-hydrated model.
- **Dashboard.** `Internal` self-traffic is excluded consistently across the SQLite store, the middleware render layer, and the By-bot-type breakdown (it is the highest-volume "type" and drowned the real ones); one canonical signature per identity (the dashboard write no longer drifts to a hex-8 id); the visitor verdict is hydrated on trust-upstream hosts (fixes "You: 0.0% Unknown"); the SPA same-origin XHR false-positive is resolved via transport JA3.
- **Naming.** Short-but-distinct display names (stop echoing the raw UA and version/OS bloat); dropped vendor-home subdomains from the discriminator (the `Meta-ExternalAgent developers.facebook.com` over-claim); fixed the atom-refactor "Unknown" regression for catalog bots and humans. **"Unknown" no longer sticks:** the clean L1-match hot path (taken by every request for an established fingerprint) never re-composed the display name, so a fingerprint named with a fallback at allocation (before its UA was visible) stayed "Unknown" forever even once later requests carried a real UA; it is now re-composed on the hot path when the current name is a fallback. No-UA hosting-provider scanners are named `Missing UA <provider>` (from `ip.provider` / `ip.asn_org`) instead of an opaque per-fingerprint hash, still marked as a fallback so a real name overwrites it if a UA later appears.
- **Reliability + tests.** OTLP log-export timeout bounded so a dead collector cannot stall the request path; deterministic `FlushDirtyAsync` that now surfaces persist failures instead of silently succeeding; SignalR broadcast flush-state per hub context (flake); Windows SQLite pool cleanup; centroid write dedupe.
- **Build.** CA analyzers pinned to warnings (clean build on SDK 10.0.201 without disabling analysis); worked around the Grpc.Tools linux_arm64 protoc segfault; `[FromServices]` fix so the AOT binary starts.

### Performance

- Detection engine hoisted to a stateless singleton: per-request atom re-wiring eliminated. Full pipeline ~100 µs/request; the identity, Markov, and vector hot paths are ns-to-low-µs and mostly zero-alloc.
- Dashboard materialisation: a tick-warmed content cache plus one-round-trip batch compose cut per-request gateway load.

## [8.0.0] - 2026-07-01

v8 is the classification-rationalisation and dashboard-V2 release. The 7.x series proved out the metastable-fingerprint identity layer; v8 makes the whole system speak with **one voice** about a visitor - one fingerprint, one probability, one bot/human verdict, one display name - and calibrates the transport-fingerprint signals against each deployment's own norms so proxy/tunnel topologies stop producing false positives.

### Headline

- **Single canonical bot/human classifier (rationalisation phase 1).** Every surface derives `is_bot` from `bot_probability >= Classification.BotFloor`, never a separately-stored boolean. `SqliteDashboardEventStore` (and the commercial PostgreSQL store) rewrote every aggregation - summary, timeseries, top-bots, country, endpoint - onto the `@botFloor` cut, so the dashboard can no longer disagree with the score it displays. Design: `docs/architecture/bot-human-classification-rationalisation.md`.
- **Signal Assay - deployment-norm calibration (`DeploymentNormTracker`).** Behind a Cloudflare tunnel or any TLS-terminating proxy the transport tier never reaches the origin, so absent JA3 / HTTP/2 stream-priority / TCP Connection-header signals were being scored as bot evidence (a real Chrome hit 0.79 and got a 403, taking staging down). Absence is now calibrated against the per-deployment, per-UA-family norm: `AboveNorm` penalises, `BelowNorm` (absent-for-everyone) does not, and during cold-start warm-up penalties fail open. Gated signals: `TcpConnectionHeader`, `Http2StreamPriority` (both added this release), plus the pre-existing HTTP/1-vs-HTTP/2 signal.
- **Dashboard V2 information architecture (default-on).** Legacy Overview/Activity/Sessions/Threats/Insights/Investigate surfaces deleted and 301-redirected to the new three-group IA: **Traffic** (landing) / **Visitors** / **Site** / **Policies** / **Configuration**. Header search + ⌘K. Charts render through a locally-vendored **Chart.js** `sb-chartlet` primitive (no CDN).
- **Policy-stack editor.** Owned + effective rule rollup, curated facet-picker catalog (YAML), templates gallery, predicate composer (AND/OR/Sustain, `InCidr`), intent classifier, and 4-level posture. Per-endpoint policy stacks surface on the Site detail page.
- **Adaptive, human-first load-shedding.** `VisitorClass`-aware shed decision with per-class gates; per-endpoint performance baseline from the dashboard store; multi-signal `PipelineLoadSensor`; sheds at Critical return 503 rather than forwarding upstream. Off by default; humans are never shed by default (contract-pinned).
- **Three-slot fingerprint naming.** `Fingerprint.DisplayName` split into `induced` / `llm` / `given` slots resolved by `FingerprintNameResolver` (given ?? llm ?? induced). Per-fingerprint LLM namer via `EphemeralLlmCoordinator` replaces the per-signature picker; name history with `name_kind` + `operator_id`; drift `was: X` signifier.
- **`BotType.Internal`.** LAN / loopback traffic is classified, listed, and filterable but never throttled; RiskBand clamped to Low.
- **PROXY protocol v1/v2** support in the gateway to recover the real client IP behind an L4 edge.
- **StyloExtract integration pack** (`Mostlylucid.BotDetection.StyloExtract`) - AI-scraper-aware content negotiation wired to action policies.
- **Mode centroid classifier**, drift badges, and the FOSS **OTLP log-sink** stack in the ASP.NET monitoring pack.

### Fixes (v8 stabilisation)

- **`DegradationStoreSampler` ambiguous-constructor crash.** The site-health sampler grew a second public constructor (`IServiceProvider`-based, for optional-DI hosts) alongside the test-only 4-arg one; the built-in DI container only considers public constructors and aborted boot with "constructors are ambiguous" under `ValidateOnBuild` (on in Development). This took down the Demo app and every Demo-hosted integration test. The test-only constructor is now `internal` (exposed via `InternalsVisibleTo`), leaving a single public DI candidate.
- **Test-suite assay.** Removed obsolete hollow test stubs (commented-out bodies, no assertions, inaccurate "removed in blackboard architecture" skip reasons for types that still exist) from `SignatureResponseCoordinatorTests`; re-enabled `ResponseSignal_RequiredPropertiesCanBeSet` (skip reason was false). Added proper Signal-Assay warm-up coverage (AboveNorm / WarmingUp / BelowNorm) for the Http2 stream-priority and TCP Connection-header penalties, which the previous tests did not cover.



The 7.6 release closes the metastable-fingerprint loop. The 7.0 - 7.5 series put the storage, matcher, and scoring pieces in place; 7.6 wires them into an adaptive learning loop that visibly self-corrects under traffic. Calibration now fires on observation pressure instead of a fixed wall clock, drift metrics expose per-archetype per-UA leakage on a new observability endpoint, umbrella shrinkage tightens over-broad archetype catchments automatically, auto-regrowth lets them recover from transient leakage, neighbour repulsion stops smaller archetypes being eaten by larger ones, and convergence detection flags pairs that have been near merging for several cycles. The honeypot dashboard surface is also fixed: detection was working all along, but the badge was reading an unpopulated field.

### Headline

- **Adaptive learning loop (Phases 1 - 5 v3).** Replaces the fixed `Tick1m + 30-minute gate` calibration cadence with an observation-driven trigger. Demos used to look dead for 30 minutes after startup; now the first observation triggers calibration within seconds (Demo profile) or once the configured observation / drift thresholds are met (production). Full design spec at `docs/superpowers/specs/2026-06-21-adaptive-learning-loop-design.md`.
- **`GET /stylobot/admin/learning/health`** (bearer-token gated). Returns the calibration tick's last decision + signals + reason; the latest drift-metric rows per `(archetype, ua_family, matches_asserted_ua)`; per-archetype umbrella-shrinkage actions; centroid mobility (top pinned, top proximate); convergence merge-candidate pairs. The single endpoint that answers "is learning actually working" without spelunking SQLite.
- **Honeypot endpoint visibility on the dashboard.** `DashboardEndpointStats.IsHoneypot` is now populated per row via `HoneypotPathDefinitions.Classify` in both the SQLite and PostgreSQL stores. The `SbEndpointsList` filter strip gains a Honeypot chip + Clear. Detection was firing all along (logs show `Tier 1 honeypot hit`, `Honeypot fake response served`); the badge was reading an unpopulated field.
- **Dashboard audience-filter audit and fix.** Six partial / API handlers (`BuildSummaryStatsModelAsync`, `ServeSummaryApiAsync`, `ServeTimeSeriesApiAsync`, `ServeCountriesPartialAsync`, `ServeCountriesApiAsync`, `ServeEndpointsApiAsync`) plus three widget-batch renderers accepted an `audience` query param and silently dropped it. The cached snapshot is audience-agnostic; humans/bots/honeypot filters now route through the store so the SQL predicate applies. Five PostgreSQL store methods (`GetSummaryAsync`, `GetTimeSeriesAsync`, `GetCountryStatsAsync`, `GetEndpointStatsAsync` for humans/bots, plus `GetSummaryAsync` for time windows) had the same gap on the commercial side; fixed and pinned by 7 contract tests in the commercial repo.
- **CI test reliability.** Three chronically flaky tests fixed by removing test-code races (the production code was structurally correct in all three). `YamlPolicyRuleStoreTemplateLoadTests` now use atomic write-to-staging + `File.Move` so the inotify watcher only ever sees the fully-written file. `DetectionBroadcastPersistOnDownstreamThrowTests` waits on a signature-completion TCS in addition to the detection TCS. `PathLifecycleStoreTests.FlushAsync_PersistsDirtyEntriesToSqlite` drops a racy "not yet flushed" probe that the background drainer beat to the punch.

### Adaptive learning loop

Five phases land together as the cohesive cut described in the design spec.

**Phase 1 - Adaptive trigger.** New types under `Mostlylucid.BotDetection.Identity.Triggers/`: `AdaptiveTriggerPolicy` (record), `TriggerContext`, `TriggerDecision`, `IAdaptiveTriggerSignalSource`, `AdaptiveTriggerEvaluator` (pure-function eval). The evaluator composes four gates: min interval (don't thrash), max interval safety net, load-band ceiling (defer under sustained pressure), and an OR-of signal threshold list (fire on observation count, drift L2, or any caller-supplied signal). `CalibrationSignalSource` plumbs two `Interlocked` ops into the existing observation + absorption hot paths (`SqliteFingerprintStore.RecordObservationAsync` and `FingerprintAbsorptionService.AbsorbAsync`). Zero-allocation, zero-lock. `IdentityWeightCalibrationService` picks `Tick1s` cadence when the adaptive trigger is enabled (else stays on `Tick1m` legacy) and evaluates the policy on every heartbeat. Signals are reset BEFORE the run so observations arriving during a long calibration count toward the NEXT cycle. Demo opts in via `appsettings.json` with `MinInterval: 1s` and `ObservationThreshold: 1` so learning is visible within seconds. Production default keeps `Trigger.Enabled = false` so existing deployments see no behavioural change unless they explicitly enable it.

**Phase 2 - UA-anchored drift metrics + observability.** New `archetype_drift_metrics` table (SQLite via `identity_core.sql` + `IdentitySchema.MigrateExistingTablesAsync`, PostgreSQL via `fingerprint-match-schema.sql` plus `ALTER TABLE ... ADD COLUMN IF NOT EXISTS variance_multiplier`). Composite PK `(archetype_id, ua_family, calibrated_at)`. `IdentityWeightCalibrationService.ComputeDriftMetrics` runs after centroid refinement: walks observations grouped by `(archetype, ua_family)`, computes mean / sample-variance / p90 of L2 to the (refined) centroid, stamps `matches_asserted_ua` against the archetype's `AssertedUaFamily`. The flag is load-bearing: `matches_asserted_ua = true` + high drift means the archetype claims the right UA family but its centroid sits in the wrong region; `matches_asserted_ua = false` + non-trivial count means the archetype is over-claiming territory it doesn't own. Three new `IFingerprintStore` methods (`InsertDriftMetricsAsync`, `ListLatestDriftMetricsAsync`, `ListRecentObservationsForDriftAsync`) with SQL window functions for the per-archetype observation sampling.

**Phase 3 - Umbrella shrinkage.** New `IdentityArchetype.VarianceMultiplier` in `[0.05, 1.0]`, persisted via the `identity_archetypes.variance_multiplier` column. The matcher's `MaskedSimilarityCore` multiplies its per-dim variance by this scalar; below 1.0 tightens the per-dim Gaussian penalty, so the same observation scores lower against an over-claiming archetype than against an untouched neighbour. `IdentityWeightCalibrationService.ApplyUmbrellaShrinkage` walks drift metrics, applies the spec §2.C action ladder per archetype (bloat = `p90_drift / RadiusBaseline`; bloat ≥ 1.0 OR leakage > 0 = shrink; bloat ≥ 2.0 = log split-candidate; ShrinkRate default 0.05; floor 0.05). Action results surface on the `/admin/learning/health` endpoint with `Kind = "shrink"` / `"regrow"` / `"split-candidate"`.

**Phase 4 - Centroid mobility.** Five new fields on `IdentityArchetype`: `CentroidDeltaLastCycle`, `PinCycles`, `DescendantVarianceLastCycle`, `NearestNeighbourId`, `NearestNeighbourDistance`. Adaptive α per spec §2.D rule 1: `IdentityWeightMath.AdaptiveAlphaFromVariance` clamps `α_cap × exp(-meanVariance / scale)` to `[α_min, α_cap]`. Low descendant variance keeps refinement at the full cap (descendants agree, move the centroid); high variance drops α toward the floor (let umbrella shrinkage purge the misassigned descendants instead of chasing their inconsistent mean). Pairwise nearest-neighbour annotation runs each cycle (O(n²) is ~12k ops at 155 archetypes - negligible). Pin warning fires when `PinCycles >= mobilityOpts.PinWarnAfterCycles` AND `DescendantVarianceLastCycle > mobilityOpts.HighVarianceThreshold` (the "pinned in the wrong position with disagreeing descendants" case the maintainer flagged at the start of the session).

**Phase 5 - Auto-regrowth, neighbour repulsion, convergence detection.**

- *Auto-regrowth.* `IdentityArchetype.HealthyCycles` counter resets on any shrink event, increments on each quiet cycle. Once `HealthyCycles >= RegrowAfterCycles` AND `VarianceMultiplier < 1.0`, the multiplier walks back up by `RegrowRate` per cycle (default 2%, deliberately slower than the 5% shrink rate to prevent oscillation). Clamped at 1.0 - never widens beyond the YAML-seeded catchment. New `ShrinkageAction.Kind` field distinguishes `"shrink"` / `"regrow"` / `"split-candidate"`.
- *Active neighbour repulsion.* `IdentityWeightMath.BuildRepulsionVector` returns null unless: the archetype has a nearest neighbour, the distance is below `RepulsionRadius`, AND this archetype has fewer descendants than the neighbour. The smaller-only rule prevents oscillation. When all three hold, `RefineArchetypeCentroidAdaptive(repulsion)` shifts the descendants-mean target away from the neighbour before the α-blend, so the refined centroid moves in the repulsion direction. Defaults: radius 0.3, strength 0.02. Set strength to 0 to disable.
- *Convergence detection (spec §2.D rule 3).* Per-pair canonical-ordered counters track how long two centroids have stayed within `ConvergenceMergeThreshold`. When the counter reaches `ConvergenceWarnAfterCycles`, the calibration emits a `centroid.merge-candidate` warning and surfaces the pair on the endpoint. Counter resets cleanly when the pair drifts back outside the threshold. Live demo verification immediately caught `adguard` and `generic-adblocker` at distance 0.000 across 45 consecutive cycles: both archetypes assert only `hdr.ua_family = "Chrome"` in their YAML and compile to mathematically identical centroids.

### Dashboard

**Honeypot endpoint visibility.** `DashboardEndpointStats.IsHoneypot` was declared on the record and consumed by the badge in `SbEndpointsList.cshtml:211` but never populated by either `SqliteDashboardEventStore.GetEndpointStatsAsync` or `PostgreSQLDashboardEventStore.GetEndpointStatsAsync`. Detection was firing correctly all along: the demo log shows `Tier 1 honeypot hit`, `Honeypot fake response served`, `Early exit triggered by HoneypotLink: VerifiedBadBot`, and detection events publishing with `action=honeypot-response` `status=404`. Only the dashboard surface was missing. Both stores now derive `IsHoneypot` per row via `HoneypotPathDefinitions.Classify` (cheap substring + dict lookup; no schema change). The view's filter strip gains a Honeypot chip; `audienceFilter=honeypot` is handled in-memory post-query because the path classification runs per row regardless.

**Audience-filter audit.** Six dashboard partial / API handlers plus three widget-batch renderers accepted an `audience` query parameter and dropped it on the floor. The `DashboardAggregateCache` holds a single audience-agnostic snapshot, so clicking a "Bots" chip on the dashboard returned the full traffic mix. Fixed: when `audience is "humans" or "bots"`, bypass the cache and route through the store so the SQL `is_bot` predicate applies. `audience=honeypot` is path-shape and continues to work on the cached snapshot. Five PostgreSQL store methods had the same gap on the commercial side (parameter accepted, predicate never substituted into the SQL); fixed and pinned by `PostgresAudienceFilterTests`. `PostgreSQLDashboardEventStore.GetSummaryAsync` also turned out to be ignoring `startTime` and `endTime`: fixed in the same cut.

### Cancellation hygiene

Three SQLite store methods (`GetEndpointStatsForSignatureAsync`, `GetHoneypotHitsAsync`, `GetUserAgentVersionHistoryAsync`) accepted `CancellationToken ct` but called `EnsureInitializedAsync()` without forwarding it. Cancellation during the schema-init window was silently lost. Caught by an agent-driven sweep with a multi-line signature regex that picked up what the original audit's single-line pattern missed. The PostgreSQL mirror is clean: `ct` routes through `CommandDefinition.cancellationToken` (Dapper's correct pattern). `SqliteDashboardCancellationTests` pins the contract going forward.

### Demo

`appsettings.json` opts the demo into the adaptive identity layer with calibration firing on every observation and the new admin endpoint reachable via `Authorization: Bearer demo-token`. Live demo run with three UA shapes (Chrome, Googlebot, bingbot) hitting 13 paths produced an immediate `umbrella.split-candidate` warning on `googlebot` (variance multiplier walked 1.000 to 0.142 across ten cycles as repeated leakage compounded), an `adguard` and `generic-adblocker` convergence candidate at distance 0.000 (real YAML duplication: both files assert only `hdr.ua_family = "Chrome"` and compile to identical centroids), and `top_proximate` flagged `chrome-desktop` and `chrome-privacy` at 0.216: the exact umbrella-leak the original test #3 surfaced at the start of the session.

### Documentation

- `docs/DOCKER_SETUP.md`: git clone URL corrected from the legacy `mostlylucid.stylobot.git` to the current `stylobot.git`.
- `docs/building-tiny-multiplatform-dotnet-executables-with-aot.md`: example link updated to the current repo + path (`stylobot/tree/main/src/Mostlylucid.BotDetection.Console`).

## [7.5.0] - 2026-06-16

Seven distinct workstreams land in this release. The most structural is the Wave 2 ScheduleCoordinator migration: 30+ background services that previously ran their own `System.Threading.Timer` loops are now tick subscribers on a single coordinator, giving operators a unified watchdog for every periodic task in the process. The security headline is the transport header trust gate, which closes the X-JA3/JA3-string spoofing surface identified in a previous audit. Detection quality improves on two fronts: an arcjet well-known-bots catalog integration fills classification gaps for bots absent from StyloBot's own YAML patterns, and forward-DNS confirmation for ActivityPub `+URL` claims catches spoofed Mastodon/fediverse UAs that previously passed on NodeInfo alone.

### Headline

- **Transport header trust gate (security fix).** `X-JA3-Hash`, `X-JA3-String`, `X-JA4`, `X-Client-TLS-*`, `X-Client-HTTP-Version`, QUIC headers, and `X-Client-ASN` are now silently discarded unless the connecting peer IP matches a `BotDetection:TransportTrust:TrustedProxyIps` CIDR entry. Before this fix an attacker hitting the gateway over direct HTTPS could inject a known-Chrome JA3 and receive the human-signal bias it would earn from a real Cloudflare edge. Operators behind a reverse proxy: configure `TrustedProxyIps` exactly as you would `ForwardedHeaders` and all injected signals continue to work as before.
- **ScheduleCoordinator: 30+ background services unified under one watchdog.** All periodic background loops (heartbeat, session atomizer, entity resolution, vector compaction, calibration, JA3 corpus refresh, and more) now subscribe to named tick signals (`tick.1s` through `tick.1h`) emitted by a central `ScheduleCoordinator`. A stalled or exception-throwing subscriber surfaces through the shared fault channel instead of silently disappearing into the host's unobserved-exception handler. No timer cadences change; no persistence paths change.
- **arcjet well-known-bots catalog integration.** `WellKnownBotIndex` is a new third-tier UA classification fallback that closes coverage gaps for bots not defined in StyloBot's YAML patterns (TurnitinBot, CrunchBot, aiHitBot, SemanticScholarBot, and others). The index refreshes on a `tick.1h` subscription and has zero impact on bots already classified by the primary and secondary tiers.
- **Forward-DNS confirmation for ActivityPub `+URL` claims.** `FediverseDomainContributor` now resolves the A/AAAA records of the claimed instance hostname and compares them against the client IP before issuing `FriendlyDomainVerified=true`. An IP mismatch writes `verifiedbot.forward_dns_matched=false` and the spoofed claim is rejected rather than trusted on NodeInfo alone.
- **Persistent verified-bot trust state.** Fingerprint records now persist `claim_status`, `verification_method`, `verified_at`, and `trust_observations` to SQLite. rDNS and NodeInfo checks short-circuit on repeat visits within the configurable `BotDetection:Trust:TrustCacheTtl` window (default 24 h), eliminating per-request DNS round-trips for known verified bots.
- **Claim-first display naming.** `FingerprintNameComposer` now leads with what the UA actually claims (Priority 1: `ua.bot_name` catalog match, Priority 2: parsed product token) before consulting archetype or centroid-derived names. A Mastodon UA is always named after its claim; a raw UA prefix (capped at 48 chars) is the Priority 4 fallback rather than null. Fixes the staging regression where Googlebot and Mastodon instances appeared as "Chrome on macOS (privacy headers)" after a low-drift match recycled a stale stored name.
- **Policy editor: full seven-kind action matrix.** The dashboard policy editor covers all action kinds (Block, LogOnly, SilentDrop, StickyDeny, Throttle, RateLimit, Challenge, Tag) as dedicated HTMX-swapped partials, a scope picker for host/method/geo/identity granularity, a promote-observe button per rule row, and a `IPolicyCanEditPolicy` seam that gives FOSS deployments full read-only visibility while gating write affordances on the commercial tier.
- **robots.txt and sitemap extensions.** New `endpoints.MapStyloBotRobotsTxt()` and `endpoints.MapStyloBotSitemap()` minimal-API extensions make it a single call to serve a policy-aware robots.txt (auto-derives `Disallow:` lines from live Block-action rules) and an adaptive sitemap.xml that serves different URL lists by detection verdict.

### Wave 2: ScheduleCoordinator Migration

Before this release, every periodic background task in StyloBot owned its own `System.Threading.Timer` or `BackgroundService` timer loop. A timer that stalled or threw an unhandled exception would silently stop firing with no log entry and no recovery path. `ScheduleCoordinator` replaces all of these with a named-tick signal bus (`tick.1s`, `tick.10s`, `tick.1m`, `tick.5m`, `tick.1h`, and a `tick-drain` slot for queue-flush work). Each service subscribes to the relevant tick; the coordinator's watchdog detects overdue handlers and routes faults through the shared fault channel. `IScheduleCoordinator` and its supporting types live in `Mostlylucid.Common` so the gateway, sidecar, UI, and third-party packs can subscribe without a hard dependency on the core detection assembly.

Operator impact: zero. All cadences are identical to the pre-migration timers. The only observable change is that a stalled background service now produces a structured log warning rather than disappearing.

Migrated services:

- `HeartbeatService` -- `tick.5m`
- `DeploymentNormCalibrationService` -- `tick.1s`
- `LicenseStateRefreshService` -- `tick.1m`
- `BrowserVersionService` -- `tick.1h`
- `CommonUserAgentService` -- `tick.1h`
- `Ja3CorpusRefreshService` -- `tick.1h`
- `VerifiedBotRegistry` -- `tick.1h`
- `EntityResolutionService` -- `tick.1m`
- `SessionAtomizerService` -- `tick.1m`
- `VectorCompactionService` (session vector snapshot compaction) -- `tick.1h`
- `FingerprintDriftService` -- `tick.10s`
- `PopulationMarkovService` -- `tick.10s`
- `IdentityGlobalWeightsCache` -- `tick.10s`
- `IdentityWeightCalibrationService` -- `tick.1m`
- `SignatureConvergenceService` -- `tick.10s`
- `BotListUpdateService` -- `tick.1h`
- `MeterListenerService` -- `tick.1m`
- `DashboardSummaryBroadcaster` -- `tick.10s`
- `RemoteMetricCollector` -- `tick.10s`
- `GeoLite2UpdateService` (MaxMind database refresh) -- `tick.1h`
- `HoneypotReporter` -- `tick-drain`
- `BackgroundEnrichmentService` (FCrDNS + LLM enrichment queue) -- `tick-drain`
- `ThreatIntelEnrichmentService` -- `tick-drain`
- `LlmClassificationCoordinator` and `IntentClassificationCoordinator` -- `tick-drain`
- `SessionPersistenceService` (session vector SQLite flush) -- `tick-drain`
- `ProfileAnalysisWorker` (YARP gateway post-request analysis) -- `tick-drain`
- `LearningBackgroundService` and `BoundedChannelLearningBus` (adaptive learning pipeline) -- `tick-drain`
- `FingerprintAbsorption`, `FingerprintModeAbsorption`, and `FingerprintRollupRecompute` -- paired wave window (absorption and recompute guaranteed to fire in order within the same tick)
- Three hosted services inside `PrometheusPack` -- tick subscriptions; `PrometheusPack` now self-registers `ScheduleCoordinator` in viewer-host mode so standalone dashboard hosts do not fail to boot when no other component has registered it

### Security: Transport Header Trust Gate

Resolves the X-JA3 header spoofing vulnerability noted in a prior audit. TLS/JA3, HTTP/2, QUIC (HTTP/3), and TCP/IP fingerprint headers injected by a reverse proxy are now gated behind a peer-IP trust check (`ITransportHeaderTrust` / `TransportTrustOptions`).

**The gap:** An attacker hitting the gateway directly over HTTPS could send `X-JA3-Hash: <known Chrome hash>` and receive the `-0.15` human-signal bias a real Cloudflare or Caddy edge would have earned. The header was trusted unconditionally with no check on where the request came from.

**The fix:** `PeerTrustDecisionService` resolves the connecting peer IP against `BotDetection:TransportTrust:TrustedProxyIps` (CIDR list). Untrusted peers have the injected headers silently discarded; their actual Kestrel TLS context is still read normally. Trust decisions are written to the blackboard as `transport.trust.peer_trusted`, `transport.trust.trust_mode`, and `transport.trust.distrust_reason` so downstream detectors and the policy stack can observe them. A `spoofed_edge_header_penalty` is applied to requests from untrusted peers that send these headers. The `X-Forwarded-For`-chain-derived topology trust arm has been removed; it was forgeable by the same class of attacker.

**Operator action:** If you run behind Cloudflare, Caddy, nginx, or any other edge proxy, add the proxy's egress IP(s) or CIDR blocks to `BotDetection:TransportTrust:TrustedProxyIps`. Operators not behind a proxy need no configuration change and gain fail-closed protection immediately. Running `Mode: Off` disables the gate and is documented as a risk for public-facing deployments.

- Added `TransportTrustOptions` config model with `TrustedProxyIps` (CIDR list) and `Mode` (Off / Strict).
- Added `transport.trust.*` signal keys so downstream consumers can observe trust decisions.
- Introduced `PeerTrustDecisionService` with CIDR allowlist resolution, bare IPv4/IPv6 support, and IPv4-mapped IPv6 normalisation.
- Gated `X-JA3-Hash`, `X-JA3-String`, `X-JA4`, `X-Client-TLS-*` in `TlsFingerprintContributor` behind peer trust.
- Gated `X-Client-HTTP-Version` / `Sb-Http-Version` in `Http2FingerprintContributor` behind peer trust.
- Gated QUIC / HTTP/3 injected headers in `Http3FingerprintContributor` behind peer trust.
- Gated `X-Client-ASN` and p0f-derived TCP/IP headers in `TcpIpFingerprintContributor` behind peer trust.
- Exposed `spoofed_edge_header_penalty` as a tunable in `TransportTrustOptions`; emitted in all four transport-protocol contributor YAML manifests.
- Removed the forgeable topology-trust arm (`X-Forwarded-For` chain inspection).
- Added regression test: direct-peer JA3 spoof scores as bot; same request from a configured trusted proxy scores as human.
- Added operator documentation covering design rationale, per-proxy configuration recipes, and an `Mode: Off` risk warning.

### Detection: Well-Known Bots Catalog

StyloBot now integrates the [arcjet well-known-bots](https://github.com/arcjet/well-known-bots) catalog as a third-tier fallback for user-agent and AI-scraper classification.

**Classification tier order (unchanged precedence):**
1. StyloBot YAML bot patterns (primary)
2. AI-scraper YAML patterns (secondary)
3. arcjet catalog (`WellKnownBotIndex`) -- new third-tier fallback

Bots now correctly classified that previously fell through as unidentified: TurnitinBot, CrunchBot, aiHitBot, SemanticScholarBot, and others present in the arcjet catalog but absent from StyloBot's own YAML definitions. The index refreshes via a `tick.1h` subscription (`WellKnownBotRefreshService`) and contributes zero overhead to the hot detection path for bots already classified by tiers 1 or 2.

- Added `WellKnownBotIndex` with a three-tier lookup and the arcjet catalog as the third tier.
- Added `WellKnownBotRefreshService` subscribing to `tick.1h` for catalog refresh.
- Fixed `DashboardLinkResolver` constructor ambiguity that caused a boot crash in every host calling `AddStyloBotDashboard` after the DI changes; added `[ActivatorUtilitiesConstructor]` to the `IOptions<>` overload.
- Added a k6 5-minute mixed load and soak test validating the `WellKnownBotIndex` fallback path at approximately 700 RPS (p50=2ms, p95=171ms, errors=0%).

### Detection: Forward-DNS Verified Bot Confirmation

Closes the spoofed-ActivityPub claim gap. Previously, a request bearing a `Mozilla/5.0 (compatible; Mastodon/4.x; +https://evil.example/)` UA could pass the NodeInfo check and receive `FriendlyDomainVerified=true` without any confirmation that the client IP actually belongs to the claimed instance.

`FediverseDomainContributor` now:

1. Extracts the instance hostname from `ua.bot_instance` (the canonical signal emitted by `UserAgentContributor` -- both contributors now read the same signal rather than running independent regexes).
2. Resolves the A/AAAA records of that hostname via a 5-minute DNS cache.
3. Compares the resolved addresses against the client IP.
4. Writes `verifiedbot.forward_dns_matched=true` on a match or `verifiedbot.forward_dns_matched=false` on a mismatch; a mismatch suppresses `FriendlyDomainVerified`.

The rDNS-after-the-fact path was also broken by `skip_when: detection.early_exit` in the manifest -- once `FastPathReputation` pinned a verdict, `verifiedbot.method=fcrdns` never fired. rDNS work is now moved to a fire-and-forget background sink so the hot path is not blocked and the check always runs regardless of early-exit state.

### Identity: Persistent Trust State and Claim-First Naming

**Persistent trust state.** Fingerprint records gained four new columns: `claim_status`, `verification_method`, `verified_at`, and `trust_observations`. The schema migration is idempotent. A new `UpdateClaimVerificationAsync` write-behind hook lets the verifier contributors skip re-verification within `BotDetection:Trust:TrustCacheTtl` (default 24 h), eliminating redundant rDNS and NodeInfo calls on repeat visits from known identities.

**Claim-first display naming.** `FingerprintMatchContributor` was short-circuiting to the stored `DisplayName` on low-drift matches regardless of the current request's signals. Bots whose stored name was stale (Googlebot, Bytespider, Mastodon) inherited incorrect labels like "Chrome on macOS (privacy headers)". Display names are now always recomposed from fresh signals. `FingerprintNameComposer` applies this priority order:

1. `ua.bot_name` from the YAML catalog (highest authority)
2. Parsed UA product token
3. Archetype-derived name (only when `archetype_kind == "human-browser"`)
4. Raw UA prefix, capped at 48 chars (`IsFallback = true`, cannot overwrite a previously stored authoritative name)

**Per-mode archetype recompute.** `FingerprintModeAbsorptionService` was copying the parent fingerprint's `InferredArchetype` (null on seed) instead of recomputing against the freshly merged centroid. The per-mode "Nearest archetype" column in the signature detail page rendered as "-" indefinitely. The drainer now invokes `IdentityArchetypeRegistry.FindNearest` and persists the result when the score clears the new `BrowserMode.MinInferredArchetypeScore` gate (default 0.55).

### Dashboard: Policy Editor UI

The dashboard policy editor now covers the full action-kind surface. All seven kinds render as dedicated HTMX-swapped partials so operators see only the fields relevant to their chosen action; switching kind mid-edit does not lose data from other kind fields.

**Action partials:**
- **Block, LogOnly, SilentDrop, StickyDeny** -- zero required fields; render immediately.
- **Tag** -- tag name input.
- **Challenge** -- kind selector (PoW / CAPTCHA) with a per-kind subfield that swaps inline.
- **RateLimit** -- RPM, burst size, key mode (IP / Subnet / ASN / Signature), mitigation strategy, and over-limit response.
- **Throttle** -- RPS target and reason label.

Additional changes:

- **Scope picker:** `SbScopePicker` reused in a multi-mode wrapper for the Apply Template flow; operators set host, method, geo, and identity scope in one step.
- **Promote-observe button:** Each rule row carries a promote button targeting a dedicated endpoint, giving a one-click path from observe-only monitoring to active enforcement.
- **`IPolicyCanEditPolicy` seam:** FOSS deployments default to read-only. The full policy stack is always visible; write affordances are gated on commercial tier.
- **Policy stack filter bar:** Sortable columns, status-code filter chips (All / 2xx / 3xx / 4xx / 5xx), and an aggregate strip on the endpoints table.
- **Stable `data-testid` selectors** on editor affordances for Playwright coverage.
- **Configurable debounce timings:** Auto-save latency on the edit row is now driven by `PolicyStackOptions` rather than hard-coded JS constants.
- **`InMemoryPolicyRuleStore` promoted to shared test support** with documented key-vocabulary bridge so unit and integration tests share identical rule-resolution semantics.

### Core: robots.txt and Sitemap Extensions

Two new minimal-API extension methods make it a single call to serve crawler-aware files from any ASP.NET Core host.

- **`endpoints.MapStyloBotRobotsTxt()`** composes a `/robots.txt` from configured `User-agent` / `Allow` / `Disallow` / `Crawl-delay` rules and auto-derives the `Sitemap:` directive from the request scheme and host (override via `StyloBotRobotsTxtOptions.SitemapUrl`). Policy-derived `Disallow` lines: the extension consults `IPolicyRuleStore` and appends a `Disallow:` entry for every live Block-action rule scoped to a single endpoint, keeping the public crawl contract automatically consistent with live enforcement. Opt out via `StyloBotRobotsTxtOptions.IncludePolicyDerivedDisallows = false`.
- **`endpoints.MapStyloBotSitemap()`** serves an adaptive `/sitemap.xml` that returns different URL lists by detection verdict: `PublicUrls` for humans and verified crawlers, `HoneypotPath` for confirmed bots, and optional `UncertainUrls` for the grey zone. All thresholds are configurable via `StyloBotSitemapOptions`.

Also shipped: **`TrailblazorDemo`** -- a self-contained ASP.NET Core MVC reference app for the Trailblazor 2026 conference talk. Demonstrates in-process middleware mode (no separate gateway), `<sb-risk-pill>`, `<sb-signal>`, `<sb-honeypot>`, `.BlockBots()`, named action policies, client-side fingerprinting (`MapBotDetectionScript` + `MapBotDetectionFingerprintEndpoint`), BotD probe, `UseForwardedHeaders` for Cloudflare tunnel, and a `/Signals` page backed by the new `SbAllSignals` view component. Ships with a `Dockerfile` and subdomain banner for hosting at `aspnet.stylo.bot`.

### Session: Dashboard Expiry Filter

The session dashboard was producing 404 responses on click for paginated session lists that referenced records outside the configured retention window. `GetSessionsAsync` now applies an expiry filter at query time so session list pages never surface records that no longer exist in SQLite.

### Performance

- **`VerifiedBotContributor` log level: `Information` -> `Debug`.** At 100 RPS the contributor emitted approximately 100 structured log entries per second to every registered sink. Downgraded to `Debug`; production deployments typically run at `Information` or above so the overhead disappears with no configuration change required.
- **`BoundedCache` eviction: LRU -> LFU.** Under cache pressure, popular bot IPs (Googlebot, UptimeRobot, known-bad repeat offenders) were being evicted because they hit the LRU tail while one-off IPs held newer slots. LFU eviction retains high-frequency entries and drops the long tail first, improving cache hit rates for the most active signatures.
- **`IScheduleCoordinator` abstracted to `Mostlylucid.Common`.** The gateway, sidecar, UI, and third-party packs (PrometheusPack) share the coordinator interface without taking a dependency on the core detection assembly, reducing binary coupling for composite deployments.

### Testing

- **`RecordingScheduleCoordinator` and `FixedTimeProvider` promoted to shared test support.** Every migrated background service can use the same deterministic fakes without re-rolling them per test project.
- **k6 5-minute soak test** for the `WellKnownBotIndex` fallback path: mixed YAML bots, arcjet catalog bots, and human browsers at approximately 700 RPS; p50=2ms, p95=171ms, errors=0%.
- **In-process throughput harness** for the detection pipeline, enabling performance regression validation without external tooling.
- **Transport-trust regression test:** direct-peer JA3 spoof scores as bot; loopback traffic unchanged.
- **Security regression tests:** CSRF, rate-limit bypass, secret redaction, and SSRF attack surfaces from the PR #29/#30 review cycle.
- **`PrometheusPack` viewer-host self-registration regression tests** confirming that remote dashboard mode boots correctly without requiring the concrete coordinator type.
- **TLS corpus integration tests:** embedded JA3 reference corpus load, subset detection, and version-delta identification; expanded edge cases from code review.
- **Live dashboard routing tests via `DemoAppFactory`:** path-segment routing, tab redirects, pack dispatch, case-insensitive pack IDs, bare pack identifiers, and query-string stripping on 301 redirects.
- **Playwright navigation suite** auditing all dashboard links against a live staging host; suites are skipped by default in local dev runs (`--filter Category=Integration` to enable after starting Demo manually) and run unconditionally in CI.
- **Client-side detector Playwright tests:** BotD, WebRTC ICE, and TTS voice probe scenarios previously blocked by fixture race conditions are now passing after a cold-start timing fix.
- **Puppeteer replaced with Playwright** across the entire integration test surface, with 2026 client-side detection research scenarios for damru/CDP fingerprint probes.
- **Identity contract tests:** Mahalanobis-style (Gaussian log-likelihood) archetype scorer pinned against regression; verdict-cache reads asserted to come from the fingerprint dictionary, not a parallel in-memory store.
- **BenchmarkDotNet regression scenarios** for cloak-detection probes (damru, Multilogin, Kameleo) and Phase 1/2 action-policy hot path.
- **`SignatureRiskVerdictComposer` scenario suite** covering the four axes: probability, threat band, reputation, and browser attestation.
- **`PolicyResolverTests` and `SbPolicyStackTests` isolated** from the global wildcard seed so test scenarios produce deterministic results regardless of the shipped baseline rule set.

## [7.0.0] - 2026-05-31

The identity layer becomes pluggable, the standalone AOT gateway gets perf
profiles, the sidecar release pipeline ships its first-ever GitHub Release,
and a sweep of fingerprint-matching corrections lands. 54 commits across the
release. **FOSS detection is unchanged — every Sqlite store stays the
default; the new interfaces only give the commercial layer a swap point.**

### ⚠️ License change

**StyloBot FOSS flips from `Unlicense` (public domain) to
`GNU AGPL-3.0-only`** in 7.0.0. The `LICENSE` file at the repo root is
now the canonical AGPLv3 text; every published NuGet package
(`Mostlylucid.BotDetection`, `Mostlylucid.BotDetection.ApiHolodeck`,
`Mostlylucid.Common`, `Mostlylucid.GeoDetection`) advertises
`PackageLicenseExpression = AGPL-3.0-only`. Practical impact for
existing users:

- Code you wrote on top of stylobot is unaffected if you don't
  distribute it as a service or redistribute the binary. Internal
  use is fine.
- Public-facing services that incorporate stylobot's source must
  offer their users the corresponding source code (this is the "A"
  in AGPL). Static linking, dynamic linking, and the SDK helpers
  all count as incorporation.
- The dual-licensed `Mostlylucid.GeoDetection.Contributor` (`MIT`)
  is unchanged.
- The commercial layer in `stylobot-commercial` retains its
  separate licence and is unaffected.

CONTRIBUTING.md was updated so new contributions are also AGPLv3.

### In-memory storage mode

New `AddBotDetectionInMemory()` DI extension routes every FOSS SQLite
store at a per-store named shared in-memory database
(`file:<name>?mode=memory&cache=shared`). No `.db` files on disk; state
evaporates on restart. Same code paths as the disk-backed mode -- just
a new `SqliteConnectionStrings` helper that centralises the
connection-string construction.

Designed for:
- Ephemeral pods / serverless deployments
- Demo / sandbox containers (the `x.stylo.bot` live samples use this)
- Integration tests (no tempdir cleanup, faster setup than file SQLite)

Trade-off: the verdict cache / fingerprint match cache / session
aggregation still work but reset on every restart -- 10-30 s warmup
cost right after deploys. Not for long-running prod deployments;
great for ephemeral ones. Backwards-compatible: callers of
`AddBotDetection()` get identical behaviour to today.

### Headline

- **Identity store is pluggable.** `IFingerprintStore`, `IClusterStore`,
  `ILicenseGraceStore`, plus a `Func<DbConnection>` factory rewrite of
  `AssetHashStore` and `CentroidSequenceStore`, mean the commercial Postgres
  build can swap the matcher write path without spawning stylobot's own
  `.db` files. The FOSS package keeps the SQLite implementations as the
  default bindings.
- **Matcher bisect: keep verdict signals at Wave-6.** The Priority-1
  experiment (allocate fingerprint earlier so the dashboard renders for
  verdict-cached visitors) had the side effect of letting the matcher's
  archetype-kind signals dominate the aggregate for borderline-but-bot
  scenarios, scoring `missing-browser-headers` Chrome traffic at **0.05**
  instead of the scenario-expected 0.6. Reverted to Priority 6. Track the
  follow-up under `project_bdf_replay_regression`.
- **Archetype-kind gate.** The composer now uses an archetype name as the
  visitor display name only when its `archetype_kind == "human-browser"`.
  Bot-shaped archetypes that match by header-coincidence (Mastodon
  matching real Chrome traffic, python-on-Firefox) fall through to UA
  family + OS instead.
- **AOT gateway perf profiles.** `STYLOBOT_PROFILE=api|site|highrisk|balanced`
  picks Kestrel limits, ThreadPool min-thread counts, and HTTP/2 window
  sizes tuned for a specific traffic shape. Default `balanced` matches
  what the 2026-05-31 1-hour soak validated (p50 11 ms, memory plateau
  150-220 MB across 150 K requests).
- **Sidecar release pipeline finally green.** `Publish StyloBot Extensions`
  has been failing on every push since at least `allbot-v6.8.3` (May 25)
  -- the bundled `linux_arm64` `protoc` in `Grpc.Tools 2.80.0` segfaults on
  the `ubuntu-22.04-arm` runner, taking the release-attach step down. Fix
  installs system `protobuf-compiler` and points `Grpc.Tools` at it via
  `-p:Protobuf_ProtocFullPath=/usr/bin/protoc`. The first-ever
  `extensions-v*` GitHub Release shipped at `extensions-v7.0.0-alpha2`
  with all 7 sidecar binaries.

### Added

- **`feat(perf): Kestrel profiles for API / site / high-risk / balanced`** (`110fb92a`)
  -- `STYLOBOT_PROFILE` env var (or `--profile` CLI flag) picks one of four
  presets. Each tunes ThreadPool min, MaxConcurrentConnections, KeepAlive,
  body size, slowloris caps, and HTTP/2 window. Logged at startup. See
  `docs/perf-profiles.md` for parameter table + recommended pairings with
  detection action-policies.
- **`perf(console): explicit Kestrel limits + ThreadPool warm-up`** (`8023555e`)
  -- cold-start "unexpected EOF" floods cut from 252 → 7 over the first 5 s of
  a 50 RPS ramp; failure mode under sustained overload becomes "fast refuse
  with TCP RST" instead of "queue, time out, EOF". See
  `docs/perf-pass-2026-05-31.md` for the full fast-path analysis.
- **`feat(bdf): harvest endpoint + archetype-from-bdf converter`** (`a7c24300`)
  -- closes the loop for empirically-derived archetype YAMLs. Debug-gated
  `/api/v1/bdf/harvest` streams persisted BDFs as NDJSON, tagged with
  verdict + self-declared-bot. Console subcommand `archetype-from-bdf` groups
  by label and writes IdentityArchetypeYaml files (frequency-as-confidence
  mask). Deploy with the flag on, real visitors hit the site, curl the
  endpoint, run the converter, the YAMLs go into
  `Definitions/IdentityArchetypes/` and get embedded into the next build.
- **`feat(identity): register IFingerprintStore FOSS default binding`** (`171fe088`)
- **`feat(data): NullSignatureCentroidStore so commercial hosts keep Sqlite out`** (`95c425f3`)
- **`feat(data): Null implementations for 7 more FOSS Sqlite-backed stores`** (`fffbe9f8`)
- **`arch: WriteBehindLfuStore<TKey,TValue,TWriteOp> base + revert diag header`** (`0ade0ee1`)
- **`arch(dashboard): gateway owns the cache, remote hosts read via REST`** (`1b064e4c`)
- **`tools: scripts/test-aot-on-maxo.sh for SQLite-AOT soak/load on a Win test box`** (`32d4b181`)

### Changed -- identity / fingerprint matching

- **`fix(matcher): revert to Priority 6 -- P1 biased aggregate toward human`** (`af54fbe0`)
  -- `0301a5de` had moved the matcher to Wave 0 so the dashboard could render
  a fingerprint shape for verdict-cached visitors instead of the
  "Calibrating" spinner. The unintended side effect: the matcher's verdict
  signals (archetype kind, cached score, client type) landed in Wave 0
  before the bot-flagging detectors did. For Chrome-with-missing-headers
  scenarios the early human-leaning match dragged the aggregate to 0.05
  against the scenario-expected 0.6. Reverted. The proper fix splits early
  fingerprint allocation from late verdict-signal emission and is tracked
  separately.
- **`fix(naming): only use archetype name when archetype is human-browser`** (`664b2600`)
  -- adds `SignalKeys.IdentityArchetypeKind` alongside the existing name
  signal. The composer requires `kind == "human-browser"` before using the
  archetype as the visitor's display name. Real Chrome visitors previously
  rendered as e.g. "Mastodon Family (header drift)" because the
  nearest-archetype matcher picked up a bot-family centroid by header
  coincidence.
- **`fix(identity): re-derive fingerprint name when the centroid flips its classification`** (`7e2fc388`)
  -- a fingerprint's display name was assigned once and never re-derived,
  while `IsBot` is recomputed every detection. A fingerprint that drifted
  human kept its "Googlebot" name and rendered as a human row.
  `FingerprintAbsorptionService` now clears the persisted display name on
  type-change; `SignatureAggregateCache` accepts a changed name on a
  bot↔human flip (authoritative rename).
- **`refactor(identity): extract IFingerprintStore so commercial can swap the matcher write path`** (`3c48448e`)
  -- 11 consumers (matcher contributor, absorption/drift/calibration
  services, brute-force anchor, cluster service, AI-opinion, BDF replay,
  LlmResultSignalRCallback) now depend on `IFingerprintStore` instead of
  the concrete `SqliteFingerprintStore`. Approval token TTL is now
  configurable (default 24h).
- **`refactor(identity): make CentroidSequenceStore + AssetHashStore backend-agnostic`** (`4390d43a`)
  -- both took a SQLite connection string and hardcoded `SqliteConnection`.
  They now take a `Func<DbConnection>` factory and use provider-agnostic
  ADO. FOSS injects a SQLite factory; commercial points them at PostgreSQL.
  No SQL or logic change.
- **`fix(dashboard): kill 'Calibrating fingerprint' on signature-detail page`** (`064ff530`)
- **`fix(dashboard): drop the degenerate archetype-origin overlay from the radar`** (`0b2f423c`)
- **`fix(dashboard): make the fingerprint radar readable for L2-normalized centroids`** (`61d58523`)

### Changed -- persistence / SQLite ↔ Postgres decoupling

- **`refactor(cluster-store): extract IClusterStore for commercial Postgres swap`** (`d9e829ba`)
- **`refactor(licensing): extract ILicenseGraceStore for commercial swap`** (`23848029`)
- **`fix(dashboard-remote): read-only RemoteRouteNameStore so the viewer has zero persistence`** (`cf2c2d5b`)
  -- remote viewer dashboards no longer create `dashboard.db`. Friendly
  route names are config-driven on the gateway and surfaced via
  `GET /api/v1/routes`.
- **`fix(broadcast): cache update + beacon BEFORE _next, DB persist fire-and-forget`** (`6230db2b`)

### Changed -- performance

- **`perf(detection): stop synchronous IP-enrichment stalling cold-IP requests`** (`2f8ac8b6`)
  -- every request from a fresh external IP was blocking ~18-25 s on
  synchronous IP enrichment. `AsnLookupService.QueryDnsTxt` called
  `Dns.GetHostEntryAsync` on the unresolvable `*.cymru.com` name (blocks
  the system resolver with no effective timeout) and then discarded the
  result and did a raw UDP query anyway. Removed the dead blocking call,
  added a 2 s overall lookup budget. `IpApiGeoLocationService` now
  negative-caches failures (5 min) and drops HTTP timeout 10 s → 2 s.
- **`perf(dashboard): stop the broadcaster hammering Postgres; serve SSR from cache`** (`054e8cde`)
- **`perf(dashboard): gateway-side cache for Summary/Detections/TimeSeries/TopBots/Threats with idle-skip`** (`5badca1c` -- reapplied after `02f5247b` revert)
- **`fix(api): /api/v1/detections cache fast-path for signature-scoped limit=1`** (`fe7848aa`)

### Changed -- gateway / verdict headers

- **`fix(gateway): TrustAllForwardedProxies actually trusts the proxy (any-network entries)`** (`39e42d08`)
- **`fix(gateway): emit X-Bot-Detection-* on the proxied request`** (`4892e817`)
- **`fix(gateway): emit full verdict header set on proxied request`** (`5913fb6a`)
- **`fix(bot-detection): seed PrimarySignature on verdict-cache short-circuit`** (`da9b7f4a`)
- **`fix(bdf): register Bdf services in AddStyloBotApi so gateway-hosted endpoint resolves`** (`6c9dd26b`)

### Changed -- dashboard (event-store-source-of-truth)

A series of fixes routes dashboard view components through the event store
instead of the volatile aggregate cache, so remote-mode viewer dashboards
render the same numbers the gateway sees:

- **`fix(dashboard): Top Bots / Live Activity reads through event store`** (`e35378c9`)
- **`fix(dashboard): SbTopBotsViewComponent reads through event store too`** (`b1be651a`)
- **`fix(dashboard): SbVisitorListViewComponent also reads through event store`** (`4cbbf986`)
- **`fix(dashboard): Visitors tab reads through event store`** (`c5a7cb77`)
- **`fix(dashboard): YourDetection looks up signature via hydrator path too`** (`b58e5626`)
- **`fix(dashboard): audienceFilter=all extends GetTopBotsAsync past is_bot`** (`b6e4f935`)
- **`fix(dashboard): correct Top Bots counts + Your-Detection event-store fallback`** (`69f2f35e`)
- **`fix(dashboard): periodic SignatureAggregateCache refresh for remote-mode hosts`** (`2ff0fc4d`, after `7709371c` revert)
- **`fix(dashboard): stop bailing when MultiFactorSignatureService is absent`** (`3806fff9`)
- **`fix(dashboard-remote): pass signature filter through /api/v1/detections`** (`efdfb070`)
- **`fix(dashboard): "active bot signatures" link uses & not ? to join filter param`** (`66186663`)
- **`fix(home-hero): never render 0% from a default; hydrate from event store in remote mode`** (`94aace71`)
- **`fix(home-hero): rename shadowed primarySig variable (build fix)`** (`4ac14787`)
- **`fix(dashboard): exclude dashboard endpoints by path, not the whole local network`** (`ff9eb859`, reverted in `ed51e6ea`)

### Fixed -- CI / release

- **`fix(ci): work around Grpc.Tools 2.80.0 linux_arm64 protoc segfault`** (`7aad0755`)
- **`fix(ci): revert to native arm64 runner + system protoc instead of x64 cross-compile`** (`5d3da21e`)
- **`fix(tests): align with SQLite-decoupling + archetype-kind name gate`** (`a5c39e94`)
  -- 14 compile errors and 6 runtime failures across `BotDetection.Test`
  fixed: SQLite-connection-factory migration in
  `AssetHashStoreTests` / `CentroidSequenceStoreTests` /
  `AssetHashMiddlewareTests` / `ContentSequenceContributorTests`, and the
  archetype-kind signal additions in the Priority-2
  `DeterministicBotNameTests`.

### Misc

- **`fix(perf): keep MinRequestBody/ResponseDataRate caps (relaxed, not disabled)`** (`4b791328`)
- **`diag(hydrator): X-Sb-Hydrator-Saw response header`** (`b91c97e2`)
- **`docs(perf): match doc snippet to actual MinDataRate values`** (`6f9e4908`)
- **`chore: stop-before-build in test-aot driver + clarify perf-profiles RSS estimates`** (`e9cde18f`)

### FOSS sizing

Ceiling soak (50 → 100 → 200 → 400 → 800 RPS in 10-min plateaus) on the
standalone win-x64 AOT gateway with default `balanced` profile, detection
ON. **246,403 requests served, 99.3% success, 660 k k6 iterations dropped
at the upper plateaus** because the gateway refused the excess at TCP.
Full table + per-deployment sizing in `docs/foss-sizing-2026-05-31.md`.

| Target | Effective | Memory | Per-deployment fit |
|---|---|---|---|
| up to 20 RPS | ~20 | ~150 MB | Pi 4 (2 GB), nano VPS |
| 20-50 RPS | ~50 | 200-260 MB | Pi 4 (4 GB), $5-10/mo VPS |
| 50-100 RPS | ~100 | 500-600 MB | 2 vCPU / 2 GB VPS |
| 100+ RPS | **~100 (per-process ceiling)** | 700-800 MB | scale horizontally, profile up to `site`, or commercial Postgres |

Above ~100 RPS the gateway absorbs the extra at Kestrel without crashing,
running out of memory, or queueing badly. The cap is single-writer SQLite
sessions.db + ThreadPool growth-rate equilibrium at ~807 in-flight
handlers.

### Known issues carried into 7.0.0

- **`Publish Stylobot Binaries → Linux .deb to Cloudsmith`** returns
  `403 Forbidden`. Credential / namespace issue, not a code fix.
  `CLOUDSMITH_API_KEY` needs rotating. Main GitHub Release attachment
  still succeeds.
- **9 Puppeteer integration tests** in `Mostlylucid.BotDetection.Orchestration.Tests`
  reference `/bot-test` which no longer exists in the Demo. Pre-existing
  rot (since the `cd550610` src-reorg, 2+ weeks before 7.0). Run the suite
  with `--filter "Category!=Puppeteer"` to skip them.

## [6.8.8] - 2026-05-26

Edge-injected TLS fingerprint forwarding through Caddy. When stylobot sits behind a TLS-terminating reverse proxy, the proxy-to-origin hop's TLS context is not the client's -- 6.8.8 wires the canonical forwarding header set end-to-end so the contributor sees the *client's* TLS shape, not Kestrel's.

### Added: Caddy Go SDK `ExtractTLS()` (`92c8c0e`)

The Caddy SDK (`sdk/caddy/headers.go`) gains an `ExtractTLS(*http.Request)` that returns `*sb.TLSInfo` with:

- `Version` and `Cipher` from `r.TLS` directly -- Caddy terminates TLS so these are free.
- `JA3` from `X-JA3-Hash` and `JA4` from `X-JA4` (or `X-JA4-Fingerprint`) when an upstream Caddy plugin has computed them. The Go stdlib doesn't compute JA3/JA4 itself.

Returns `nil` when the request did not arrive over TLS (so detection can still distinguish "TLS terminated upstream of Caddy" from "no TLS at all").

### Added: dual-name TLS headers on the contributor

`TlsFingerprintContributor` now reads **both** the documented public names *and* the legacy nginx-shaped names it shipped with, so docs and code agree regardless of which convention the operator wired at the edge:

| Signal | Documented (new) | Legacy (still accepted) |
|--------|------------------|-------------------------|
| TLS version | `X-Client-TLS-Version` | `X-TLS-Protocol` |
| Cipher suite | `X-Client-TLS-Cipher` | `X-TLS-Cipher` |
| JA3 | `X-JA3-Hash` | (same -- single-name surface) |

This is the source of truth alignment that the in-flight Caddy SDK + `DetectionBroadcastMiddleware` cross-references in `docs/REVERSE_PROXY_SIGNALS.md`.

### Added: BDF replay carries TLS-forwarding headers end-to-end

`SyntheticHttpContext` + `BdfReplayEndpoints` now propagate the edge-injected TLS headers through the replay pipeline. Two new fixtures pin the behaviour under `DetectionPolicy.Default`:

- `test-suites/bots/14-curl-with-tls-forwarding.bdf.json`
- `test-suites/humans/fp-05-firefox-linux-with-tls-forwarding.bdf.json`

Both run as part of `BdfReplayTests.Integration` -- a regression in the forwarding path breaks the integration suite.

### Added: detector-registration coverage test

`DetectorRegistrationCoverageTests` is a DI wiring guard. If a contributor is added to the YAML manifests but never registered (or vice versa), the test fails the build -- closes the "silent disabled detector" failure mode.

---

## [6.8.7] - 2026-05-26

Scoring fix: declared bots now read correctly on the dashboard. Plus an in-flight cleanup of two pre-existing test regressions surfaced while verifying the change.

### Fixed: declared-bot probability is categorical, not probabilistic

Known-bot UAs (Googlebot, Mastodon, MJ12bot, generic `python-requests`, etc.) were rolling up through the sigmoid + 0.90 AI-clamp + coverage-throttled confidence path. A clean Googlebot ended up looking like "70% bot at 0.4 confidence" -- which is the wrong framing: **nobody pretends to be a bot**. If a UA self-declares as automation the bot/human verdict is categorical, not a guess. The remaining question lives on a separate identity axis.

`DetectionLedgerExtensions.ToAggregatedEvidence` now applies a declared-bot override when `SignalKeys.UserAgentIsBot == true`:

- `BotProbability` is pinned to **1.0** -- bypasses the 0.90 non-AI clamp.
- `Confidence` becomes the **identity axis**: 1.0 once any verification (`friendly.ip_verified`, `friendly.domain_verified`, or `verifiedbot.checked`) has run (positive *or* negative -- a confirmed spoofer is also a confident identity judgement), 0.5 while the UA is still merely declared.

Verified-good early-exit (`CreateEarlyExitResult`) and the friendly-pin RiskBand logic (`DetermineRiskBand`) are unchanged -- both still set probability/confidence to 1.0/1.0 for verified Googlebot / Bingbot / fediverse instances. The override only fires on the path *between* "UA self-declares" and "rDNS / NodeInfo confirmed". See [`docs/declared-bot-scoring.md`](src/Mostlylucid.BotDetection/docs/declared-bot-scoring.md) for the full semantics and the four regression-pinning tests in `DefaultPolicyAndCoverageTests`.

Dashboard effect: a Mastodon signature without `friendly.*_verified` wired flips from "~0.7 probability capped at 0.90, ~0.4 confidence" to a clean **1.0 / 0.5**. The moment NodeInfo or vendor-IP verification fires, confidence pins to **1.0 / 1.0**.

### Fixed: home YourDetection radar empty for quorum-exit visitors (`852c972`)

The home YourDetection radar was empty for every visitor whose orchestrator quorum-exits before wave-30 `SessionVectorContributor` runs - i.e. every clearly-human visitor on first paint. The live `SessionStore` cache stays empty in that case, and the persisted fallback was broken (loaded the persisted session row but never projected from its stored vector). Restored the fallback: project directly from `SqliteSessionStore.DeserializeVector(latest.Vector)` into the 12-axis clock when no live session is in cache. Steady state for the home card becomes "2-min-stale at worst" via `SessionAtomizerService`.

### Fixed: red palette overload on dashboard (`852c972`)

Red on the dashboard meant two different things at once. Reserved red exclusively for the **danger** semantic; type column + sparkline updated to use a non-red palette so a glance at the row no longer conflates "this is a bot" with "this is dangerous".

### Fixed: stale `ContentSequenceContributor` priority assertion

`ContentSequenceContributor` was moved from priority 4 to priority 6 so `RequestMarkovClassifier` could read `TransportProtocol`'s `signalr` / `upgrade` / `protocol_class` signals (previously SignalR negotiates were misclassified as `PageView`). The YAML manifest, xml-doc summary, and the `Priority_ReturnsFour` test were left behind -- updated all three to match the actual priority 6.

### Fixed: stale `Compose12Axes` placement assertion

`19abf2b` reorganised the 12-axis behavioural clock into four contiguous quadrants (Footprint / Surface / Cadence / Signal) so a visitor paints a single fat lobe rather than scattered spikes. The `Compose12Axes_places_each_source_at_its_clock_hour` test still asserted the pre-quadrant interleave. Production code is the source of truth -- the test now pins the quadrant layout.

---

## [6.8.2] - 2026-05-25

Hotfix release that unblocks the brownfield retrofit story. Two changes: the gateway always starts even without a configured HMAC key, and a new `--origin-tunnel` flag lets stylobot reach a private origin through Cloudflare Tunnel with zero public exposure on the backend.

### Added: `--origin-tunnel <private-hostname>` -- brownfield retrofit's second tunnel

Stylobot now bundles the second cloudflared instance the [brownfield retrofit](../docs/brownfield-retrofit.md) needs. Before 6.8.2 the fully-private-origin shape (the "old box has zero public exposure" story) required the operator to manually launch a separate `cloudflared access tcp` on the stylobot host. Now it's one flag.

```bash
stylobot 5080 --origin-tunnel oldsite.tunnel.example.org --tunnel <ingress-token>
```

What happens:
1. Stylobot picks a free loopback port at startup (let's say 47891).
2. Launches `cloudflared access tcp --hostname oldsite.tunnel.example.org --url localhost:47891` as a sidecar.
3. Sets its own upstream to `http://localhost:47891`. From stylobot's point of view it's just a normal proxy hop.
4. The `--tunnel <ingress-token>` flag still handles the public ingress side (Tunnel A).

Result: neither the stylobot host nor the legacy host has any inbound port open. Both speak outbound 443 to Cloudflare only. Reaches the three-step retrofit narrative without manual cloudflared juggling.

Precedence: an explicit `<upstream>` argument (positional, `--upstream`, or `DEFAULT_UPSTREAM` env) wins over `--origin-tunnel`. Operators who supply both get a warning, and the explicit upstream is used so a misconfigured retrofit can't silently divert production traffic.

`OriginTunnelLauncher` (new) parallels the existing `CloudflaredTunnelLauncher`: same cloudflared-presence check, same log routing, same "exited within 5s = warn loud" diagnostic. No new dependencies.

### Fixed: production mode no longer refuses to start without a configured HMAC key

Before 6.8.2, `stylobot <port> <upstream> --mode production` *terminated* at startup if `SignatureLogging:SignatureHashKey` was missing or held the default placeholder -- an operator-hostile wall for the brownfield retrofit (the canonical "I just installed stylobot, what do I do" path).

Now the validator (`Mostlylucid.BotDetection.Console.Helpers.ConfigValidator.ResolveHmacKey`) generates a fresh 32-byte random key in memory for this process, logs a loud warning naming the trade-off (signatures don't survive a restart -- visitors look new on restart; dashboard search-by-signature misses), and continues.

The security goal (no shared default key across deployments) is preserved -- each process generates its own unique key. Operators who care about cross-restart signature continuity set `SignatureLogging:SignatureHashKey` explicitly via env var / appsettings / Key Vault. `stylobot genkey` still emits a fresh value for that purpose.

`ConfigValidator.ValidateHmacKey(config, mode)` remains as an `[Obsolete]` shim for backward compat (it's a void method now that `SignatureLoggingConfig.SignatureHashKey` is init-only). New callers should use `ResolveHmacKey(configuredKey, mode)` and feed the returned value into the config initializer.

---

## [6.8.0] - 2026-05-24

The 6.8 line lands the policy-grammar consolidation in three behaviour-preserving phases, one user-facing default flip, and a dashboard surface for the new state. Out of the box: **block malicious bots, rate-limit search and AI bots, leave humans untouched, slow bots harder when the origin slows down.** No "detect but do nothing" by default any more. See [`policy-defaults.md`](src/Mostlylucid.BotDetection/docs/policy-defaults.md) for the full per-`BotType` map + the 6.7 -> 6.8 migration recipe.

### Added: `PolicyIntent` grammar -- phase 1 (`1dda2c4`)

`PolicyIntent` (Pass / Block / RateLimit / Throttle / Challenge) sits one layer above `ActionType`: action type is "which class is wired up", intent is "what the operator is trying to do". Many-to-one mapping -- `block`, `block-hard`, `block-soft` all carry `Block`.

- `IActionPolicy.Intent` property with a default implementation that derives from `ActionType` -- existing third-party policies keep working without touching them. The five built-in classes declare their intent explicitly so the concrete type also exposes it.
- `PolicyState` + `PolicyFiringStats` records and `IPolicyStateProvider` contract for the dashboard read model.
- `RegistryPolicyStateProvider` baseline that walks the registry. Phase 1 reports empty stats; phases 2 and 4 fill them in.
- 13 new tests pin the `ActionType` -> `Intent` mapping and the per-class declarations.

### Added: `RateLimitActionPolicy` primitive -- phase 2 (`145b658`)

Real token-bucket rate limit (not delay-based throttle) keyed on `SignalKeys.PrimarySignature` (default) or remote IP. Bucket overflow delegates to a composable `OverLimitAction` resolved through the registry -- typo falls back to bare 429 + `Retry-After: 60` so a config slip doesn't open the gate.

- `ITokenBucketStore` + `InMemoryTokenBucketStore` (lock-free CAS-based, per-(policy, identity) isolation, fail-open on misconfig).
- Four built-in policies registered alongside the existing throttle/block set:
  - `rate-limit-search` (60 req/min, burst 10 -> `throttle-status`)
  - `rate-limit-ai` (10 req/min, burst 2 -> **`block-soft`** -- AI scrapers ignore `Retry-After`, so the overflow path bounces harder than search)
  - `rate-limit-social` (30 req/min, burst 5 -> `throttle-status`)
  - `rate-limit-monitoring` (6 req/min, burst 2 -> `throttle-status`)
- `RegistryPolicyStateProvider` exposes the rate-limit params (`requestsPerMinute` / `burstSize` / `overLimitAction` / `keyBy`) so the dashboard renders real numbers instead of just policy names.
- 33 new tests (token-bucket math, key isolation, OverLimitAction routing including the missing-fallback path, built-in registration, param surfacing).

### Added: adaptive scaling -- phase 4 (`f9ac9b6`)

Origin-aware bot rate limits. When the upstream slows down (P95 latency rises) or starts erroring (5xx rate climbs), every `RateLimitActionPolicy` scales its effective `RequestsPerMinute` by the active degradation tier's `BotMultiplier`. Humans don't traverse rate limits, so they're untouched -- this is what operationalises "prioritise humans" from the plan.

- **`DegradationAtom`** (salvaged from PR #16): rolling exponential-moving-average tracker for `response.error_rate_5xx`, `response.rate_429`, and `response.latency_ema`. ~60s effective window, configurable. Background timer decays stale buckets every 5s so a quiet period doesn't poison the average forever.
- **`HysteresisTracker`** (salvaged from PR #16): "true for N seconds" gate. A single-request 5xx spike never halves bot allowance -- the tier transition is dwell-gated.
- **`AdaptiveScalingTracker`** + **`AdaptiveScalingOptions`**: reads the EMA signals on demand, picks the worst tier whose threshold is exceeded *and* whose dwell has elapsed, returns the current multiplier + tier name + time-in-tier. Recovery is asymmetric: coming back up multiplies by `RecoveryMultiplier` (default 0.8) per evaluation so we don't snap straight back to baseline on a single healthy sample.
- **Default tier ladder** (override in `BotDetection:RateLimit:AdaptiveScaling:Tiers`):
  - `nominal` -- P95 < 500ms AND 5xx < 1% -- multiplier 1.0
  - `degraded` -- P95 >= 1000ms OR 5xx >= 3% -- multiplier 0.5 (halve bot allowance)
  - `critical` -- P95 >= 2000ms OR 5xx >= 10% -- multiplier 0.1 (10% of nominal)
- **`RateLimitActionPolicy.ExecuteAsync`** consults the tracker; effective RPM is `RequestsPerMinute * CurrentMultiplier` (floored at 1).
- **New response headers** when adaptive scaling is active: `X-RateLimit-Tier` (the degradation tier name) and `X-RateLimit-Multiplier` (so operators can trace why the effective limit is below configured). `X-RateLimit-Limit` always reports the effective value, not the configured one.
- **`DegradationAtom.RecordResponse`** hooked into `BotDetectionMiddleware` via `HttpResponse.OnCompleted` -- every upstream response (status + latency) feeds the rolling averages, regardless of detection path.
- **`RegistryPolicyStateProvider`** surfaces `currentMultiplier` and `effectiveRequestsPerMinute` on every rate-limit policy state so the dashboard chip renders the *live* numbers, not just the configured ones.
- 13 new tests (dwell gate, tier transitions, recovery curve, multiplier propagation through `X-RateLimit-Limit`/`X-RateLimit-Tier` headers, hysteresis damping of flap cases).

Adaptive scaling defaults to `Enabled: true`. Set `BotDetection:RateLimit:AdaptiveScaling:Enabled = false` to lock all rate-limit policies at their configured RPM regardless of origin health.

### Changed: default policy posture -- phase 3 (**USER-FACING**)

The 6.7 default was "detect everything, do nothing": `BlockDetectedBots = false`, `DefaultActionPolicyName = null`, `BotTypeActionPolicies` covered only 2 of 11 `BotType` values. 6.8 ships a full default policy map and routes everything through it.

- **`BotTypeActionPolicies` default covers every `BotType`:**
  - `MaliciousBot` / `ExploitScanner` / `ClickFraud` -> `block-hard`
  - `Tool` -> `throttle-tools`
  - `Scraper` -> `throttle-aggressive`
  - `AiBot` -> `rate-limit-ai`
  - `SearchEngine` / `GoodBot` / `VerifiedBot` -> `rate-limit-search`
  - `SocialMediaBot` -> `rate-limit-social`
  - `MonitoringBot` -> `rate-limit-monitoring`
  - `Unknown` is intentionally omitted; falls through to `DefaultActionPolicyName`.
- **`DefaultActionPolicyName` default** is now `"throttle-stealth"` (was `null`). Visible bots that escape per-type routing get silently slowed rather than hard-blocked. Override to `"block"` for strict default-deny.
- **`ObserveOnly` opt-in flag** replaces the implicit `BlockDetectedBots = false` posture as the canonical calibration-mode knob. When set, every action policy that would have fired is shadowed through `logonly` instead -- the dashboard still records *which* policy would have fired (via `AggregatedEvidence.TriggeredActionPolicyName`) but the visitor sees no behaviour change. Log lines on the shadow path are tagged ` [observe-only shadow]`.
- **`BlackboardOrchestrator` consults `BotTypeActionPolicies` directly** (was only consulted in the fallback path in the middleware). Per-type routing now fires in the main flow, taking precedence over the hard-coded friendly-bot soft-throttle fallback. The fallback remains as a safety net for operators who clear the map in config.

**Migration:** if you were running observe-only via `BlockDetectedBots = false` (the 6.7 implicit default), set `BotDetection:ObserveOnly = true` to keep that posture. To revert to "detect but do nothing", set both `BlockDetectedBots = false` AND clear `BotTypeActionPolicies` + `DefaultActionPolicyName`. The legacy `BlockDetectedBots` / `MinConfidenceToBlock` / `AllowVerifiedSearchEngines` flags are `[Obsolete]` and will be removed in v7.

15 new tests pin the default mapping and the registry-cross-check (every value in the default map must be a registered built-in policy).

### Added: policy detail tab -- phase 5 (`404e0bc`)

New `policy` tab in the investigate tab strip (between `honeypot` and `geo`). Reads from `IPolicyStateProvider` so the data pipe was already there in phase 1; phase 5 just renders.

- **Header chip**: observe-only badge when calibration mode is on, otherwise the default-fallback policy name.
- **`BotType` -> policy grid**: every `BotType` in plan order shows its mapped policy, intent badge (colour-coded by `PolicyIntent`), and (for rate-limits) the *effective* req/min. Unmapped types flagged red so a config typo doesn't hide silently behind `DefaultActionPolicyName`.
- **Per-policy cards**: rate-limit policies first (the new stuff), each showing intent + tier badge + effective RPM with a muted `(configured x multiplier)` breakdown when adaptive scaling is below 1.0. Burst, key-by mode, and `OverLimitAction` rendered as monospace metadata.

### Fixed: per-`BotType` routing fires in the main middleware flow (`61baabe`)

End-to-end campaign against the FOSS demo surfaced a regression: phase 3 added per-`BotType` consultation to `BlackboardOrchestrator`, but the FOSS demo runs `EphemeralDetectionOrchestrator` (default service registration). Result: every detected bot was hitting `DefaultActionPolicyName` (`throttle-stealth`), bypassing the per-type map the 6.8 defaults are built around.

Fix is a single middleware chokepoint -- `BotDetectionMiddleware`'s main-flow fallback now consults `BotTypeActionPolicies` before `DefaultActionPolicyName`. Covers both orchestrators: `BlackboardOrchestrator` pre-populates `TriggeredActionPolicyName` (block is a no-op), `EphemeralDetectionOrchestrator` leaves it null (block resolves the per-type policy). Mirrors the lookup that's been in `HandlePostDetectionActionsAsync` (line 1742) all along; the main flow was the missing site.

Verified live with the BDF + k6 + curl campaign: `curl/8.6.0` -> `throttle-tools`, `Scrapy/2.11.0` -> `throttle-aggressive`, with log lines now carrying `(type=Tool)` / `(type=Scraper)` instead of falling through to the default.

### Docs

- New [`policy-defaults.md`](src/Mostlylucid.BotDetection/docs/policy-defaults.md) -- canonical "what stylobot does out of the box" reference.
- [`configuration-reference.md`](src/Mostlylucid.BotDetection/docs/configuration-reference.md) refreshed: `BotTypeActionPolicies` default table, `DefaultActionPolicyName` default flipped, new `ObserveOnly` + adaptive-scaling sections.
- [`action-policies.md`](src/Mostlylucid.BotDetection/docs/action-policies.md) gained a "Rate-Limit Policies (6.8+)" section between Throttle and Challenge.
- [`policy-system.md`](src/Mostlylucid.BotDetection/docs/policy-system.md) built-in names list expanded.

---

## [6.7.7] - 2026-05-24

Hotfix release. Single commit (`864a4af`):

- **`fix(data)`**: re-enable 8 data sources that were silently flipped from `Enabled = true` to `false` as drive-by edits in `3c28bdc` ("`fix(ui): update partial view paths missed in namespace move`", 2026-04-19). `stylobot setup` reported *No patterns fetched from any source, using fallback patterns* on every install because every gate was short-circuiting. Restored: `IsBot`, `AwsIpRanges`, `GcpIpRanges`, `CloudflareIpv4`, `CloudflareIpv6`, `BrowserVersions`, `ScannerUserAgents`, `CoreRuleSetScanners`. The three that legitimately default to `false` (`Matomo`, `CrawlerUserAgents`, `AzureIpRanges`) stay off.

---

## [6.7.6] - 2026-05-24

Two-commit consolidation that gives the honeypot subsystem a category enum and rewires the dashboard intent label to read from it. No behavioural change to detection -- catalog matches, blocks, exemptions, and rate-limiting work exactly as they did in 6.7.5; the win is structural (single source of truth) and operator-facing (a colour-grouped Category chip on the Honeypot tab).

### Added: `HoneypotCategory` enum on every catalog entry (`6052f0f`)

`Mostlylucid.BotDetection/Honeypot/HoneypotPathDefinitions.cs` was a flat pair of `Tier1 -> FrozenSet<string>` and `Tier2 -> FrozenSet<string>` collections that the rest of the codebase had to re-classify with parallel `StartsWith` chains to figure out what kind of scanner intent a path implied (credentials theft vs config leak vs webshell upload, etc.). 12 files duplicated the knowledge.

This release restructures the catalog around a single `_catalog` array of `(Tier, Category, Paths)` tuples; the two tier sets and the per-category sets are derived from it.

- **`HoneypotCategory` enum** -- 14 values (`Credentials`, `Config`, `VersionControl`, `Database`, `Webshell`, `Admin`, `Debug`, `Backup`, `Metadata`, `PathTraversal`, `BuildArtifact`, `ApiDoc`, `Cgi`, `Cms`) plus `None` as sentinel.
- **`ClassifyDetailed(path)`** returns `ClassificationResult(Tier, Category, Pattern)` -- one catalog lookup yields everything the dashboard, the rate limiter, the holodeck, and the threat report need. The bare `Classify(path, out matched)` overload is preserved for older call sites.
- **`CategoryForPattern`**, **`GetPathsByCategory`**, **`GetAllPaths`** helpers so consumers can render and filter by category without re-deriving the catalog.
- The `SuspiciousExtensions` fallback (`.sql`, `.bak`, `.pem`, `.sqlite`, `.ini`, `.log`, ...) is now category-tagged too, so an arbitrary `*.sql` hit reads as `Database` and an arbitrary `*.pem` as `Credentials`.
- **52 new tests** assert every Tier 1 + Tier 2 entry has a non-`None` category, `GetPathsByCategory(Webshell)` returns the expected set, and the back-compat `Classify(..., out)` overload still works. Full honeypot suite goes 92 -> 144 passing.

Reference doc at [`src/Mostlylucid.BotDetection/docs/honeypot-catalog.md`](src/Mostlylucid.BotDetection/docs/honeypot-catalog.md).

### Added: dashboard intent label driven by `HoneypotCategory` (`39c7fa0`)

The Honeypot tab's "Why" column used to derive its intent chip from a 45-line `IntentForPath` switch in `SqliteDashboardEventStore` -- `if (path.StartsWith("/.aws") || path.Contains("credentials") || path.Contains("id_rsa")) return "credentials theft";` and 7 other branches like it. Adding a category meant editing two places (the catalog *and* the heuristic) and hoping they didn't drift.

- **`SqliteDashboardEventStore.LabelForCategory(category, tier)`** is now a public, category-keyed lookup table: `Credentials -> "credentials theft"`, `VersionControl -> "version-control exposure"`, `Metadata -> "metadata SSRF probe"`, etc. The `None` sentinel falls back to a tier-derived default ("always-honeypot" / "probable scanner").
- **`HoneypotHitRow.Category`** is now part of the dashboard model so future filter/group surfaces (Threats widget, endpoint-detail page) can use the enum directly.
- **Honeypot tab** (`Views/StyloBot/Dashboard/_InvestigateHoneypot.cshtml`) renders a new `Category` column with a colour-grouped chip: red for `Credentials`, orange for `Config`/`VersionControl`, yellow for admin/database/debug/backup, purple for webshell/path-traversal/metadata, grey for the rest. Each row carries `data-category` so client-side filters can hide categories without a server round-trip.
- **16 new tests** pin the label table -- screenshots and runbooks survive across versions, and `EveryEnumValue_HasANonEmptyLabel` catches a new enum value added without a label entry.

### Notes

- Behaviour-preserving: existing chip text matches the old `IntentForPath` output for every catalog entry. Full honeypot suite 160/160; full project suite 2130/2130.
- The wider consolidation (Haxxor YAML category lists, `ResponseCoordinator` defaults, `EndpointRiskClassifier`, inline references in `SignatureToBdfMapper` / `HeuristicFeatureExtractor` / `ThreatIntelContributor`) remains in [`docs/deferred/scanner-path-catalog-consolidation.md`](docs/deferred/scanner-path-catalog-consolidation.md) -- step 1, step 2, and the dashboard slice of step 5 are now marked done.

---

## [6.7.5] - 2026-05-23

Follow-up to the pre-launch hardening pass. Three substantive additions on top of 6.7.0: a fediverse-domain corroboration channel for the friendly-pin gate (so a Mastodon stampede on arbitrary cloud IPs can be cleared without a spoofable-UA shortcut), a Mostlylucid.Notify-backed auth email pipeline (replaces the bespoke `StyloBotSmtpEmailSender` MailKit code with three RazorSlices templates), and a third stage of the dashboard investigate redesign that promotes the signature-card pattern across Endpoints / Detections / Geo. Plus the usual sweep of risk-band ergonomics, single-source cache fixes, and licensing-gate plumbing.

### Added: fediverse NodeInfo verification (`FediverseDomainContributor`, priority 5)

The 6.7.0 friendly-pin gate required `friendly.ip_verified = true` before it would downgrade a friendly-classified UA to its calm risk band. That works for SearchEngine / GoodBot / VerifiedBot where the vendor publishes IP ranges, but fediverse software (Mastodon, Pleroma, Misskey, Akkoma, Firefish, Sharkey, etc.) runs on arbitrary cloud IPs by design -- there is no range to match -- so 50-instance card-preview stampedes were dropping straight through to behavioural scoring and getting labelled scanner-shaped. (`154dea5`)

ActivityPub defines the cross-corroboration channel: `/.well-known/nodeinfo` on every conformant instance is a discovery doc pointing at a machine-readable software descriptor. If a UA carrying `+https://instance/` resolves to a NodeInfo naming real fediverse software, the UA is corroborated by a non-UA channel (an outbound HTTPS GET against the claimed domain). Same trust level as IP-range verification, just a different proof method.

- **`IFediverseDomainVerifier` / `FediverseDomainVerifier`**: typed `HttpClient` with strict SSRF guards (no IP literals -- including `169.254.169.254` IMDS -- no `.local` / `.localhost` / `.invalid` / `.internal` / `.arpa` / `.test` / `.example`, https-only, `AllowAutoRedirect = false`, 3s per-hop timeout, 32KB max body, fediverse software whitelist). Two-step lookup (discovery → NodeInfo doc) with same-host requirement on the discovery link so a NodeInfo pointer can never be followed to a third party. 24h positive / 1h negative cache; in-flight de-dup collapses 50 concurrent visitors for one domain into ONE outbound request.
- **`FediverseDomainContributor`**: runs in the first wave alongside `VerifiedBotContributor`. UA prefilter + `+https://instance/` regex; calls the verifier; writes `friendly.domain_verified` (true / false / null) and emits a `DetectionContribution` row for the dashboard trace (negative weight on success, positive on failed verification so a spoofed Mastodon-claim UA reads as suspicious).
- **`SignalKeys.FriendlyDomainVerified`**: mirrors `FriendlyIpVerified` semantics. The gate in `DetectionLedgerExtensions.DetermineRiskBand` now reads "EITHER signal=true satisfies; either signal=false blocks; both null falls through to standard band calc". Trace strings name which channel fired: `fired:ledger+domain:Mastodon`, `fired:yaml+ip+domain:...`, `skipped:domain_check_failed`.
- **36-test SSRF guard contract** covers IP literals (incl. IMDS), reserved TLDs, malformed hostnames, length cap, normalisation.

### Added: Mostlylucid.Notify auth email pipeline

The dashboard's Identity surface (registration confirmation, password reset, MFA code) was using bespoke MailKit code in `StyloBotSmtpEmailSender`. This release routes it through `Mostlylucid.Notify` v0.1.1 with three typed RazorSlices templates so the auth email path matches the rest of the platform's notification pipeline. (`1fb67b1`, `583ebe5`, `83fa263`, `68e1179`, `2a837a3`, `ebe3ec9`, `937c73d`, `0ee1268`)

- **Three M0 templates** under `Mostlylucid.BotDetection.UI/Notifications/`:
  - `RegistrationVerifyEmail` (template id `registration.verify`)
  - `PasswordResetEmail` (template id `auth.password.reset`)
  - `MfaCodeEmail` (template id `auth.mfa.code`)

  Each has a typed model (`RegistrationVerifyModel`, `PasswordResetModel`, `MfaCodeModel`), a RazorSlices `.cshtml` for HTML rendering, and a plain-text fallback. Validity windows render natural-language ("2 hours" / "15 minutes") rather than raw timespan strings (`937c73d`).
- **`StyloBotSmtpEmailSender` is now a shim onto `INotificationSender`** (`68e1179`). Identity's `IEmailSender<StyloBotUser>` contract is preserved; each of the three typed callbacks maps to one of the three templates with no string-matching heuristic. The class is `[Obsolete]` -- callers should migrate to `INotificationSender` + typed models directly.
- **`StyloBotDevEmailSender` is `[Obsolete]`** with the replacement (`AddNotifyEmailLogging`) in the message (`2a837a3`).
- **`AddStyloBotSmtp(IConfiguration)`** wires the Notify pipeline (`AddNotify` → `AddNotifyEmail` → `AddEmailTemplate × 3`) alongside the existing `IEmailSender` registration. The library can't reach `IHost` itself; XML doc tells consumers to call `ActivateNotifyTemplates()` after `Build()`.
- **Hotfix: dropped the outbox + drain wiring** (`0ee1268`). Staging crash-looped with `Unable to resolve service for type 'Mostlylucid.Ephemeral.IEphemeralCoordinator' while attempting to activate 'Mostlylucid.Notify.Drain.EphemeralDrainStarter'`. Root cause: Notify v0.1's `EmailSender` does synchronous direct-send via MailKit and doesn't enqueue, so `AddNotifyOutboxSqlite` + `StartDrainOnCoordinator` were dead code that registered a drain starter requiring an `IEphemeralCoordinator` the host didn't pre-register. Hotfix removes both lines; direct-send keeps working. Outbox-backed retry will return in Notify 0.1.2+ once the library bootstraps its own coordinator.
- **Notify bumped 0.1.0 → 0.1.1** (`ebe3ec9`) for an IL2091 AOT annotation fix.

### Added: dashboard investigate redesign stage 3 (cards everywhere)

Stages 1 and 2 (`3777638`, `51b041c`) introduced the icon + colour-bar + badge + inline-SVG-sparkline pattern on the signature cards. Stage 3 (`cf6ba58`) rolls the same pattern across the three remaining tabs in the Investigation panel, replacing the old inline-CSS tables and text-heavy risk labels.

- **Endpoints**: method-pill (`GET` green / `POST` orange / `PUT`-`PATCH` blue / `DELETE` red) + path-category icon (ENV, Config, Admin, Backup, API, Auth, App) + 5-bar risk meter + bot-probability strip in place of the `37%` column.
- **Detections**: cards replace the table-with-hidden-tr expansion; the detail row is now an Alpine `$data` toggle directly on the card so the chevron rotates and keyboard tab order stays sane. Also kills a silent bug: the old inline switch matched lower-case `high` / `medium` / `low` against the canonical `VeryHigh` / `High` / `Medium`, so the colour never lit -- now goes through `BotDisplayHelpers.ColorForRiskBand` (single source).
- **Geo**: flag SVG + demonym + bot:human ratio strip (red/green split bar) replaces the `27 bots / 14 human` arithmetic. Risk bars only render when `DominantRiskBand` is non-null so empty rows don't show a stale meter.
- **New `BotDisplayHelpers` entries** (single-source so future relabels hit one switch): `IconForHttpMethod`, `ColorForHttpMethod`, `IconForPathCategory` (mirrors `CategorizePath`).

### Changed: risk-band gate (UA alone is never enough)

A short series of iterations on the friendly-pin gate that ended with the strict rule. (`5591647`, `2d13cd0`, `3ffeeef`)

- First the YAML friendly pattern was given override authority over `isConfirmedBad` so a recovering Googlebot wasn't pinned forever on a stale reputation row (`5591647`).
- Then walked that back to its current form (`3ffeeef`): a friendly UA match is necessary but never sufficient. Friendly pin REQUIRES `friendly.ip_verified == true` OR `friendly.domain_verified == true`. Three states each, both null = "no verifier in the pipeline yet, UA alone is not enough, fall through to standard band calc". `isConfirmedBad` still blocks the pin in all branches; the threat-score gate (`< 0.55`) still universal.
- Documented production reality: with no friendly-IP contributor wired yet (only `GoodBotIpRangeRefreshService` loading vendor ranges; no contributor emitting `friendly.ip_verified`), the new fediverse domain channel is the *only* corroboration source today, so the gate fires for fediverse traffic and falls through for everyone else. That's the correct default; risking false-Low pins on a spoofable signal erodes trust more than over-banding a real GoodBot.

### Changed: dashboard single-source for `risk_band` + `threat_band`

Same fingerprint was showing different risk bands depending on which surface the operator looked at: overview rendered `SignatureAggregateCache.RiskBand`; the signature-detail page on cache miss read `detections[0].RiskBand` straight from SQLite. The two could disagree any time a cache-eviction race landed between writes, and the operator saw an authority gap. (`99dd94e`, `58a6191`, `b4d04af`)

- **`SignatureAggregateCache` is now the only read source** for `risk_band`, `threat_band`, `bot_name`, `bot_type`, `bot_probability` across every dashboard widget. Cache is ephemeral / LRU-style; on miss it warms itself from the persistent store via `WarmFromDetections(signature, detections)` and the caller re-reads from cache. No surface reads SQLite directly for these fields.
- **Majority-vote band folding**: when warming an aggregate from N detection rows, `RiskBand` and `ThreatBand` fold by majority across rows (severity ties go to the higher band) so a single anomalous row can't flip the headline value. Bot name / type take the first non-empty across rows; per-request fields (Action, ProcessingTimeMs, Country, Narrative) take the freshest. Score history is rebuilt oldest → newest so sparkline reads left-to-right.
- **`VisitorListCache.GetFiltered`** now overrides `RiskBand` and `ThreatBand` from the aggregate at render time alongside the pre-existing `BotName` / `BotType` override, so the visitor card and the signature-detail page can't drift apart on these fields.
- **`RiskJustification` tracks the current band, not the historical one** (`58a6191`): ghost-campaign matches read from cache so a band downgrade actually reflects in the trace string instead of showing yesterday's justification.
- **Bot-name fallback to UserAgent signals** (`b4d04af`): when the ledger has no `BotName` set yet (early-life fingerprint), `DetectionLedgerExtensions` now falls back through `useragent.bot_name` → `useragent.family` → `useragent.client_name` so the visitor list never shows a raw signature ID.

### Changed: live-update animations use the View Transitions API

The previous OOB-swap animation racing was fragile: it depended on the `htmx-added` class landing before the browser painted, and dropped frames on slower hardware. (`36b5e24`, `de4676f`, `361f535`, `9917037`)

- **`SbLiveUpdatesTagHelper` drives the swap through `document.startViewTransition`** when the browser supports it. Falls back to the previous behaviour on older browsers (no flicker, just no animation).
- **`htmx.ajax` wrapper hooks the transition**, rather than racing the OOB syntax (`de4676f`): the wrapper schedules the transition around the response apply step, so the in / out animations always pair up across the full DOM diff instead of per-element.
- **SSR-vs-OOB row count + flicker on swap** (`9917037`): the SSR view-component's collapsed/expanded state now matches the OOB swap, so the panel doesn't flicker open-then-closed on page load.

### Changed: PR #24 -- millisecond-precision timing + flexible investigation filters

`Refactor time tracking and investigation filters` (`49641e3`). Two threads:

- **`ProcessingTimeMs` widened from `int` to `double`** across `BotDetectionResult`, the orchestrator chain (`BlackboardOrchestrator`, `EphemeralDetectionOrchestrator`, `ResponseDetectionOrchestrator`, `ReactiveDetectionOrchestrator`, `SignalDrivenDetectionService`), `BotDetectionService`, `WeightStore`, and `VersionAgeDetector`. Sub-millisecond timing was being floored to 0ms, hiding the real shape of the latency distribution. The dashboard renders rounded but the histogram bucket is now meaningful.
- **Investigation filter accepts flexible free-text input**: `InvestigationModels` + `SqliteDashboardEventStore` broaden the matching surface so an operator typing `mastodon`, `RU`, `/wp-admin`, or `163.172.` all narrow the list rather than requiring exact column matches. PostgreSQL store updated to parity (in `stylobot-commercial`).

### Changed: visitor-list naming pipeline deleted

The visitor list still carried a parallel regex-based naming pipeline (a 287-line `VisitorListCache` switch over a stack of friendly-name regexes) left over from before the 6.7.0 naming-pipeline collapse. It produced different names than `FingerprintNameComposer` did, so the visitor card and the signature-detail page could disagree even when their data sources agreed. (`153c84d`, `d90adff`)

- **`VisitorListCache` cut from 287 lines to 33** (`153c84d`). Naming comes from `SignatureAggregateCache` only; the regex pipeline is gone.
- **`FindFriendlyBotType` dropped** (`d90adff`). The detail-page model was carrying the helper plus a duplicate version of the same friendly-type lookup that already lived in `DetectionLedgerExtensions`. Now one path.

### Changed: licensing gate covers endpoint pinning

`Pin Endpoint` is a paid feature. 6.7.0 hid the UI on the FOSS build but left the POST endpoint open. (`f92333b`)

- **`EndpointsListModel` / `EndpointDetailModel` carry `IsCommercial`** sourced from `IsCommercialMode(context)` (same gate every other paid feature uses). The "Pin Endpoint" button + form in `SbEndpointsList` and the "Unpin" button in `_EndpointDetail` are hidden when false. Pinned/honeypot badges still render in FOSS so seeded sample data demonstrates the feature exists; only mutation is gated.
- **API handlers return `402 Payment Required`** on the FOSS build for `GET /api/endpoint-pins`, `POST /api/endpoint-pins`, `DELETE /api/endpoint-pins/{id}` -- closes the "hide UI but leave POST open" hole.

### Changed: navbar theme switcher trimmed to Dark / Light / System

`_StylobotNavbar.cshtml` carried six theme options inherited from the original Tailwind-themed marketing site. The actual styled themes were Dark + Light + System (everything else fell through to one of the three at render time). (`6fca30d`)

Now three options. Less indecision, no behavioural change.

### Fixed

- **`FediverseDomainContributor` missing using** (`f04efb0`): added `using` for `DetectionContribution` so the new contributor compiled cleanly on Release.
- **Razor build break in `_EndpointsCompact`** (`f83e435`): nested `@{}` inside an `else` block was rejected by the new Razor compiler.
- **Unreachable patterns in the friendly-pin trace switch** (`2d13cd0`): hotfix on top of the YAML override commit; switch arm ordering meant the explicit `(null, null)` arm was never reached.
- **Unescaped double quotes inside a verbatim-interpolated JS block** in `SbLiveUpdatesTagHelper` (`361f535`).
- **Activity-tab 50/50 split + chrome alignment** (`58b2e6f`): the two panels were rendering with different padding so the bottom border didn't align across the fold.

### Docs

No new doc files; surfaces updated as commit messages above. Operator-facing notes on the new `friendly.domain_verified` channel land in the next docs pass alongside the verifier configuration block.

## [6.7.0] - 2026-05-21

This release is the pre-launch hardening pass. The detection engine is feature-frozen; this round is about operator ergonomics (admin reload, edge-injected client signals), dashboard surface (compact metric strip, session detail full-page route, endpoint detail panel, theme picker), naming coherence (one canonical pipeline, no duplicate names across a fingerprint), and a pipeline quality sweep that removes dead code from 6.x prototyping. Default posture is observe-only with a pre-launch banner so operators can calibrate before flipping to hard block.

### Added: admin reload + restart endpoints

Operators can now apply config changes (action-policy weights, path policies, learning toggles) without redeploying. Two endpoints live under `/stylobot/admin/`:

- `POST /admin/reload`: reloads `IConfigurationRoot`. `IOptionsMonitor` consumers see the new values on their next read. No process restart, no traffic interruption. Returns `200 {"status":"reloaded"}`.
- `POST /admin/restart`: flushes the response and calls `IHostApplicationLifetime.StopApplication()`. The supervisor (Docker, systemd, launchctl) brings a fresh process up. Returns `202 {"status":"restarting"}`.

Off by default (preserves the small-surface posture). Fail-closed: `Enabled=true` with an empty `Token` returns `401` plus the exact config key to set, not an anonymous allow path. Bearer token compared in constant time, attempts logged at Warning with source IP. `BasePath` defaults to `/stylobot/admin` so existing reverse-proxy rules covering the dashboard already cover admin. See `docs/admin-endpoints.md`.

### Added: edge-injected client signals (Cloudflare Transform Rules + Sb-* fallbacks)

When the gateway sits behind a reverse proxy that terminates TLS, it only sees the proxy-to-origin hop's protocol/TLS/IP, so the Fingerprint Profile card on the dashboard would show `HTTP/1.1`, blank TLS version, etc. forever. The gateway now reads injected headers from the edge first and falls back to `HttpContext.Request.*` only when none is present:

- `X-Client-HTTP-Version` (also accepts `Sb-Http-Version`): real client HTTP version. Sourced from `http.request.version` on Cloudflare, `$server_protocol` on nginx, `{http.request.proto}` on Caddy.
- `X-Client-TLS-Version`, `X-Client-TLS-Cipher`, `X-Client-TLS-Ext-Sha1`: TLS handshake metadata. Cloudflare `cf.tls_version` / `cf.tls_cipher` / `cf.tls_client_extensions_sha1`.
- `X-Client-ASN`: edge-resolved ASN for datacenter detection.

Setup is a single Cloudflare Transform Rule (one rule, five dynamic headers). No code changes on the gateway side. Documented end-to-end in `docs/REVERSE_PROXY_SIGNALS.md` with Cloudflare, Caddy, nginx, and AWS ALB recipes.

Commercial CF Enterprise extension (in `stylobot-commercial`): four more headers (`X-Client-Bot-Score`, `X-Client-Verified-Bot`, `X-Client-JA3`, `X-Client-JA4`) surface CF Bot Management signals as `HttpContext.Items` keys for downstream contributors. The FOSS gateway ignores them when the plugin is absent.

### Added: pre-launch observe-only default

The Gateway image ships with `BlockDetectedBots = false` and `DefaultActionPolicyName = throttle-stealth` for the calibration window leading into RTM. Detection runs as normal; the response is delayed rather than refused. A pre-launch banner across the dashboard chrome ((130ebc0)) tells operators (and dashboard visitors on stylo.bot itself) that this site is observe-only while the engine learns real-traffic baselines.

To flip to hard block once you've calibrated, set:

```json
"BotDetection": {
  "BlockDetectedBots": true,
  "DefaultActionPolicyName": "block"
}
```

Pick one of `block` / `throttle-status` / `throttle-tools` / `challenge` depending on whether you want a 403, a polite 429+Retry-After, an exponential-backoff 429 (for curl/wget), or a CAPTCHA path.

### Changed: naming pipeline (one canonical path, one display name per fingerprint)

Six separate places used to compose display names. They drifted: the Razor card and the sessions list and the top-bots widget would each call a different helper and show three labels for the same fingerprint. This release collapses every naming path to one canonical pipeline owned by `FingerprintNameComposer`:

- `DescriptiveBotName` and display helpers extracted from Razor to `BotDisplayHelpers` with test coverage (`9d61e14`).
- Session BotName resolver extracted to `SessionEnrichmentExtensions`; sessions list now shows English bot names instead of raw signature IDs; falls back to the `dashboard_signatures` table on cache miss (`23f1ea4`, `d7aa277`, `02528fb`).
- Hard-coded friendly-bot list deleted; the names live in `bot-patterns.yaml` instead (`c843c98`, `c043503`).
- `(country:sigprefix)` no longer cramming itself into composed names (`35e4c83`).
- Verified-bot convergence: when multiple verified-bot rows resolve to the same canonical name, they collapse into one row at the data layer (humans + tools stay distinct) (`e0325a0`, `bac663f`).
- `Suspicious` is no longer applied to 1% bot-probability humans (`bac663f`).
- No-duplicate-names invariant enforced at render time in the top-bots widget (`0e55c43`).
- New: distinctive modifier on `FingerprintNameComposer` guarantees one display name per fingerprint id even when two visitors collide on user-agent string (`bdc9a84`).

End result: every signature row in the FOSS card clicks through to a signature detail page (`131c8d5`); groupable identities like Amazonbot collapse to one row in the Visitors list and on the endpoint detail "Most regular" table (`5080137`, `f35f810`).

### Changed: dashboard restructure (compact strip + map+chart on top + fewer tabs)

The dashboard header was two big cards taking half the fold. Now: a single compact metric strip across the top, then the world threat map and traffic chart equal-height side-by-side, then the tabbed surface with several legacy tabs removed (`a7ad7c1`, `24fb44a`, `dfa4fc4`).

- **Behavioural-shape radar in bot-detection-details** (`743b7e9`): the 8-axis projection from the 129-dim session vector is now shown on the detail card, not just on the session timeline.
- **Endpoint detail panel** (`7d60bdb`): per-endpoint response-time stats (min / avg / p95 / max) (`90981e6`), top visitors, recent activity. Razor comment that was leaking as literal HTML fixed (`4abe970`).
- **UA-version history** (`8d73760`): time-series of Chrome / Firefox / Safari major-version distribution sourced from existing detection data (no new ingest path).
- **Theme picker** (`b38bf04`): the dropdown actually applies themes now and the option labels are readable. One shared early-paint theme init across marketing + every FOSS-served page (`090bffc`) so the page doesn't flash light-then-dark on load.
- **Vendored flag SVGs** (`77230e8`): all 271 country flags ship locally; `flagcdn.com` removed from the network path (and from CSP, except a small allow for legacy paths during transition: `838dd0d`).
- **Detection details icons** (`222d5e6`): emoji icons replaced with boxicons for consistent rendering across OSes.
- **Pre-launch banner** (`130ebc0`): visible on every dashboard page.
- **RenderPage / RenderShell** (`e20ab22`, `9bb4f91`): host MVC integration; the dashboard header is theme-responsive and embeddable hosts can opt out of the shell.
- **`IOptions<StyloBotDashboardOptions>` registered** (`b1fe5b6`) so widgets see host config instead of defaults.

### Changed: session + endpoint detail are first-class routes

- **Full-page session detail route** (`62ed8cc`): `/_stylobot/sessions/{id}`; Behavioral Sessions rows in the dashboard click through.
- **Synthetic in-flight session view** (`b722fd3`): a session that hasn't been finalised yet still renders with its accumulated state.
- **Behavioral history reads in-flight sessions** (`5953f68`, `a33aadc`): the per-fingerprint history pulls from persisted detections rather than the in-memory accumulator, so a fingerprint that's still mid-session shows its current shape correctly.
- **Shared navbar** across signature + endpoint detail (`1984f50`, `823f3f2`); the partial broke once in the FOSS bundle context and was reverted+refixed (`971ea36`, `4804ab4`).
- **Sessions filter propagation** (`340c163`): drill-down preserves the parent filter.

### Changed: live-update arbitration (the user always wins)

The dashboard polls SignalR for live invalidations *and* lets the user filter/sort/page. The previous behaviour: an OOB swap from the background poll could clobber the user's filter selection mid-interaction. New rule: user-active widgets refuse OOB swaps; a cooldown absorbs late-arriving SignalR responses; user paging + filter + sort always wins over a background refresh (`15e8273`, `903dc67`, `410df77`, `65f38a5`, `7856a6d`).

- `COOLDOWN_MS` magic promoted to `StyloBotDashboardOptions` so it can be tuned per deployment (`6d163a5`).
- `data-href` click handler promoted to the `SbLiveUpdates` global instead of being re-attached per widget (`2b13c09`).
- Activity-tab SSR view-component matches OOB collapse state, so the panel doesn't flicker open-then-closed on page load (`0d14e0d`).
- Live-activity widget wired into the SignalR invalidation pipeline (`13e4cdb`).

### Changed: pipeline quality sweep

- **29 dead SignalKeys removed** (`1d215b6`): keys that no detector wrote and no consumer read. Reduces the noise in the blackboard contract.
- **Rate-limiter TOCTOU fixed** (`1d215b6`): the "is this signature over its budget" check and the budget update are now under one lock.
- **`Periodicity` + `IdentityChange` marked `IFoundationContributor`** (`e4f40fe`): both compute identity from the request rather than depending on prior detector output, so they must run unconditionally. Previously they were policy-gated and silently skipped on some paths. See `docs/architecture/signal-contracts.md` for the foundation contract.
- **`FingerprintMatchContributor` self-computes the identity vector** (`96abda2`) when the wave race elides the upstream signal: it no longer fails silently when `IdentityVectorContributor` lands after the matcher in a particular ordering.
- **`IdentityChange` signals deduped** (`1d215b6`): same identity-change event was being recorded twice on some paths.
- **Cache warm from DB on startup** (`8eba798`): `SignatureAggregateCache` pre-populates from the persisted `signatures` table instead of waiting for live traffic to repopulate it. Distinct-by-signature on warmup so a chatty source can't blank the cache (`862124c`).
- **`ExtractThreatScore` reads honeypot + attack signals** (`8c0a9f0`): previously only looked at the bot-typed signals. Now a CVE probe with no bot-typed contribution still scores correctly.
- **YARP evidence.Signals null-guard** (`7e9032a`): the YARP integration path no longer NREs when a detection arrives with an empty signal map.

### Fixed: dead code removal

- `_RecentActivity` partial + its route + the dispatcher case all removed; the Activity tab uses the canonical `sb-top-bots` widget instead (`e16ca40`, `da5ab94`).
- Dead `Unique()` wrapper deleted; `IsGroupableIdentity` is the single check (`3dab851`).
- 29 SignalKeys deleted (above).
- BDF replay rig at `Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs` runs under `DetectionPolicy.Default` and asserts on the foundation read surface so future contributor additions can't silently regress the dashboard.

### Docs

- New: `docs/admin-endpoints.md` covers the `/admin/reload` + `/admin/restart` surface end-to-end.
- New: `docs/REVERSE_PROXY_SIGNALS.md` covers Cloudflare Transform Rules + Caddy + nginx + AWS ALB recipes for injecting client TLS/HTTP/ASN signals; commercial CF Enterprise recipe documented.
- Updated: `docs/README.md` indexes the new operator docs.

## [Unreleased] - 6.5.1

### Added - Friendly clustering bots (`throttle-status` + per-instance naming)

The fediverse stampede problem: when someone shares a URL on Mastodon, 50 federated instances all hit the same URL within a second to render link previews. Each instance is well-behaved individually, but in aggregate it looks like an attack - and blocking with `403` makes them give up entirely. This release adds a polite path for that and similar friendly clustering bots (search engines, monitoring, GoodBot, VerifiedBot).

- **`throttle-status` action policy**. Fast (50ms) HTTP 429 response with `Retry-After: 60` header. The new `RetryAfterSeconds` option on `ThrottleActionOptions` decouples the advertised back-off interval from the per-request delay so a policy can return quickly and still hint at a meaningful wait. RFC 7231 integer formatting under invariant culture so the header is never `Retry-After: 60,0` in European locales.
- **Friendly-bot routing in the orchestrator**. When no other policy fired AND `PrimaryBotType` is in the friendly set (`SocialMediaBot`, `MonitoringBot`, `SearchEngine`, `GoodBot`, `VerifiedBot`) AND probability is over `BotThreshold` AND `ThreatScore < 0.55`, route through `throttle-status` instead of the default block/tarpit. The friendly set + threat-gate constant live in one place (`Models/BotTypeClassification.cs`) so the existing risk-band logic in `DetectionLedgerExtensions` shares the same source of truth.
- **Per-instance UA discriminator naming**. New `UserAgentDiscriminator` extracts the per-deployment hostname from the RFC 7231 `+https://host/` product-comment convention used by every fediverse server (Mastodon, Pleroma, Misskey, Calckey, Akkoma, Pixelfed, Lemmy, Friendica, Hubzilla, PeerTube). `FingerprintNameComposer` Priority 1 now produces `"Mastodon mastodon.social"` instead of one giant `"Mastodon"` pile - a stampede shows as N distinct signatures on the dashboard. The vendor-home skiplist (openai.com, facebook.com, google.com, etc.) lives in `Definitions/VendorHomeHosts/vendor-home-hosts.yaml` as an embedded resource; editing the list is a YAML change, not a code change.
- **`(!)` deceptive-bot marker**. When `VerifiedBotContributor` flags `verifiedbot.spoofed` (UA claims a verifiable bot identity but IP isn't in the published range) or `verifiedbot.rdns_mismatch`, the displayed name picks up the ` (!)` suffix (e.g. `Googlebot (!)`) so an operator scanning the dashboard sees the impersonation attempt immediately. Marker is a public constant on `FingerprintNameComposer.SpoofedMarker` for downstream filtering.

### Fixed - Native AOT runtime errors

Two bugs the published AOT sidecar binary hit on Mac during the 6.5.0 rollout:

- **`SimulationPackLoader` failed with `MissingMethodException` on `DictionaryFormatter<string,string>`**. `VYamlBootstrap` registered every closed-generic dictionary formatter SimulationPack needs except this one. Under AOT, `BuiltinResolver` fell back to `Activator.CreateInstance` on the unregistered closed generic, which has no parameterless constructor emitted by the ILCompiler because nothing statically referenced it. Added the missing registration.
- **`DetectorConfigProvider.MergeTiming` NRE on every detector trip → circuit-breaker opened after one second of traffic**. Under AOT, VYaml's source-generated deserializer only assigns properties whose keys are present in the YAML and bypasses the C# property initializer `= new()` that would otherwise default sub-defaults to non-null instances. A manifest without a `timing:` subkey left `DetectorDefaults.Timing` null, and `MergeTiming` threw on the first `yaml.TimeoutMs` access. Added `yaml ??= new()` to all four merge methods (Weights / Confidence / Timing / Features) and widened the parameter to nullable.

### Added - Extensions release (cross-platform binaries)

The three "extension" deployment binaries (`stylobot-sidecar`, `stylobot-ui`, `stylobot-all`) now ship as cross-platform self-contained binaries attached to a dedicated `extensions-v{version}` GitHub Release - separate from the main `allbot-v*` NuGet release so the NuGet release page isn't polluted with platform binaries.

- **`publish-extensions.yml` workflow**. Six runtimes per product (linux x64/arm64, win x64/arm64, osx x64/arm64), each as a self-contained archive with its own README and its own SHA256SUMS file (`stylobot-sidecar-SHA256SUMS.txt`, `stylobot-ui-SHA256SUMS.txt`, `stylobot-all-SHA256SUMS.txt`) so someone pulling only one product can verify without downloading the whole set. SLSA build provenance attestation via sigstore.
- **Native ARM runners for AOT cross-arch**. `stylobot-sidecar` is Native AOT (~37MB); the matrix uses `ubuntu-22.04-arm` and `windows-11-arm` so AOT compilation runs natively per-arch instead of cross-compiling (which would need a second toolchain per OS). Linux jobs `apt-get install clang zlib1g-dev` for the ILCompiler platform linker; macOS Xcode and Windows MSVC are preinstalled.
- **Sidecar Dockerfile fix**. The 6.5.0 `<PublishAot>true</PublishAot>` switch in the sidecar csproj started failing the Docker build with "Platform linker not found" because `mcr.microsoft.com/dotnet/sdk:10.0` lacks clang. Dockerfile now installs `clang zlib1g-dev` and drops `--platform=$BUILDPLATFORM` so buildx runs the SDK image natively under QEMU per target arch (no cross-toolchain needed).

## [Unreleased] - 6.5.0

### Added - Remote-mode dashboard (`stylobot-ui` as HTTP viewer)

`stylobot-ui` was previously a self-detecting binary that read its own local SQLite, which is the wrong design for a dashboard meant to be hosted inside a network as a viewer. This release reframes it as a config-driven REST client of a remote `stylobot` gateway: every dashboard read goes over `/api/v1/*` with `X-SB-Api-Key` auth, write paths are absent because the viewer never produces detections. `stylobot-all` stays single-process with local SQLite (correct topology for that binary), and the gateway gains an opt-in `--enable-api` flag so it can act as the data source.

- **`--enable-api` flag on `stylobot` gateway** (Console). Off by default (preserves the small-surface posture). When on: maps the full `/api/v1/*` REST surface + the SignalR invalidation hub at `/api/v1/hub` + runs `DashboardSummaryBroadcaster` and `DetectionBroadcastMiddleware` so the read endpoints serve real data. Fails fast at startup if no `StyloBot:ApiKeys` are configured.
- **Full REST parity for the dashboard.** Ten new endpoint files in `Mostlylucid.BotDetection.Api/Endpoints/`: clusters, labels, approvals, endpoint-pins, sessions, useragents/search, investigate (+ shape-search + presets), bdf export, config manifests, fingerprints (+ unabsorbed counts). All return existing store POCOs verbatim so the matching `Remote*` stores deserialise 1:1. Every response DTO + envelope variant registered in `StyloBotJsonContext` for AOT.
- **Three interface extractions for substitutability.** `IConfigEditorService` (was sealed `ConfigEditorService`), `IFingerprintReader` (was sealed `SqliteFingerprintStore`), `IBotClusterReader` (added to existing `BotClusterService`). Cluster + Config interfaces are async so the dashboard middleware awaits HTTP I/O instead of blocking thread-pool threads via `.GetAwaiter().GetResult()`. Concrete classes keep their sync methods for internal callers and add async overloads via `Task.FromResult`. Four middleware sites + the `SbIdentitiesListViewComponent` ctor switched to interface dispatch.
- **Eight `Remote*` store implementations** in `Mostlylucid.BotDetection.UI/Adapters/Remote/`: `RemoteDashboardEventStore`, `RemoteSessionStore`, `RemoteSignatureLabelStore`, `RemoteFingerprintApprovalStore`, `RemotePinnedEndpointStore`, `RemoteShapeSearchStore`, `RemoteFingerprintReader`, `RemoteConfigEditorService`, `RemoteBotClusterReader`. Shared `GatewayApiClient` typed `HttpClient` + `RemoteEnvelope<T>` deserialisation helper handle the HTTP plumbing. Write methods throw `NotSupportedException`; remote-viewer write paths never run because the concrete (writer) types aren't registered alongside.
- **SignalR live-feed relay.** `SignalRBeaconRelay` background service in `Stylobot.Ui` opens a `HubConnection` to the gateway's `/api/v1/hub`, sends `X-SB-Api-Key`, auto-reconnects, and forwards `BroadcastInvalidation` + `BroadcastAttackArc` beacons into the local `IHubContext<StyloBotDashboardHub>`. End-to-end: gateway detection → gateway hub beacon → relay → local hub → browser HTMX → `Remote*` store → gateway REST → render.
- **`AddStyloBotDashboardRemote(IConfiguration)` extension** wires the typed `HttpClient` (base URL + API-key header + `TimeoutSeconds`) and registers every `Remote*` impl ahead of the local `TryAddSingleton` fallbacks. Bound from `StyloBot:Source:Pull` (`Type: rest|local`, `Url`, `ApiKey`, `TimeoutSeconds`) and `StyloBot:Source:Live` (`Type: signalr|none`, `Url`).
- **`DashboardSourceType` + `DashboardLiveFeedType` enums** replace the previous `"rest"`/`"local"` magic strings.

### Added - New binaries

- **`Stylobot.Ui`** (`src/Stylobot.Ui/`) - dashboard-host product. Loopback bind by default (`http://127.0.0.1:5095`). Dockerfile, not packable, not AOT (Razor + SignalR + dynamic JSON need reflection). Prints the dashboard URL + the active mode (remote/local) at startup.
- **`Stylobot.All`** (`src/Stylobot.All/`) - YARP gateway + detection + dashboard in one process. Binds `0.0.0.0:8080`. ReverseProxy config in appsettings drives upstream routing; with no routes it still runs and self-monitors. Dockerfile, not packable, not AOT.

### Added - CLI ergonomics

- **`-d` / `--daemon` shorthand** for the existing `stylobot start` subcommand. Operators expect the standard CLI shape `stylobot <port> <upstream> -d` to fork to background; existing `start` subcommand still works. Wraps `DaemonCommands.Start` after stripping the flag.
- **`--output-config <file>`** dumps the effective `BotDetectionOptions` tree to disk in `appsettings.json` shape so operators don't have to grep the source for valid keys + default values. Loads the same config sources `WebApplicationBuilder` would (appsettings.json + appsettings.{Env}.json + env vars + CLI args). AOT-clean via dedicated `ConfigOutputJsonContext` source-generated `JsonTypeInfo`.

### Changed - Naming pipeline (humans get names too)

`DetectionLedgerExtensions.ResolveDisplayName` now falls through to `FingerprintNameComposer.Compose` when neither the matcher-set `identity.display_name` signal nor a ledger `BotName` is present. Keeps the "every visitor always has a name" invariant working when the metastable identity layer is off (`Identity:Enabled = false` default). Humans surface as `"Chrome on Windows (US:abcd)"` rather than null.

### Changed - CLI dashboard layout

`LiveDetectionTable` in `Mostlylucid.BotDetection.Console`: widened the fingerprint label column to 23 chars so composer-derived names fit; dropped the sparkline + risk-band columns (posterior colour already encodes the same signal); grew the list to fill available rows; collapsed the Config block to a one-liner.

### Changed - Native AOT path (sidecar 131MB → 36MB)

Replaced YamlDotNet (+ silently-broken `Vecc.YamlDotNet.Analyzers.StaticGenerator`) with **VYaml** across every YAML-loaded model (`DetectorManifest` family, `PipelineManifest`, `BotPatternFile`/`Entry`, `UaProfileFile`/`Entry`, `SimulationPack` family, `CompliancePack` family, `IdentityArchetypeYaml`). Each marked `partial` + `[YamlObject(NamingConvention.SnakeCase)]`; eight loaders rewired from `IDeserializer` to `YamlSerializer.Deserialize<T>(utf8Bytes)`; `ManifestYamlContext` deleted. `VYamlBootstrap` module initializer explicitly calls every `__RegisterVYamlFormatter()` (trimming would otherwise strip the methods the reflective `GeneratedResolver` looks up) and pre-instantiates `ListFormatter<T>` / `DictionaryFormatter<K, V>` closed generics our models use (defeats AOT-incompatible `Activator.CreateInstance + MakeGenericType` in VYaml's `StandardResolver`).

REST surface made AOT-clean independently: every endpoint in `Mostlylucid.BotDetection.Api` converted to `TypedResults<T>` with concrete `Ok<T>` / `Results<T1, T2>` return types so `RequestDelegateFactory` can statically resolve `JsonTypeInfo`. New `StyloBotJsonContext` covers every response type; `ConfigureHttpJsonOptions` inserts it ahead of the reflection resolver chain. `EnableRequestDelegateGenerator` wired on the API csproj.

Sidecar publishes at **37MB** AOT (down from 131MB self-contained-single-file), boots cleanly, serves `/api/v1/detect` and `/_sb/metrics/snapshot`. Console gateway with `--enable-api` publishes at 59MB AOT (8MB cost for the Api + UI transitive dependency).

### Fixed - Graceful gateway-unreachable handling

`GatewayApiClient.GetEnvelopeAsync` / `PostEnvelopeAsync` now catch `HttpRequestException`, `TaskCanceledException`, and `JsonException`; they log a warning and return default instead of bubbling HTTP 500 to the dashboard user. A gateway maintenance window shows empty panels with a logged warning, not a stack trace.

### Removed - `Mostlylucid.BotDetection.Demo` retained but no longer documented as the "dashboard host"

The Demo project stays as the dev test bench (controllers + test endpoints + mock LLM). Production dashboard hosting moves to the new `Stylobot.Ui` (remote viewer) or `Stylobot.All` (single-process) binaries - both ship with Dockerfiles and aren't NuGet packages.


## [6.4.7]

### Removed - ONNX text embeddings; clustering uses metastable centroids

The `OnnxEmbeddingProvider` (and its `IEmbeddingProvider` interface, `OnnxSetupResource`, and `EmbeddingOptions` config) is gone. It existed as a workaround for not having a real behavioural vector - embedded a hand-summarised text string (`RATE:42/min | PATHS:/wp-login,/.env | COUNTRY:RU | ...`) through `all-MiniLM-L6-v2` to fake similarity over numeric features we already had natively. With the metastable identity layer landing in this release, that workaround is strictly worse than the alternative: the per-fingerprint centroid is the actual learned shape, weighted by per-fp + global Fisher.

- **`BotClusterService`** now reads `fingerprints.centroid` via the new `SqliteFingerprintStore.GetCentroidsBySignaturesAsync` (single round-trip per cluster pass) and feeds the cosine of the centroid into the cluster similarity blend at the same weight the prior text-embedding axis used. Same Leiden algorithm, same blend formula - better vector. Falls back to heuristic-only similarity when Identity is disabled or a signature has no resolved fingerprint binding.
- **`ClusterOptions.EnableSemanticEmbeddings` → `EnableBehaviouralVectorAxis`**, **`SemanticWeight` → `BehaviouralVectorWeight`**. Defaults preserved (true / 0.4). `BotDetection:Embedding:*` config block is silently ignored - operators with old `Embedding` entries can delete them.
- **Packages dropped:** `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`. Native binary footprint reduction across rids; AOT path improves (these packages had known AOT trim issues).
- **Operator action:** if you'd downloaded the `all-MiniLM-L6-v2.onnx` model file (~90 MB), you can delete it. Cluster output may shift slightly because the input vector changed from text-embedding-of-summary to learned-behavioural-shape - this is an upgrade in fidelity, not a regression. UA-family clustering is preserved via the existing `UaFamily` categorical match boost (heuristically parsed from UA string).


### Added - Metastable fingerprint identity

A new identity layer that treats each visitor as a *shape* (a learned vector centroid + per-fingerprint weight vector + observation cloud) rather than a single hash. Replaces the load-bearing role of `PrimarySignature` (HMAC of IP + UA) for visitors whose IP or UA rotates. Reads `PrimarySignature` first as a fast L1 point lookup; falls back to a vector cosine search (L2) when the rotation guarantee doesn't hold. Dormant by default; flip on with `BotDetection:Identity:Enabled = true`.

The full design and contracts live in [`docs/architecture/fingerprint-match.md`](docs/architecture/fingerprint-match.md). User-facing reader version at [`identity-fingerprint-match.md`](src/Mostlylucid.BotDetection/docs/identity-fingerprint-match.md).

- **Two-pass match** - Pass 1 looks up `fingerprint_keys[primary_signature]` and runs a quick weighted-cosine confirm against the candidate's centroid (fast-path; humans pay microseconds). Pass 2 runs `IIdentityAnchorIndex.SearchAsync` over the centroid + observation set when L1 misses or fails confirm (slow-path; bots pay it). Pass 2 disagreement triggers a *correction*: per-fp weights nudge toward dims that distinguished the new winner, and `fingerprint_keys` re-binds.
- **Per-fingerprint weight learning** - every fingerprint carries its own dim-weight vector. Two learning signals: corrections (sharp edits when L1 was wrong) and stability (gentler nudges every absorption, based on per-dim deviation from centroid).
- **Centroid absorption (`FingerprintAbsorptionService`)** - folds detailed observations into the centroid via a maturity-weighted mean (`new = (centroid * maturity + obs) / (maturity + 1)`) so a year-old visitor's shape is preserved while detail compresses. Recomputes inferred client type against the archetype registry on every absorption; emits a structured drift log when classification flips.
- **Drift verifier (`FingerprintDriftService`)** - re-checks L1-confirmed fingerprints whose `cached_score_updated_at` is older than `CachedScoreTtlSeconds`. Closes the "L1 still observes" guarantee - a "passes-as-human" fast-path verdict cannot persist indefinitely without L2 agreement on the latest observation.
- **Calibration (`IdentityWeightCalibrationService`)** - periodically computes a global per-dim weight vector via the Fisher discriminant ratio (between-cluster variance / within-cluster variance) over fingerprints grouped by inferred client type. High-discriminating dims get amplified; noise dims suppressed. Same tick refines each archetype centroid by blending in the mean of its descendants (cap-bounded by `ArchetypeRefinementCap` so an archetype can never drift more than half its identity per cycle).
- **Global weights cache (`IdentityGlobalWeightsCache`)** - hosted singleton that reads the calibrated weights on every `GlobalRefreshSeconds` tick. The matcher composes them multiplicatively with per-fp weights at confirm + Pass 2 time. `Volatile.Write` atomic swap; live matching never sees a torn vector.
- **Archetype registry** - nine starter archetypes loaded from embedded YAML at `src/Mostlylucid.BotDetection/Definitions/IdentityArchetypes/*.yaml`. Used as cold-start templates for new fingerprints and as cluster labels for calibration. Self-refining - descendants pull their archetype's centroid toward the population mean.
- **`IFoundationContributor` wave** - `IdentityVectorContributor` (priority 5) composes the request vector from upstream signals and raw headers; `FingerprintMatchContributor` (priority 6) runs the two-pass match. Both are foundation: they run unconditionally under any policy, never gated by classifier filters.
- **Signal contract** - `identity.fingerprint_id`, `identity.match_score`, `identity.is_new_fingerprint`, `identity.is_correction`, `identity.rotation_candidate`, `identity.client_type`, `identity.client_type_confidence`, `identity.client_type_origin`, `identity.cached_bot_probability`, `identity.cached_risk_band`. All emitted by `FingerprintMatchContributor`; consumed by downstream display, the BDF rig, and `IdentityVerdictLookup` (the verdict-cache composition path).
- **Verdict cache composition** - when Identity is enabled, `SignatureVerdictGate` reads both the per-signature aggregate (sliding window, scoped to IP+UA) and the per-fingerprint cached verdict (scoped to the metastable identity, survives rotation). Fresher source wins. Skip-path responses set `X-StyloBot-VerdictSource: identity-cache` (vs. plain `cache`) when the fingerprint cache was the winner, and emit `X-StyloBot-IdentityFingerprint` with the resolved fingerprint id. Returning visitors whose IP+UA has changed inherit their prior verdict instead of paying for a fresh pipeline pass.
- **Dashboard "Identities" tab** - new tab listing every metastable fingerprint with the surface an operator needs to triage drift candidates: fingerprint id (short), inferred client type + confidence, total observation count, **unabsorbed observation count** (the freshness budget the next absorption tick will fold), correction count, cached bot probability + risk band, last verified, last seen, archetype origin. Sorted by unabsorbed-count desc so drift candidates float to the top. Two per-row actions: **Re-verify** posts to `POST /api/identities/{id}/reverify` and runs `FingerprintDriftService.VerifyOneAsync` on demand (skips the `CachedScoreTtlSeconds` gate, bumps `cached_score_updated_at`, returns the row HTML for HTMX in-place swap); **Run AI** posts to `POST /api/identities/{id}/run-ai` and invokes `IdentityAiOpinionService` (see below). Empty-state copy when `Identity:Enabled = false`.
- **`SqliteVecIdentityAnchorIndex` - vec0 perf path with brute-force fallback** - when [sqlite-vec](https://github.com/asg017/sqlite-vec) (`vec0.dylib`/`vec0.so`/`vec0.dll`) is available on the OS library search path (or at the path specified by `BotDetection:Identity:Engine:SqliteVecExtensionPath`), `SqliteFingerprintStore` auto-loads it at init, creates the `fingerprints_vec` and `observations_vec` virtual tables (centroid keyed by `fingerprint_id` TEXT primary key; observations keyed by integer `observation_id` with `+fingerprint_id` as a queryable aux column), and mirrors every `InsertFingerprintAsync` / `RecordObservationAsync` / `AbsorbObservationAsync` write into the matching vec0 row in the same transaction. KNN searches dispatch via `WHERE col MATCH ? AND k = ?` and translate vec0's L2 distance back to cosine (`1 - distance² / 2` for L2-normalised vectors) so scores stay parity with the brute-force engine. When the extension isn't installed, isn't loadable, or errors mid-flight, the index falls through to `BruteForceIdentityAnchorIndex` per-call - the FOSS package ships zero native dependencies and operators opt into the perf path by installing the binary themselves.
- **`IdentityAiOpinionService` - operator-triggered classifier on demand** - given a fingerprint id, builds a prompt summarising the fingerprint's metadata (inferred client type + confidence, observation count, centroid maturity, correction count, age, current cached verdict, archetype origin), sends it synchronously to the registered `ILlmProvider` (resolved by reflection so core takes no hard dependency on the optional Llm packages), parses the JSON reply, and writes the verdict back to `fingerprints.cached_*`. Returns a structured `IdentityAiOpinionResult` with one of `ok`, `identity-disabled`, `not-found`, `no-llm-provider`, `llm-not-ready`, `llm-error`, or `parse-error` so the dashboard can show exactly why a click was a no-op. The middleware forwards the status as `X-StyloBot-AiOpinion-Status`, the bot probability as `X-StyloBot-AiOpinion-Probability`, and the error detail (CR/LF-stripped, capped at 200 chars) as `X-StyloBot-AiOpinion-Detail`.
- **SQLite schema** - seven core tables in `fingerprints.db` (separate file from the main detection DB): `fingerprints`, `fingerprint_keys`, `fingerprint_observations`, `fingerprint_corrections`, `identity_dimension_weights`, `identity_archetypes`, `identity_vector_layout`. Vector layout version is fixed at deployment; mismatched layouts on startup fail loud rather than silently corrupt data.
- **Test coverage** - 19 identity unit tests (Fisher math, weight composition, drift verifier, calibration end-to-end, global weights cache) plus 17 BDF replay scenarios that probe the metastable contract: every request emits `identity.fingerprint_id`, the last request of each scenario doesn't allocate a new fingerprint.

### Added - Verdict cache (rolled forward from 6.4.6)

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

#### Customer Portal (stylo.bot)
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