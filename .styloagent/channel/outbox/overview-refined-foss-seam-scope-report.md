**To:** overview-  
**From:** foss-  
**Priority:** normal

# Refined FOSS Seam Report (Trial License) — Scope 1-4 Analysis

Acknowledged: mae- owns fixing the broken ILicenseState plumbing on commercial side. FOSS scope refined to write-path hook + interface seam + timing confirmation + banner scope.

---

## 1. Write-Path Hook (Your Lane)

**Scope:** Gate all learning-write call sites on `!LearningFrozen` before persisting. Detection/read path completely untouched.

**Call sites to gate (10 total):**
- Fingerprint centroid/observation: RecordObservationAsync, AbsorbObservationAsync, RecordCorrectionAsync, UpdateRollupCentroidAsync, RecordVerdictAsync, RecordVerdictWriteBehind, BumpAmbiguityPersistenceAsync
- Session: RecordTransitionAsync, SessionPersistenceService.FlushAsync
- Trust: RecordClaimVerificationAsync

**Pattern:** Inject `ILearningFreezeState`, gate each write with `if (!state.LearningFrozen) { await write(...) }`

**Status:** Ready to implement once interface seam (item 2) is approved.

---

## 2. Interface Seam (Architectural — Your Call)

**Recommendation: FOSS-owned interface + no-op impl, commercial provides real impl via DI.Replace**

Reasoning: Mirrors other FOSS seams (IFingerprintReader, ISessionStore, etc.). Zero FOSS→commercial dependency.

**Proposed pattern:**

```csharp
// FOSS: Mostlylucid.BotDetection/Identity/ILearningFreezeState.cs
namespace Mostlylucid.BotDetection.Identity
{
    public interface ILearningFreezeState
    {
        bool LearningFrozen { get; }
    }

    internal sealed class FossLearningFreezeState : ILearningFreezeState
    {
        public bool LearningFrozen => false;  // FOSS never freezes
    }
}

// Commercial: wrap ILicenseState → ILearningFreezeState
services.AddSingleton<ILearningFreezeState>(sp => 
    new LicenseStateFacade(sp.GetRequiredService<ILicenseState>()));
```

**Call this approved? Or prefer a different seam pattern?**

---

## 3. Hot-Reload Timing (`ILicenseManager`)

**Finding:** `LicenseStateRefreshService` uses `IOptionsMonitor<LicensingOptions>` (live config detection) + 60s refresh tick via `IScheduleCoordinator.Tick1m`.

**Answer:** License token changes are **picked up LIVE within 60 seconds** — no gateway restart needed.

**UX implication:** Trial customer changes license token in portal → gateway detects within 60s → learning resumes automatically.

---

## 4. Dashboard-Chrome Banner Slot (Scope Only)

**Finding:** FOSS `/_stylobot` middleware has no license-state UI extension point. Self-hosted trial deployments need a banner slot in dashboard chrome.

**Scope proposal:**
- Add optional `ILicenseStateRenderer` interface (FOSS-owned, null default)
- Commercial supplies impl to render trial/grace/expired badges in dashboard navbar
- No build yet — confirm scope before I enumerate the chrome extension point

**Priority:** Lower (portal pages already render trial state fine)

---

## Next Steps

1. **Approve interface seam** (item 2) — once confirmed, I implement write-path hook (item 1)
2. **Confirm banner scope** (item 4) — list the chrome extension point once you approve
3. **mae- completes plumbing** — once gates land, LearningFrozen reflects real token state

