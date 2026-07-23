---
name: foss-session-2026-07-23-reengaged-task1-task2
description: Re-engaged by overview-. Task 1 (csproj gaps) does NOT reproduce — reported, awaiting ecommerce- repro info. Task 2 (upstream-always-200) root cause FOUND — DegradationAtom/UpstreamHealthGate never wired to real traffic in production. Design sent to overview-, gated for approval, NOT built yet.
metadata:
  type: project
---

# foss- saved context (2026-07-23, re-engaged) — AWAITING overview- GATE

## Task 1 — build gaps (P1) — does not reproduce
Wiped obj/bin for PrometheusPack/UI/core, `dotnet build mostlylucid.stylobot.sln` clean = 0 errors. VYaml
1.4.0 + Mostlylucid.Common 8.0.0-alpha2 are both already direct `PackageReference`s in
`Mostlylucid.BotDetection.csproj` (flow transitively) and both genuinely published on nuget.org (checked the
flatcontainer index directly, not just local cache). No fix made — nothing to fix. Reported to overview-,
asked for ecommerce-'s exact repro (branch/command/log) since I can't reproduce from source.

## Task 2 — "upstream always returns 200" investigation — ROOT CAUSE FOUND, DESIGN SENT (gated, not built)
**Client status-code propagation is fine**: no YARP transform in `Stylobot.Gateway` touches `Response.StatusCode`
(checked `AddTransforms` in `ServiceCollectionExtensions.cs`, `UpstreamTimingTransform`,
`TlsFingerprintingTransform`) — standard passthrough. Per-request capture is also fine
(`BotDetectionMiddleware.EmitResponseSignals` reads status post-`_next`; PR #117 fix intact, verified live).

**Real gap**: the aggregate upstream-health subsystem is dead code in production —
- `DegradationAtom` (5xx/4xx/404/429 EWMA + latency) — **never registered in DI** anywhere (not
  `AddBotDetection*`, not Gateway). Only unit tests construct it directly.
- `DegradationAtom.RecordResponse(...)` — **zero production call sites**. The intended wiring
  (`if (context.IsResponseFromUpstream()) degradationAtom?.RecordResponse(...)`) exists only as a doc-comment
  in `BotDetectionMiddlewareDegradationAtomGateTests.cs` line 15 — never actually added to
  `BotDetectionMiddleware.EmitResponseSignals`.
- `UpstreamHealthGate` (reads the atom to suppress status-derived false-positives during outages) —
  also never registered; that outage-protection feature never engages.
- `DegradationStoreSampler` (UI) resolves the atom via `GetService<DegradationAtom>()` → always null →
  permanently inert → **zero `DegradationSnapshot` rows ever persisted**, in any deployment.
- `SiteHealthChartletBuilder`/`SbSiteHealth` render from that always-empty history → "Upstream healthy, no
  incidents" is the EMPTY-DATA DEFAULT, not an observed absence of errors. I saw this exact message live on
  Traffic page during an earlier smoke test, before knowing why. This is why the operator's `/dashboard/traffic`
  500 went unnoticed.

**Proposed minimal fix (sent to overview-, awaiting gate)**: (1) `TryAddSingleton<DegradationAtom>()` +
`TryAddSingleton<UpstreamHealthGate>()` in `AddBotDetection`; (2) inject optional `DegradationAtom?` into
`BotDetectionMiddleware`, call `RecordResponse(statusCode, latencyMs, path)` in `EmitResponseSignals` gated on
`IsResponseFromUpstream()`, reusing `UpstreamTimingTransform`'s stamped latency when present. No dashboard
changes needed — the chartlet/card already render whatever snapshot history exists. Small, precisely-scoped
(one registration line + ~2 lines in one method). **Do not build until overview- gates it.**

## Task 2b update — Logs view spec: FLAGGED as likely duplicate/misrouted, not specced
overview- refined Task 2 into a bigger "Logs view" ask (list requests/sessions with correlated Warning+
logs, sourced from the existing LFU, no second store, PII-audited). Before speccing, read
`stylobot-commercial/.styloagent/channel/saved-context/otel-context.md` — the actual infra
(`FingerprintTimelineAtom`, `IOtelMeshTimelineReader`, `IRecentLogEntriesProvider`, `LogSinkOptions`/OTLP
log-sink) all live in `Stylobot.Commercial.OtelMesh` / `Stylobot.Commercial.AspNetPack` (commercial repo,
otel-/aspnet- owned) — none of it is in my FOSS checkout. `otel-`'s own context already has this exact task
queued verbatim as "LOOSE END #2" (fingerprint-anchored OTel+logs correlation, gated behind reading
`project_fingerprint_anchored_otel_logs`, coordinate w/ aspnet-, NOT started). Flagged to overview- rather
than duplicate/guess at commercial internals I can't see: either route 2b to otel-/aspnet- who already own
it, or tell me the specific FOSS-side seam needed (if any) once they've scoped the read contract. Task 2a
(the factual "does the gateway mask 5xx to 200" answer) is reconfirmed and closed — no.

## Next step if resuming
Waiting on overview- reply on: Task 1 repro info, Task 2b routing decision. Nothing else pending.

## Current state
- **Branch:** foss/dashboard-collapse
- **HEAD:** 15c8f63c (feat(dashboard): shared _DetectionReasons partial for signature-detail)
- **Repo:** /Users/scottgalloway/RiderProjects/stylobot (FOSS) — shared checkout, fixing in place
- **Coordination:** commercial repo at /Users/scottgalloway/RiderProjects/stylobot-commercial

## 2026-07-23: re-dispatch of the same 2026-07-18 mission — stale, no action taken
`.styloagent/missions/foss-.md` handed me the identical Visitors-bare-table / Top-Content-lost-links
mission already closed out in the 2026-07-20 checkpoint below. Re-verified from scratch (not just trusting
the old checkpoint) since the user was described as furious about a recurrence:
- Read current source: `_Visitors.cshtml` → `Visitors/Index.cshtml` → `SbVisitorList` ViewComponent →
  `Default.cshtml` — full UA/Ver/bot-type/risk/prob/hits/action/seen columns + filter pills + drift badges,
  all intact.
- Read current source: `Traffic/_Body.cshtml` — `<sb-endpoints-list compact="true" content-only="true">`
  + `#endpoint-detail-panel` target, both intact (this is the ec6907af/4bb79e26 fix).
- Ran the Demo app locally and loaded both pages in a real browser (`/stylobot/visitors`,
  `/stylobot/traffic`): Visitors rendered the full rich table with real rows; Traffic's "Top content
  pages" rendered the SbEndpointsList empty state correctly (no content-page hits landed in the 24h
  window during this manual smoke test — loopback curl traffic classifies as `botType=Internal`, which
  the content-pages aggregate appears to exclude from "external" traffic; that's a data/classification
  question, not a rendering regression, and out of scope for this mission).
- git-log swept 7 days of Views/ViewComponents commits: continuous fix trail from 4bb79e26 (07-16) through
  ec6907af (07-20) to a18963b6 (07-22) — all ancestors of current HEAD.

**Conclusion: neither regression reproduces on current HEAD. No fix commit made this session — there was
nothing to fix.** Told overview- to check whether the "recurrence" report is against a stale deployment /
browser cache rather than this checkout, since this IS the shared checkout the whole fleet's local builds
reference.

**Gotcha for future me:** the dashboard route is `{BasePath}/{area}` (e.g. `/stylobot/visitors`), NOT
`{BasePath}/dashboard/{area}` — hitting `/stylobot/dashboard/visitors` 404s/renders "Unknown dashboard
section" because `ParseRowRef` treats `dashboard` as the area segment. CLAUDE.md's `/dashboard/traffic`
examples describe the *commercial* mount path convention, not this Demo host's `/stylobot` BasePath.

---

# Prior checkpoint (2026-07-20)

## Missions completed this session

### 1. FOSS Dashboard Regressions (2026-07-18)
**Status: BOTH FIXED ✓**

**Issue #1: Visitors page bare table**
- Root: `_Visitors.cshtml` partial invoked ViewComponent with NO query parameters
- Fix: Extract country/bot_type/threat/fingerprint/internal from HttpContext.Request.Query
- Pattern: Mirrors _Traffic.cshtml — all dashboard registry rows extract + pass URL filters
- Commit: ec6907af (main)

**Issue #2: Top Content pages bare card**
- Root: Was re-implemented as bare `<ul>` instead of reusing SbEndpointsList component
- Status: Already fixed in HEAD — uses `<sb-endpoints-list compact="true" content-only="true">` + #endpoint-detail-panel target
- Verified in: _Body.cshtml lines 153-168

**Hard principle:** Never build new "Top X" controls when existing components can be styled + pre-filtered.

### 2. Learning-Write Freeze Gate (Seam analysis)
**Status: ANALYSIS COMPLETE, AWAITING OVERVIEW- APPROVAL**

**Deliverable:** Message to overview- at `.styloagent/channel/outbox/overview-foss-seam-request-read-only-analysis.md`

**Key findings:**
- ILicenseState location: `Stylobot.Commercial.Licensing` (clean, FOSS no-op impl exists)
- Seam pattern: Create thin FOSS-owned `ILearningFreezeState` interface (avoids commercial dependency)
- Call sites enumerated: 10 major write surfaces (fingerprint centroid/observation, session Markov, trust state)
- Gate design: "skip write when frozen" — zero impact on detection/read paths

**Next (pending approval):**
1. Create ILearningFreezeState + FossLearningFreezeState no-op impl in FOSS
2. Inject into all 10 learning-write call sites
3. Gate each with `if (!state.LearningFrozen) { write() }`
4. Commercial wraps ILicenseState via DI adapter

## Files touched
- src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Visitors.cshtml (fixed)
- Memory: MEMORY.md (compacted), project_dashboard_regression_fixes_2026_07_20.md (added)
- Channel: analysis sent to overview- in outbox/

## Blocked/pending
- Learning-write gate implementation: awaiting overview- approval on seam pattern
- C4a seam: overview- owns EndpointPolicy extension seam (caps-atom blocked)
- TLS ClientHello: deferred post-staging
- StyloExtract: tier decision pending (mae-)

## For next session
Pick up from overview- response. If approved:
- File path: outbox message contains full call-sites list (10 points, 3 subsystems)
- Changes scoped: one interface + registrations + 10 gate points, all in FOSS detection namespace
- No detection/read path changes required
- Independent work stream
