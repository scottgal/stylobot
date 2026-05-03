# Licensing

## FOSS

No license required. Detection, learning, blocking, and all features work indefinitely.
Configure with `AddBotDetection()` and never set `BotDetection:Licensing:Token`.

## Commercial

A signed license JWT is required. Set it in configuration:

```json
{
  "BotDetection": {
    "Licensing": {
      "Token": "<your-license-jwt>",
      "Domains": ["yourdomain.com"]
    }
  }
}
```

Start a free 30-day trial (one per organization) at https://stylobot.net.

## What happens when a license expires

**30-day grace period (once per account):**
Detection and blocking continue exactly as normal. Learning services pause:
reputation patterns stop updating, cluster detection stops retraining, centroid
sequences freeze. Accuracy degrades naturally as traffic patterns drift.

The dashboard shows the grace period end date. Renew any time to immediately
resume learning.

**After grace period:**
All action policies are forced to log-only mode. Detection still runs and the
dashboard still shows results, but no traffic is blocked or throttled. Learning
remains paused.

**Renewal:**
Drop a new JWT into your configuration file. The system picks it up within
60 seconds and resumes learning automatically - no restart required.

## Second expiry

The grace period is a one-time offer per account. If a license lapses, the
grace period is consumed, you renew, and it lapses again - the second expiry
goes directly to log-only mode with no grace window.
