# Response PII Masking

Response PII masking is an opt-in response action that can sanitize outgoing payloads for risky traffic.

- Action markers: `mask-pii`, `strip-pii` (`strip-pii` is an alias)
- Feature flag: `BotDetection:ResponsePiiMasking:Enabled`
- Default: disabled (`false`)

## When it runs

Masking only runs when both are true:

1. A response action marker is present (`mask-pii` / `strip-pii`)
2. `ResponsePiiMasking.Enabled == true`

If disabled, the response is passed through unchanged and a skip signal is emitted.

## Performance model

Masking is stream-based and fail-open by design:

- Sliding-window response stream (cross-chunk match support)
- Minimum process chunk to avoid tiny-write overhead
- UTF-8 boundary-safe chunk processing
- Content-type guard (text/json/xml/js/form)
- Compression guard (skips compressed response bodies)
- Size cap (large responses pass through)

## What is masked

Microsoft Recognizers Text is used to identify:

- Email
- Phone
- IP address
- URL
- GUID
- DateTime

Matched entities are replaced with:

```text
[REDACTED:PII]
```

## Signals, headers, and logs

When masking runs:

- Response header: `X-Bot-Response-Action: mask-pii`
- Context items:
  - `BotDetection.ResponsePiiMasking.Attempted`
  - `BotDetection.ResponsePiiMasking.Masked`
  - `BotDetection.ResponsePiiMasking.RedactionCount`
  - `BotDetection.ResponsePiiMasking.Mode`
  - `BotDetection.ResponsePiiMasking.FailOpen`
- Aggregated signals:
  - `response.pii_masking.attempted`
  - `response.pii_masking.masked`
  - `response.pii_masking.redaction_count`
  - `response.pii_masking.mode`
  - `response.pii_masking.fail_open`

When skipped (feature disabled or service missing):

- Context items:
  - `BotDetection.ResponsePiiMasking.Attempted=false`
  - `BotDetection.ResponsePiiMasking.Skipped=true`
  - `BotDetection.ResponsePiiMasking.SkipReason`
- Aggregated signals:
  - `response.pii_masking.attempted=false`
  - `response.pii_masking.skipped=true`
  - `response.pii_masking.skip_reason`

## Configuration

### Recommended production baseline

```json
{
  "BotDetection": {
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

### Standard ASP.NET Core env vars

```bash
BotDetection__ResponsePiiMasking__Enabled=true
BotDetection__ResponsePiiMasking__AutoApplyForHighConfidenceMalicious=true
BotDetection__ResponsePiiMasking__AutoApplyBotProbabilityThreshold=0.9
BotDetection__ResponsePiiMasking__AutoApplyConfidenceThreshold=0.75
```

### Website host env vars (Stylobot.Website)

```bash
BOTDETECTION_RESPONSE_PII_MASKING_ENABLED=true
BOTDETECTION_RESPONSE_PII_MASKING_AUTO_APPLY=true
BOTDETECTION_RESPONSE_PII_MASKING_AUTO_APPLY_BOT_THRESHOLD=0.9
BOTDETECTION_RESPONSE_PII_MASKING_AUTO_APPLY_CONFIDENCE_THRESHOLD=0.75
```

## Rollout guidance

1. Enable in one environment first.
2. Start with explicit action policy usage on a limited surface.
3. Monitor redaction counts and skip reasons.
4. Expand to broader policies after validating latency and output safety.
