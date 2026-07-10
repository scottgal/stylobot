# Data Guardians — bounded, self-optimizing storage

**Status:** spec (2026-07-03; **tier model added + guardian-first framing superseded 2026-07-10**).
**Why it matters:** no StyloBot store may grow unbounded regardless of traffic shape. This is the
long-term-stability guarantee; without it a UA-rotating bot (the exact thing StyloBot exists to
catch) is a storage-DoS.

## 0. The tier model (authoritative — read this first)

**[2026-07-10 ratification, overview.]** Guardians are NOT the primary bounding mechanism — that
framing (§1–§6 below) was the drift. Bounding is a property of the *write path*, not a periodic
cleanup. The model:

- **Evidence Accumulator** — the hot in-memory tier. It does not "cache"; it *accumulates
  behavioural evidence*, folding raw samples into an evolving shape (the centroid) on arrival.
  **It IS the current state.** Reads hit it first; the durable tier is consulted only on cold miss
  (read-through) and warm-on-start.
- **Behavioural Admission Filter** — the gate. What is admitted / retained / sampled is decided by
  **behavioural significance** (`DecisionNecessity` = uncertainty ⊕ risk × recency), **NOT
  frequency.** "LFU" is the wrong lens: a rarely-seen-but-uncertain fingerprint is the *most*
  valuable to keep. (`WriteBehindLfuStore` is the closest current impl; its `ColdnessScore`/LFU
  eviction should become this behavioural filter — a rename to `EvidenceAccumulator` is a
  follow-up.)
- **The persisted UNIT is the centroid (the accumulated shape), never raw rows.** Observations fold
  into the fingerprint centroid; sessions into the session/fingerprint shape — in the Accumulator,
  before anything is written. There is no raw row to later absorb or prune.
- **The write is a behavioural sample:** flush the highest-significance ∧ dirty shapes from the
  Accumulator, coalesced by key, batched — never every mutation, never every row. That sampling is
  what keeps the durable tier bounded; a flood cannot outrun it because the flood never becomes rows.
- **At-insert eviction is the FAILURE-MODE cap only** (flood backstop), not the normal path.

**What this leaves for guardians:** almost nothing. `FingerprintObservationRetention` /
`SessionCompaction` sweep raw-row tables (`fingerprint_observations`, `sessions`) that **should not
exist** — fold those rows into centroids and the guardians delete (retire `FingerprintObservationRetention`
*with* Gap B, not before — until the drift readers move off the table and novel obs stop writing rows,
the residual novel rows still need a floor). `SignatureCap` / `HnswCompaction` become the failure-mode
cap *at insert* on their accumulators, not periodic DELETEs. `CentroidRetention` survives but is
**reshaped to significance-weighted** (`DecisionNecessity` × recency — a cold-but-high-significance
centroid must not age out just because it's old; a cold-and-harmless one goes early); NOT blind age.
`BucketRetention` stays age-based only because a chart aggregate's significance ≈ its recency, so the
same admission filter degenerates to age there — it's the general model with flat significance, not a
special case. The reference implementation is the keyed-upsert centroid store on a fixed Evidence
Accumulator; two gaps to close: the drainer must behavioural-sample (pull dirty ∧ significant),
not op-replay; and the raw-row tables must fold into centroids.

Sections §1–§6 below describe the earlier guardian-centric design. Keep them for the guardian
*framework* (discrete jobs, roster, `IGuardian`) — that machinery is still correct for the small
TTL + failure-cap role — but read them through §0: the guardians are the backstop, the tier model
is the mechanism.

## 1. Problem

Measured on the Maxo (9950X, win-x64 AOT) soak, 80 RPS, bounded 3000-visitor pool:

| Metric | 0 min | 19 min | Slope |
|---|---|---|---|
| RSS | 256 MB | 748 MB | **flat** (ramps to ~830, GC reclaims to ~748) |
| SQLite | 1.8 MB | 43.4 MB | **+2.2 MB/min, linear, no plateau** |
| `signatures-*.jsonl` | 0 | 3.7 MB | bounded here; **14 GB observed** under unique-UA traffic |

The **in-memory** behavioural compression works: `Analysis/SessionVector.cs:988
CompactHistory` bounds per-signature snapshot history in `IMemoryCache`
(2 h sliding expiry; >10 snapshots → maturity-weighted root + keep recent 5).
That is why RSS is flat.

It **never touches persistence.** `SessionFinalized` →
`Data/SessionPersistenceService.cs` → `AddSessionAsync` INSERTs one row per
finalized session, forever; `Data/SqliteSessionStore.cs` has no
prune/evict/compact (only `LIMIT` on reads). And `LoadSignaturesFromJsonL`
(`Console/Program.cs:846`) re-reads every `signatures-*.jsonl` at boot — the
14 GB accumulation made the gateway appear to hang on start.

So: memory-side compaction ✓, storage-side compaction ✗. The fix is to apply
the same compaction/eviction discipline to the durable tier — as a **guardian**.

## 2. Guardians are a global concept (FOSS core)

Guardians are periodic, self-scheduling workers that keep an invariant true —
Name + Interval + `Guard → report`, walked by a service, DI-registered, surfaced
as a live count. Today the only implementation is
`Stylobot.Commercial.Compliance.Guardians.IComplianceGuardian` (walked by
`ComplianceGuardianService`; `RetentionGuardian`, `DriftGuardian`,
`DsarDeadlineGuardian`). **That's the wrong home:** the guardian *framework* is a
global concern, not a commercial one. Storage bounding is a FOSS invariant
(SQLite is the FOSS store; detection sensitivity is never degraded, only storage
is bounded), and it needs guardians.

So this spec **promotes the guardian framework to FOSS core** and adds
**categories**: `data`, `compliance`, `license`. Compliance guardians become one
category on the shared framework; data guardians are another. One interface, one
walker, one count.

**FOSS vs commercial split (the only line):** the framework, the guardians, their
scheduling, and their **config-file control** are all **FOSS**. What's commercial
is the **in-app editing experience** — the dashboard UI to tune guardian
intervals/retention/caps live. FOSS operators edit the same knobs via
`appsettings.json` (same pattern as endpoint policies: FOSS config, commercial
live-edit).

## 3. Design — one compaction, carried to the DB

**This is not a new mechanism.** The behavioural compression already exists and is
correct — `SessionVector.CompactHistory:988` merges a signature's old snapshots
into a maturity-weighted **root** (the behavioural *shape*) and keeps the recent
few. The previous implementation simply **stopped at `IMemoryCache`** and never
carried the compacted root to the durable tier. The data guardian is the
**carrier**, not a parallel compactor.

Carrying it through also fixes a correctness bug, not just size: today the
compacted root lives only in `IMemoryCache` (2 h expiry, gone on restart) while
the DB still holds every raw session — memory and durable tier **diverge**.
Persisting the root **write-through** makes the durable tier the source of truth
(`feedback_no_unbacked_imemorycache`).

**Retention is prioritized by behavioural shape, not LFU.** The `Lfu` in
`WriteBehindLfuStore` is only the hot-tier cache-aside eviction (a memory
detail); it must **never** decide what persists. Access frequency does not drive
retention. Two behavioural-value criteria do, in order:

| # | Scope | Criterion | Component | Status |
|---|---|---|---|---|
| 1 (primary) | **within a signature** | keep the behavioural **shape** (root), drop per-session detail | `CompactHistory` extended to the DB | logic ✅, DB-reach ⏳ |
| 2 (under cap pressure) | **across signatures** | value-of-information — keep uncertain + risky, shed resolved-and-harmless | `Storage/DecisionNecessity` | ✅ committed |
| cap size | — | how much we can afford (host-adaptive) | `Storage/MemoryAdaptiveCap` | ✅ committed |

Initially, layer 1 (behavioural compaction to the DB) is the bounding mechanism;
layer 2 (cross-signature `DecisionNecessity` eviction) only engages when the cap
is hit. The data-category `IGuardian` applies both on the durable tier.

### 3.1 `IGuardian` — the global framework (FOSS core, `Mostlylucid.BotDetection`)

One interface for every guardian category (data / compliance / license):

```csharp
public enum GuardianCategory { Data, Compliance, License }

public interface IGuardian
{
    string Name { get; }
    GuardianCategory Category { get; }
    TimeSpan Interval { get; }
    Task<GuardianReport> GuardAsync(CancellationToken ct = default);
}

public sealed record GuardianReport
{
    public required string GuardianName { get; init; }
    public required string Status { get; init; } // ok | compacted | evicted | pruned | alert | error
    public long RowsBefore { get; init; }
    public long RowsAfter  { get; init; }
    public long BytesReclaimed { get; init; }
    public double DurationMs { get; init; }
    public string? Details { get; init; }
}
```

A FOSS `GuardianService` (hosted service, or a subscription on the existing
`IScheduleCoordinator`) walks every registered `IGuardian` on its own `Interval`,
records the last `GuardianReport` per guardian, and exposes the roster + reports.
The dashboard's live **guardian count** and a new **storage panel** read from it.

**Migration:** `IComplianceGuardian` (commercial) is re-based onto this — either
`IComplianceGuardian : IGuardian` (Category = Compliance) or replaced outright.
The commercial `ComplianceGuardianService` folds into the FOSS `GuardianService`.
No behaviour change; the compliance guardians just register on the shared roster.

**Data guardians are the `Data` category.** `SqliteDataGuardian` (FOSS) and
`PostgresDataGuardian` (commercial pack) are `IGuardian`s with
`Category = Data`.

### 3.2 What a data guardian does per run (in priority order)

1. **Behavioural compaction (primary).** For any signature with more than
   `MaxSessionsPerSignature` rows, run the *same* maturity-weighted root-merge
   `CompactHistory` already does — on the durable rows: fold the old sessions
   into the signature's root row, keep the recent few, delete the folded rows.
   The compacted root is the behavioural shape; per-session detail beyond the
   window is dropped. Under steady traffic this alone bounds storage — the DB
   converges to ~(distinct signatures × recent-window + root), not
   ×(all-sessions-ever).
2. **Retention.** Delete rows past the retention window.
3. **jsonl rotation.** Roll `signatures-*.jsonl` daily, delete beyond retention,
   cap total on-disk footprint (kills the 14 GB / slow-boot problem).
4. **Cap enforcement (last resort, only if still over `MemoryAdaptiveCap`).**
   Evict whole low-value signatures by `DecisionNecessity.ColdnessScore` — shed
   resolved-and-harmless first; uncertain + risky survive. This engages only when
   behavioural compaction + retention haven't brought the distinct-signature
   count under the (host-adaptive) cap.

### 3.3 Per-provider guardians

| | `SqliteDataGuardian` (FOSS core) | `PostgresDataGuardian` (commercial pack) |
|---|---|---|
| Strategy | cap + DecisionNecessity eviction + persisted root-merge + jsonl rotation | time/volume retention + **partition pruning** + larger caps |
| Cap | `MemoryAdaptiveCap`, low default (~20 k signatures), host-adaptive | generous / time-based; HNSW scales |
| Interval | short (15–30 min) — disk-bound, single-node | hours |
| Notes | the FOSS invariant | absorbs today's `RetentionGuardian`; aligns it to `IDataGuardian` |

### 3.4 Provider policy (config)

`DataRetentionPolicy`, per provider, under `BotDetection:Storage:Retention`:

```jsonc
"Storage": { "Retention": {
  "Sqlite":   { "MaxSignatures": "adaptive", "MaxSessionsPerSignature": 20,
                "RetentionWindow": "14.00:00:00", "JsonlRetentionDays": 3 },
  "Postgres": { "MaxSignatures": 5000000, "RetentionWindow": "90.00:00:00",
                "PartitionPruneAfter": "90.00:00:00" }
}}
```

`MaxSignatures: "adaptive"` → the guardian uses `MemoryAdaptiveCap` (ceiling
from config, ramped down under memory pressure).

## 4. Integration with the bounded-sampling store

- **Hot tier** (memory): the `WriteBehindLfuStore<signature>` subclass evicts by
  `DecisionNecessity.ColdnessScore` (deferred store-subclass work).
- **Durable tier** (SQLite/Postgres): the **data guardian** enforces the same
  key + cap periodically. Write-behind keeps appending; the guardian keeps the
  durable store bounded. Both tiers use `MemoryAdaptiveCap` + `DecisionNecessity`
  — one continuous policy across memory and disk.
- `SessionPersistenceService` is unchanged (still write-behind); it no longer
  needs to bound anything itself.

## 5. Build sequence

1. **Global framework, FOSS core:** `IGuardian` + `GuardianCategory` +
   `GuardianReport` + `GuardianService` (walker) + roster/reports accessor. ✅ done.
2. **Re-base compliance guardians** onto `IGuardian` (Category = Compliance); fold
   `ComplianceGuardianService` into `GuardianService`. No behaviour change — proves
   the framework is category-agnostic. (commercial repo, pending)
3. **The store-bounding work is DISCRETE Data guardians — one job each, individually
   toggleable, each its own roster entry + last-run report.** *(2026-07-09 ratification,
   overview — supersedes the earlier "one `SqliteDataGuardian` split by mechanism"
   framing.)* The operator's north star: guardians are first-class, VISIBLE dashboard
   jobs, each individually enable/disable + tune (FOSS = `BotDetection:Guardians:<Name>:{
   Enabled, Interval, ... }` config; commercial = in-app editor). A monolith hiding N
   phases behind one on/off contradicts that. So `VectorCompactionService`'s five phases
   are **extracted, behaviour-preserving** (the phases are already fault-isolated methods
   with distinct config; the `GuardianService` walker runs guardians sequentially, so no
   new races), into five discrete Data guardians:
   - **`BucketRetentionGuardian`** (Phase 1) — prune stale bucket rows. Config `BucketRetention`.
   - **`SessionCompactionGuardian`** (Phase 2 + its per-signature HNSW entry update) —
     fold overflowing signature sessions into the maturity-weighted root. `MaxSessionsPerSignature`.
   - **`HnswCompactionGuardian`** (Phase 3) — bulk L1/L2 vector-index compaction.
     `HnswLevel1Threshold`/`Level2`/`L2CompactionPriorityThreshold`.
   - **`CentroidRetentionGuardian`** (Phase 4) — prune stale signature/session/intent centroid rows. `CentroidRetentionDays`.
   - **`SignatureCapGuardian`** (Phase 5) ✅ logic — cross-signature `MemoryAdaptiveCap`
     eviction by `DecisionNecessity.ColdnessScore` (uncertainty peaks at `Classification.BotFloor`).
     Tests seed N signatures at varied bot-prob/threat/age and assert count ≤ cap AND survivors are high-`DecisionNecessity`.
   - **`SignatureJsonlRetentionGuardian`** ✅ (Console gateway, Data) — already correctly
     discrete: prunes `signatures-*.jsonl` past the window then oldest-first under a byte cap,
     sparing today's active file; `SignatureLoaderService` boot scan is `FileInfo.Length`
     (O(files), not O(bytes)). Config `SignatureLogging:Retention:*`. **This is the exemplar the others copy.**

3bis. **Identity durable-bounding = TWO discrete Data guardians** (same pattern: within-scope
   retention + cross-scope cap eviction), on `Identity/SqliteFingerprintStore`:
   - **`FingerprintObservationRetentionGuardian`** — prune absorbed `fingerprint_observations`;
     keep-set = (all `absorbed_at IS NULL`) ∪ (most-recent-K per fingerprint). **Preserve the two
     drift readers that scan absorbed rows with no `absorbed_at` filter:** `GetLatestObservationVectorAsync`
     (`id DESC LIMIT 1` — keeping recent-K covers it) and `ListRecentObservationsForDriftAsync`
     (`observed_at DESC` per archetype — recent-K MUST be ≥ its `maxRowsPerArchetype`, or gate the
     prune on a recency floor).
   - **`FingerprintEvictionGuardian`** — cross-fingerprint `MemoryAdaptiveCap` eviction by
     `DecisionNecessity.ColdnessScore`, cascade-delete all per-fp tables (`fingerprints`,
     `fingerprints_vec`, `fingerprint_observations`, `observations_vec`). Protect `claim_status =
     'verified'`. NB: "operator-pinned" is a net-new `is_pinned` column (does not exist yet) — a
     separate decision, not assumed by this guardian.
4. Persisted root-merge compaction (durable mirror of `CompactHistory`). Tests.
5. Wire the `WriteBehindLfuStore<signature>` subclass hot-tier eviction to
   `DecisionNecessity` (the deferred store piece).
6. `PostgresDataGuardian` (commercial pack, Data): absorb/align `RetentionGuardian`;
   add partition pruning.
7. Dashboard: guardian roster + storage panel read the reports (FOSS view). The
   **in-app editor** for intervals/caps/retention is the commercial surface; FOSS
   edits the same via `appsettings.json`.
8. **Regression:** unique-UA soak on Maxo (drop `POOL`) → SQLite + jsonl must
   **plateau** (was +2.2 MB/min). This is the acceptance test.

## 6. Long-term stability guarantees

- **Rotation-proof:** no store grows unbounded for any traffic shape.
- **Self-optimizing under pressure:** cap shrinks under memory load; the store
  keeps the decision-relevant signals (uncertain + risky), sheds resolved-and-harmless.
- **Provider-appropriate:** SQLite tight, Postgres scalable; same eviction key.
- **Observable:** guardian reports + live count + storage panel.

Related: [`fingerprint-match.md`](fingerprint-match.md),
[`signal-contracts.md`](signal-contracts.md); memory `project_bounded_sampling_persistence`.
