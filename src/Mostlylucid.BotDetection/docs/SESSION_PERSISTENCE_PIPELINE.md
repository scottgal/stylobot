# Session Persistence Pipeline

**Handoff doc.** Everything you need to know to work on session persistence without re-reading the codebase.

**Status:** signal-driven pipeline landed on `main` (FOSS `529711ef` merge, commercial `bc04214` merge). The old CLR-event bridge is still physically present but dead — that's the follow-up.

---

## TL;DR

There are **two** session-related pipelines in the tree today. One is new, live, tested. The other is old, dead, still in the tree.

**New (live):** signal-driven, via `Orchestration.Sessions.SessionStore`. Aggregates per-fingerprint state during a TTL window; on eviction, a two-phase signal handshake gives subscribers a bounded deadline to persist an echo before the aggregate leaves memory.

**Old (dead):** CLR-event driven, via `Analysis.SessionStore` → `Data.SessionPersistenceService` → `IDetectionArchive.AddSessionAsync`. The `SessionPersistenceService` is not registered in DI anywhere in FOSS or commercial. Its CLR-event subscription never fires in production. `PersistedSession` rows with Markov vectors haven't been written for however long the DI reg has been missing.

---

## Architectural principles that hold

Do not violate these without a design conversation:

1. **Signals over CLR events.** New cross-atom communication uses `TypedSignalSink<T>`. CLR events are legacy.
2. **Blackboard IS the SignalSink.** No `ILogger` for inspection, no bare DB in middleware, no signal-as-state.
3. **Atoms hold state. Signals announce.** Atoms are the durable inspection point. Signals are the announcement channel.
4. **Sessions = TTL windows on a shared per-domain sink.** Not per-fingerprint (doesn't scale to millions of fingerprints per site).
5. **Shaped eviction, not FIFO/LFU.** Behavioural priority — ambiguous-still-learning stays longer, well-classified goes first under pressure. Honeypot always high priority.
6. **Universal write-behind.** Hot ConcurrentDictionary + bounded channel + single-reader drainer. SQLite can't take synchronous DB writes on the hot path.
7. **Two-tier persistence, single write.** Under back-pressure the record still writes, just with fewer optional fields. Priority determines *resolution*, not *whether to write*.
8. **Escalators write into sinks. Coordinators lazy-boot on first raise** via `AddOnInitSignal<T>` against `SessionStoreOptions.InitSignal`.
9. **Every knob is configurable.** No magic numbers. Options class lives at `SessionStoreOptions`.
10. **FOSS owns the detection engine.** Commercial layers Postgres, Redis, cloud LLMs, hot-reload, fleet, licensing. Detection is one product.
11. **Ack on failure.** Signal-response protocols ack even on write failure — otherwise a transient outage becomes a memory leak.

---

## The pipeline (live path)

```
Request arrives
  ↓
EscalateToSessionActionPolicy  (or manual test Upsert)
  Builds a SessionSample and calls SessionStore.Upsert(sample)
  ↓
Orchestration.Sessions.SessionStore
  Per-site partition: ConcurrentDictionary<fingerprintId, SessionAggregate>
  Aggregate merged with SessionAggregateMerge.Merge (rolling mean, honeypot count,
    upstream status distribution, RetentionPriority recomputed)
  Raises the merged aggregate on `Changes` sink
    signal key: SessionSignalKeys.AggregateUpdated ("session.aggregate.updated")
  ↓
Cleanup loop (RunSiteCleanupAsync, every CleanupInterval)
  Identifies eviction candidates:
    - Age: absoluteAge > MaxLifetime   → SessionFinalizeReason.MaxLifetime
           lastSampleAge > effectiveTtl → SessionFinalizeReason.Ttl
    - Pressure: over cap after age-out → SessionFinalizeReason.Pressure (lowest priority first)
  ↓
Phase 1 — Finalizing
  For each candidate:
    - Add pending-eviction latch to _pending dict, keyed by "siteId|fingerprintId"
    - Raise SessionFinalizingSignal on Lifecycle sink
      Payload: FingerprintId, SiteId, Aggregate, DeadlineUtc, Reason
    - Aggregate STAYS in the partition
  ↓
Phase 2 — Wait for acks
  Poll _pending latches every AckPollInterval (default 10ms)
  Latch complete when Interlocked count ≥ ExpectedAckCount (dedup by AckedBy)
  Return early if all latches complete
  Return anyway if effectiveDeadline (adaptive to pressure) elapses
  ↓
SessionEchoAtom (subscribes to Lifecycle sink)
  OnFinalizing → TryWrite payload to bounded channel (DropOldest, cap 512)
  Drainer (single-reader): reads channel, builds SessionEcho.From(signal, echoedAt),
    calls _echoStore.AddEchoAsync(echo)
  ALWAYS raises SessionFinalizedAckSignal, even on write failure
    Payload: FingerprintId, SiteId, AckedBy="SessionEchoAtom"
  ↓
SessionStore.Finalizations sink
  Ack handler updates _pending[key] latch (increment count, dedup by AckedBy)
  ↓
Phase 3 — Remove
  For each latch:
    partition.Aggregates.TryRemove(fingerprintId)
    _pending.TryRemove(key)
  ↓
DetectionArchiveEchoStore : ISessionEchoStore
  Delegates AddEchoAsync → IDetectionArchive.AddEchoAsync
  ↓
SqliteDetectionArchive
  INSERT INTO session_echoes (fingerprint_id, site_id, started_at, ended_at,
    sample_count, mean_bot_probability, max_bot_probability, latest_confidence,
    honeypot_hits, upstream_status_counts, dominant_client_type,
    retention_priority, finalize_reason, echoed_at)
  Row lands in the durable archive.
```

---

## File map

**New / built in this refactor:**

| File | Role |
|---|---|
| `Orchestration/Sessions/SessionLifecycleSignals.cs` | `SessionFinalizingSignal`, `SessionFinalizedAckSignal`, `SessionFinalizeReason` |
| `Orchestration/Sessions/SessionStore.cs` (rewritten cleanup loop) | Two-phase eviction; adds `Lifecycle` + `Finalizations` sinks + `_pending` latch dict + `PendingEviction` inner class |
| `Orchestration/Sessions/SessionStoreOptions.cs` (added knobs) | `FinalizeDeadline`, `MinFinalizeDeadlineUnderPressure`, `ExpectedAckCount`, `EmitFinalizingSignal`, `AckPollInterval` |
| `Orchestration/Sessions/SessionEcho.cs` | `SessionEcho` record, `ISessionEchoStore` interface, `NullSessionEchoStore`, `DetectionArchiveEchoStore` bridge |
| `Orchestration/Sessions/SessionEchoAtom.cs` | Subscribes to Lifecycle, drains via bounded channel, writes echo, acks |
| `Data/Schema/session_echoes.sql` | New table + 3 indexes (fp+time, site+time, reason+time) |
| `Data/SqliteDetectionArchive.cs` (added method) | `AddEchoAsync` — INSERT into `session_echoes` |
| `Data/NullDetectionArchive.cs` (added method) | `AddEchoAsync` no-op returns 0 |
| `Data/SessionPersistence.cs` (interface addition) | `IDetectionArchive.AddEchoAsync` |

**Renamed (Data.ISessionStore → Data.IDetectionArchive family):**

| Was | Now |
|---|---|
| `Data.ISessionStore` | `Data.IDetectionArchive` |
| `Data.SqliteSessionStore` | `Data.SqliteDetectionArchive` |
| `Data.NullSessionStore` | `Data.NullDetectionArchive` |
| `UI/Adapters/Remote/RemoteSessionStore` | `UI/Adapters/Remote/RemoteDetectionArchive` |
| Commercial `PostgreSQLSessionStore` | `PostgreSQLDetectionArchive` |

42 FOSS files + 11 commercial files touched by the rename. Zero behaviour change from the rename itself.

**DI wiring added in `Modules/BotDetectionModule.cs`:**

```csharp
// Detection archive — FOSS default is SQLite. Commercial Postgres pack
// replaces via TryAdd-loses. AddStyloBotDashboardRemote replaces with
// RemoteDetectionArchive for remote-mode dashboards.
services.TryAddSingleton<Data.IDetectionArchive, Data.SqliteDetectionArchive>();

// ISessionEchoStore → routes echoes through IDetectionArchive.AddEchoAsync
services.TryAddSingleton<Orchestration.Sessions.ISessionEchoStore,
    Orchestration.Sessions.DetectionArchiveEchoStore>();
services.AddOnInitSignal<Orchestration.Sessions.SessionEchoAtom>(
    Orchestration.Sessions.SessionStoreOptions.InitSignal);
```

`IDetectionArchive` itself was missing from FOSS DI before this — the interface was only registered by the remote-mode adapter and the commercial Postgres pack. This was a Step-7 casualty; fixed here.

---

## READ THIS BEFORE RE-ADDING SERVICE REGISTRATIONS

**Please don't grep for "missing" `TryAddSingleton` and add them back mechanically.** Step 7 (the contributor purge) dropped a lot of registrations, and a big chunk of this session's earlier work was going through and putting them back correctly. Some things you see unregistered *should* stay unregistered. Some things were re-registered under a different code path.

### Already re-registered this session (don't add duplicates)

If you see these referenced but not obviously registered — they're wired. Search `Modules/BotDetectionModule.cs`.

Infrastructure:
- `IScheduleCoordinator` → `ScheduleCoordinator` + `ScheduleCoordinatorWatchdog` (hosted)
- `IHttpContextAccessor` (via `AddHttpContextAccessor()`)
- `Func<System.Data.Common.DbConnection>` (SQLite factory)
- `AddPolicyDispatcher()` called inside the module
- `AddHttpClient()` + `AddMemoryCache()`
- `BotDetectionHostedSingletonsBootstrap` (hosted, eager-resolves all singleton subscribers)

Action policies:
- `IActionPolicyRegistry` → `ActionPolicyRegistry`
- Five `IActionPolicyFactory` impls (LogOnly / Block / Challenge / Throttle / Redirect)
- `EscalateActionPolicyFactory` (one factory keyed on `ActionType.Escalate`; reads `options["Target"]` = learning|session|llm to dispatch)

Manifest / config:
- `DetectorManifestLoader`
- `IDetectorConfigProvider` → `DetectorConfigProvider`

Store defaults (FOSS SQLite / null / config-driven variants; commercial packs Replace):
- `IDetectionArchive` → `SqliteDetectionArchive` **(this session)**
- `IBotListDatabase` → `BotListDatabase`, `IBotListFetcher` → `BotListFetcher`
- `IChallengeStore` → `InMemoryChallengeStore`
- `IFingerprintApprovalStore` → `NullFingerprintApprovalStore`
- `IPatternReputationCache` → `InMemoryPatternReputationCache`
- `IHoneypotExemptStore` → `ConfigHoneypotExemptStore`
- `IPathLifecycleStore` → `NullPathLifecycleStore`
- `IFingerprintStore` → `SqliteFingerprintStore`
- `IFingerprintBrowserModeStore` → `SqliteFingerprintBrowserModeStore`
- `IFingerprintPoolCollisionTracker` → `SqlitePoolCollisionStore`
- `ISignatureCentroidStore` → `NullSignatureCentroidStore`
- `IIntentCentroidStore` → `NullIntentCentroidStore`
- `ICveFingerprintMatcher` → `NullCveFingerprintMatcher`
- `IIdentityAnchorIndex` → `BruteForceIdentityAnchorIndex`
- `IThreatIntelCoordinator` → `ThreatIntelCoordinator` + 4 offline providers + refresh service (hosted)
- `IApiKeyStore` → `InMemoryApiKeyStore` (registered inside `AddStyloBotApi`)
- `IDetectionEventPublisher` → `NullDetectionEventPublisher`
- `DomainEntitlementValidator` (commercial — registered inside `AddStyloBotCommercialPlugin`)

Identity subsystem:
- `IdentityVectorLayout` / `IdentityVectorEncoder` / `EncoderResultCache` / `HeaderHashCollector`
- `IdentityArchetypeRegistry` / `IdentityGlobalWeightsCache`
- `IdentityProcessingCoordinator`
- Browser modes: `ModeCentroidCatalogue` / `ModeCentroidClassifier` (materialised at boot via `LoadAsync().GetAwaiter().GetResult()`) / `IBrowserModeResolver` → `CentroidBrowserModeResolver` / `IBrowserModeSeedSource` → `YamlBrowserModeSeedSource`

Services / support:
- `IBrowserVersionService` → `BrowserVersionService`
- `IDnsResolver` → `SystemDnsResolver`
- `IFediverseDomainVerifier` → `FediverseDomainVerifier`
- `VerifiedBotRegistry`, `ProjectHoneypotLookupService`, `UaProfileStore`, `CountryReputationTracker`, `ReactiveSignalTracker`
- `Services.SequenceContextStore`, `Services.CentroidSequenceStore`, `Services.EndpointDivergenceTracker`
- `Services.BotClusterService` (hosted)
- `Analysis.SessionStore` — legacy sliding-vector window (this **is** registered; SessionVectorAtom needs it)
- `Analysis.DeploymentNormTracker`
- `ClientSide.FingerprintPopulationTracker` + `IBrowserFingerprintStore` → `BrowserFingerprintStore`
- Similarity: `FeatureVectorizer` / `IntentVectorizer` / `IIntentSimilaritySearch` → `SlimIntentSearch` / `ISignatureSimilaritySearch` → `SlimSignatureSimilaritySearch`
- Legacy detectors still consumed by 4 atoms: `HeuristicDetector`, `VersionAgeDetector`, `BehavioralDetector`, `ClientSideDetector`
- `Orchestration.SignatureCoordinator` (+ options)
- `Orchestration.Atoms.WaveformHistoryStore` (IdentityChange surface-dim drift lookback now rides the bounded `IFingerprintStore` hot cache as ephemeral `SurfaceDims`, #16)
- `Dashboard.MultiFactorSignatureService`
- `Data.PatternReputationUpdater`
- `SimulationPacks.ISimulationPackRegistry` → `SimulationPackLoader`
- `Privacy.PiiHasher` (fixed FOSS-default key; operator overrides via own `AddSingleton`)

Session fabric:
- `SessionStore` (the new one, `Orchestration.Sessions.SessionStore`)
- `SessionAtom` + `SessionPersistenceAtom` + `SessionEchoAtom` all wired via `AddOnInitSignal` against `SessionStoreOptions.InitSignal`
- `ISessionEchoStore` → `DetectionArchiveEchoStore`

Learning fabric:
- `TypedSignalSink<LearningEvent>` (fires the init signal on first raise via `IInitSignalBus`)
- `ILearningCoordinator` → `LearningCoordinator` + `LearningBackgroundService` (both via `AddOnInitSignal`)

LLM classification:
- `TypedSignalSink<LlmClassificationRequest>` (init-signal aware)
- `LlmClassificationCoordinator` via `AddOnInitSignal`

Escalators (as `IActionPolicy` factories, keyed by `ActionType.Escalate`; per-instance via the combined `EscalateActionPolicyFactory`):
- `EscalateToLearningActionPolicy`
- `EscalateToSessionActionPolicy`
- `EscalateToLlmActionPolicy`

### Intentionally absent — DO NOT re-add without a design conversation

- **`Data.SessionPersistenceService`.** Dead code — see next section. Adding this back means "silently restart the CLR-event bridge without a design decision", which is exactly the thing you were brought in to think about.
- **`SessionPersistenceServiceLifecycleHost`** (companion type inside `SessionPersistenceService.cs`). Same story.
- **Old `IContributingDetector` / `BlackboardOrchestrator` / `BlackboardState`.** Purged in Step 7. Gone for good. If you see references in comments or docs, those are stale.
- **`SessionVectorContributor`.** Replaced by `SessionVectorAtom`. The old contributor is gone.

### The pattern that keeps catching people

A lot of "why isn't this registered?" cases in this codebase turn out to be:

1. **Step-7 casualty** — the delete-59-contributors commit accidentally dropped a `services.TryAddSingleton<T>()` alongside a contributor's registration. Fix: add it back to `BotDetectionModule`.
2. **Dead code that predates Step 7** — registration was already missing, nothing was calling `GetService` hard-required, and the tree kept compiling because the type was only pulled via `_services.GetService<T>()` (soft lookup) which returns null. Fix: don't add it back; delete the dead code.

Distinguishing the two: if a required-service ctor takes the type as non-nullable, or if a middleware pipeline takes it as a positional arg → Step-7 casualty, add it back. If only `GetService<T>()` (soft) references exist, or only test files reference it → probably dead code.

`SessionPersistenceService` is in the second category. Verify before you decide:

```bash
grep -rn "SessionPersistenceService" --include="*.cs" src/ | grep -v Test | grep -v "GetService<"
```

Should return zero hard references outside `SessionPersistenceService.cs` itself and one line in `BotDetectionHostedSingletonsBootstrap.cs` that uses the soft `GetService<>` (null-tolerant) form.

---

## What's dead (for follow-up)

`Data.SessionPersistenceService` (315 lines) is **never registered in DI** anywhere:

- FOSS: no `AddSingleton<SessionPersistenceService>` anywhere
- Commercial: same
- `BotDetectionHostedSingletonsBootstrap.cs` calls `_services.GetService<SessionPersistenceService>()` (soft lookup, returns null when unregistered)

So the CLR event `Analysis.SessionStore.SessionFinalized` fires on every session boundary, and nothing subscribes. `PersistedSession` rows never get written via that path. Any dashboard code that reads `PersistedSession.Vector` (5 sites in `StyloBotDashboardMiddleware.cs` — similarity search, radar projection, drift comparison) gets an empty vector or missing row.

Distinct question, three options — this is where you (the follow-up agent) come in:

1. **Delete the dead code.** `SessionPersistenceService` + its `SessionPersistenceServiceLifecycleHost` + related tests. `Analysis.SessionStore.SessionFinalized` CLR event too. Keeps `Analysis.SessionStore` itself (SessionVectorAtom still uses it for per-request Markov work).
2. **Wire it back up.** Register `SessionPersistenceService` in `BotDetectionModule` alongside the other hosted singletons. Restores the Markov-vector-to-archive pipeline. Doesn't unify architecture (keeps the CLR event).
3. **Migrate to signals.** Make `Analysis.SessionStore` raise a signal on `SessionFinalized`; new atom subscribes and writes to `IDetectionArchive.AddSessionAsync` (or extends the echo model to carry the Markov vector optionally under priority-gating).

Whichever path you take, do NOT silently drop Markov vector persistence — see `feedback_no_feature_cuts` in operator memory. The dashboard code that reads it is real; deciding "no one uses it in production" is the operator's call.

---

## What SessionVectorAtom does (context for the design choice)

`Orchestration/Atoms/SessionVectorAtom.cs` (priority 30, ConstrainerAtom) is the one live consumer of `Analysis.SessionStore`. Per request it:

- Calls `RecordRequestAsync(signature, sessionRequest, fpContext)` to add the current request to the per-signature sliding window
- Reads `GetCurrentSession(signature)` — request list for the current session
- Reads `GetHistory(signature)` — completed `SessionSnapshot` list for prior sessions
- Runs a bunch of analyses:
  - Partial-chain archetype match (3–4 requests, cosine sim vs `MarkovArchetypes.All`)
  - Current-session vector similarity vs history
  - Inter-session velocity + acceleration + fingerprint-rotation
  - Frequency rhythm + cross-session rhythm preservation
  - Voidness (HNSW-based)
  - Trajectory toward attack cluster

Each analysis raises named signals on the sink (`SignalKeys.SessionVelocityMagnitude`, `SignalKeys.SessionTopSimilarity`, etc.). The **live detection pipeline uses these signals** — this isn't dead code.

`Analysis.SessionStore` is genuinely used. Only the CLR-event *persistence* path is dead. The per-request Markov work SessionVectorAtom does needs somewhere to live.

If you're going with option 3 (migrate to signals), the design principle is: signals announce, atoms hold state. `Analysis.SessionStore` keeps holding state. You're only replacing the CLR event with a signal for the persistence trigger.

---

## Testing surface

Where to look for canonical test patterns:

- `test/Orchestration/Sessions/SessionStoreLifecycleTests.cs` — pins the two-phase eviction (finalizing signal, fast ack, deadline fallback, disabled-signal short-circuit)
- `test/Orchestration/Sessions/SessionEchoAtomTests.cs` — echo built + ack raised; failed-write still-acks; honeypot survives projection
- `test/Orchestration/Sessions/SessionEchoArchiveIntegrationTests.cs` — end-to-end through real SQLite temp DB
- `test/Orchestration/Sessions/SessionPersistenceAtomTests.cs` — the *other* session-persistence atom (shift-detection → IFingerprintStore), separate concern

Rules that apply:

- Tests hit a real SQLite temp DB, not mocks (per `feedback_no_mocks_for_db` in memory)
- Every test uses configurable options (no magic numbers)
- Ack-based synchronization, not `Task.Delay` sleep loops
- Boundary tests: TTL, MaxLifetime, Pressure, Explicit reasons

Full FOSS suite state on `main` after merge: 3900/3905 pass, 5 pre-existing skips. One occasional flake in `NullScheduleCoordinator_logs_warning_when_subscribed` (unrelated to this work; passes 4/4 in isolation).

---

## Merged commits (branch history)

Six commits landed via `unify-session-stores`:

| Commit | Summary |
|---|---|
| `ffb46f12` | signal-driven two-phase eviction on SessionStore |
| `2ed6825e` | (interleaved) verdict-correctness fixes on live atom path |
| `3b22a7ad` | SessionEchoAtom + ISessionEchoStore + NullSessionEchoStore |
| `edff9368` | (interleaved) BlockResponseGate honours per-endpoint BotPolicyAttribute |
| `8220c686` | rename Data.ISessionStore → IDetectionArchive (42 files, FOSS) |
| `a059c21d` | (interleaved) restore learning feedback-loop DI registrations |
| `279480d9` | route ISessionEchoStore → IDetectionArchive; SQLite persists echoes |
| `529711ef` | merge to main |

Commercial side:

| Commit | Summary |
|---|---|
| `6d8b4f6` | rename callers (11 files, commercial) |
| `16fe99d` | stub AddEchoAsync on PostgreSQLDetectionArchive |
| `bc04214` | merge to main |

---

## Things worth double-checking before assuming they still work

- **`Analysis.SessionStore` DI registration** — I added it back during the DI-audit sweep. It's used by SessionVectorAtom which is priority 30 in the live pipeline.
- **Commercial `PostgreSQLDetectionArchive.AddEchoAsync`** — logs and drops (no throw). Session_echoes migration in the Postgres pack is a follow-up. Don't ship echo-driven dashboard reads on Postgres yet.
- **`RemoteDetectionArchive.AddEchoAsync`** — throws `NotSupportedException`. Remote hosts run in read-only viewer mode. If a remote host somehow drives a SessionStore.Upsert, the resulting finalization ack will fail. Not a real scenario today but worth knowing.
- **`SessionSample.Path` and `SessionSample.RequestId`** — carried on the sample, aggregated into counts on `SessionAggregate`, dropped by the time the echo lands. The echo has status counts but not the path list. If you want paths in the echo (operators asked about "which endpoints did this session hit?"), the aggregate needs to grow a bounded path bag first.