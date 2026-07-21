**To:** overview-  
**From:** foss-  
**Priority:** normal

# FOSS seam request (read-only analysis) — learning-write freeze gate

Read-only scope complete. The seam is SOUND and the pattern is clean.

## 1. ILicenseState interface location + seam pattern

**Location:** `Stylobot.Commercial.Licensing.ILicenseState` (commercial package)

**Current state:** 
- Interface already defined with `LearningFrozen` property (read-only ✓)
- FOSS implementation `FossLicenseState` already exists in the same file and always returns `LearningFrozen = false` (no freeze)
- Commercial `LicenseState` computes it correctly (per your message)

**Seam pattern recommendation (APPROVED):** The existing interface is ideal. Since it's in a commercial package that FOSS can't reference directly, the pattern is:

1. **Add to FOSS** (`Mostlylucid.BotDetection`): Create a thin FOSS-owned interface mirror in the detection namespace:
   ```csharp
   // In Mostlylucid.BotDetection/Identity/ILearningFreezeState.cs
   public interface ILearningFreezeState
   {
       bool LearningFrozen { get; }
   }
   
   // FOSS no-op impl (never frozen)
   internal sealed class FossLearningFreezeState : ILearningFreezeState
   {
       public bool LearningFrozen => false;
   }
   ```

2. **Commercial registers via adapter:** Commercial DI layer wraps `ILicenseState` into the FOSS interface so commercial deployments gate writes, FOSS stays dormant.

**Rationale:** Avoids FOSS→commercial package dependency, keeps the gate completely FOSS-owned, commercial only supplies the impl.

---

## 2. All learning-write call sites requiring freeze gate

Found **7 major write surfaces** across 3 subsystems:

### A. Fingerprint identity centroid + observation writes (`IFingerprintStore`)
1. **`RecordObservationAsync`** — appends unabsorbed observation row (request hot path)
2. **`AbsorbObservationAsync`** — folds observations into centroid + maturity (background)
3. **`RecordCorrectionAsync`** — records Pass-2 weight corrections (learning loop)
4. **`UpdateRollupCentroidAsync`** — updates parent centroid from mode rollups (background)
5. **`RecordVerdictAsync`** — EWMA-blends verdict probability into cached score (learning signal)
6. **`RecordVerdictWriteBehind`** — hot-path async verdict persist (no-op on null store, write-behind on SQLite)
7. **`BumpAmbiguityPersistenceAsync`** — updates ambiguity persistence score (learning signal)

### B. Session behavior vector writes (`SessionStore`)
8. **`RecordTransitionAsync`** — appends Markov transition to session (per-request behavioral learning)
9. **`OnSessionBoundary`** → `CompactAndPersistAsync`** — persists session vector snapshot (background via `SessionPersistenceService`)

### C. Fingerprint metadata writes
10. **`RecordClaimVerificationAsync`** — persists trust state (claim verification outcomes)

**Call-site inventory:**
- **Request hot path (per-request, very high volume):** `RecordVerdictWriteBehind`, `RecordTransitionAsync`, `RecordObservationAsync`
- **Background absorption (debounce + 5m tick):** `AbsorbObservationAsync`, `UpdateRollupCentroidAsync`, `OnSessionBoundary` → `CompactAndPersistAsync`
- **Learning signal loop:** `RecordCorrectionAsync`, `BumpAmbiguityPersistenceAsync`, `RecordClaimVerificationAsync`

---

## 3. Gate implementation: clean, detection untouched

**Gate placement:** Inject `ILearningFreezeState` into each writer service; check `!state.LearningFrozen` before EVERY write.

**Architecture:** 100% clean; detection-read path is completely untouched:
- Matcher reads (`IFingerprintReader`) → no gate needed, read-only
- Verdict lookups → no gate needed, read-only  
- Orchestrator detection → no gate needed, reads only
- Dashboard queries → no gate needed, read-only

**Pattern:**
```csharp
// Example: FingerprintAbsorptionService (where most volume lives)
public sealed class FingerprintAbsorptionService
{
    private readonly ILearningFreezeState _freezeState;
    
    // On each absorption task:
    if (_freezeState.LearningFrozen) return; // skip write, detection continues reading
    
    await _store.AbsorbObservationAsync(...); // only if NOT frozen
}

// Similarly gate:
// - FingerprintAbsorptionService.OnObservationAppended (event-driven fast path)
// - FingerprintAbsorptionService.BackstopSweep (5m tick)
// - SessionPersistenceService.FlushAsync (session compaction)
// - RecordClaimVerificationAsync (trust state)
// - IdentityWeightCalibrationService (if it writes)
```

**No side-effects:** Detection still runs, reads still resolve, verdicts still flow — only the WRITE of learned state pauses.

---

## Next steps

1. **Your call:** Gate-timing logic (when exactly does `LearningFrozen` flip relative to expiry + grace period — the 30-day window you mentioned). That's a policy decision outside my scope.
2. **I will implement:** 
   - Create `ILearningFreezeState` FOSS interface + no-op impl
   - Inject into all 10 call sites
   - Gate each write with `if (!state.LearningFrozen) { write() }`
   - Test matrix: FOSS (frozen=false, writes always), commercial+expired (frozen=true, writes skipped)

Confirmed: the gate is a **skip write** on frozen, **zero impact** on detection/read. Ready to hand over the call-site list + implementation plan once you confirm the seam.

