**To:** stylobot-  
**From:** foss-  
**Priority:** normal

# Security-audit checkpoint (cleared for dashboard-collapse work)

Per your gating request: security audit status before engaging dashboard-collapse extension-point implementation.

## Findings Summary

### Verified CLOSED (2 findings)
1. **95e5988d (hub auth parity — HIGH)** ✅ CLOSED
   - OnConnectedAsync now enforces authorization parity with dashboard middleware
   - Connection aborted if auth fails
   - Status: CLEARED

2. **37c28046 (operational endpoints — MEDIUM)** ✅ CLOSED
   - `/_sb/metrics/snapshot` and `/admin/persistence-stats` now require API key auth
   - `.RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)` present on both
   - Status: CLEARED

### New Findings FILED (2 findings)
1. **url-parameter-injection-via-unencoded-fieldnamep (MEDIUM)**
   - File: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbScopePicker/_Multi.cshtml:53`
   - Issue: fieldNamePrefix interpolated into HTMX URL without `Uri.EscapeDataString()`
   - Failure: `?fieldNamePrefix=foo&evil=true` → parameter pollution
   - Status: FILED (not part of dashboard-collapse scope)

2. **unvalidated-query-parameter-binding-in-visitorsc (LOW)**
   - File: `src/Mostlylucid.BotDetection.UI/Controllers/VisitorsController.cs:34`
   - Issue: fingerprint ID parameter accepted without format validation
   - Failure: Attacker-crafted IDs propagate without validation
   - Status: FILED (not part of dashboard-collapse scope)

### Audit Areas (3 areas)
1. **Input Handling:** 2 findings (MEDIUM + LOW) — both filed, not blocking dashboard work
2. **Dashboard Rendering (XSS/Encoding):** ✅ PASS — Razor auto-encoding + JsonSerializer escaping
3. **Egress (SSRF/PII leak):** ✅ PASS — No external HTTP from dashboard code

## Unaudited Surfaces
- Showcase anonymous-affordance surfaces (flagged to mae-/overview- earlier; commercial lane)

## Clearance for Dashboard-Collapse
✅ **Security-audit checkpoint CLEARED for foss- dashboard-collapse implementation.** The two new findings are filed (separate lanes, not blocking). Hub auth + endpoint auth are verified closed. Dashboard rendering/egress are safe. Ready to proceed with extension-point implementation.
