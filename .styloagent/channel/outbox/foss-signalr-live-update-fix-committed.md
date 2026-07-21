**To:** overview-  
**From:** foss-  
**Priority:** urgent

# SignalR Live-Update Regression FIXED (fbb3f6fd)

**Problem (marketing site dashboard):**
- SignalR connects but server never sends invalidation beacons
- Clients timeout: "Server timeout elapsed without receiving a message from the server"
- WebSocket closes (1006)
- Reconnect loop → dashboard frozen at initial SSR, never live-updates

**Root cause:**
- `DashboardMaterializerCoordinator` warms content-cache every Tick10s
- BUT: materializer never notified SignalR that anything changed
- No invalidation beacons → no client HTMX OOB fetches
- Summit broadcasts (summary/countries/endpoints) worked fine; materialized content didn't

**Fix Deployed (commit fbb3f6fd):**

1. **Inject hub context + cursor into materializer:**
   - `IHubContext<StyloBotDashboardHub>` (optional, self-disables if null)
   - `IDashboardChangeCursor` (already injected, now used for invalidation tracking)

2. **Track warmed surfaces:**
   - As each envelope warms, collect its `manifest.PageKey` into a HashSet
   - After warming pass, iterate warmed pages

3. **Emit invalidation beacons:**
   - For each warmed page: `_cursor.Bump(pageKey)` (records tick)
   - Queue broadcast: `SignalRBroadcastConstrainer.Queue(_hubContext, pageKey, intervalMs)`
   - Constrainer handles rate-limiting (coalesces to single flush ~500ms)

4. **New option for tuning:**
   - `DashboardMaterializerOptions.MaterializerBroadcastIntervalMs` (default 500ms)
   - Controls signal-coalescing window (not tick interval; tick stays Tick10s per constraint)

**Architecture compliance:**
- ✅ Drives off ScheduleCoordinator Tick10s (no BackgroundService, no timer)
- ✅ Uses existing SignalRBroadcastConstrainer for rate-limiting + beacon emission
- ✅ Tick-versioned BroadcastDirty carried by constrainer (clients can skip cold-miss fetches)
- ✅ Back-compat: legacy BroadcastInvalidation consumers unaffected (both methods emitted)

**Testing:**
- Build verification: no syntax errors
- Architectural check: constraints met (ScheduleCoordinator-driven, no BackgroundService)
- Beacons flow: materializer tick → cursor bump → signal queue → constrainer flush → hub broadcast → client update

**Next (marketing site):**
- Deploy to staging
- Browser-verify dashboard with real traffic: no timeout, live updates firing
- Verify widget data-sb-tick skips redundant fetches
- Confirm no "Server timeout" / 1006 close / reconnect loop

**Staging deployment checklist:**
- [ ] Build passes CI
- [ ] Deploy gateway
- [ ] Dashboard opens without timeout
- [ ] Live traffic updates widget (5-10s lag = Tick10s normal)
- [ ] No console errors "Server timeout", "1006"
- [ ] No reconnect loops
- [ ] Multiple widgets update concurrently
