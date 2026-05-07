# Profile Mode Design

**Goal:** Let operators collect calibration data from live traffic before committing to a blocking threshold, with near-zero per-request overhead.

**Architecture:** Fingerprint-only inline path feeds a bounded background channel; a hosted worker runs the full detection pipeline on queued snapshots; results go to an isolated SQLite calibration store; an admin endpoint exposes a threshold simulator.

**Primary audience:** Operators who want to understand their traffic before enabling blocking — "what threshold should I set?"

---

## Problem

Shadow mode (the existing `monitor` detection policy + `logonly` action) runs the full detection pipeline inline on every request. It answers "what would happen if I blocked?" but at the cost of full detection latency on every request. Profile mode trades inline cost for background depth: negligible per-request overhead, full analysis deferred to a background worker.

---

## In-Request Path

A new `profile` named detection policy that runs only the `SignatureContributor` (Priority 1). No UA analysis, no behavioral, no IP reputation, no heuristics inline.

After fingerprinting, a serialized `ProfileRequestSnapshot` (headers, IP, UA, TLS metadata, path, method, timestamp) is pushed onto a bounded background channel via a non-blocking write. The request then passes to the upstream immediately.

Per-request overhead: ~300-500ns fingerprinting plus a non-blocking channel write.

The `profile` policy is defined in `BotDetectionOptions` as a named policy, not hardcoded — operators can apply it to specific paths via `PathPolicies` if they want finer control.

---

## Background Analysis

`ProfileAnalysisWorker` is a hosted service that drains the channel and runs the full detection pipeline on each snapshot. This uses the existing `BlackboardOrchestrator` with a reconstructed `DetectionContext` built from the snapshot.

Results go to `ProfileCalibrationStore` (SQLite) — **not** the live reputation or session store. Profile data is isolated: it does not contaminate active detection if the operator later switches to a blocking mode.

`ProfileAnalysisChannel` wraps a `System.Threading.Channels.Channel<ProfileRequestSnapshot>` with:
- `BoundedChannelFullMode.DropOldest` backpressure
- Configurable capacity (default 5000)
- Configurable worker concurrency (default 2)
- Metrics: queue depth, total enqueued, total processed, total dropped

---

## Calibration Store

SQLite table `profile_calibration` stores per-analysis results:

| Column | Type | Notes |
|--------|------|-------|
| `id` | INTEGER PK | |
| `signature_hash` | TEXT | HMAC-SHA256 of IP+UA, no raw PII |
| `bot_probability` | REAL | 0.0–1.0 |
| `risk_band` | TEXT | Low/Medium/High/VeryHigh |
| `bot_type` | TEXT | nullable |
| `bot_name` | TEXT | nullable |
| `top_detector` | TEXT | highest-weight detector that fired |
| `path_pattern` | TEXT | normalized path (no query string) |
| `analyzed_at` | TEXT | ISO-8601 UTC |

No raw IP addresses, user agents, or query strings are stored.

---

## Admin API Endpoint

`GET /admin/calibration` — requires `ADMIN_SECRET` like all admin endpoints.

Response shape:

```json
{
  "totalAnalyzed": 14823,
  "collectionPeriodHours": 72,
  "scoreDistribution": {
    "0.0": 8241, "0.1": 1203, "0.2": 891,
    "0.3": 412, "0.4": 201, "0.5": 387,
    "0.6": 289, "0.7": 156, "0.8": 203,
    "0.9": 512, "1.0": 328
  },
  "thresholdSimulation": [
    { "threshold": 0.50, "wouldBlock": 1875, "percentOfTraffic": 12.6, "topBotTypes": ["Scraper", "MaliciousBot"] },
    { "threshold": 0.70, "wouldBlock": 847,  "percentOfTraffic": 5.7,  "topBotTypes": ["Scraper"] },
    { "threshold": 0.85, "wouldBlock": 203,  "percentOfTraffic": 1.4,  "topBotTypes": ["MaliciousBot"] }
  ],
  "recommendedThreshold": 0.70,
  "recommendationReason": "Largest score gap between 0.65 and 0.75 — separates bot cluster from human cluster.",
  "queueDepth": 0,
  "totalDropped": 0
}
```

`recommendedThreshold` is computed by finding the largest gap in the score distribution histogram (the natural valley between the human-score cluster and the bot-score cluster). If no clear gap exists, the recommendation is omitted and `recommendationReason` says "Insufficient data or no clear score separation — collect more traffic."

`GET /admin/calibration/reset` (POST, admin-protected) — clears the calibration store to start a fresh collection period.

---

## Gateway Configuration

```
GATEWAY_PROFILE_MODE=true
GATEWAY_PROFILE_CHANNEL_CAPACITY=5000    # optional, default 5000
GATEWAY_PROFILE_CONCURRENCY=2            # optional, default 2
```

`ConfigureProfileMode` in `Program.cs` (parallel to `ConfigureDemoMode`) sets all paths to the `profile` detection policy when `GATEWAY_PROFILE_MODE=true` or `Gateway:ProfileMode:Enabled=true`.

Profile mode and demo mode are mutually exclusive — if both are set, profile mode takes precedence and a warning is logged.

The startup banner shows `Profile  collecting (background analysis active)` in the policy row when profile mode is enabled.

---

## Files

| File | Action |
|------|--------|
| `src/Stylobot.Gateway/Configuration/ProfileModeOptions.cs` | Create — config binding for profile mode env vars |
| `src/Stylobot.Gateway/Services/ProfileAnalysisChannel.cs` | Create — bounded channel wrapper with metrics |
| `src/Stylobot.Gateway/Services/ProfileRequestSnapshot.cs` | Create — serializable request snapshot record |
| `src/Stylobot.Gateway/Services/ProfileAnalysisWorker.cs` | Create — hosted service draining the channel |
| `src/Stylobot.Gateway/Data/ProfileCalibrationStore.cs` | Create — SQLite store, queries, recommendation engine |
| `src/Stylobot.Gateway/Endpoints/CalibrationEndpoint.cs` | Create — `GET /admin/calibration`, `POST /admin/calibration/reset` |
| `src/Stylobot.Gateway/Configuration/ServiceCollectionExtensions.cs` | Modify — register new services when profile mode enabled |
| `src/Stylobot.Gateway/Program.cs` | Modify — add `ConfigureProfileMode`, banner update |
| `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs` | Modify — add `profile` named policy entry |

---

## Scope

No changes to the detection engine internals, dashboard UI, session store, reputation system, or any non-Gateway project. All calibration data is stored in a separate SQLite table and never written to the live detection state.
