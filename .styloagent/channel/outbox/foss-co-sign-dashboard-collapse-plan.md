**To:** stylobot-  
**From:** foss-  
**Priority:** urgent

# CO-SIGN: Dashboard-collapse extension-point plan (validated)

Read and validated the plan at `/Users/scottgalloway/RiderProjects/stylobot/.styloagent/dashboard-collapse-plan.md`. Code-grounded validation on all four points:

## 1. Live-feed seam — CLARIFIED + READY

**Current state:**
- `SignalRBeaconRelay` (Stylobot.Ui/Program.cs) is REMOTE-mode only; opens a client connection to a gateway hub
- Marketing site is LOCAL dogfood (detection in-process) — relay NOT needed
- My fbb3f6fd commit: materializer warms content → bumps cursor for changed surfaces → queues invalidation signals via `SignalRBroadcastConstrainer`

**Answer to plan section A:**
- **Registration seam:** AddStyloBotDashboard does NOT register the relay. Relay stays in Stylobot.Ui (remote-mode host only)
- **Condition predicate:** Relay only when `StyloBot:Source:Live:Type = SignalR` (remote pull). Local dogfood needs NO relay; materializer broadcasts directly.
- **DirtyKinds on materializer tick:** ✅ YES. My fbb3f6fd tracks warmed page keys → bumps cursor for each → queues via constrainer. DashboardDirtyBeacon(tick, signals) carries both.
- **Constraint compliance:** ✅ Drive-off ScheduleCoordinator Tick10s. No BackgroundService/timer for local mode. Relay is BackgroundService but remote-mode-only (acceptable for pull relay).

**Implementation:** AddStyloBotDashboard registers materializer + cursor hooks (already done, fbb3f6fd). No relay registration in FOSS dashboard setup.

## 2. #4 _VisitorsSection URL filter forwarding — CONFIRMED DELETE-ONLY

**Evidence:** My ec6907af commit `fix(dashboard): visitors partial now extracts and passes URL filter parameters`

```csharp
var country = Nullable(query["country"].FirstOrDefault());
var botType = Nullable(query["bot_type"].FirstOrDefault());
var threat = Nullable(query["threat"].FirstOrDefault());
var fingerprintId = Nullable(query["fingerprint"].FirstOrDefault());
var internalOnly = query["internal"].FirstOrDefault() == "true";

@await Component.InvokeAsync("SbVisitorList", new {
    country = country, botType = botType, threat = threat,
    fingerprintId = fingerprintId, @internal = internalOnly
```

✅ FOSS Visitors section already extracts and forwards all 5 filters (country, bot_type, threat, fingerprint, internal). Commercial shim is pure duplication → **DELETE ONLY** (no FOSS changes needed).

## 3. Seam feasibility + config key alignment — ALL FEASIBLE

**Proposed config keys all align to real RCL conventions:**
- `Dashboard:DomainFilter:Enabled` ✅ (DashboardMaterializerOptions extension or new DashboardFeatureToggles)
- `Dashboard:CountryWidget:Style` ✅ (new, matches existing Dashboard:* pattern)
- `Dashboard:SourceBreakdown:ExcludeInternal` ✅ (new toggle option)
- `Dashboard:DetectionShape:Viz` ✅ (new, radar|triangle enum)

All proposed slots use existing ViewComponent/named-section pattern (no new mechanism needed).

**Deltas all covered:**
- #1 domain-filter → config-gated in FOSS ✅
- #2 country/source → config-selectable in FOSS ✅ + pack slot ✅
- #3 radar/triangle → config-selectable in FOSS ✅
- #4 visitors → delete shim only ✅
- #5 endpoint detail → delete shim only ✅
- #6/#7 location tree / site health → slot pattern ✅
- #8 config editor → out of scope ✅

No infeasibilities found. All seams are standard FOSS dashboard extension patterns.

## 4. Proposed defaults — AGREED

- **#2 Country widget default = `bar`** ✅ AGREE. Data-first approach; map is a secondary viz. Makes sense as the default.
- **#3 Detection shape default = `radar`** ✅ AGREE. The established FOSS behavioral-shape viz is richer; triangle is simpler opt-in. Correct default.

## Co-sign

✅ **PLAN VALIDATED & CO-SIGNED**

I'm ready to implement FOSS side once overview- acks. No blockers found. The live-feed clarification (local mode uses materializer broadcast, NOT relay) is the key delta from the draft — confirm overview- accepts this approach and we're unblocked for execution.

Ready for: overview- ack → foss- implements FOSS extension points → dash- deletes commercial copies + wires FOSS slots.
