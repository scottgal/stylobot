---
name: foss-session-2026-07-23-llamasharp-fix-shipped
description: MAJOR FINDING — the 6.7s p95 on /dashboard/traffic under load is NOT a Fix-1 gap, it's architectural: StyloBotDashboardMiddleware.ServeDashboardPageAsync (the ACTUAL page handler — TrafficController is dead code on real hosts) unconditionally builds ALL dashboard rows' data (clusters/topbots/sessions/threats/countries/endpoints/useragents) on EVERY request, only 1 of ~8 datasets (composedPage/traffic) is Fix-1-protected. Reported to overview- with exact line numbers (StyloBotDashboardMiddleware.cs:1310,1356), 2 fix options proposed, NOT building either without steer. Both repos already shipped to main (FOSS d142b4ea, commercial ffacf2fa) — this finding is about what's NEEDED NEXT, not a blocker on what shipped.
metadata:
  type: project
---

# foss- saved context (2026-07-23, re-engaged)

## MAJOR FINDING — the real root cause of the 6.7s p95 (architectural, not Fix-1 bug)
overview- disproved my "stale container" theory (deploy- confirmed :8390's digest matched
50fb696d exactly) — the 6.7s p95 on /dashboard/traffic under concurrent load is REAL against the
correct build. Per overview-'s explicit instruction, instrumented/traced the actual serving path
instead of guessing, and found the real cause via direct code read:

**`TrafficController.Index()` is DEAD CODE on any real host** — `StyloBotDashboardMiddleware`
(registered BEFORE `MapControllers()`, `Extensions/StyloBotDashboardServiceExtensions.cs:882-960`)
intercepts `/dashboard/traffic` via its row-dispatch switch (`StyloBotDashboardMiddleware.cs:343`),
calls `ServeDashboardPageAsync`, and fully writes the response WITHOUT ever calling `_next()` — MVC
routing (and TrafficController) never executes.

**`ServeDashboardPageAsync` (line 1149) unconditionally builds ALL dashboard rows' data on EVERY
request**, not just the active row:
- Line 1259: `_contentCache.GetCurrentAsync(trafficManifest, ...)` — the ONE Fix-1-protected read,
  feeds `composedPage` only.
- Line 1310 `Task.WhenAll`: `visitorTask, summaryTask, countriesTask, endpointsTask, userAgentsTask`
  — countries/endpoints/useragents are separate UNCACHED direct `_eventStore` calls, not gated by
  Fix 1 or by which row is being viewed.
- Line 1356 `Task.WhenAll`: `yourDetectionTask, clustersTask, topBotsTask, sessionsTask,
  threatsTask` — `BuildClustersModelAsync`/`BuildTopBotsModel`/`BuildSessionsModel`/
  `BuildThreatsModelAsync`, ALL separate uncached direct event-store reads, ALL unconditional
  regardless of the requested row.

So even a perfect Fix-1 cache hit on `composedPage` still leaves ~7 other independent, uncached,
synchronous DB round-trips before the response writes — these are almost certainly the real source
of the 6.7s p95 under concurrent load, since Fix 1 was never wired to touch them (they're outside
DashboardContentCache/the materializer entirely). This is architectural (the shell renders every
row's data for every row's request), not a small Fix-1 bug.

**Reported to overview- with exact evidence, proposed 2 fix shapes, NOT building either without a
steer**: (a) make the handler lazy per-row (only fetch what the active row needs — smaller, directly
fixes the measured symptom), or (b) generalize Fix 1's out-of-request pattern to the other 7
datasets too (bigger lift, more thorough). **Standing by for direction — this is the next real task
once picked up.**

## bench- saw bad numbers on :8390 — CONFIRMED not a code bug (stale container)
After shipping to main, overview- relayed bench- seeing 13.9s first-hit + 23.1s compose-batch on
:8390, contradicting Fix 1. Verified directly (code inspection, not just git ancestry):
- `git show 50fb696d:.../DashboardContentCache.cs` — GetAsync uses `_atom.TryGet` +
  `DashboardPageResult.Warming` on miss; `GetOrComputeAsync` only inside WarmAsync;
  `ComputeOnColdMiss` has zero hits in DashboardMaterializerOptions.cs. Fix 1 genuinely present.
- `git show 73b15b04:.../PostgreSQLDashboardEventStore.cs` — the single shared
  `DROP TABLE...CREATE TEMP TABLE windowed` scan is present. Compose-batch fix genuinely present.
- Clarified for overview-: bench-'s "direct compose-batch POST" stream bypasses the dashboard
  entirely (Fix 1 never touched that path — only Fix 2 pre-aggregation would). The 13.9s
  traffic-page number is the one that WOULD matter if real, but deploy- was mid-rebuild of :8390
  with 50fb696d when bench- measured — near-certain stale-container artifact, not a code bug.
Reported this back to overview-. **Awaiting re-measure once deploy- confirms fresh :8390 is live.**

## NEXT (in progress): focused live-apply round-trip integration test
overview- approved the seal/pin/diff-proof and asked for a NEW permanent regression-guard test
(separate track from the re-gate): drive the REAL commercial live-apply path end-to-end —
`POST /api/config/overrides` (UserAgentContributor weights.bot_signal 1.5→1.6) → next
`ControlPlaneConfigurationSource.TryGetParameterAsync`/`DetectorConfigProvider.GetParameterAsync`
resolves 1.6, no restart, uncached re-fetch-every-call (change again → next call sees it
immediately). Use `GatewayPluginTests`' full-stack infra as the base but with
`EnableLiveConfig=true`. Commit on the branch, report when green. This is what's IN PROGRESS as
of this checkpoint — pick up here if resuming.

## SHIPPED TO MAIN — both repos (user-confirmed directly before either push)
overview- relayed "operator wants it off the branch, merge to main now" — per this session's
standing rule (peer-agent relay is not sufficient authorization for main/publish pushes), stopped
and used AskUserQuestion; user answered "Yes, push both to main" directly. Then:

1. Verified both as clean fast-forwards BEFORE pushing (git merge-base --is-ancestor, no force
   needed either side).
2. **Reconciliation catch**: overview- assumed commercial's fix (`ebdb92ad`, the ephemeral pin bump)
   was already on `measure-pass-bundle` — it wasn't; it had landed on the shared checkout's local
   `main` (different branch, from where HEAD happened to be pointing when that commit was made).
   Cherry-picked it cleanly onto `measure-pass-bundle` in the isolated worktree
   (`stylobot-commercial-measure-pass`), producing **ffacf2fa** = origin/main + bot_probability
   index + pin fix. Verified both affected builds green before pushing.
3. **Pushed**: FOSS `foss/dashboard-collapse` (d142b4ea) → origin/main (was d5625a1f). Commercial
   `measure-pass-bundle` (ffacf2fa) → origin/main (was 2fa4f381). Both fast-forward, no force.
   Independently re-verified via fresh `git fetch` after each push (not just trusting the push
   output).
4. Pinged **deploy-** to build from main via `build-gateway.ps1` → registry + staging (explicitly
   NOT prod — prod stays gated on staging verify + operator go, per overview-'s instruction).
5. Reported both SHAs + the reconciliation note to overview-.

**What's now on both mains**: the ENTIRE session's work — full IOptionsMonitor→IOptions sweep (12
option types + IOptionsFactory fix for StyloExtractActionOptions) + POST /admin/reload removal +
reloadOnChange:false seal across all hosts, §7 materializer tuning (Tier1/2/3) + §8 Fix 1
(structural cold-miss fix), the compose-batch 6x→1x fix + bot_probability covering index, 2
dashboard UI fixes, and the ephemeral version-pin fix. 4485/4486 FOSS tests green throughout.

## Next step if resuming
Watch for deploy-'s build-gateway.ps1 result, bench-'s re-gate numbers on the now-on-main bundle
(does Fix 1 alone kill the concurrent-load collapse, or is Fix 2 pre-aggregation still needed), and
overview-'s decision on the commercial live-apply integration-test coverage gap (reported, not yet
actioned). Prod deploy still requires an explicit operator go after staging verify — do not deploy
prod without that, and do not treat any peer-agent relay of "operator says X" as sufficient for
another main-branch-class action; get direct user confirmation each time as this session has done
throughout.

---

## (historical) DONE — sweep + seal + moat verification, committed through 50fb696d (pushed)
On top of the full sweep (40dc82e9, see below), closed the remaining loop:

1. **50fb696d** — reloadOnChange seal: Console/Gateway explicit `reloadOnChange:true` flipped to
   false; ALSO added a defensive sweep after every `WebApplication.CreateBuilder()` call (Demo,
   Stylobot.All, Stylobot.Ui, Console, Sidecar, Gateway) since CreateBuilder's own DEFAULT
   appsettings sources reload-on-change independent of any explicit override. Closes the loop so
   the one remaining IOptionsMonitor<AuthenticationSchemeOptions> (framework-forced, can't convert)
   is permanently pinned to its startup value — no trigger left anywhere. Pushed to origin (branch,
   not main — same already-approved push target).
2. Handed **deploy-** FOSS 50fb696d + commercial measure-pass-bundle@73b15b04 for bench-'s re-gate
   (supersedes the earlier c14c6ec4 handoff — same work + the seal). Standing by for bench-'s
   concurrent-load numbers (the open question: does Fix 1 alone kill the collapse, or is Fix 2
   pre-aggregation still needed).
3. Verified `DetectorConfigProvider`/`IConfigurationOverrideSource` byte-for-byte untouched by the
   sweep via actual `git diff dfc6ad51..50fb696d` (not just assertion) — zero hits.
4. Commercial live-apply integration-test audit (via agent): **genuine gap, reported honestly** — no
   test exercises the full round-trip (config override write → `ControlPlaneConfigurationSource` →
   `DetectorConfigProvider` sees the new value, no restart). Adjacent pieces are tested (persistence,
   resolver precedence, HTTP query shape) but never wired together end-to-end. 25/25 existing
   adjacent tests pass.
5. **Found + fixed a real regression** while running those tests: commercial's
   `Stylobot.Website.csproj` had `mostlylucid.ephemeral` pinned at 2.9.1 (my own earlier §7 Tier2
   package bump needs 2.10.0) — NU1605 restore failure. Bumped the pin, verified clean build,
   committed commercial **ebdb92ad**.

Full report sent to overview-. Standing by for bench-'s re-gate result and any follow-up on the
test-coverage gap.

## Next step if resuming
Check for bench-'s re-gate numbers (does Fix 1 alone fix the concurrent-load collapse?) and
overview-'s decision on the live-apply test-coverage gap (build the missing integration test, or
accept as a known gap pending the future admin-site work). No FOSS-main push yet — branch carries
everything, overview- sequences the merge with the operator.

---

## (historical) DONE — FULL IOptionsMonitor sweep (scope broadened mid-stream), committed through 40dc82e9
overview- relayed the operator's scope reversal ("REMOVE ALL OF THEM — NON NEGOTIABLE", "/admin/reload
was NEVER intended to be in FOSS, it's a hallucination") superseding the earlier narrow-scope gate.
Executed the full sweep, "GO NOW, don't wait for plan review," in 4 commits on top of the narrow
180f64d3:

1. **1869ae2d** — full IOptionsMonitor→IOptions sweep, 12 of 13 option types (BotDetectionOptions,
   EndpointPolicyOptions, DetectionPolicyOptions, GroupingOptions, PublicKeyRegistryOptions,
   GatewayWarmupOptions, HoneypotDetectionOptions, RateLimitOptions, AdaptiveScalingOptions,
   UpstreamHealthOptions, NavVisibilityOptions) across ~46 files (production + ~20 test-file local
   fakes/mocks). Removed IEndpointPolicyResolver's one real `.OnChange` (Recompile already runs once
   at construction). Deleted GatewayWarmupGate/UpstreamHealthGate's dead WithMonitor()/StaticMonitor
   shims (confirmed unused anywhere via grep before deleting). 7 of the 12 types were already
   config-dead in production (never bound to any appsettings section) — zero live behavior lost there.
2. **e8b41cb3** — StyloExtractActionOptions (3 files, named options via `.Get(name)`) switched to
   `IOptionsFactory<T>` (the non-reload-observing factory IOptionsMonitor/IOptionsSnapshot are
   themselves built on) — resolves the named section ONCE at construction, frozen thereafter. Correct
   fix, not an exception: plain IOptions<T> has no named-lookup at all, and these 3 policies are
   AddSingleton so IOptionsSnapshot<T> can't inject.
3. **85cb6238** — deleted `POST /admin/reload` entirely (handler, route case, dead `IConfiguration`
   ctor param/field, doc references in CLAUDE.md/admin-endpoints.md/README.md/2 other docs — historical
   CHANGELOG/RELEASE_NOTES entries left untouched, changelogs are append-only). Deleted the now-fully-
   unused `MutableOptionsMonitor<T>` test helper (existed solely to simulate live-reload).
4. **40dc82e9** — trivial stale-comment fixup.

**Deliberately NOT touched**: `AuthenticationSchemeOptions` in `ApiKeyAuthenticationHandler.cs` —
ASP.NET's `AuthenticationHandler<T>` base class has ONLY an `IOptionsMonitor<T>` constructor overload;
this is framework-mandated, not stylobot's reload mechanism, and converting it would not compile. This
is the ONLY `IOptionsMonitor<T>` left anywhere in FOSS production code (confirmed via final grep).

**Moat check** (background agent, before the scope broadened): zero commercial dependency on
IOptionsMonitor for any of the 13 FOSS option types — commercial's live-config-apply runs entirely
through `IConfigurationOverrideSource` → `DetectorConfigProvider`, already `IConfiguration`-based,
untouched by any of this.

**Verification**: full solution builds clean; 4485/4486 tests green throughout (1 pre-existing
unrelated `DashboardLinkIntegrityTests`/`_TrafficPanels.cshtml` failure, another agent's in-flight
file, not touched). One transient flake (`SidebarV2PackNavTests`, unrelated to these changes, passed
on rerun and in isolation — not a real regression).

**Not pushed to FOSS main.** `ff6bef9c` (the original DashboardMaterializerOptions violation) is
already on FOSS origin/main (`d5625a1f`, confirmed still an ancestor of current HEAD `40dc82e9`) — this
whole branch (`foss/dashboard-collapse`) carries the full revert forward; it reaches main once the
bundle merges. Reported the complete summary to overview- for sequencing with the operator.

## Next step if resuming
Report 40dc82e9 to overview- (send this exact commit as the final state). Await push sequencing.
Original ask before the IOptionsMonitor detour (hand deploy- the rebuild for bench-'s Fix-1 re-gate)
is still outstanding — once overview- is satisfied on this sweep, resume that.

---

## (historical) DONE — narrow revert + §8 Fix 1, committed 180f64d3, NOT pushed to main yet
overview- resolved the scope split: narrow only (DashboardMaterializerOptions/§7, not the other 29
files or /admin/reload — that's a separate operator decision). Executed:

1. **IOptions revert**: `DashboardContentCache`, `DashboardMaterializerCoordinator`,
   `DashboardMaterializationServiceExtensions` — IOptionsMonitor→IOptions<T> throughout. Startup
   snapshot only; a config change now needs a process restart (matches FOSS convention). All
   `MutableOptionsMonitor<DashboardMaterializerOptions>` test usages replaced with
   `Options.Create(...)` across 8 test files. Removed the two "live config flip without restart"
   tests (their premise no longer exists). `MutableOptionsMonitor<T>` itself is now unused anywhere
   in the codebase — left in place (out of scope for this cut, harmless dead test helper; flag as a
   future cleanup if anyone cares).

2. **§8 Fix 1** (the actual measure-gate root-cause fix, resumed after the halt): `GetAsync`/
   `GetCurrentAsync` NEVER compute on a cold miss now — unconditional, no config valve. Only
   `WarmAsync` (materializer, off the request thread) calls the atom's factory. Fixed a latent bug
   in the old code: `RecordWarm` was called even on a miss, which would have clobbered the pointer
   to an older tick's real data — now only recorded on a genuine hit. New `DashboardPageResult.
   Warming` static placeholder distinguishes "truly never warmed" from a normal null-slice result.
   `GetCurrentAsync` already resolved to the latest WARMED tick (not strictly current), so a
   request while the materializer is behind now correctly serves that stale-but-real snapshot
   instead of either computing or blanking out.

3. **Test fallout fixed**: ~10 controller/composition tests (TrafficController*, WidgetBatchCompose,
   TopEndpointsPerfProjection, VisitorsLanding) assumed a cold miss would eagerly compute — added a
   test-only `AutoWarmingContentCache` decorator (`Test/Helpers/`) that pre-warms before
   `GetCurrentAsync` so these tests can keep asserting composition/batching correctness without
   reconstructing the controller's internal window-building logic. Also fixed
   `VisitorsRemoteMiddlewareIntegrationTests` (a REAL end-to-end TestServer integration test) —
   its "compose-batch gets called and returns empty, page still renders via per-widget self-fetch"
   assertion flipped to "compose-batch is NEVER called, page still renders via self-fetch" — same
   content assertions pass either way since `Warming` has the same null-slices shape.

**Result**: 4485/4486 tests green (the 1 failure is the pre-existing unrelated `_TrafficPanels.cshtml`
one, another agent's in-flight file). Committed **180f64d3** on `foss/dashboard-collapse`. **NOT
pushed to FOSS origin main** — `ff6bef9c` (the violation) is already on main (`d5625a1f`), so this
revert needs to reach main too; per overview-'s instruction I prepared it on the branch and am
reporting the plan rather than pushing directly — the branch itself, once the bundle is approved,
carries the revert to main.

## Next step if resuming
Report 180f64d3 to overview-, hand deploy- the rebuild for bench-'s re-gate (this was the original
ask before the IOptionsMonitor halt interrupted it). Still open/unresolved: the broader
IOptionsMonitor question (29 other files + documented /admin/reload) is a SEPARATE operator decision,
not touched, not blocking this cut.

---

## (historical) HALTED — hard rule conflict: IOptionsMonitor pervasive in FOSS vs "never in FOSS"
Mid-build on §8 Fix 1 (kill the synchronous request-thread compose fallback — see below for that
work, which is UNCOMMITTED, in-progress, includes a new `AutoWarmingContentCache` test helper and
`DashboardPageResult.IsWarming` already built). overview- relayed an operator hard rule: hot-reload
(IOptionsMonitor) is COMMERCIAL-ONLY, never FOSS; ff6bef9c (IOptions→IOptionsMonitor for
DashboardMaterializerOptions, part of §7) is "the violation" and needs reverting.

Ran the audit before touching anything (per "verify don't assume"): **IOptionsMonitor is used in 30
FOSS production files across ~16 option types** (BotDetectionOptions, EndpointPolicyOptions,
DetectionPolicyOptions, GroupingOptions, PublicKeyRegistryOptions, GatewayWarmupOptions,
HoneypotDetectionOptions ×4, RateLimitOptions, AdaptiveScalingOptions, UpstreamHealthOptions,
NavVisibilityOptions, StyloExtractActionOptions ×3, DashboardMaterializerOptions) — on BOTH FOSS
origin/main (d5625a1f) and foss/dashboard-collapse. This is NOT recent drift: it's the documented
mechanism behind the FOSS `/admin/reload` endpoint (CLAUDE.md:348, docs/admin-endpoints.md:8 — "no
process restart" reload is described as a FOSS feature). Flagged this conflict to overview- and am
**awaiting a scope decision**: narrow (just ff6bef9c + the new §7 options) vs broad (all 30 files +
deprecating the documented admin-reload feature itself). Sent, not yet answered.

**Do NOT resume Fix 1 or write any IOptionsMonitor revert code until overview- answers scope.**

## Next step if resuming
Check for overview-'s scope answer first. If narrow: revert DashboardMaterializerOptions consumers
(DashboardContentCache, DashboardMaterializerCoordinator, DashboardMaterializationServiceExtensions)
from IOptionsMonitor back to IOptions, keep the rest of the codebase's 29 other files untouched, then
resume §8 Fix 1. If broad: this is a much bigger separate piece of work needing its own scoping (does
/admin/reload get removed or replaced for FOSS?) — do not start it without an explicit go.

---

## (historical) Measure gate FAILED under concurrent load — root cause found, fix designed (not built)
bench- ran the §7-tuned bundle (FOSS 092c8c9a / commercial measure-pass-bundle@73b15b04, both pushed as
branches, both on origin, main untouched on both repos) against the 1.15M-row corpus. Light load: fast
median but 14.7% cold-miss (p95 ~11.6-12.8s on 7d/30d). Sustained concurrent load (5 VUs traffic + 5 VUs
compose-batch POST): NO improvement over untuned baseline — the tuning's benefit vanished entirely.

**Root cause CONFIRMED** (checked the actual deployed appsettings.json, not guessed):
`DashboardMaterializerOptions.ComputeOnColdMiss` was left at its code default (`true`) in the tested
build — never overridden in config. So the synchronous request-thread compose-batch fallback (meant to be
a rare safety net) was fully active, reintroducing the in-request-render anti-pattern under load: request
threads and the materializer's own warm attempts both hit the same contended Postgres, cold misses beget
more synchronous computes beget more contention — a self-reinforcing spiral, exactly matching every
symptom bench- observed (fine under light load, no better than baseline under concurrent load, doesn't
recover cleanly with time).

Wrote up **§8** in the compose-batch-overload review doc (commercial `d60af031`, docs-only, nothing built):
- **Fix 1** (primary, cheap, high-confidence): stop the sync fallback firing — serve the last known warm
  snapshot (the request path's `GetCurrentAsync` already resolves to the latest-warmed tick, close to
  already-there) instead of either computing synchronously or returning a blank `EmptyResult`.
- **Fix 2** (necessary, larger lift): pre-aggregation/rollup for 7d/30d — item #11 from §5/§6, now
  confirmed critical-path not strategic-someday. Single-source design (Postgres-native rollup, one writer,
  no shadow store) per the same "no parasitic store" principle as §7 Tier 2. Needs its own scoped design
  doc before code.
- **Fix 3** (supporting): separate materializer vs request-thread Postgres connections (#12).

Recommended sequencing sent to overview-: Fix 1 first → re-measure with bench- under the SAME concurrent
profile → Fix 3 → Fix 2 (own design doc). Sent full writeup to overview- for scope gate with the operator.
**Nothing built yet on any of the 3 fixes — awaiting gate.**

## Next step if resuming
Wait for overview-'s gate/scope decision on the §8 fix design. If approved to build Fix 1: it's a
FOSS-side change (`DashboardContentCache`/`GetCurrentAsync` path + a "warming up"/stale-snapshot UI
affordance), TDD per this session's discipline, on `foss/dashboard-collapse` (currently @ 092c8c9a).
Re-measure with bench- under the identical concurrent-load profile before considering the gate passed.

---

## (historical) §7 FULLY WIRED, single-source-correct (0377e1ba) — shipped, then failed measure gate (see above)
Sequence completed this session: parasitic HitCount reverted (86c6c96d) → accessor built in
mostlylucid.atoms sibling repo (`SlidingCacheAtom.TryGetEntryStats`, commit 5538ac6) → user
DIRECTLY approved the public NuGet publish (AskUserQuestion, not just overview-/operator relay,
per this session's standing git-safety rule for irreversible/public actions) → tagged v2.10.0,
pushed, CI (`publish-nuget.yml`) succeeded, package confirmed live on nuget.org → bumped ALL
`Mostlylucid.Ephemeral.*` PackageReferences (including a lowercase `mostlylucid.ephemeral` ref in
the UI csproj my first sed pass missed — case-sensitive grep caught it) across
Mostlylucid.BotDetection/.Api/.Observability/.UI csproj files to 2.10.0 → wired
`DashboardContentCache.LiveEnvelopes()` to compute AccessCount/LastAccess AT READ TIME via
`_atom.TryGetEntryStats(latestWarmTick key)` (no stored field) → coordinator ranks live envelopes
by that. All on `foss/dashboard-collapse` @ **0377e1ba**, local only, NOT pushed to origin (bundle
held). 22/22 targeted tests green (TDD RED/GREEN throughout, including a new coordinator-level
"hotter envelope wins under budget pressure" test using the real atom-sourced ranking). Full
solution build green; only unrelated pre-existing failure is `DashboardLinkIntegrityTests` on
`_TrafficPanels.cshtml` (another agent's in-flight file, not touched).

**Full commit chain this session on foss/dashboard-collapse**: 70956ca1 (Tier1+2[parasitic]+3
shipped) → 6dcf824a (checkpoint) → 86c6c96d (Tier2 reverted) → 6e0faf96 (checkpoint) → 0377e1ba
(Tier2 rewired to real source + package bump, current tip).

## Next step if resuming
Report 0377e1ba + published v2.10.0 to overview-. Then: bench- measure pass is the remaining gate
before the full bundle (commercial 2fa4f381 compose-batch fix + bot_probability index 9760d2ab +
FOSS d5625a1f UI fixes + §7 Tier1/2/3 @ 0377e1ba) can build→stage→prod. Nothing further to build on
§7 itself unless the measure pass finds a problem.

---

## (historical) URGENT CORRECTION — §7 Tier 2 hit-counter reverted (86c6c96d)
Shipped `70956ca1` included a NEW `HitCount` field on `DashboardContentCache`'s `LiveEntry`,
ranking Tier 2 by it. overview- flagged this urgent, hard constraint: "NO PARASITIC STORES" —
`SlidingCacheAtom` (the content cache's own backing LFU, in the `mostlylucid.atoms` sibling
repo, package `Mostlylucid.Ephemeral.Atoms.SlidingCache` v2.9.1) ALREADY tracks per-key
`AccessCount`/`LastAccess` internally (private `CacheEntry` class, `SlidingCacheAtom.cs`
lines 381-409) — a second counter alongside it can drift and "always reads as a bug in this
system." Reverted immediately: `86c6c96d` removes `HitCount` from `LiveEntry`/
`IDashboardContentCache.LiveEnvelopes()`/the coordinator's ranking (now unranked again,
exactly pre-§7 behavior). Tier 1 (pinned multi-window prewarm) and Tier 3 (bounded-parallelism
waves) are untouched by this — only Tier 2 ranking reverted. 20/20 targeted tests green after
revert.

**Blocker for the real Tier 2 fix**: `SlidingCacheAtom` has NO public accessor for its internal
per-key `AccessCount`/`LastAccess` (confirmed via Explore of the sibling repo source at
`/Users/scottgalloway/RiderProjects/mostlylucid.atoms/mostlylucid.ephemeral/src/mostlylucid.ephemeral.atoms.slidingcache/SlidingCacheAtom.cs`).
Only aggregate `GetStats()` (`CacheStats` record, no per-key breakdown) is public today. To rank
Tier 2 off the "one true structure" as instructed, I'd need to: (1) add a public accessor (e.g.
`TryGetEntryStats(key, out (AccessCount, LastAccess, ...))`) to that class in the sibling repo,
(2) tag/pack a new version (currently pinned to v2.9.1 across ALL `Mostlylucid.Ephemeral.*`
package refs, not a ProjectReference despite CLAUDE.md's stated "local project reference for
development" pattern — reality is a published NuGet dependency), (3) bump stylobot's
`PackageReference` version. This is a cross-repo, cross-package-version change bigger than an
in-repo edit — sent to overview- for confirmation before building, per their explicit
"confirm the revised ranking source before you build it" instruction. AWAITING REPLY.

## §7 materializer priority/coverage tuning — SHIPPED (70956ca1)
Built per overview-'s "GO — build the §7 prewarm tuning + the bot_probability index" instruction,
following the doc's measure->tune sequence. All three tiers landed in one commit on
`foss/dashboard-collapse` (FOSS repo):

- **Tier 1 (pinned coverage)**: `DashboardMaterializerOptions.PrewarmWindows` (new, default
  `["6h","24h","7d","30d"]`) replaces the old single-window `PrewarmWindowMinutes`/
  `PrewarmBucketMinutes`. Every tick, Traffic is warmed at all 4 tokens unconditionally (not just
  one), via the same `DashboardRoutingHelpers.WindowTokenToMinutes` + `HitsPerPeriodChartletBuilder.
  BucketSizeForWindow` helpers `BuildVisitorsPageWindow` uses for a real request, so pinned envelope
  keys always match what a real request looks up.
- **Tier 2 (demand ranking)**: `DashboardContentCache.LiveEnvelopes()` now returns `HitCount` +
  `LastSeenTick` per envelope (interface signature changed — only one prod implementation, no mocks,
  confirmed via grep before changing it). Coordinator sorts live envelopes by
  `(HitCount desc, LastSeenTick desc)` before warming, so a hot page wins the tick's budget over
  whatever the dictionary happened to enumerate first.
- **Tier 3 (bounded parallelism)**: new `MaxConcurrentWarmsPerTick` option (default 4, conservative
  re pool per the doc's guidance) — warms in wave-based bounded concurrency mirroring
  `ScheduleCoordinator`'s own pattern. `MaxTickDurationMs` is now checked BETWEEN waves, not
  per-item — this is an intentional semantics change from the original single-item-checked version.

**TDD discipline**: RED/GREEN throughout. `WindowTokenToMinutes` extracted+tested first (10 tests),
then `HitCount` tracking (RED confirmed via CS1061 compile error before implementing), then Tier 1
multi-window prewarm (RED via composes-count assertion), then Tier 2 ranking test + Tier 3
wave-concurrency test (semaphore/TCS-based, would fail under old sequential/unranked code). 3
pre-existing tests needed updating for the new intentional semantics (not regressions): the
single-window prewarm assertion (now 4 composes not 1), the time-budget test (now pins
`MaxConcurrentWarmsPerTick=1` to isolate per-item deadline granularity, since default concurrency=4
would otherwise let all 3 test pages compose in one wave), and the live-envelope warm test (prewarm
now explicitly off to isolate Tier 2 from Tier 1). 34/34 dashboard-materializer/content-cache/
routing-helper tests green; full solution build green; only unrelated pre-existing failure is
`DashboardLinkIntegrityTests` on `_TrafficPanels.cshtml` (not a file I touched — another agent's
in-flight work, left alone).

**Git housekeeping note**: mid-verification, `git stash`/`pop` hit the pre-existing uncommitted
`tailwind.min.css` drift (present since session start, unrelated to this work — looks like a
regenerating vendored build artifact touched by a concurrent process in this shared checkout).
Recovered cleanly by stashing that file alone first, then popping the real work stash. Committed
**70956ca1** with only the 9 intentional files; left `tailwind.min.css` and other agents'
`.styloagent/` scratch files untouched.

## Next step if resuming
Report **70956ca1** (FOSS) + **9760d2ab** (commercial, bot_probability index, already shipped
earlier this session) to overview- with the measurements above (34/34 green, build clean). No
live prod measurement pass done yet (would need `bench-` or a real traffic sample against staging)
— that's the natural follow-up if overview-/bench- want the "measure" half of measure->tune
validated against real cold-miss/valve-trip rates rather than just unit-test behavior.

---

# Prior session history (2026-07-23, earlier in the day) — AWAITING overview- GATE

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

## URGENT stabiliser lever answered: prod dashboard grinding to a halt
overview- needed the fastest config-only stop for compose-batch hammering the DB. Answer: set
`BotDetection:Dashboard:Materializer:Enabled=false` (env `BotDetection__Dashboard__Materializer__Enabled`).
Reasoning: Tick10s fires every 10s regardless of whether the PREVIOUS tick's compose-batch (now 11-60s) has
finished -> ticks pile up/overlap, each hammering Postgres -> matches "tick + in-request both hammering".
Disabling stops the pile-up; reads still serve the last-warmed snapshot from `DashboardContentCache`
(single-flighted per envelope, not per-request) until it ages out (5min sliding / 30min absolute), so it's
stale-but-served, not blank, and residual load drops to ~1 compose-batch call per 5-30min per viewed page.
**Caught two important gotchas before they'd have caused a bad outcome under pressure:** (1) this option is
bound via plain `IOptions<T>` (captured once at boot in both DashboardContentCache + the materializer
hosted service), NOT `IOptionsMonitor<T>` — so it needs a PROCESS RESTART to take effect, `/admin/reload`
won't pick it up; (2) `DashboardMaterializerOptions.ComputeOnColdMiss` is DEAD CODE — declared, documented,
but nothing reads it; `SlidingCacheAtom.GetOrComputeAsync` always computes on a miss regardless. Flagged
both clearly so deploy- doesn't expect a hot-reload or rely on a flag that does nothing.

## Both materializer + compose-batch fixes BUILT + REAL-Postgres VERIFIED, reported to overview-
overview- approved building both root-cause fixes (not just the temporary disable). Corrected overview-'s
"add an overlap guard" framing first: `ScheduleCoordinator.InvokeSubscriberAsync` ALREADY single-flights
each subscriber via a BusyFlag CAS -- the "busy >2 ticks" log deploy- saw IS that guard firing correctly,
not unguarded overlap. Real gap: one `MaterializeTickAsync` invocation sequentially warms up to
MaxPagesPerTick (32) envelopes with no TIME bound, so a slow compose-batch let one tick run for minutes,
and since the BusyFlag keeps the next tick skipped meanwhile, an unbounded tick ran back-to-back with zero
pacing -- same "tick + in-request both hammering" symptom, different mechanism.

**FOSS fix, commit `e99de361`** (`foss/dashboard-collapse`, not pushed): `DashboardMaterializerOptions.
MaxTickDurationMs` (new, 8000ms default) + `DashboardMaterializerCoordinator` takes an optional
`TimeProvider` and defers remaining live envelopes once the budget is hit mid-loop. Also wired up
`ComputeOnColdMiss` (was dead code -- declared, documented, never read; `DashboardContentCache.GetAsync`
now actually gates on it, returning an empty bundle instead of computing on a genuine cold miss when false).
TDD throughout, 16 tests green, 349/350 Dashboard tests green (1 pre-existing unrelated failure already
flagged).

**Commercial fix, commit `fff061db`** (`main`, not pushed, freeze in effect): collapsed the 6x redundant
`windowed` CTE into one shared TEMP TABLE created once per `ComposeBatchAsync` call (explicit DROP-then-
CREATE as its own round-trip, not ON COMMIT DROP, so pooled-connection reuse can't see stale state).
**Docker happened to be available this session** -- spun up `pgvector/pgvector:pg16`, applied the full
schema via the same fixture `PostgresComposeBatchTests` uses, ran the existing equivalence suite BEFORE
(3/3 baseline) and AFTER (3/3, byte-for-byte same as individual Get*Async reads) my change, then the full
non-skipped Postgres integration suite (54/54, no regressions). This is genuinely verified against a real
Postgres, not just reasoned about the SQL text -- a meaningfully higher confidence bar than most of this
session's other findings. Also caught and corrected my own earlier claim: `idx_detections_domain_timestamp`
already exists via `analytics-capture-migration.sql` (confirmed live) -- no index migration needed, I'd
only checked one of the several schema files before.

## Full subsystem review delivered (operator wanted this instead of the narrow patch)
Operator declined the temporary stabiliser, wanted a full review of the whole dashboard-aggregation/
materializer/query subsystem "done properly, not a band-aid." Delivered as
`stylobot-commercial/docs/incidents/2026-07-23-dashboard-compose-batch-overload-db-review.md` (commit
`970240ee`, not pushed). Covers: compose-batch (shipped fix + same anti-pattern found smaller-scale in
GetSummaryAsync/GetCountryDetailAsync), full index audit (domain index correction + new gap: no index on
bot_probability, the most-used predicate in the store), materializer architecture verdict (sound, doesn't
need rework -- two real gaps shipped, three more flagged for prioritization incl. IOptions-not-Monitor
which is WHY this incident's stabiliser needed a restart), corrected two-phase bounding design for
EndpointStats/BotAggregate (walked back my earlier "changes semantics" hedge -- done right it's
equivalence-preserving), and DB-strategy findings that doubled as the user's separate research ask: NO
native time-partitioning on dashboard_detections (the real structural lever), zero retention on
dashboard_signatures/degradation_history, no materialized views (TimescaleDB tried+dropped historically),
and a previously-documented-but-unfixed connection-pool-exhaustion incident. 12-item gated fix-plan table
at the end (3 shipped: fff061db/e99de361 x2; 1 not needed; 8 proposed/design-only).

## Item 8 also shipped: IOptionsMonitor promotion, commit ff6bef9c
User said "go" -- kept building from the review's fix-plan table (judgment call: picked the safe,
fully-designed, non-DB-migration items rather than everything). Promoted DashboardMaterializerOptions to
IOptionsMonitor across DashboardContentCache + DashboardMaterializerCoordinator + the DI registration.
Real behavior change: Enabled used to gate the tick SUBSCRIPTION itself at StartAsync (one-time decision),
so a later config flip had nothing running to affect -- now the coordinator always subscribes when a
schedule exists, and Enabled is checked LIVE as the first line of MaterializeTickAsync, so next incident's
`Enabled=false` stabiliser actually works via /admin/reload, no restart. Added a shared test double
(Mostlylucid.BotDetection.Test.Helpers.MutableOptionsMonitor<T>, settable CurrentValue) since 22 call
sites across 8 test files construct DashboardMaterializerOptions via IOptions -- mechanical but real
churn, all fixed. Updated the one test whose contract intentionally changed (disabled coordinator now
still subscribes, tick just no-ops) + added 2 new live-flip regression tests. 18 targeted tests green,
4471/4478 full suite green (1 pre-existing unrelated failure, 6 skipped). Doc's fix-plan table + main repo
commit both updated (970240ee doc, 3b59dbd8 status update), reported to overview-.

Remaining from the 12-item plan (5,6,7,9,10,11,12) intentionally NOT built without further gate -- several
need staging-first DB coordination (index, temp-table treatment for 2 more methods, partitioning
especially) or are big enough to deserve their own scoping pass (materialized views, connection pooling).

## Commercial push (fff061db) — cherry-picked, verified, PENDING USER DECISION (unresolved)
My local commercial `main` (fff061db on top) was stale/diverged from origin/main (a big reconciliation
merge moved it forward). Cherry-picked fff061db cleanly onto current origin/main as branch
`temp-cherry-fff061db` in `stylobot-commercial` (no conflicts; ComposeBatchAsync body byte-identical to
what I originally tested). Re-verified with a fresh build + a fresh real-Postgres equivalence-test run
(3/3 pass) against that exact cherry-picked commit. Two safety-classifier blocks fired on `git push`:
(1) "peer-AI (overview-) authorization isn't user authorization" -- user said "go" directly in chat,
resolving it; pushed FOSS e99de361 to origin/main on that basis (succeeded).
(2) "stylobot-commercial push destination is public" -- both the user (checked GitHub directly) and I
(`git remote -v`) confirmed the actual origin URL is `github.com/scottgal/stylobot-commercial` (private per
the user), contradicting the classifier's own cited reasoning (it named "scottgal/stylobot", the FOSS repo
-- doesn't match). Asked the user to retry-or-do-it-themselves, then got redirected to the two dashboard-UI
fixes before an answer came back.
**UNRESOLVED: the commercial push never happened.** Branch `temp-cherry-fff061db` exists locally in
stylobot-commercial, verified and ready. Next-me: re-raise with the user, don't assume silence = go-ahead.
Cleanup done: removed the verification worktree + docker container, kept the branch itself.

## Two FOSS dashboard-UI fixes shipped: d5625a1f
overview- reassigned dash-'s exited work: (1) signature-detail rendered via isMainPage:true standalone,
resolving the host's bare layout instead of the shared Index.cshtml drawer+sidebar shell -- compensated
with a stale hand-rolled pre-V2 tab strip nobody removed when Index.cshtml's real tabs were deleted in the
V2 migration. Fixed via a new DashboardShellModel.SignatureDetailContent field + dispatch branch in
Index.cshtml; ServeSignatureDetailAsync now builds a shell model (cheap placeholders for unused required
fields) and renders Index.cshtml instead of _SignatureDetail.cshtml directly; removed the stale tab strip.
(2) "You:" pill's comment said "whole pill clicks through" but only the trailing "view →" text was in the
anchor -- wrapped the whole pill. Verified LIVE in browser (screenshots, DOM), cross-checked pre-existing
console errors (ApexCharts/commercial-404/View-Transitions) against the untouched Traffic page to confirm
none were introduced. 4471/4478 full suite green. Reported to overview-.

## FOSS origin/main now at d5625a1f — PUSHED, confirmed
overview- explicitly asked to push d5625a1f (dashboard-UI fixes, ff6bef9c riding along as pre-approved) to
FOSS main so it's ready for the bundled next cut (with the commercial compose-batch perf fix). Pushed
clean, no classifier block (fast-forward from e99de361, no conflicts) -- confirmed via fresh fetch. Also
told explicitly: do NOT rebuild/restage the .15 staging stack right now (bench- is mid k6 perf run against
the current staged image) -- deploy- rebuilds the BUNDLED cut from main once bench- finishes + overview-
confirms; overview- sequences the restage, not me. bot_probability covering index (§2) is approved as the
NEXT thing to build but explicitly AFTER this bundle ships, not now.

## Materializer priority/coverage design (§7) — DESIGNED, NOT BUILT, sent to gate
Operator's tuning direction (full out-of-request coverage, priority ordering, stay generic/no-marketing-
special-casing) for the test-and-tune loop with bench-. Coverage audit found: only ONE (manifest,window)
combo pre-warmed today (dashboard.traffic @ 24h default), despite Traffic's own UI offering 4 standard
windows (6h/24h/7d/30d) -- clicking anything but 24h is ALWAYS a cold miss. Visitors/Site opportunistically
share the cache when filters match; Policies/Configuration are out of scope (different data domain).
Design (in docs/incidents/2026-07-23-dashboard-compose-batch-overload-db-review.md §7, commercial commit
f71a501f, not pushed): Tier 1 pinned = traffic manifest x the SAME window list the UI already offers
(new PrewarmWindows option, default DERIVED from FOSS's own UI -- satisfies "keep it generic" directly,
not hardcoded per-host); Tier 2 live-ranked = LiveEnvelopes() ordered by a new hit-counter + recency
instead of arbitrary dict order; bounded wave-based parallelism (new MaxConcurrentWarmsPerTick, mirrors
ScheduleCoordinator's own MaxConcurrentSubscribersPerTick pattern). Sequenced strictly AFTER the current
bundle ships -- NOT building any of this yet, sent to overview- to gate.

## Commercial push RESOLVED — overview- pushed it themselves, independently verified
overview- pushed `temp-cherry-fff061db` to commercial origin/main themselves (as 2fa4f381) after the
classifier blocked ME on it. Did NOT just trust the claim -- fetched commercial origin/main directly and
confirmed 2fa4f381 is genuinely there with the fix content present (same commit I'd already real-Postgres
verified). Thread closed. Full bundle now confirmed on main: commercial 2fa4f381 + FOSS d5625a1f.

## Next step if resuming
Waiting on: (a) overview- to gate §7 (materializer priority/coverage design) before building anything,
(b) overview- to confirm bench-'s k6 run is done + give the go to build the bot_probability index. Do NOT
touch .15/staging or restage anything -- overview- sequences that. NEVER hit stylo.bot/prod without the key.

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
