# Deploy on Server

This is the practical path for most teams.

## Minimal deployment

1. Copy `.env.example` to `.env`
2. Set required secrets
3. Run compose

```bash
cp .env.example .env
docker compose up -d
```

## Required values

- `BOTDETECTION_SIGNATURE_HASH_KEY`
- `BOTDETECTION_CLIENTSIDE_TOKEN_SECRET`
- `POSTGRES_PASSWORD` (if using database services)

## Full production template (server)

Use this as a baseline when you want response PII masking only for malicious traffic while keeping stealth throttling as default.

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "DefaultActionPolicyName": "throttle-stealth",
    "BotTypeActionPolicies": {
      "Tool": "throttle-tools",
      "MaliciousBot": "mask-pii"
    },
    "ResponsePiiMasking": {
      "Enabled": true,
      "AutoApplyForHighConfidenceMalicious": true,
      "AutoApplyBotProbabilityThreshold": 0.9,
      "AutoApplyConfidenceThreshold": 0.75
    }
  }
}
```

Equivalent `.env` overrides for the website container/process:

```bash
BOTDETECTION_ACTION_POLICY=throttle-stealth
BOTDETECTION_RESPONSE_PII_MASKING_ENABLED=true
BOTDETECTION_RESPONSE_PII_MASKING_AUTO_APPLY=true
BOTDETECTION_RESPONSE_PII_MASKING_AUTO_APPLY_BOT_THRESHOLD=0.9
BOTDETECTION_RESPONSE_PII_MASKING_AUTO_APPLY_CONFIDENCE_THRESHOLD=0.75
```

## Verify deployment

```bash
docker compose ps
docker compose logs --tail 100
curl http://localhost/health
curl http://localhost/bot-detection/health
```

## Recommended rollout

1. Deploy in detect-only mode
2. Observe for at least 24 hours
3. Tune thresholds and policies
4. Enable stronger actions gradually

## Production hardening checklist

- Configure and rotate detection secrets
- Restrict dashboard access (`/_stylobot`) by policy/network
- Send logs/metrics to your observability platform
- Backup persistence stores where used
- Define incident response for false-positive spikes

## Change-management pattern

1. Create baseline metrics (bot %, challenge rate, block rate, false positives).
2. Adjust one policy dimension at a time.
3. Validate on a fixed time window before next change.
4. Promote configuration by environment.
