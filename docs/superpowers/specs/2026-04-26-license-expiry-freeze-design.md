# License Expiry Freeze - Design Spec

**Date:** 2026-04-26
**Status:** Approved
**Scope:** `Mostlylucid.BotDetection` (FOSS - unaffected), `stylobot-commercial` (portal grace period claim), `Mostlylucid.BotDetection` commercial DI path (hard gate + freeze)

---

## Summary

Commercial StyloBot requires a valid license JWT to start. When a license expires, a one-time 30-day grace period activates: detection and blocking continue normally, but all learning services freeze. After the grace period, the system drops to log-only mode with frozen patterns. Renewing the license immediately unfreezes learning and restores all actions. FOSS is entirely unaffected.

---

## State Machine

| State | Detection | Learning | Actions | Trigger |
|---|---|---|---|---|
| FOSS (no license) | Full | Full, forever | Block/throttle/etc. | `AddBotDetection()` only |
| Commercial, active | Full | Full | Block/throttle/etc. | Valid, non-expired JWT |
| Commercial, grace | Full | **Frozen** | Block/throttle/etc. | JWT expired, `grace_eligible: true`, within 30d |
| Commercial, post-grace | Full | **Frozen** | **Log-only** | Grace window elapsed |
| Commercial, renewed | Full | Unfreezes immediately | Block/throttle/etc. | New valid JWT loaded |
| Commercial, no license at startup | **Won't start** | n/a | n/a | Missing/invalid JWT at boot |

---

## Part 1: Hard Commercial Gate at Startup

### Behavior

When commercial packages are loaded (`AddStyloBot()` with commercial DI), the registration path validates that a license JWT is present and parseable before registering learning services. If absent or invalid, startup throws with a clear message:

```
StyloBot commercial features require a valid license.
Start a free 30-day trial at https://stylo.bot
```

FOSS registration path (`AddBotDetection()`) never checks for a license and is entirely unaffected.

### Implementation

- New `LicenseStartupValidator` called from commercial `ServiceCollectionExtensions`
- Reads license path from `BotDetectionOptions.CommercialOptions.LicensePath`
- Validates JWT signature using bundled Ed25519 public key
- On failure: throws `InvalidOperationException` with the message above
- On success: registers `ILicenseState` singleton with the parsed claims

---

## Part 2: `ILicenseState` - Central Freeze Signal

New singleton interface, resolved once at startup from the validated JWT and refreshed on a 1-minute background timer (re-reads the license file from disk to pick up renewals without restart).

```csharp
public interface ILicenseState
{
    bool IsActive { get; }           // valid license, not expired
    bool IsInGrace { get; }          // expired, grace window active
    bool LearningFrozen { get; }     // IsInGrace || post-grace (freeze write ops)
    bool LogOnly { get; }            // post-grace (no blocking actions)
    DateTimeOffset? GraceEndsAt { get; }
    DateTimeOffset? ExpiresAt { get; }
}
```

### Computed properties

```
IsActive      = now < ExpiresAt
IsInGrace     = !IsActive && grace_eligible (JWT claim) && now < grace_started_at + 30d
LearningFrozen = !IsActive
LogOnly       = !IsActive && !IsInGrace
```

### Grace started tracking

`grace_started_at` is persisted to a `license_state` table in the local SQLite database (one row). Written the first time `!IsActive && grace_eligible` is observed. Cleared when a new valid license is loaded.

```sql
CREATE TABLE IF NOT EXISTS license_state (
    id               INTEGER PRIMARY KEY DEFAULT 1,
    grace_started_at INTEGER,   -- Unix ms, NULL until grace begins
    updated_at       INTEGER NOT NULL
);
```

### Refresh

`LicenseStateRefreshService` (`BackgroundService`) re-reads the license file every 60 seconds. When a renewed license appears on disk, `IsActive` flips to true, `LearningFrozen` becomes false, and `grace_started_at` is cleared. No restart required.

---

## Part 3: Learning Freeze Guards

Each learning service adds a single guard at the top of its periodic/event-driven write operation. No restructuring needed.

### Services that check `ILicenseState.LearningFrozen`

| Service | Method guarded |
|---|---|
| `ReputationMaintenanceService` | `RunDecaySweepAsync`, `RunGcAsync`, `HandleEventAsync` |
| `BotClusterService` | `RunClusteringAsync` |
| `CentroidSequenceRebuildHostedService` | `OnClustersUpdated` handler |
| `LearningBackgroundService` | `ProcessEventAsync` |

### Guard pattern

```csharp
// At top of each guarded method:
if (_licenseState.LearningFrozen)
{
    _logger.LogDebug("Learning frozen (license expired). Skipping {Operation}.", nameof(RunDecaySweepAsync));
    return;
}
```

### What is NOT frozen

- Detection reads from the frozen pattern cache (reputation lookups, centroid matching) - unchanged
- SQLite session/signature persistence - unchanged (historical data still accumulates)
- Dashboard data - unchanged
- GeoLite2 updates - unchanged (not learning)

---

## Part 4: Log-Only Mode After Grace

When `ILicenseState.LogOnly` is true, the action policy system is forced to `logonly` regardless of configured policy. This is enforced in `BotDetectionMiddleware` (or the action dispatch layer) before any action is applied.

```csharp
// In action dispatch:
if (_licenseState.LogOnly)
    effectivePolicy = ActionPolicy.LogOnly;
```

A dashboard banner surfaces the state:
- **Grace active:** "License expired. Detection active, learning paused. X days remaining before log-only mode. [Renew]"
- **Post-grace:** "License expired. Actions disabled (log-only). [Renew to restore blocking]"

---

## Part 5: Grace Period Claim in License JWT

The portal (`LicenseIssuer`) embeds a `grace_eligible` boolean claim in every license JWT:

- **Trial license:** `grace_eligible: true`
- **Paid license (first):** `grace_eligible: true`
- **Renewal after grace was consumed:** `grace_eligible: false`

The portal tracks whether an organization has consumed a grace period in the `License` table (new `grace_consumed_at` nullable column). When issuing a renewal after `grace_consumed_at` is set, the new JWT carries `grace_eligible: false`.

Grace window duration changed from current 7 days to **30 days** (`PortalLicenseOptions.GracePeriodDuration`).

---

## Part 6: Pricing and Documentation Updates

### Docs to update (`Mostlylucid.BotDetection/docs/`)

- `configuration.md` - Add license path configuration option
- New: `licensing.md` - Trial, expiry, grace period, renewal behavior; explicit note that FOSS needs no license

### Pricing page (stylobot-commercial)

Update to reflect:
- "30-day free trial (one per organization, license required)"
- "License expires? 30-day grace period included - detection and blocking continue, learning pauses"
- "Renew anytime to immediately restore full learning"
- Clear FOSS callout: "Self-hosted FOSS? No license, no limits, no expiry"

---

## Files to Create/Modify

### New files

1. `Mostlylucid.BotDetection/Licensing/ILicenseState.cs` - Interface
2. `Mostlylucid.BotDetection/Licensing/LicenseState.cs` - Implementation (reads JWT claims + SQLite grace state)
3. `Mostlylucid.BotDetection/Licensing/LicenseStartupValidator.cs` - Startup hard gate
4. `Mostlylucid.BotDetection/Licensing/LicenseStateRefreshService.cs` - 60s background refresh
5. `Mostlylucid.BotDetection/docs/licensing.md` - New licensing doc

### Modified files

6. `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` - Register `ILicenseState`, `LicenseStateRefreshService`; call `LicenseStartupValidator` on commercial path
7. `Mostlylucid.BotDetection/Services/ReputationMaintenanceService.cs` - Inject `ILicenseState`, add freeze guards
8. `Mostlylucid.BotDetection/Services/BotClusterService.cs` - Inject `ILicenseState`, add freeze guard
9. `Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs` - Inject `ILicenseState`, add freeze guard
10. `Mostlylucid.BotDetection/Services/LearningBackgroundService.cs` - Inject `ILicenseState`, add freeze guard
11. `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` (or action dispatch) - Force log-only when `LogOnly`
12. Portal: `LicenseIssuer.cs` - Add `grace_eligible` claim; track `grace_consumed_at`
13. Portal: `Entities.cs` - Add `grace_consumed_at` to `License` entity + migration
14. Portal: `PortalLicenseOptions.cs` - Change `GracePeriodDuration` default to 30 days
15. `Mostlylucid.BotDetection/docs/configuration.md` - Add license path option
16. `Mostlylucid.BotDetection/docs/licensing.md` - New doc

---

## What This Does NOT Do

- No phone-home or online verification (license is validated locally from JWT signature)
- No hard block of detection (detection always runs regardless of license state)
- No FOSS code changes whatsoever
- No data deletion on expiry (frozen patterns remain readable)
- No mid-request interruption (state is read once per minute, not per request)
