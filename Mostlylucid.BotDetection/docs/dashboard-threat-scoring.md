# Dashboard Threat Scoring

The intent detection system (see [Intent Classification](../Orchestration/ContributingDetectors/IntentContributor.cs)) computes per-request threat scores and threat bands. This document describes how those values flow through the dashboard pipeline and appear in the operator UI.

## Architecture

Threat scoring is **orthogonal to bot probability**. A human probing `.env` files has low bot probability but high threat score. The two dimensions are surfaced independently throughout the dashboard.

```
IntentContributor
  -> intent.threat_score / intent.threat_band signals
  -> AggregatedEvidence.ThreatScore / ThreatBand
  -> DetectionBroadcastMiddleware
     -> DashboardDetectionEvent.ThreatScore / ThreatBand
     -> DashboardSignatureEvent.ThreatScore / ThreatBand
     -> SignatureAggregateCache (top bots)
     -> VisitorListCache (visitor list)
     -> StyloBotDashboardMiddleware APIs
     -> SignalR broadcasts
     -> Frontend (Alpine.js + Razor)
```

## Threat Bands

| Band | Score Range | Dashboard Color | CSS Class |
|------|------------|----------------|-----------|
| Critical | >= 0.80 | `#ef4444` (red) | `badge-error` |
| High | >= 0.55 | `#f97316` (orange) | `badge-warning` |
| Elevated | >= 0.35 | `#f59e0b` (amber) | `badge-info` |
| Low | >= 0.15 | `#22c55e` (green) | `badge-success` |
| None | < 0.15 | Hidden | N/A |

When the threat band is `None` or absent, no badge is displayed. This ensures backwards compatibility -- detections without intent data show no extra UI.

## Data Flow

### Signal Whitelist

The `intent.` prefix is included in `DetectionBroadcastMiddleware.AllowedSignalPrefixes`, allowing intent signals to pass through to the dashboard. These signals are classification outputs (not raw user data) and carry no PII.

### Detection Builders

**Local detection path** (`BuildDetectionFromEvidence`):
- `ThreatScore` is set from `AggregatedEvidence.ThreatScore` when > 0
- `ThreatBand` is set from `AggregatedEvidence.ThreatBand` when != `ThreatBand.None`

**Upstream/proxy path** (`BuildDetectionFromUpstream`):
- `ThreatScore` is extracted from `importantSignals["intent.threat_score"]`
- `ThreatBand` is extracted from `importantSignals["intent.threat_band"]`

### Cache Layer

Both `SignatureAggregateCache` and `VisitorListCache` carry `ThreatScore` and `ThreatBand` fields:
- New entries: populated from the detection event
- Existing entries: updated with null coalescing (`detection.ThreatScore ?? existing.ThreatScore`) to preserve values when new detections lack threat data
- Thread safety: all mutations are under existing `SyncRoot` locks

### API Endpoints

| Endpoint | New Fields |
|----------|-----------|
| `/_stylobot/api/detections` | `threatScore`, `threatBand` on each detection |
| `/_stylobot/api/signatures` | `threatScore`, `threatBand` on each signature |
| `/_stylobot/api/topbots` | `threatScore`, `threatBand` on each bot entry |
| `/_stylobot/api/clusters` | `dominantIntent`, `averageThreatScore` on each cluster |
| `/_stylobot/api/me` | `threatScore`, `threatBand` on your detection |
| SignalR `BroadcastDetection` | `threatScore`, `threatBand` on each detection |
| SignalR `BroadcastSignature` | `threatScore`, `threatBand` on each signature |

### Cluster Enrichment

`BotClusterService` computes `DominantIntent` (the most common intent category in the cluster) and `AverageThreatScore` (mean threat score across cluster members). These appear in both the SSR page load and the clusters API.

## Dashboard UI

### Detection Detail Panel

When viewing a detection, a threat badge appears next to the risk band badge:

```
[High]  [Threat: Critical]
```

The threat badge uses DaisyUI badge classes colored by threat band.

### "Your Detection" Panel

If the current visitor has a threat band, it appears after their risk band:

```
You're Human
[Low]  [Threat: Elevated]
```

### Cluster Cards

Clusters display:
- Threat percentage badge when `averageThreatScore > 0.1`
- Dominant intent label when present
- Member count badge

### Visitor List

Each visitor row shows a compact threat band indicator after the risk band label.

### Signal Drill-Down

Intent signals (`intent.*`) appear in a dedicated "Intent / Threat" category in the signal detail view, with a target emoji icon.

## Narrative Enhancement

Bot narratives include a threat qualifier prefix for elevated+ threats:

| ThreatBand | Prefix | Example |
|-----------|--------|---------|
| Critical | `CRITICAL THREAT: ` | "CRITICAL THREAT: Config Scanner on /.env - caught by session intent analysis" |
| High | `High-threat ` | "High-threat Exploit Scanner on /shell - caught by attack payload detection" |
| Elevated | `Elevated-threat ` | "Elevated-threat Possible bot on /api/v1 - caught by behavioral analysis" |
| Low / None | (none) | "Scraper on /robots.txt - caught by user-agent analysis" |

## Backwards Compatibility

All new fields are nullable (`double?`, `string?`) or default to zero (`AverageThreatScore`). Existing detections without intent data:
- Have `null` for `ThreatScore` and `ThreatBand`
- Show no threat badges in the UI (all guarded with `x-show`)
- Have no threat prefix in narratives (switch defaults to empty string)
- Have `0` for `AverageThreatScore` on clusters (badge hidden when <= 0.1)

No database schema changes are required. The new fields are carried in-memory only (via caches and SignalR). Persisted detections that predate the change will have `null` for threat fields when loaded, which is handled correctly.

## Files Modified

| File | Purpose |
|------|---------|
| `UI/Models/DashboardModels.cs` | Added `ThreatScore`/`ThreatBand` to 3 DTOs; `DominantIntent`/`AverageThreatScore` to cluster DTO |
| `UI/Models/DashboardTopBotEntry.cs` | Added `ThreatScore`/`ThreatBand` |
| `UI/Middleware/DetectionBroadcastMiddleware.cs` | Added `"intent."` to signal whitelist; populated threat in both detection builders and signature storage |
| `UI/Middleware/StyloBotDashboardMiddleware.cs` | Added intent/threat to cluster projections (SSR + API) and `BuildYourDetectionJson` |
| `UI/Services/SignatureAggregateCache.cs` | Threaded threat through `SignatureAggregate` class, `CreateNew`, `Update`, `ToEntry`, `SeedFromTopBots` |
| `UI/Services/VisitorListCache.cs` | Threaded threat through `CachedVisitor`, `Upsert` (create + update), `SnapshotAll` |
| `UI/Services/DetectionNarrativeBuilder.cs` | Threat qualifier prefix on bot narratives |
| `UI/Views/Dashboard/Index.cshtml` | Threat badges on clusters, detection detail, your detection, visitor list |
| `website/src/dashboard.ts` | `threatBandClass` helper; `intent.` signal category; exposed in both Alpine apps |

## Security Considerations

- **XSS**: All threat data rendered via Alpine.js `x-text` (sets `textContent`, auto-escapes HTML). Server-rendered JSON uses `SafeJson()` to prevent script tag breakout.
- **PII**: `ThreatScore` is a numeric value; `ThreatBand` is an enum string. Neither carries PII.
- **Signal whitelist**: `intent.*` signals are classifier outputs, not raw request data. The existing `BlockedSignalKeys` set blocks PII signal keys.
- **Thread safety**: New cache fields are guarded by existing `SyncRoot` locks in all mutation and snapshot paths.
