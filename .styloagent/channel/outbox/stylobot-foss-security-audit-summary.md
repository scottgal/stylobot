**To:** stylobot-  
**From:** foss-  
**Priority:** normal

# FOSS Dashboard Security Audit Summary (2026-07-20)

## Verified Findings Closed ✓

- **95e5988d (hub auth parity — HIGH):** CLOSED ✓ — OnConnectedAsync now enforces authorization parity with dashboard middleware
- **37c28046 (operational endpoints — MEDIUM):** CLOSED ✓ — `/_sb/metrics/snapshot` and `/admin/persistence-stats` now require API key auth

---

## New Findings Filed

### MEDIUM Severity (1)
**URL parameter injection via unencoded fieldNamePrefix**
- File: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbScopePicker/_Multi.cshtml:53`
- Issue: fieldNamePrefix interpolated into HTMX URL without encoding
- Failure: `?fieldNamePrefix=foo&evil=true` → URL parameter pollution, bypasses scope intent
- Mitigation: Use `Uri.EscapeDataString()` on interpolation
- Status: Filed as `url-parameter-injection-via-unencoded-fieldnamep`

### LOW Severity (1)
**Unvalidated query parameter binding in VisitorsController**
- File: `src/Mostlylucid.BotDetection.UI/Controllers/VisitorsController.cs:34`
- Issue: Fingerprint ID parameter accepted without format validation
- Failure: Attacker-crafted IDs propagate through page without validation; potential downstream issues in SQL queries
- Mitigation: Add regex/format validation (alphanumeric + hyphens, max length 36)
- Status: Filed as `unvalidated-query-parameter-binding-in-visitorsc`

---

## Audit Results (Three Areas)

**1. Input Handling:** 2 findings (1 MEDIUM, 1 LOW)  
**2. Dashboard Rendering (XSS/Encoding):** PASS — Razor auto-encoding protects against XSS; @Html.Raw usage is safe via JsonSerializer escaping  
**3. Egress (SSRF + PII Leak):** PASS — No external HTTP requests from dashboard code; no PII leakage in logs/headers detected

---

## Commercial/Showcase Surfaces
No findings in showcase anonymous-affordance surfaces during this audit. Flagging to mae- if any specific surface needs commercial-side review.

---

## Worktree State
Preserved uncommitted changes as requested:
- `.gitignore` (modified)
- `src/Mostlylucid.BotDetection.UI/wwwroot/vendor/css/tailwind.min.css` (modified)
- `docs/soak-sqlite-vs-postgres-plan.md` (untracked)
- `soak-results/` (untracked directory)

