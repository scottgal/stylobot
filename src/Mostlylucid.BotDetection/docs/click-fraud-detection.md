# Click Fraud Detection

StyloBot detects click fraud and invalid ad traffic (IAB IVT class: **SIVT** - Sophisticated Invalid Traffic) using the `ClickFraudContributor` (priority 38) and `PiiQueryStringContributor` (which extracts UTM parameters and click IDs). Together they form a two-stage pipeline: signal extraction followed by weighted pattern scoring.

---

## Why dedicated click fraud detection

Generic bot detection catches obvious threats but misses the click fraud-specific threat model. A sophisticated click fraud actor may:

- Use residential or mobile proxy IPs (not flagged by datacenter detection alone)
- Send complete asset bursts (bypassing content sequence divergence)
- Have a plausible TLS/HTTP2 fingerprint
- Arrive with a valid `gclid` or `fbclid` click ID

The click fraud detector combines ad traffic signals with behavioral signals to produce an **IAB SIVT classification** independent of the main bot probability score. It feeds back into `IntentContributor` threat scoring and `ReputationBiasContributor` weighting, making future requests from the same fingerprint more aggressively flagged when paid traffic is involved.

---

## Signal flow

```
Request arrives with ?gclid=... or utm_source=...
        |
        v
PiiQueryStringContributor (priority 19)
  - strips and hashes gclid, fbclid, msclkid, ttclid
  - sets utm.present, utm.has_gclid, utm.source_platform
  - checks referrer vs. declared ad platform -> utm.referrer_mismatch
        |
        v
ClickFraudContributor (priority 38)
  - reads utm.* + ip.* + session.* + fingerprint.* + resource.*
  - scores 8 patterns with configurable weights
  - emits clickfraud.confidence, clickfraud.pattern, clickfraud.is_paid_traffic
        |
        +---> IntentContributor (priority 40): if confidence > 0.55 -> ad_fraud threat category
        |
        +---> ReputationBiasContributor: amplifies weight by paid_traffic_bias_multiplier (default 1.5)
        |
        +---> HeuristicFeatureExtractor: cf:click_fraud_score (weight 0.8), cf:is_paid_traffic (weight 0.3)
```

---

## Detection patterns

All weights are configurable via YAML (see [Configuration](#configuration)). The composite score is clamped to `[0.0, 1.0]`.

| Pattern | Trigger | Default weight | Notes |
|---------|---------|---------------|-------|
| `datacenter_paid` | Datacenter IP + paid ad landing | 0.50 | High-confidence fraud signal |
| `referrer_spoof` | Click ID present + referrer absent or platform mismatch | 0.40 | Bots often strip or fake the referrer |
| `headless_paid` | Headless browser (`fingerprint.headless_score` > 0) + paid landing | 0.40 | Headless on organic traffic: 0.20 |
| `vpn_paid` | VPN/anonymizer + paid landing | 0.25 | Not conclusive alone; adds to composite |
| `proxy_paid` | Open proxy + paid landing | 0.20 | Open proxies on paid traffic: near-certain fraud |
| `immediate_bounce` | Single-request session (`session.request_count` == 1) | 0.20 | Bots land, register the impression, exit |
| `engagement_void` | Document request but zero assets loaded (`resource.asset_count` == 0) | 0.15 | No CSS/JS/images = no real page render |
| `datacenter_organic` | Datacenter IP without paid traffic | 0.15 | Lower weight; datacenter + organic is common for crawlers |

Multiple patterns accumulate: a headless browser arriving via a datacenter IP on a Google Ads click scores `0.40 + 0.50 = 0.90` (capped at 1.0).

---

## IAB IVT classification

The detector aligns with IAB Tech Lab's Invalid Traffic (IVT) taxonomy:

| Class | Definition | Patterns covered |
|-------|-----------|-----------------|
| **GIVT** (General) | Known bots/crawlers, out-of-scope traffic | Covered by upstream UA/IP detectors |
| **SIVT** (Sophisticated) | Harder-to-detect fraud: datacenter traffic disguised as human, ad injection, session fraud | `datacenter_paid`, `referrer_spoof`, `headless_paid`, `immediate_bounce`, `engagement_void` |

The `clickfraud.pattern` signal maps directly to SIVT sub-categories.

---

## Output signals

| Signal key | Type | Description |
|-----------|------|-------------|
| `clickfraud.confidence` | `double` | Composite fraud score, 0.0-1.0. Scores above `bot_threshold` (default 0.55) produce a bot contribution |
| `clickfraud.pattern` | `string` | Primary pattern label(s), comma-separated: `datacenter_paid`, `referrer_spoof`, `headless_paid`, `vpn_paid`, `proxy_paid`, `immediate_bounce`, `engagement_void` |
| `clickfraud.is_paid_traffic` | `bool` | True when UTM parameters or a click ID (`gclid`, `fbclid`, `msclkid`, `ttclid`) is present |
| `clickfraud.checked` | `bool` | Gate signal - true once the detector has run; used by `IntentContributor` trigger conditions |

### Input signals consumed

From `PiiQueryStringContributor`: `utm.present`, `utm.has_gclid`, `utm.has_fbclid`, `utm.has_msclkid`, `utm.has_ttclid`, `utm.referrer_mismatch`, `utm.source_platform`

From upstream detectors: `ip.is_datacenter`, `geo.is_vpn`, `geo.is_proxy`, `fingerprint.headless_score`, `session.request_count`, `resource.asset_count`, `transport.protocol_class`

---

## Configuration

All weights and thresholds are YAML-configurable. Override any value in `appsettings.json`:

```json
{
  "BotDetection": {
    "Detectors": {
      "ClickFraudContributor": {
        "Weights": {
          "BotSignal": 1.5
        },
        "Parameters": {
          "datacenter_paid_weight": 0.50,
          "datacenter_unpaid_weight": 0.15,
          "vpn_paid_weight": 0.25,
          "proxy_paid_weight": 0.20,
          "referrer_mismatch_clickid_weight": 0.40,
          "referrer_mismatch_paid_weight": 0.25,
          "single_page_weight": 0.20,
          "no_assets_weight": 0.15,
          "headless_paid_weight": 0.40,
          "headless_unpaid_weight": 0.20,
          "bot_threshold": 0.55
        }
      }
    }
  }
}
```

### Tuning guidance

**Ad networks with low-quality traffic** (programmatic display, popunder): raise `datacenter_paid_weight` to 0.70 and `referrer_mismatch_clickid_weight` to 0.60. These networks route heavily through datacenter proxies.

**Search ads (Google, Bing)**: `referrer_spoof` and `immediate_bounce` are the highest-signal patterns. The ad network itself provides the referrer, so mismatches are strongly indicative.

**Low-traffic sites** where a single-page view is normal: lower `single_page_weight` to 0.05 or 0.0 to avoid false positives on genuine first-page bounces.

**High-value campaigns** where you want aggressive filtering: lower `bot_threshold` to 0.40 and raise `BotSignal` weight to 2.0. This blocks more, so combine with a `challenge` action policy rather than `block` to avoid blocking legitimate visitors.

---

## Integration with other detectors

### IntentContributor

When `clickfraud.checked` is true and `clickfraud.confidence` exceeds `clickfraud_threshold` (default 0.55), `IntentContributor` produces an `ad_fraud` threat category with a blended threat score that incorporates both the click fraud confidence and the standard intent signals.

```json
{
  "BotDetection": {
    "Detectors": {
      "IntentContributor": {
        "Parameters": {
          "clickfraud_weight": 0.7,
          "clickfraud_threshold": 0.55
        }
      }
    }
  }
}
```

### ReputationBiasContributor

Known-bad fingerprints arriving via paid traffic get amplified weighting. This makes the fast-path reputation check more aggressive for returning fraudsters. Configure via:

```json
{
  "BotDetection": {
    "Detectors": {
      "ReputationBiasContributor": {
        "Parameters": {
          "paid_traffic_bias_multiplier": 1.5
        }
      }
    }
  }
}
```

### HeuristicFeatureExtractor

Click fraud signals appear in the 50-feature heuristic model as:
- `cf:click_fraud_score` (weight 0.8) - the `clickfraud.confidence` value
- `cf:is_paid_traffic` (weight 0.3) - boolean cast to 0/1

This means the heuristic model's learned weights will over time adapt to your specific traffic patterns, reducing false positives for organic traffic while remaining sensitive to paid fraud.

---

## Dashboard

The dashboard surfaces click fraud detections in:

- **Real-time ticker**: requests with `clickfraud.pattern` show the pattern label in the signal column
- **Threats tab**: CVE probes and click fraud appear together under the threat intelligence view
- **Visitor detail**: `clickfraud.is_paid_traffic` and `clickfraud.confidence` appear in the signal trace for each detection

---

## Signals in `appsettings.json` custom filters

Access click fraud signals in custom filter policies:

```csharp
// In a custom IDetectionFilter
var fraudScore = context.GetSignal<double>(SignalKeys.ClickFraudConfidence);
var isPaid = context.GetSignal<bool>(SignalKeys.ClickFraudIsPaidTraffic);
var pattern = context.GetSignal<string>(SignalKeys.ClickFraudPattern);

if (isPaid && fraudScore > 0.7)
{
    // route to honeypot rather than block outright
}
```

Or in a YAML-driven custom policy weight:

```json
{
  "BotDetection": {
    "CustomSignalWeights": {
      "clickfraud.confidence": 0.9,
      "clickfraud.is_paid_traffic": 0.3
    }
  }
}
```

---

## Related

- [`PiiQueryStringContributor`](./response-pii-masking.md) - extracts and hashes UTM/click-ID parameters
- [`IntentContributor`](./detection-strategies.md#intent-classification) - downstream threat scoring
- [`ReputationBiasContributor`](./reputation-bias.md) - fast-path reputation amplification
- [`action-policies.md`](./action-policies.md) - configuring block, challenge, and throttle responses
