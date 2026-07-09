# Guardians Discrete-Job Decomposition + Identity Durable-Bounding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the guardian tier into discrete, individually-toggleable, dashboard-visible jobs: decompose the 5-phase `VectorCompactionService` monolith into 5 discrete Data guardians (behaviour-preserving) and add the 2 missing identity Data guardians that bound `fingerprints.db` (the one durable store no guardian covers, and the residual source of the soak+load disk-growth leak).

**Architecture:** The shipped `IGuardian` / `GuardianService` framework already models one-job-per-guardian; `GuardianService` collects `IEnumerable<IGuardian>` and walks each on its own `Interval` (subscribed to `Tick1m`, sequential — no concurrency between guardians). This plan (a) extracts each `VectorCompactionService` phase into its own `IGuardian` calling the SAME store method it calls today, and (b) adds `FingerprintObservationRetentionGuardian` + `FingerprintEvictionGuardian` over `SqliteFingerprintStore` using the same `MemoryAdaptiveCap` + `DecisionNecessity` machinery the signature cap guardian uses. Each guardian gets a `BotDetection:Guardians:<Name>` config block (`Enabled` + `Interval` + its settings). No behaviour changes to any phase; the only functional addition is the identity bounding.

**Tech Stack:** C#/.NET 10, `Mostlylucid.BotDetection.Guardians` (`IGuardian`/`GuardianService`/`GuardianReport`), `Storage.MemoryAdaptiveCap`, `Storage.DecisionNecessity`, `Microsoft.Data.Sqlite`, xUnit + in-memory SQLite. FOSS core (`Mostlylucid.BotDetection`).

## Global Constraints

- **Behaviour-preserving extract for Part A.** Each new vector guardian calls the exact store method the corresponding `RunPhaseN` calls today; move the logic verbatim, do not rewrite it. The existing per-phase tests are the behaviour-preservation harness — move each onto the new guardian's `GuardAsync`.
- **`IGuardian` contract** (`src/Mostlylucid.BotDetection/Guardians/IGuardian.cs`): `string Name` (unique roster key), `GuardianCategory Category` (all guardians here are `Data`), `TimeSpan Interval`, `Task<GuardianReport> GuardAsync(CancellationToken ct)`. Return `GuardianReport.Ok(this, sw.Elapsed.TotalMilliseconds)` for a no-op pass; a working pass returns a full `GuardianReport { GuardianName=Name, Category, Status, RowsBefore, RowsAfter, BytesReclaimed, DurationMs }` (`At` is stamped by the walker). Exemplar to copy: `src/Mostlylucid.BotDetection.Console/Services/SignatureJsonlRetentionGuardian.cs`.
- **Registration:** each guardian is `services.AddSingleton<IGuardian>(...)`; `GuardianService` (ctor `IEnumerable<IGuardian> guardians`) collects them. No central registry edit needed beyond the `AddSingleton<IGuardian>` calls.
- **Per-guardian config:** `BotDetection:Guardians:<Name>:{ Enabled: bool, Interval: TimeSpan, ... }`. FOSS = config + read-only roster visibility; commercial = in-app toggle (the one commercial line — do NOT add editing UI here).
- **`Enabled`:** a disabled guardian must still appear on the roster (as disabled) but must NOT run. Implemented via an `Enabled` default member on `IGuardian` (Task 1).
- **Store-API pattern:** new `IFingerprintStore` methods are **default no-op interface members**; only `SqliteFingerprintStore` overrides them (`NullFingerprintStore`/any proxy opt out). Mirror `IDetectionArchive.GetSignatureCountAsync`/`GetAllSignaturePriorityInfoAsync`/`DeleteSignaturesAsync` in `src/Mostlylucid.BotDetection/Data/SessionPersistence.cs:446-461` and their `SqliteDetectionArchive.cs:1534-1620` implementations.
- **Identity guardians register only under `Identity:Enabled`** (they operate on `fingerprints.db`, which is dormant unless identity is on).
- **Drift preservation (identity retention):** `MaxObservationsPerFingerprint` (recent-K) MUST be `>=` the drift reader's `maxRowsPerArchetype`. `ListRecentObservationsForDriftAsync` (`SqliteFingerprintStore.cs:2559`) and `GetLatestObservationVectorAsync` (`:1475`) read observations with NO `absorbed_at IS NULL` filter, so pruning below recent-K would starve archetype drift.
- **Eviction protection:** `FingerprintEvictionGuardian` never evicts `claim_status = 'verified'` fingerprints. Operator-pin (`is_pinned`) does NOT exist in the schema and is OUT OF SCOPE.
- **No em dashes** anywhere. **No magic numbers** — guardian settings come from config.
- **DecisionNecessity.ColdnessScore** signature (`src/Mostlylucid.BotDetection/Storage/DecisionNecessity.cs:76`): `ColdnessScore(double botProbability, double threat, double ageSeconds, double threshold, double halfLifeSeconds, double bandwidth = 0.15) -> long` (lower = colder = evicted first).

## File Structure

- `Guardians/IGuardian.cs` — add `bool Enabled { get; }` default member (Task 1).
- `Guardians/GuardianService.cs` — skip `!Enabled` in the walk, keep the disabled guardian on the roster (Task 1).
- `Guardians/GuardianConfig.cs` (new) — helper to read `BotDetection:Guardians:<Name>:{Enabled,Interval}` with a fallback (Task 1).
- `Guardians/BucketRetentionGuardian.cs`, `SessionCompactionGuardian.cs`, `HnswCompactionGuardian.cs`, `CentroidRetentionGuardian.cs`, `SignatureCapGuardian.cs` (new, Tasks 2-6) — one extracted phase each.
- `Services/VectorCompactionService.cs` — phases removed as extracted; shell retired in Task 7.
- `Identity/IFingerprintStore.cs` — new default-no-op APIs (Task 8).
- `Identity/SqliteFingerprintStore.cs` — override the new APIs (Task 8).
- `Identity/FingerprintPriorityInfo.cs` (new) — priority record mirroring `CompactionSignatureInfo` (Task 8).
- `Identity/FingerprintObservationRetentionGuardian.cs`, `Identity/FingerprintEvictionGuardian.cs` (new, Tasks 9-10).
- `Models/BotDetectionOptions.cs` / `IdentityOptions.cs` — `MaxObservationsPerFingerprint`, `MaxFingerprints`, `MinFingerprints`, `FingerprintRecencyHalfLife` config (Tasks 9-10).
- Registration in the guardian wiring site (Tasks 2-6, 9-10, verified against `BotDetectionHostedSingletonsBootstrap.cs` eager-resolve).
- Tests in `src/Mostlylucid.BotDetection.Test/` mirroring existing `Services/VectorCompactionService*Tests.cs` + `Data/SqliteSignatureEvictionTests.cs`.

---

### Task 1: Per-guardian `Enabled` + config helper

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Guardians/IGuardian.cs`
- Modify: `src/Mostlylucid.BotDetection/Guardians/GuardianService.cs` (the walk loop, ~line 71)
- Create: `src/Mostlylucid.BotDetection/Guardians/GuardianConfig.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Guardians/GuardianEnabledTests.cs`

**Interfaces — Produces:**
- `IGuardian.Enabled` (`bool`, default member `=> true`).
- `GuardianConfig.Read(IConfiguration config, string name, TimeSpan defaultInterval)` → `(bool Enabled, TimeSpan Interval)` reading `BotDetection:Guardians:{name}:Enabled` (default true) and `:Interval` (default `defaultInterval`).

- [ ] **Step 1: Write the failing test** `GuardianEnabledTests`: a fake `IGuardian` with `Enabled => false` is NOT invoked by `GuardianService.RunDueAsync` but IS present in `GuardianService.Guardians`; an `Enabled => true` fake IS invoked. And `GuardianConfig.Read` returns config values when present, defaults when absent.
- [ ] **Step 2: Run** `dotnet test src/Mostlylucid.BotDetection.Test --filter FullyQualifiedName~GuardianEnabledTests` → FAIL (no `Enabled` member / no `GuardianConfig`).
- [ ] **Step 3: Implement.** Add to `IGuardian`: `/// <summary>Whether this guardian runs. Disabled guardians stay on the roster but are skipped by the walker.</summary> bool Enabled => true;`. In `GuardianService`'s per-guardian walk, `if (!g.Enabled) continue;` (after the roster is populated so a disabled guardian still shows). Add `GuardianConfig.Read` as above.
- [ ] **Step 4: Run** the filter → PASS.
- [ ] **Step 5: Commit** `feat(guardians): per-guardian Enabled + config helper`.

---

### Task 2: `BucketRetentionGuardian` (extract Phase 1)

**Files:**
- Create: `src/Mostlylucid.BotDetection/Guardians/BucketRetentionGuardian.cs`
- Modify: `src/Mostlylucid.BotDetection/Services/VectorCompactionService.cs` (remove `RunPhase1BucketPruneAsync` + its call in `RunCompactionAsync`)
- Modify: the guardian registration site (add `AddSingleton<IGuardian, BucketRetentionGuardian>()`)
- Test: `src/Mostlylucid.BotDetection.Test/Guardians/BucketRetentionGuardianTests.cs`

**Interfaces — Produces:** `BucketRetentionGuardian : IGuardian`, `Name => "BucketRetention"`, `Category => Data`, `Interval` from `GuardianConfig` (default `RetentionOptions.CompactionInterval`). `GuardAsync` runs the exact body of `VectorCompactionService.RunPhase1BucketPruneAsync` against the SAME `IDetectionArchive` store, returns a `GuardianReport` with `Status="pruned"` + rows.
**Consumes:** `IDetectionArchive`, `IOptions<BotDetectionOptions>` (for `Retention.BucketRetention`), `IConfiguration` (for `GuardianConfig`), `ILogger<BucketRetentionGuardian>`.

- [ ] **Step 1: Write the failing test.** Move `VectorCompactionServiceTests`' Phase-1 bucket-prune assertions onto `BucketRetentionGuardian.GuardAsync`: seed bucket rows older than `BucketRetention` + some newer, run `GuardAsync`, assert old rows deleted, newer kept, and the report's `Status`/`RowsAfter` reflect it. (Read the existing Phase-1 test in `src/Mostlylucid.BotDetection.Test/Services/VectorCompactionServiceTests.cs` and port its arrangement.)
- [ ] **Step 2: Run** `--filter FullyQualifiedName~BucketRetentionGuardianTests` → FAIL (class missing).
- [ ] **Step 3: Implement.** Create `BucketRetentionGuardian` mirroring `SignatureJsonlRetentionGuardian`'s shape; `GuardAsync` = the moved `RunPhase1BucketPruneAsync` body wrapped in a `Stopwatch` + `GuardianReport`. Remove `RunPhase1BucketPruneAsync` and its `RunCompactionAsync` call from `VectorCompactionService`. Register `AddSingleton<IGuardian, BucketRetentionGuardian>()` at the guardian wiring site.
- [ ] **Step 4: Run** the filter → PASS; run `--filter FullyQualifiedName~VectorCompactionService` → still green (phase removed cleanly).
- [ ] **Step 5: Commit** `refactor(guardians): extract BucketRetentionGuardian from VectorCompaction phase 1`.

---

### Task 3: `SessionCompactionGuardian` (extract Phase 2 + its HNSW entry-update)

**Files:**
- Create: `src/Mostlylucid.BotDetection/Guardians/SessionCompactionGuardian.cs`
- Modify: `VectorCompactionService.cs` (remove `RunPhase2SessionCompactionAsync` + `UpdateHnswEntryForSignatureAsync` + their calls)
- Modify: registration site
- Test: `src/Mostlylucid.BotDetection.Test/Guardians/SessionCompactionGuardianTests.cs`

**Interfaces — Produces:** `SessionCompactionGuardian : IGuardian`, `Name => "SessionCompaction"`, `Interval` default `RetentionOptions.CompactionInterval`. `GuardAsync` = the moved `RunPhase2SessionCompactionAsync` body; the incremental `UpdateHnswEntryForSignatureAsync` helper moves INTO this guardian (it is how compaction maintains the index per-signature). Config: `RetentionOptions.MaxSessionsPerSignature`. Returns `Status="compacted"` + count.
**Consumes:** `IDetectionArchive`, `ISessionVectorSearch?`, `IOptions<BotDetectionOptions>`, `IConfiguration`, `ILogger`.

- [ ] **Step 1: Write the failing test.** Port the Phase-2 session-compaction test (signatures over `MaxSessionsPerSignature` get their overflow folded into the behavioural centroid, count returned) onto `SessionCompactionGuardian.GuardAsync`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~SessionCompactionGuardianTests` → FAIL.
- [ ] **Step 3: Implement.** Move `RunPhase2SessionCompactionAsync` + `UpdateHnswEntryForSignatureAsync` into the guardian; wrap in report. Remove from `VectorCompactionService`. Register.
- [ ] **Step 4: Run** filter → PASS; VectorCompaction suite still green.
- [ ] **Step 5: Commit** `refactor(guardians): extract SessionCompactionGuardian from VectorCompaction phase 2`.

---

### Task 4: `HnswCompactionGuardian` (extract Phase 3)

**Files:**
- Create: `src/Mostlylucid.BotDetection/Guardians/HnswCompactionGuardian.cs`
- Modify: `VectorCompactionService.cs` (remove `RunPhase3HnswCompactionAsync` + its guarded call)
- Modify: registration site
- Test: `src/Mostlylucid.BotDetection.Test/Guardians/HnswCompactionGuardianTests.cs`

**Interfaces — Produces:** `HnswCompactionGuardian : IGuardian`, `Name => "HnswCompaction"`, `Interval` default `RetentionOptions.CompactionInterval`. `GuardAsync` = moved `RunPhase3HnswCompactionAsync`; no-op (`GuardianReport.Ok`) when `ISessionVectorSearch` is null (mirrors today's `if (_vectorSearch != null)` guard). Config: the HNSW thresholds it reads today (`HnswLevel1Threshold`/`Level2`/`L2CompactionPriorityThreshold` on `RetentionOptions`/`SelfMaintenanceOptions` — read the current method to confirm the exact source).
**Consumes:** `ISessionVectorSearch?`, `IOptions<BotDetectionOptions>`, `IConfiguration`, `ILogger`.

- [ ] **Step 1: Write the failing test.** Port the Phase-3 HNSW-compaction test (index over threshold → bulk L1/L2 compaction runs; null vector-search → no-op report) onto the guardian.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~HnswCompactionGuardianTests` → FAIL.
- [ ] **Step 3: Implement + remove from VectorCompaction + register.**
- [ ] **Step 4: Run** filter → PASS; suite green.
- [ ] **Step 5: Commit** `refactor(guardians): extract HnswCompactionGuardian from VectorCompaction phase 3`.

---

### Task 5: `CentroidRetentionGuardian` (extract Phase 4)

**Files:**
- Create: `src/Mostlylucid.BotDetection/Guardians/CentroidRetentionGuardian.cs`
- Modify: `VectorCompactionService.cs` (remove `RunCentroidPruningAsync` + its call)
- Modify: registration site
- Test: `src/Mostlylucid.BotDetection.Test/Guardians/CentroidRetentionGuardianTests.cs`

**Interfaces — Produces:** `CentroidRetentionGuardian : IGuardian`, `Name => "CentroidRetention"`, `Interval` default `RetentionOptions.CompactionInterval`. `GuardAsync` = moved `RunCentroidPruningAsync`, pruning stale rows from all three centroid stores (`ISignatureCentroidStore`/`ISessionCentroidStore`/`IIntentCentroidStore`). Config: `RetentionOptions.CentroidRetentionDays` (confirm the exact knob in the current method).
**Consumes:** the three centroid stores, `IOptions<BotDetectionOptions>`, `IConfiguration`, `ILogger`.

- [ ] **Step 1: Write the failing test.** Port the Phase-4 centroid-pruning test (stale centroid rows past the retention window deleted across all three tables) onto the guardian.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~CentroidRetentionGuardianTests` → FAIL.
- [ ] **Step 3: Implement + remove + register.**
- [ ] **Step 4: Run** filter → PASS; suite green.
- [ ] **Step 5: Commit** `refactor(guardians): extract CentroidRetentionGuardian from VectorCompaction phase 4`.

---

### Task 6: `SignatureCapGuardian` (extract Phase 5)

**Files:**
- Create: `src/Mostlylucid.BotDetection/Guardians/SignatureCapGuardian.cs`
- Modify: `VectorCompactionService.cs` (remove `RunPhase5CapEnforcementAsync` + the `_signatureCap`/`_botThreshold` fields it owns)
- Modify: registration site
- Test: `src/Mostlylucid.BotDetection.Test/Guardians/SignatureCapGuardianTests.cs`

**Interfaces — Produces:** `SignatureCapGuardian : IGuardian`, `Name => "SignatureCap"`, `Interval` default `RetentionOptions.CompactionInterval`. `GuardAsync` = moved `RunPhase5CapEnforcementAsync`: `MemoryAdaptiveCap(Retention.MaxSignatures, floor: Retention.MinSignatures)` (null when `MaxSignatures==0` → `GuardianReport.Ok`); over cap → pull `GetAllSignaturePriorityInfoAsync(overflow*2+100)`, order by `DecisionNecessity.ColdnessScore(s.BotProbability, Math.Max(s.BotProbability, RiskBandToRisk(s.RiskBand)), (now-s.LastSeen).TotalSeconds, BotFloor, SignatureRecencyHalfLife.TotalSeconds)`, evict lowest `overflow` via `DeleteSignaturesAsync`. Move the `RiskBandToRisk` helper too. `_botThreshold` = `Classification.BotFloor`.
**Consumes:** `IDetectionArchive`, `IOptions<BotDetectionOptions>`, `IConfiguration`, `ILogger`.

- [ ] **Step 1: Write the failing test.** Move `VectorCompactionServicePhase5Tests` (cold evicted / uncertain+risky survive / within-cap no-op) onto `SignatureCapGuardian.GuardAsync`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~SignatureCapGuardianTests` → FAIL.
- [ ] **Step 3: Implement + remove from VectorCompaction + register.**
- [ ] **Step 4: Run** filter → PASS; `--filter FullyQualifiedName~VectorCompactionService` green.
- [ ] **Step 5: Commit** `refactor(guardians): extract SignatureCapGuardian from VectorCompaction phase 5`.

---

### Task 7: Retire the `VectorCompactionService` shell

**Files:**
- Delete: `src/Mostlylucid.BotDetection/Services/VectorCompactionService.cs`
- Delete/retire: `src/Mostlylucid.BotDetection.Test/Services/VectorCompactionServiceTests.cs`, `VectorCompactionServiceTickTests.cs`, `VectorCompactionServicePhase5Tests.cs` (their assertions now live on the per-guardian tests)
- Modify: remove the old `AddSingleton<IGuardian, VectorCompactionService>()` registration; update `BotDetectionHostedSingletonsBootstrap.cs:74` comment/eager-resolve to reference the guardian set (GuardianService still eager-resolves; the 5 guardians are collected via `IEnumerable<IGuardian>`)

**Interfaces — Consumes:** all 5 extracted guardians (Tasks 2-6). After this task there is no `VectorCompactionService`.

- [ ] **Step 1:** Confirm every phase is extracted (grep `RunPhase` in `VectorCompactionService.cs` → only `RunCompactionAsync`/`GuardAsync` orchestration shells remain, all empty).
- [ ] **Step 2: Run** the full `Mostlylucid.BotDetection.Test` suite to capture the pre-delete green baseline for the guardian tests.
- [ ] **Step 3:** Delete `VectorCompactionService.cs` + its 3 test files; remove its registration; fix any remaining references (grep `VectorCompactionService` across `src/` → zero non-comment hits).
- [ ] **Step 4: Run** `dotnet build src/Mostlylucid.BotDetection -c Release` (0 errors) + full test suite (green; count drops by the retired monolith tests, per-guardian tests cover the behaviour).
- [ ] **Step 5: Commit** `refactor(guardians): retire VectorCompactionService shell (5 discrete guardians)`.

---

### Task 8: `IFingerprintStore` durable-bounding APIs

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IFingerprintStore.cs` (4 default-no-op members)
- Modify: `src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs` (override the 4)
- Create: `src/Mostlylucid.BotDetection/Identity/FingerprintPriorityInfo.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Identity/FingerprintStoreBoundingApiTests.cs`

**Interfaces — Produces (mirroring `IDetectionArchive` Phase-5, `SessionPersistence.cs:446-461`):**
- `record FingerprintPriorityInfo(string FingerprintId, double BotProbability, string? RiskBand, DateTime LastSeen, bool Protected)` — `BotProbability`=`cached_bot_probability`, `RiskBand`=`cached_risk_band`, `Protected`=`claim_status='verified'`.
- `Task<int> GetFingerprintCountAsync(CancellationToken ct = default) => Task.FromResult(0);`
- `Task<IReadOnlyList<FingerprintPriorityInfo>> GetAllFingerprintPriorityInfoAsync(int limit, CancellationToken ct = default) => Task.FromResult((IReadOnlyList<FingerprintPriorityInfo>)Array.Empty<FingerprintPriorityInfo>());` — oldest (`last_seen ASC`) first, `LIMIT @limit`.
- `Task<int> DeleteFingerprintsAsync(IReadOnlyList<string> fingerprintIds, CancellationToken ct = default) => Task.FromResult(0);` — cascade-delete across ALL per-fp tables in one transaction: `fingerprints`, `fingerprints_vec`, `fingerprint_observations`, `observations_vec`, `fingerprint_keys`, `fingerprint_corrections`, `fingerprint_approvals`, `fingerprint_modes`, `fingerprint_mode_observations`, `fingerprint_name_history`, `fingerprint_root_history` (grep the store for the exact table set + the `observations_vec` join key `observation_id`).
- `Task<int> PruneAbsorbedObservationsAsync(int keepPerFingerprint, CancellationToken ct = default) => Task.FromResult(0);` — delete `fingerprint_observations` rows that are `absorbed_at IS NOT NULL` AND rank `> keepPerFingerprint` within `PARTITION BY fingerprint_id ORDER BY id DESC` (keep all unabsorbed + most-recent-K); also delete the matching `observations_vec` rows.

- [ ] **Step 1: Write the failing tests** in `FingerprintStoreBoundingApiTests` (real `SqliteFingerprintStore` on a temp db): (a) `GetFingerprintCountAsync` counts distinct fingerprints; (b) `GetAllFingerprintPriorityInfoAsync` returns oldest-first within limit, carries `BotProbability`/`RiskBand`/`LastSeen`, and sets `Protected=true` iff `claim_status='verified'`; (c) `DeleteFingerprintsAsync` removes the fingerprint AND leaves zero orphan rows in every per-fp table; (d) `PruneAbsorbedObservationsAsync(K)` deletes only absorbed rows beyond the newest-K per fingerprint, keeps all unabsorbed + newest-K, and `GetLatestObservationVectorAsync` still returns the latest vector after the prune.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~FingerprintStoreBoundingApiTests` → FAIL (methods are the no-op defaults).
- [ ] **Step 3: Implement** the 4 overrides in `SqliteFingerprintStore` + the `FingerprintPriorityInfo` record. Use the store's existing connection pattern; run `DeleteFingerprintsAsync` and the prune in transactions.
- [ ] **Step 4: Run** the filter → PASS.
- [ ] **Step 5: Commit** `feat(identity): fingerprint-store bounding APIs (count/priority/delete-cascade/prune)`.

---

### Task 9: `FingerprintObservationRetentionGuardian`

**Files:**
- Create: `src/Mostlylucid.BotDetection/Identity/FingerprintObservationRetentionGuardian.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs` (add `MaxObservationsPerFingerprint`)
- Modify: registration site (register `AddSingleton<IGuardian, ...>()` under `Identity:Enabled`)
- Test: `src/Mostlylucid.BotDetection.Test/Identity/FingerprintObservationRetentionGuardianTests.cs`

**Interfaces:**
- Consumes: `IFingerprintStore.PruneAbsorbedObservationsAsync` (Task 8), `IOptions<BotDetectionOptions>`, `IConfiguration`, `ILogger`.
- Produces: `FingerprintObservationRetentionGuardian : IGuardian`, `Name => "FingerprintObservationRetention"`, `Category => Data`, `Interval` default 30 min. `GuardAsync` calls `PruneAbsorbedObservationsAsync(effectiveK)` where `effectiveK = Math.Max(MaxObservationsPerFingerprint, driftMaxRowsPerArchetype)` (the drift-recency guard).
- Produces config: `IdentityOptions.MaxObservationsPerFingerprint` (default 50; MUST default `>=` the drift `maxRowsPerArchetype` — read the drift caller to confirm that value and set the default at or above it).

- [ ] **Step 1: Write the failing test.** Seed a fingerprint with (i) unabsorbed observations, (ii) many absorbed observations, and (iii) absorbed observations recent enough that `ListRecentObservationsForDriftAsync` would rank them within `maxRowsPerArchetype`. Run `GuardAsync`. Assert: all unabsorbed survive; the newest-K absorbed survive; older absorbed are pruned; AND `ListRecentObservationsForDriftAsync` still returns the drift-rankable rows (the recency guard). Assert the report `Status="pruned"` + `RowsBefore/After`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~FingerprintObservationRetentionGuardianTests` → FAIL.
- [ ] **Step 3: Implement** the guardian + the `MaxObservationsPerFingerprint` config + the `effectiveK` guard. Register under `Identity:Enabled`.
- [ ] **Step 4: Run** the filter → PASS.
- [ ] **Step 5: Commit** `feat(identity): FingerprintObservationRetentionGuardian (drift-preserving absorbed-observation prune)`.

---

### Task 10: `FingerprintEvictionGuardian`

**Files:**
- Create: `src/Mostlylucid.BotDetection/Identity/FingerprintEvictionGuardian.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs` (add `MaxFingerprints`, `MinFingerprints`, `FingerprintRecencyHalfLife`)
- Modify: registration site (register under `Identity:Enabled`)
- Test: `src/Mostlylucid.BotDetection.Test/Identity/FingerprintEvictionGuardianTests.cs`

**Interfaces:**
- Consumes: `IFingerprintStore.GetFingerprintCountAsync`/`GetAllFingerprintPriorityInfoAsync`/`DeleteFingerprintsAsync` (Task 8), `DecisionNecessity.ColdnessScore`, `MemoryAdaptiveCap`, `IOptions<BotDetectionOptions>` (`Classification.BotFloor`), `IConfiguration`, `ILogger`.
- Produces: `FingerprintEvictionGuardian : IGuardian`, `Name => "FingerprintEviction"`, `Category => Data`, `Interval` default 30 min. `GuardAsync` mirrors `SignatureCapGuardian` (Task 6): `MemoryAdaptiveCap(MaxFingerprints, floor: MinFingerprints)` (null when `MaxFingerprints==0` → `Ok`); if `count > effective`, pull `GetAllFingerprintPriorityInfoAsync(overflow*2+100)`, **filter out `Protected`**, order the rest by `DecisionNecessity.ColdnessScore(p.BotProbability, Math.Max(p.BotProbability, RiskBandToRisk(p.RiskBand)), (now-p.LastSeen).TotalSeconds, BotFloor, FingerprintRecencyHalfLife.TotalSeconds)`, evict lowest `overflow` via `DeleteFingerprintsAsync`. If protected-alone already exceeds the cap, log and hold (evict nothing).
- Config: `IdentityOptions.MaxFingerprints` (default 50_000), `MinFingerprints` (default 2_000), `FingerprintRecencyHalfLife` (default 7d). Reuse a `RiskBandToRisk` helper (extract to a shared static in Task 6 or duplicate the small mapping — DRY: put it on a shared `GuardianRisk` static so both cap guardians use it).

- [ ] **Step 1: Write the failing test.** Seed fingerprints over `MaxFingerprints`: some cold+harmless (low botprob, old `last_seen`), some uncertain (botprob near `BotFloor`), some risky (high `cached_risk_band`), some `claim_status='verified'`. Run `GuardAsync`. Assert: cold+harmless evicted first; uncertain + risky retained; `verified` NEVER evicted (even if cold); count returns to `<= effective` (unless protected-alone exceeds it, in which case hold). Assert report `Status="evicted"`.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~FingerprintEvictionGuardianTests` → FAIL.
- [ ] **Step 3: Implement** the guardian + config + the shared `GuardianRisk.RiskBandToRisk`. Register under `Identity:Enabled`.
- [ ] **Step 4: Run** the filter → PASS.
- [ ] **Step 5: Commit** `feat(identity): FingerprintEvictionGuardian (DecisionNecessity cap eviction, protect verified)`.

---

### Task 11: Wiring verification + dashboard roster + full-suite green

**Files:**
- Verify: the guardian wiring site registers all 7 (`AddSingleton<IGuardian, ...>` for the 5 vector guardians unconditionally + the 2 identity guardians under `Identity:Enabled`)
- Verify: `BotDetectionHostedSingletonsBootstrap.cs` still eager-resolves `GuardianService` (which collects all 7)
- Verify: `GuardianRosterModel` + `SbGuardiansViewComponent` render each guardian's `LatestReports` row (no code change expected; confirm 7 rows when identity on, 5 when off)
- Test: `src/Mostlylucid.BotDetection.Test/Guardians/GuardianRegistrationCoverageTests.cs`

**Interfaces — Consumes:** all 7 guardians.

- [ ] **Step 1: Write the failing test** `GuardianRegistrationCoverageTests`: build a provider with `AddBotDetection` + `Identity:Enabled=true`, resolve `IEnumerable<IGuardian>`, assert the 7 expected `Name`s are present (`BucketRetention`, `SessionCompaction`, `HnswCompaction`, `CentroidRetention`, `SignatureCap`, `FingerprintObservationRetention`, `FingerprintEviction`) and Category=Data for all; with `Identity:Enabled=false`, assert the 5 vector guardians present and the 2 identity guardians absent.
- [ ] **Step 2: Run** `--filter FullyQualifiedName~GuardianRegistrationCoverageTests` → FAIL if any registration is missing.
- [ ] **Step 3: Fix** any missing/mis-gated registration.
- [ ] **Step 4: Run** the FULL `Mostlylucid.BotDetection.Test` suite (green) + `dotnet build mostlylucid.stylobot.sln -c Release` (0 errors).
- [ ] **Step 5: Commit** `test(guardians): registration coverage for the 7 discrete data guardians`.

---

## Self-Review

- **Spec coverage:** Part A = Tasks 2-7 (5 extracts + shell retire); Part B = Tasks 8-10 (store APIs + 2 identity guardians); framework `Enabled`/config = Task 1; wiring + roster = Task 11. Drift-recency guard = Task 9 step 1 + the `effectiveK` guard. Verified protection = Task 10. Operator-pin excluded per scope. All covered.
- **Behaviour-preservation:** Tasks 2-6 move existing methods verbatim and port the existing tests; Task 7 only deletes once green. No phase logic rewritten.
- **Type consistency:** `FingerprintPriorityInfo` (Task 8) consumed by Task 10; `PruneAbsorbedObservationsAsync(keepPerFingerprint)` (Task 8) consumed by Task 9; `GuardianConfig.Read` (Task 1) consumed by every guardian; `GuardianRisk.RiskBandToRisk` shared by Tasks 6 + 10. Guardian `Name`s consistent across Tasks 2-6/9-10/11.
- **Open items the implementer must confirm from code (not placeholders — pointers):** the exact HNSW threshold config source (Task 4), `CentroidRetentionDays` knob name (Task 5), the current `AddSingleton<IGuardian, VectorCompactionService>()` registration site (Tasks 2/7), the drift `maxRowsPerArchetype` value (Task 9), and the full per-fp table set for the cascade (Task 8) — each names the file + method to read.
