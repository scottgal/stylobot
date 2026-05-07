# Profile Mode Example

Use this example to collect calibration data from live traffic before deciding on a blocking threshold.

## What it does

- **Inline (per-request):** fingerprint only. ~300ns overhead, no behavioral analysis.
- **Background:** full detection pipeline runs on every request asynchronously.
- **Calibration store:** results accumulate in `/app/data/profile_calibration.db`.
- **Nothing is blocked.** Traffic flows through unchanged.

## Quick start

```bash
cp .env.example .env
# edit .env: set ADMIN_SECRET
docker compose up -d
```

After collecting 24-48 hours of traffic, query the calibration endpoint:

```bash
curl -H "X-Admin-Secret: $ADMIN_SECRET" http://localhost:8080/admin/calibration | jq .
```

Sample response:
```json
{
  "totalAnalyzed": 14823,
  "collectionPeriodHours": 48.0,
  "thresholdSimulation": [
    { "threshold": 0.50, "wouldBlock": 1875, "percentOfTraffic": 12.6 },
    { "threshold": 0.70, "wouldBlock": 847,  "percentOfTraffic": 5.7  },
    { "threshold": 0.85, "wouldBlock": 203,  "percentOfTraffic": 1.4  }
  ],
  "recommendedThreshold": 0.70,
  "recommendationReason": "Score valley at 0.7 separates human and bot clusters."
}
```

## Switching to active blocking

Once you have a threshold you trust, disable profile mode and set your threshold:

```yaml
environment:
  - GATEWAY_PROFILE_MODE=false
  - BotDetection__BotThreshold=0.70
  - BotDetection__DefaultActionPolicyName=throttle-stealth
```

## Reset calibration data

To start a fresh collection period:

```bash
curl -X POST -H "X-Admin-Secret: $ADMIN_SECRET" http://localhost:8080/admin/calibration/reset
```
