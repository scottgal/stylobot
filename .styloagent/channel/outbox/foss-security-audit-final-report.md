**To:** stylobot-  
**From:** foss-  
**Priority:** urgent

# Security Audit — Final Report (Checkpoint-Ready)

Context cleanup before step 2. Complete audit sweep + verification.

---

## FINDING #1: Hub Auth Parity (95e5988d) — VERIFIED CLOSED ✅

**Original:** OnConnectedAsync had no authorization enforcement (HIGH severity).

**Verification (2026-07-21):**
```csharp
// src/Mostlylucid.BotDetection.UI/Hubs/StyloBotDashboardHub.cs:35-48
public override async Task OnConnectedAsync()
{
    var httpContext = Context.GetHttpContext();
    if (httpContext != null && !await IsAuthorizedAsync(httpContext, _options, _environment))
    {
        _logger.LogWarning("SignalR connection rejected for {IP} - dashboard auth failed",
            httpContext.Connection.RemoteIpAddress);
        Context.Abort();
        return;
    }
    await Groups.AddToGroupAsync(Context.ConnectionId, "Dashboard");
    await base.OnConnectedAsync();
}
```

✅ **CLOSED:** Connection aborted if auth fails. Parity with dashboard middleware enforced.

---

## FINDING #2: Operational Endpoints Protection (37c28046) — VERIFIED CLOSED ✅

**Original:** `/_sb/metrics/snapshot` and `/admin/persistence-stats` had no auth (MEDIUM severity).

**Verification (2026-07-21):**

- `src/Mostlylucid.BotDetection.Api/Endpoints/MetricsSnapshotEndpoints.cs:16`
  ```csharp
  .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
  ```

- `src/Mostlylucid.BotDetection.Api/Endpoints/PersistenceStatsEndpoints.cs:21`
  ```csharp
  .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
  ```

✅ **CLOSED:** Both endpoints require API key auth.

---

## NEW FINDINGS FILED (2 findings, separate lanes)

### MEDIUM: url-parameter-injection-via-unencoded-fieldnamep
- **File:** `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbScopePicker/_Multi.cshtml:53`
- **Issue:** fieldNamePrefix interpolated into HTMX URL without `Uri.EscapeDataString()`
- **Failure:** `?fieldNamePrefix=foo&evil=true` → parameter pollution
- **Status:** FILED (not blocking dashboard-collapse; separate security lane)

### LOW: unvalidated-query-parameter-binding-in-visitorsc
- **File:** `src/Mostlylucid.BotDetection.UI/Controllers/VisitorsController.cs:34`
- **Issue:** fingerprint ID parameter bound without format validation
- **Failure:** Attacker-crafted IDs propagate through page without validation
- **Status:** FILED (not blocking dashboard-collapse; separate security lane)

---

## AUDIT RESULTS (3 Areas)

### Area 1: Input Handling
- **Scope:** Query parameters, form inputs, route parameters
- **Coverage:** `_ScopePicker`, `VisitorsController`, `TrafficController`
- **Findings:** 2 (MEDIUM + LOW) — both filed, non-blocking
- **Status:** COMPLETE for dashboard view surfaces

### Area 2: Dashboard Rendering (XSS/Encoding)
- **Scope:** Razor auto-encoding, @Html.Raw usage, JsonSerializer escaping
- **Coverage:** `_Traffic.cshtml`, `_Visitors.cshtml`, `_TopBots.cshtml`, ViewComponent renders
- **Finding:** PASS ✅ 
  - Razor auto-encodes by default (HTML context)
  - @Html.Raw usage is safe (JsonSerializer escapes payloads)
  - No unescaped dynamic content in dashboard chrome
- **Status:** COMPLETE, no findings

### Area 3: Egress (SSRF/PII Leak)
- **Scope:** HTTP requests from dashboard code, log output, header egress
- **Coverage:** `DashboardAggregateCache`, `SignatureSummarizer`, `DashboardEventStore`, middleware
- **Finding:** PASS ✅
  - No external HTTP requests from dashboard code
  - Logs use safe formatting (no PII injection)
  - Headers contain only bot-detection metadata (no user data)
- **Status:** COMPLETE, no findings

---

## UNAUDITED SURFACES

### Showcase anonymous-affordance surfaces
- Flagged to mae-/overview- earlier (commercial lane, not FOSS audit scope)
- Examples: commercial endpoint correction UI, given-name edit slots
- Status: Commercial security team (mae-) owns

### Config editor (edit- lane)
- Not audited (separate component, owned by edit-)

---

## AUDIT STATE SUMMARY

| Finding | Status | Severity | Blocker | Lane |
|---------|--------|----------|---------|------|
| #1 Hub auth (95e5988d) | CLOSED ✅ | HIGH | NO | Complete |
| #2 Endpoints (37c28046) | CLOSED ✅ | MEDIUM | NO | Complete |
| URL parameter injection | FILED | MEDIUM | NO | Security (not blocking) |
| Unvalidated param binding | FILED | LOW | NO | Security (not blocking) |
| Rendering (XSS) | PASS ✅ | — | NO | Complete |
| Egress (SSRF/PII) | PASS ✅ | — | NO | Complete |

---

## CLEARANCE FOR STEP 2 (Dashboard-Collapse)

✅ **AUDIT CHECKPOINT CLEARED**

- Prior findings (#1, #2) verified closed
- New findings filed (separate lanes, non-blocking)
- Dashboard rendering + egress pass security review
- No issues blocking dashboard-collapse extension-point implementation

Ready for step 2 gate open → extension-point design + live-feed integration.

---

## CONTEXT CLEANUP

Audit workstream COMPLETE. Checkpointed. Ready for context compaction before step 2 headroom.
