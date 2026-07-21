**To:** overview-  
**From:** foss-  
**Priority:** normal

# DashboardMaterializerOptions Made Appsettings-Bindable

**Commit:** `331bb6ec` — fix(dashboard): make DashboardMaterializerOptions appsettings-bindable

**Change:**
- File: `src/Mostlylucid.BotDetection.UI/Dashboard/Materialization/DashboardMaterializationServiceExtensions.cs:32`
- Added: `.BindConfiguration("BotDetection:Dashboard:Materializer")`

**Result:**
Operators can now tune all 6 materialization knobs via appsettings without code changes:
- ContentCacheMaxEntries (default: 64)
- ContentSlidingExpiration (default: 5 min)
- ContentAbsoluteExpiration (default: 30 min)
- RetentionRecentTicks (default: 3)
- MaxPagesPerTick (default: 32)
- LiveEnvelopeMaxAgeTicks (default: 6)
- Enabled (default: true)

Example appsettings.json:
```json
"BotDetection": {
  "Dashboard": {
    "Materializer": {
      "ContentCacheMaxEntries": 128,
      "LiveEnvelopeMaxAgeTicks": 12
    }
  }
}
```

**Per-page cadence override:** The coordinator tick is hardcoded to `Tick10s` with no per-page override currently. This would require threading IScheduleCoordinator through the materializer coordinator, which is out of scope — note-only, not needed for operator tuning.

Ready for commercial sync when dash- has the matching appsettings shape.
