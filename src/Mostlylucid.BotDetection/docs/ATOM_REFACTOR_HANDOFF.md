# The Contributor → Atom Refactor: Handoff

**Purpose:** if you're a fresh agent working on stylobot's detection engine and you don't have the context of the last few weeks of work, read this first. It's the umbrella architectural doc that ties together why the codebase looks the way it does and what the trap is if you start "fixing" things you don't understand.

**Related detailed docs (read in this order after this one):**
- `SINK_COORDINATOR_ARCHITECTURE.md` — the sink+coordinator ephemeral pattern the atom world uses
- `SESSION_PERSISTENCE_PIPELINE.md` — one concrete example of the pattern end-to-end
- `SessionSignatureEscalator_PatternDesign.md` — an earlier design note; superseded but useful for background

---

## TL;DR

There were two detection engines in this codebase. There is now one.

**Old (deleted):** `IContributingDetector` implementations shared state via `BlackboardState`. `BlackboardOrchestrator` iterated them in wave order. `BotDetectionMiddleware` (old) hosted the pipeline. Persistence, learning, and cross-request coordination happened via method calls and CLR events.

**New (current):** `IDetectorAtom` implementations write to a shared `SignalSink` (blackboard = sink). No orchestrator per se — `BotDetectionOrchestrator` composes atoms via DI. Escalators write to `TypedSignalSink<T>`. Coordinators subscribe. Session state lives on `Orchestration.Sessions.SessionStore` (shared per-domain, TTL windows, behavioural eviction).

**The migration finished at Step 7** (commit `1a8d2745`): the old world got deleted wholesale — 59 contributors, `BlackboardOrchestrator`, `IFoundationContributor`, `BlackboardState`, the old middleware, plus a bunch of helpers.

**What Step 7 didn't do:** it didn't clean up all the DI registrations that had been dropped along the way. Multiple sessions of follow-up work have been finding and re-adding them. That's the trap you'll fall into if you don't know this history — some services really did lose their registrations in Step 7 and need to come back; others are dead code that predates Step 7 and should stay dead.

---

## The two architectures side by side

```
OLD (deleted in Step 7)                     NEW (current)
────────────────────────                    ─────────────
IContributingDetector                       IDetectorAtom
BlackboardState (HttpContext.Items)         SignalSink (SignalKey / TypedSignalSink<T>)
BlackboardOrchestrator                      BotDetectionOrchestrator (DI-composed atoms)
Wave-based ordering                         Priority-based ordering (int Priority on the atom)
Direct method calls                         Signals + IActionPolicy
CLR events for cross-request state          TypedSignalSink events + IInitSignalBus for boot
Middleware/BotDetectionMiddleware (old)     Middleware/BotDetectionMiddleware (new — same name, atom-based)
IFoundationContributor / IReactiveDetector  IDetectorAtom taxonomy roles
ILearningEventBus                           TypedSignalSink<LearningEvent>
```

Same names in some cases, completely different implementations. **Don't grep for the type name and assume you're looking at the old world.** Look at the folder — everything under `Orchestration/Atoms/` is the new world.

---

## The atom taxonomy (from Mostlylucid.Ephemeral)

`IDetectorAtom : DetectorAtomBase` inherits from an Ephemeral taxonomy that assigns each atom a role:

- **SensorAtom** — observes raw input, emits typed signals. No opinion on outcome. (E.g. `TlsFingerprintAtom`, `Http2FingerprintAtom`.)
- **ExtractorAtom** — computes derived semantic units from raw content. (E.g. `IdentityVectorAtom`.)
- **ProposerAtom** — issues a candidate contribution toward the verdict. Most detection atoms live here.
- **ConstrainerAtom** — validates / constrains proposals. Priority 30–40 range. (E.g. `SessionVectorAtom`.)
- **RankerAtom** — priority-adjacent to Constrainer; ranks candidates. (E.g. `MultiLayerCorrelationAtom`.)
- **RendererAtom** — formats output for downstream sinks / responses.
- **CoordinatorAtom** — orchestrates dormant subscribers via init signals.
- **FeedbackAtom** — closes the loop; feeds outcomes back to learning.
- **EscalatorAtom** — hands off to a slower / more expensive lane (LLM, learning, session). Implements `IActionPolicy`.
- **GuardAtom** — hard safety / policy gates. (E.g. `FastPathReputationAtom`, `HoneypotLinkAtom`.)

The role isn't just naming — it's how the pipeline reasons about ordering, gating, and where signals should flow.

---

## The signal-first principle

Everything above `IDetectorAtom` communicates via signals. **No CLR events for cross-atom communication.** No direct method calls between atoms. Every atom emits to a `SignalSink` (the blackboard); atoms that need to react subscribe.

This is the principle that gets violated most often when people are "fixing" things. If you find yourself writing `atomA.SomeMethod(...)` from `atomB`, you're doing it wrong — atomA should emit a signal, atomB should subscribe.

**The signal-first principle also drives persistence.** No atom writes to a durable store directly. Instead:

1. Atom emits a signal (e.g. `SessionFinalizingSignal`).
2. A persistence atom subscribes to that signal.
3. The persistence atom does the durable write (via a `TypedSignalSink` bridge or a bounded channel to the store).
4. The persistence atom raises an ack signal so upstream can free memory.

See `SESSION_PERSISTENCE_PIPELINE.md` for a full worked example. Any new persistence work should follow this shape.

---

## What Step 7 actually deleted

Full list (from commit `1a8d2745`):

- `Orchestration/ContributingDetectors/` — 58 contributor files
- `Orchestration/IContributingDetector.cs` — the interface plus `ContributingDetectorBase` and `BlackboardState`
- `Orchestration/IFoundationContributor.cs`
- `Orchestration/BlackboardOrchestrator.cs`
- `Orchestration/EphemeralDetectionOrchestrator.cs`
- `Orchestration/DetectorAvailability.cs`
- `Orchestration/DetectionStatePool.cs`
- `Orchestration/IDetectionOrchestrator.cs`
- `Orchestration/ResponseAnalysisContext.cs`
- `Orchestration/ResponseDetectionOrchestrator.cs`
- `Orchestration/SignatureEmission.cs`
- `Orchestration/Manifests/ConfiguredContributorBase.cs`
- `Orchestration/Atoms/ContributingDetectorAdapter.cs`
- `Middleware/BotDetectionMiddleware.cs` — the OLD one (the atom-orchestrator middleware inherited the name)
- `Learning/LearningTriggers.cs`
- `Policies/PolicyEvaluator.cs`
- `Dashboard/DetectionRecord.cs`
- `Endpoints/BdfReplayEndpoints.cs`
- `Endpoints/PolicyEndpoints.cs`
- `Honeypot/{EndpointHistory,HoneypotLink}Contributor.cs`
- `SimulationPacks/CveProbeContributor.cs`
- `ThreatIntel/CveFingerprintContributor.cs`
- `Mostlylucid.GeoDetection.Contributor/{Geo,GeoClient}Contributor.cs`

Plus renames: the new `BotDetectionMiddleware` in `Orchestration/Atoms/BotDetectionOrchestrator.cs` took the middleware name from the old one that was deleted.

**None of these should come back.** If you find a reference in comments, doc strings, or old design notes, treat those references as stale.

---

## The DI-registration trap

Step 7 dropped a lot of `services.TryAddSingleton<T>()` calls alongside the contributors that used them. Multiple sessions of follow-up have been auditing and re-registering. **The default assumption when you find an unregistered service should be:**

1. Is it referenced by anything currently in the tree (grep `--include="*.cs" -r`) → yes: it's a Step-7 casualty, add it back to `Modules/BotDetectionModule.cs`
2. Is it only referenced by hard-required ctor injection (non-nullable ctor param) or middleware pipeline positional argument → yes: definitely need to re-register or the app can't start
3. Is it only referenced by soft `GetService<T>()` calls (null-tolerant) or by test files → probably dead code that predates Step 7; **don't add it back until you've decided**

### Services already re-registered this session (don't add duplicates)

See `SESSION_PERSISTENCE_PIPELINE.md` for the full list. Highlights:

Infrastructure: `IScheduleCoordinator`, `IHttpContextAccessor`, `IDetectionArchive`, `Func<DbConnection>`, `AddPolicyDispatcher()`, `AddHttpClient()`, `AddMemoryCache()`, `BotDetectionHostedSingletonsBootstrap`

Action policies: `IActionPolicyRegistry`, 5 built-in factories, `EscalateActionPolicyFactory`

Stores (FOSS defaults; commercial replaces): `IBotListDatabase`, `IBotListFetcher`, `IChallengeStore`, `IFingerprintApprovalStore`, `IPatternReputationCache`, `IHoneypotExemptStore`, `IPathLifecycleStore`, `IFingerprintStore`, `IFingerprintBrowserModeStore`, `IFingerprintPoolCollisionTracker`, `ISignatureCentroidStore`, `IIntentCentroidStore`, `ICveFingerprintMatcher`, `IIdentityAnchorIndex`, `IThreatIntelCoordinator` + 4 providers + refresh service, `IApiKeyStore`, `IDetectionEventPublisher`

Identity: full stack — layout, encoder, cache, archetype registry, weights cache, processing coordinator, browser modes

Services: `IBrowserVersionService`, `IDnsResolver`, `IFediverseDomainVerifier`, `VerifiedBotRegistry`, `ProjectHoneypotLookupService`, `UaProfileStore`, `CountryReputationTracker`, `ReactiveSignalTracker`, `SequenceContextStore`, `CentroidSequenceStore`, `EndpointDivergenceTracker`, `BotClusterService` (hosted), `Analysis.SessionStore` (legacy sliding-vector window, still consumed by `SessionVectorAtom`), `DeploymentNormTracker`, `FingerprintPopulationTracker`, `IBrowserFingerprintStore`

Similarity: `FeatureVectorizer`, `IntentVectorizer`, `IIntentSimilaritySearch`, `ISignatureSimilaritySearch`

Legacy detectors still consumed by atoms: `HeuristicDetector`, `VersionAgeDetector`, `BehavioralDetector`, `ClientSideDetector`

Session fabric: `SessionStore` (new — `Orchestration.Sessions.SessionStore`), `SessionAtom`, `SessionPersistenceAtom`, `SessionEchoAtom`, `ISessionEchoStore` → `DetectionArchiveEchoStore`

Learning fabric: `TypedSignalSink<LearningEvent>` (init-signal-aware), `ILearningCoordinator`, `LearningBackgroundService`

LLM: `TypedSignalSink<LlmClassificationRequest>` (init-signal-aware), `LlmClassificationCoordinator`

### Intentionally absent — do not re-add without a design conversation

- **`Data.SessionPersistenceService`** — dead code. See `SESSION_PERSISTENCE_PIPELINE.md` for details. Its CLR-event subscription to `Analysis.SessionStore.SessionFinalized` has been inert for however long. Deciding whether to delete / wire / migrate is what you're being brought in for. **Adding it back mechanically is exactly the trap.**
- **Old contributor path** — anything under the deleted-in-Step-7 list above. Truly gone. If you see comments or design notes referencing it, treat as stale.
- **`SessionVectorContributor`** — replaced by `SessionVectorAtom`. Contributor gone.

---

## The distinguishing test

When you find a service that seems unregistered:

```bash
# Step 1: is it referenced in the tree at all?
grep -rn "TypeName" --include="*.cs" src/ | grep -v Test | grep -v obj | grep -v bin

# Step 2: is it referenced by anything that hard-requires it?
grep -rn "TypeName" --include="*.cs" src/ | grep -v Test | grep -v obj | grep -v bin | grep -v "GetService<"

# Step 3: check the git log for what deleted the registration
git log --all --oneline --diff-filter=D --name-only -- "**/TypeName*"
git log --all -S "AddSingleton<TypeName" --oneline
```

If step 2 returns hard references outside its own defining file, it's live and needs registration.
If step 2 returns nothing outside its own file, and step 3 shows Step 7 or later removed the registration, it's dead code. Delete it. Don't re-add it.

---

## What you're most likely being asked to work on

Given the timing, if you're here now, it's probably one of these:

**1. The `SessionPersistenceService` dead-code question.** Three options in `SESSION_PERSISTENCE_PIPELINE.md`. Don't just re-register it — pick one of delete / wire / signal-migrate and commit to that path.

**2. A Step-7 DI casualty in an atom you're touching.** Find the atom's ctor. Non-nullable dep? Add the FOSS default in `Modules/BotDetectionModule.cs`. Match the pattern for the closest existing registration.

**3. A regression on `main` that was fixed on a session branch.** Two such fixes landed via the recent branch merge (learning feedback loop restored, BlockResponseGate per-endpoint policy). Verify they're on `main` before re-fixing (`git log --all --oneline | grep -i "your regression"`).

---

## The "keep it small" reflex

Old habit from operator memory: don't cut features silently, don't propose rollback as a way out, don't silently register something that changes runtime behaviour without a design conversation. The atom refactor was months of work; if you find something confusing, ask before you "fix" it.

Merge-scope discipline applies: every commit should build clean, every commit should have tests where feasible, every commit should describe what changed and why in the message body (not just the subject). Prefer many small commits over one large one.

---

## Where things live now

- `Orchestration/Atoms/` — all `IDetectorAtom` implementations
- `Orchestration/Atoms/BotDetectionOrchestrator.cs` — DI composition + atom registration list (long list, ordered by priority)
- `Orchestration/Sessions/` — the new signal-driven session fabric (SessionStore, SessionAtom, SessionPersistenceAtom, SessionEchoAtom, SessionSample, SessionAggregate)
- `Analysis/SessionVector.cs` — legacy `Analysis.SessionStore` (still live; SessionVectorAtom's data source)
- `Actions/` — all `IActionPolicy` implementations including the three escalators + the combined factory
- `Learning/` — the sink+coordinator learning fabric
- `Services/LlmClassificationCoordinator.cs` — same pattern for LLM classification
- `Modules/BotDetectionModule.cs` — the DI wiring master list (~500 lines; grep here first when auditing registrations)
- `Middleware/BotDetectionMiddleware.cs` — the NEW middleware (old one gone)