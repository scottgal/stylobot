# Timescale Reputation Detection

Wave: 0 (Fast Path)
Priority: 15
Configuration: `timescale.detector.yaml`

## Purpose

Queries 90-day historical reputation data from TimescaleDB for IP+UA signatures. Analyzes historical bot ratios, request velocity, and activity patterns to provide reputation-based classification. Falls back gracefully when TimescaleDB is not configured.

## Signals Emitted

| Signal Key | Type | Description |
|------------|------|-------------|
| `ts.bot_ratio` | double | Historical bot-to-total ratio (0.0-1.0) |
| `ts.hit_count` | int | Total historical hit count |
| `ts.days_active` | int | Number of distinct days signature was active |
| `ts.velocity` | int | Requests in last hour (burst detection) |
| `ts.is_new` | bool | True for first-time signatures |
| `ts.is_conclusive` | bool | Whether data is conclusive enough to skip LLM |
| `ts.avg_bot_prob` | double | Average bot probability across observations |

## Configuration

Via YAML manifest `timescale.detector.yaml` with appsettings.json overrides:

```json
{
  "BotDetection": {
    "Detectors": {
      "TimescaleReputationContributor": {
        "Parameters": {
          "high_bot_ratio": 0.8,
          "low_bot_ratio": 0.2,
          "min_hits_conclusive": 3,
          "high_velocity_per_hour": 50
        }
      }
    }
  }
}
```

## Detection Logic

1. Looks up IP+UA signature in TimescaleDB continuous aggregates (90-day window)
2. If no history: marks as new signature, neutral contribution
3. If high bot ratio (>= 0.8) with sufficient hits: strong bot signal
4. If low bot ratio (<= 0.2) with sufficient hits: human signal
5. Velocity check: > 50 requests/hour triggers burst detection
6. If `min_hits_conclusive` reached, marks as conclusive (can skip expensive LLM)

## Performance

Typical execution: <1ms (TimescaleDB query via continuous aggregate).
Falls back to no-op when TimescaleDB not configured.

## Dependencies

- Requires `Mostlylucid.BotDetection.UI.PostgreSQL` package for TimescaleDB storage
- Works with plain PostgreSQL (continuous aggregates degraded to regular queries)
