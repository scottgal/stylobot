# Fast-Path Reputation Detection

Ultra-fast Wave 0 contributor that checks for confirmed patterns (both good and bad) in the reputation cache, enabling instant allow or abort decisions for known actors before any expensive analysis runs.

## How It Works

The detector runs first in Wave 0 (priority 3) with no dependencies. It queries the `IPatternReputationCache` using the raw User-Agent and client IP to find previously classified patterns. This is a circuit-breaker style check that avoids running the full 29-detector pipeline for requests that have already been conclusively identified.

For **confirmed good patterns** (ConfirmedGood or ManuallyAllowed states), the detector emits a strong human contribution with negative confidence delta (-0.8) and weight 2.5. This does not trigger an early exit but strongly biases the final score toward human classification. This approach is intentional: reputation-confirmed "good" patterns are typically real humans, and `VerifiedGoodBot` early exit is reserved for cryptographically verified bots (Googlebot, Bingbot via DNS/IP verification).

For **confirmed bad patterns** (ConfirmedBad or ManuallyBlocked states), the detector emits a `VerifiedBot` contribution with the cached bot score as confidence delta and a very high weight (default 3.0), enabling instant abort. The pattern's accumulated support score (observation count) is written to signals for downstream use.

The detector works in tandem with `ReputationBiasContributor` (priority 45): FastPathReputation handles instant allow/abort for high-confidence patterns, while ReputationBias handles softer scoring bias for suspect/neutral patterns after signals have been extracted.

## Signals Emitted

| Signal Key | Type | Description |
|---|---|---|
| `reputation.fastpath.hit` | boolean | Whether a reputation cache match was found |
| `reputation.can_allow` | boolean | Pattern qualifies for fast allow |
| `reputation.can_abort` | boolean | Pattern qualifies for fast abort |
| `reputation.fastpath.{type}.pattern_id` | string | Matched pattern identifier |
| `reputation.fastpath.{type}.state` | string | Reputation state of matched pattern |
| `reputation.fastpath.{type}.score` | number | Bot score of matched pattern |
| `reputation.fastpath.{type}.support` | number | Observation count for the pattern |

Where `{type}` is `useragent` or `ip`.

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "FastPathReputationContributor": {
        "Parameters": {
          "fast_abort_weight": 3.0,
          "min_support_allow": 10.0,
          "min_support_abort": 5.0,
          "allow_max_bot_score": 0.1,
          "abort_min_bot_score": 0.9
        }
      }
    }
  }
}
```

## Parameters

| Parameter | Default | Description |
|---|---|---|
| `fast_abort_weight` | 3.0 | Contribution weight for confirmed bad patterns |
| `min_support_allow` | 10.0 | Minimum observations needed for auto-allow |
| `min_support_abort` | 5.0 | Minimum observations needed for auto-block |
| `allow_max_bot_score` | 0.1 | Maximum bot score to qualify for fast allow |
| `abort_min_bot_score` | 0.9 | Minimum bot score to qualify for fast abort |
