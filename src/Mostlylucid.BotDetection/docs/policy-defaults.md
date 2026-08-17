# Policy defaults: what stylobot does out of the box (6.8+)

The 6.7 line shipped with `BlockDetectedBots = false` as the literal default -- detection ran, no policy fired, every request went through unmodified. 6.8 ships a full action-policy map. This document is the source of truth for *what fires when*, *what knobs change it*, and *how to revert to observe-only* during pre-launch calibration.

If you only read one section, read [`The contract`](#the-contract).

## The contract

Out of the box, stylobot:

1. **Blocks malicious bots.** `MaliciousBot` / `ExploitScanner` / `ClickFraud` -> `block-hard` (HTTP 403, minimal response).
2. **Rate-limits search and AI bots.** A real token bucket keyed on the visitor signature. `SearchEngine` / `GoodBot` / `VerifiedBot` get 60 req/min (burst 10); `AiBot` gets 10 req/min (burst 2) and bounces harder on overflow because AI scrapers ignore `Retry-After`.
3. **Leaves humans untouched.** Humans never traverse rate limits or throttles -- the per-`BotType` map only fires when a request is classified as a bot. Detection still runs; the action layer is a no-op.
4. **Throttles bots harder when the origin gets slower.** The adaptive-scaling tier ladder halves bot allowance at P95 latency >= 1000ms or 5xx rate >= 3%, drops to 10% of nominal at P95 >= 2000ms or 5xx >= 10%. The multiplier applies to bots only -- humans see no change. This is what operationalises "prioritise humans".

## The full BotType -> policy mapping

| `BotType` | Default policy | Notes |
|-----------|----------------|-------|
| `MaliciousBot` | `block-hard` | 403, minimal response |
| `ExploitScanner` | `block-hard` | Same |
| `ClickFraud` | `block-hard` | Same |
| `Tool` | `throttle-tools` | HTTP 429 + `Retry-After` + exponential backoff (curl / wget / httpie) |
| `Scraper` | `throttle-aggressive` | Long delay with high jitter |
| `AiBot` | `rate-limit-ai` | 10 req/min, burst 2. Over-limit -> `block-soft` (AI scrapers ignore `Retry-After`) |
| `SearchEngine` | `rate-limit-search` | 60 req/min, burst 10. Over-limit -> `throttle-status` (informational 429) |
| `GoodBot` | `rate-limit-search` | Same |
| `VerifiedBot` | `rate-limit-search` | Same |
| `SocialMediaBot` | `rate-limit-social` | 30 req/min, burst 5. Fediverse / Slack / Twitter link-preview stampede case |
| `MonitoringBot` | `rate-limit-monitor` | 6 req/min, burst 2 (1 every 10s is plenty for uptime checks) |
| `Unknown` | (omitted) | Falls through to `DefaultActionPolicyName` (default: `throttle-stealth`) |

Override any of these via `BotDetection:BotTypeActionPolicies` in `appsettings.json`. The dictionary completely replaces the default if you set it -- merge defaults in yourself if you want to keep them.

## The adaptive-scaling tier ladder

`rate-limit-*` policies consult `IAdaptiveScalingTracker` for the current multiplier. Effective `RequestsPerMinute` = configured * multiplier, floored at 1.

| Tier | Threshold | Multiplier | Effective `rate-limit-search` |
|------|-----------|------------|------------------------------|
| `nominal` | P95 < 500ms AND 5xx < 1% | 1.0 | 60 req/min |
| `degraded` | P95 >= 1000ms OR 5xx >= 3% | 0.5 | 30 req/min |
| `critical` | P95 >= 2000ms OR 5xx >= 10% | 0.1 | 6 req/min |

Tier transitions are dwell-gated (`Hysteresis.DwellSeconds`, default 30s) so a one-request 5xx spike doesn't halve the allowance. Recovery is asymmetric: coming back applies `Hysteresis.RecoveryMultiplier` (default 0.8) per evaluation -- prevents oscillation when a service wobbles right around a threshold.

When scaling is active, responses carry:

- `X-RateLimit-Tier: degraded` -- the active tier name
- `X-RateLimit-Multiplier: 0.50` -- the current multiplier
- `X-RateLimit-Limit: 30` -- always the *effective* value, not configured

Disable adaptive scaling globally with `BotDetection:RateLimit:AdaptiveScaling:Enabled = false`. The multiplier locks at 1.0 and rate limits behave as static caps.

## Observe-only mode (the calibration knob)

`BotDetection:ObserveOnly = true` shadows every action policy that would have fired through `logonly`. The visitor sees no behaviour change; the dashboard still records *which* policy would have fired via `AggregatedEvidence.TriggeredActionPolicyName`. Log lines on the shadow path are tagged ` [observe-only shadow]`.

Use this when you want to calibrate against real traffic before flipping the switch. Replaces the older implicit `BlockDetectedBots = false` posture as the canonical opt-in knob.

## Migration from 6.7

If you were running with the implicit observe-only default (`BlockDetectedBots = false` and `DefaultActionPolicyName = null`):

```json
"BotDetection": {
  "ObserveOnly": true
}
```

That single line preserves the 6.7 posture: detection runs, dashboard records what *would* have fired, no visitor-visible action.

To revert to "detect but truly do nothing" (no logonly shadow either), clear both:

```json
"BotDetection": {
  "BlockDetectedBots": false,
  "BotTypeActionPolicies": {},
  "DefaultActionPolicyName": null
}
```

The legacy `BlockDetectedBots` / `MinConfidenceToBlock` / `AllowVerifiedSearchEngines` flags remain `[Obsolete]` and will be removed in v7. The new flags above are the canonical surface from 6.8 onwards.

## Where to look in the dashboard

The `Policy` tab at `/dashboard/investigate?tab=policy` renders:

- An observe-only badge when calibration mode is on, otherwise the default-fallback policy name.
- A `BotType -> policy` grid showing the live mapping per type, including the current effective RPM for rate-limit policies.
- Per-policy cards with the configured params, the active tier, and the live multiplier breakdown when adaptive scaling is below 1.0.

Per-policy live numbers come straight from `IPolicyStateProvider`; the tab is operator-facing and doesn't poll.

## Design notes

- `PolicyIntent` (Block / RateLimit / Throttle / Challenge / Pass) sits one layer above `ActionType` -- the action *class* is "which class is wired up", the intent is "what the operator is trying to do". Many policy classes can share an intent (`block`, `block-hard`, `block-soft` all carry `Block`).
- Rate-limit buckets are signature-keyed by default. Switch a policy to IP-keyed via `KeyBy: "Ip"` for grey-area bot classes where signature evasion is a concern. Signature is more stable across IP rotation; IP cycles more easily but is harder to spoof.
- `OverLimitAction` is just another action policy name. A typo falls back to bare HTTP 429 + `Retry-After: 60` so a config slip doesn't open the gate. Override per policy in `BotDetection:ActionPolicies`.

## See also

- [`action-policies.md`](action-policies.md) -- full grammar reference for every built-in policy class.
- [`policy-system.md`](policy-system.md) -- where the intent grammar sits in the larger detection -> policy -> action pipeline.
- [`configuration-reference.md`](configuration-reference.md) -- complete option list with types and defaults.
