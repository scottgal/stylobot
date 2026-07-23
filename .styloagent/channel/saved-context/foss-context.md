---
name: foss-session-2026-07-23-llamasharp-fix-shipped
description: Task 2 shipped (9e6d1f0c). LlamaSharp missing-PackageReference build bug (mae- found it) fixed + shipped (d50fd0f1) — confirmed IS in the gateway SKU build path. Task 1 (csproj gaps ecommerce- reported) still doesn't reproduce, standing by.
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

## Task 2 — SHIPPED, commit 9e6d1f0c on foss/dashboard-collapse (not pushed)
overview- approved the minimal fix. Built via TDD (RED confirmed on CS0117 for the not-yet-existing members
first): `BotDetectionModule.TryAddSingleton<DegradationAtom>()` + `<UpstreamHealthGate>()`; new
`internal static BotDetectionMiddleware.RecordDegradation(context, atom, requestStartTicks)` called from
`EmitResponseSignals`, gated on `IsResponseFromUpstream()`, resolves the atom per-request via
`context.RequestServices.GetService<DegradationAtom>()` (null-safe, matches DegradationStoreSampler's own
established safe pattern rather than an optional ctor param — its own comments warn that pattern bit them
before). Latency via new `ResolveUpstreamLatencyMs`: prefers gateway-stamped
`HttpContext.Items["StyloBot.ProxyTiming.UpstreamElapsedMs"]` (literal-duplicated, core can't reference
Gateway project), falls back to a stopwatch spanning `_next`. 7 new tests (2 DI registration + 5 middleware
gate/latency), 152 RateLimit/middleware/DI tests green, full solution builds clean. Noted one *unrelated*
pre-existing test failure in passing: `DashboardLinkIntegrityTests...` — `_TrafficPanels.cshtml` hardcoded
`/dashboard/…` mount, not touched by me, flagged to overview- in case dash- doesn't already have it.

## LlamaSharp build bug — SHIPPED, commit d50fd0f1 on foss/dashboard-collapse (not pushed)
mae- found a real one during an isolated-worktree verify: `Mostlylucid.BotDetection.Llm.LlamaSharp.csproj`
called `OptionsBuilder<T>.BindConfiguration()` (LlamaSharpServiceExtensions.cs:20) without referencing
`Microsoft.Extensions.Options.ConfigurationExtensions` — only had the base `Microsoft.Extensions.Options`.
Added the missing `PackageReference` (10.0.9, matches sibling ref). **Confirmed this project IS in the
gateway SKU build path** — `Stylobot.Gateway.csproj` has a direct ProjectReference to it — so overview-
is treating as urgent for the Maxo deploy.

Honest note for next-me: I could NOT reproduce the CS1061 on a clean rebuild of this checkout even before
the fix (0 errors either way) — dug in and confirmed `project.assets.json` genuinely has NO trace of
`Microsoft.Extensions.Options.ConfigurationExtensions` anywhere in the resolved graph for this project (no
direct ref, nothing transitive), so my local build succeeding was the anomaly (likely some warm-cache/SDK
quirk on this machine), not evidence the fix was unneeded. Opposite of the Task-1 VYaml/Mostlylucid.Common
situation, where the "missing" refs actually already existed elsewhere in the graph. Applied it anyway
since the reference being truly absent means a cold restore (Maxo/CI/fresh clone) would hit the same
CS1061 regardless of what my machine does. Verified clean rebuilds of the LlamaSharp project alone, the
full Gateway project, and the whole solution — 0 errors after the fix in all three.

## DEFINITIVE main-build verify — origin/main @ 15c8f63c builds clean, cold + network
overview- asked for a genuinely cold, network-forced check of FOSS origin/main (not the shared checkout,
which had another agent's active uncommitted WIP at the time -- did NOT touch it). Used an isolated
`git worktree add --detach <scratch> origin/main` + `dotnet restore -p:RestorePackagesPath=<fresh empty
folder>` (forces every package through the network, bypassing my warm ~/.nuget/packages) + full solution
build. **0 errors**, both suspect projects (UI: ephemeral 2.9.1 consistent, no type mismatch; LlamaSharp:
builds fine even WITHOUT my d50fd0f1 fix, on origin/main which predates it). Conclusion: main is not
broken; benchviz-/ecommerce-'s reported errors are environmental on their side (likely no nuget.org egress
or a restricted source list) -- reported this to overview- with the exact repro steps, and reconfirmed
9e6d1f0c + d50fd0f1 are both ready to fold into main whenever operator go lands. Cleaned up the worktree
after.

## 9e6d1f0c + d50fd0f1 CONFIRMED on FOSS origin/main
Reconciled deploy landed them (main @ 7732d185 includes both, plus hidden-nav). overview- then broadcast a
main-push FREEZE for the duration of this deploy cycle (verify on prod first) — no FOSS/commercial main
pushes without coordinating through overview- first; feature branches keep working locally, merge queue
reopens after. I have nothing queued to push (all local commits per instructions all along), so this is a
no-op for me — just holding.

## Fleet rule: ZERO keyless hits to prod (stylo.bot), ever
overview- hard-stop 2026-07-23 ~16:38: no agent hits the prod surface without the prod GUID key (keyless
hits poison the corpus + tarpit legit users, incl. the operator). I never have (only ever hit the local
Demo app on localhost) and I don't hold the key — deploy- owns that path. Just internalizing; no change
needed on my end.

## nuget.config/SDK comparison sent to ecommerce- (low-pri, bounded, done)
overview- confirmed the main-clean verdict was accepted + deploy unblocked; d50fd0f1/9e6d1f0c already on
FOSS origin/main, nothing pending from me this cycle. Follow-up ask: one bounded comparison with
ecommerce- to help their local-build gap. Sent: SDK 10.0.201 (matches theirs), no repo-local nuget.config/
global.json in either repo, my user-level NuGet.Config sources = nuget.org + two /tmp-local machine-
specific feeds (neither of which carries VYaml/Mostlylucid.Common). Best guess handed over: check whether
their nuget.config has nuget.org in the source list at all, or is `<clear/>`'d to internal-only. Told them
to drop it and verify on staging if it doesn't unstick quickly — not chasing further myself per the
"low-pri, don't rabbit-hole" instruction.

## PERF root cause found: /api/v1/compose-batch — GATED, NOT built yet
overview- urgent task: prod dashboard /api/v1/compose-batch times out (11-18s, some 60s+) at prod corpus
scale; clean on staging (small corpus). Traced the call chain: FOSS `ReadEndpoints.HandleComposeBatch` ->
`IDashboardEventStore.ComposeBatchAsync` -> on prod, commercial `PostgreSQLDashboardEventStore.ComposeBatchAsync`
(`stylobot-commercial/src/Stylobot.Commercial.Persistence.Postgres/Storage/PostgreSQLDashboardEventStore.cs:2504`).

**Root cause:** each of up to 6 branch SELECT statements (SummaryStats x2, TimeBuckets, BotAggregate,
GeoBreakdown, EndpointStats), sent together in one `QueryMultipleAsync` batch, independently re-declares
its OWN copy of the same `WITH windowed AS MATERIALIZED (SELECT * FROM dashboard_detections WHERE
<time/domain/audience predicate>)` CTE. `MATERIALIZED` only dedupes re-evaluation *within* one statement
(e.g. BotAggregate's internal filtered/agg/latest chain) — it does NOT share the scan ACROSS the 6 separate
top-level statements, since each is independently planned by Postgres. So the shared base scan runs up to
6x per compose-batch call, every time pulling ALL columns (`SELECT *`), not just what each branch needs.
Cheap on staging's tiny corpus, multiplies to timeout territory on prod's real volume — a mechanical,
corpus-size-driven explanation that fits "same binary, staging clean, prod times out" exactly.
Secondary compounders flagged (not the primary fix): EndpointStats' PERCENTILE_CONT/mode() aggregates over
EVERY distinct (method,path) before its LIMIT; BotAggregate's GROUP BY primary_signature over the whole
window before its LIMIT; no index on `domain` (existing composite indexes cover is_bot/country_code/
risk_band/method+path+timestamp, not domain).

**Fix plan sent to overview-, gated, NOT built:** (1) collapse the 6 redundant per-branch CTEs into ONE
shared materialization (e.g. a TEMP TABLE created once per call, each branch SELECTs from it directly,
column-pruned to only what's needed) — same round-trip count, 6x->1x base scan; (2) add
`idx_detections_domain_timestamp (domain, timestamp DESC)`; (3) NOT fixing now — bounding EndpointStats/
BotAggregate's group cardinality before the expensive aggregates changes result semantics slightly, flagged
as a product-call follow-up if #1+#2 aren't enough. Didn't/won't touch prod to verify (zero-keyless-hit
rule + I don't hold the key) — reasoned from query structure + `dashboard-schema.sql`'s index list; offered
deploy- the exact EXPLAIN ANALYZE query if empirical before/after confirmation is wanted.

## Next step if resuming
Standing by: merge queue is frozen until overview- verifies the reconciled deploy on prod, then reopens it.
Waiting on overview- to gate the compose-batch fix plan (touches commercial-repo
PostgreSQLDashboardEventStore.cs, not FOSS — asked who to coordinate the actual patch with). If picking up
new work meanwhile: Task 2b (Logs view) stays routed to otel-/aspnet-. NEVER hit stylo.bot/prod without the
key (I don't hold one — route through deploy-).

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
