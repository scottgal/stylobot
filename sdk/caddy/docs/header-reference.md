# Header reference

The `caddy-stylobot` middleware injects nine headers onto the upstream (proxied) request before it reaches your application. These headers are not added to the HTTP response seen by the browser.

This document describes each header in detail, including its type, example value, the conditions under which it appears, and how to interpret its values.

---

## X-StyloBot-IsBot

**Type:** boolean string (`true` or `false`)
**Always present:** yes

```
X-StyloBot-IsBot: true
```

`true` when the sidecar's combined detector score exceeds the configured `BotThreshold` (default 0.7). `false` for all other requests, including ones that are suspicious but not yet over the threshold.

Do not use this header alone to make blocking decisions in your app. Prefer `X-StyloBot-Action`, which expresses the sidecar's policy decision rather than the raw classification.

---

## X-StyloBot-Probability

**Type:** float string, 4 decimal places, range 0.0000-1.0000
**Always present:** yes

```
X-StyloBot-Probability: 0.9312
```

The aggregated bot probability from all detectors that ran on this request. This is the value compared against `BotThreshold` to set `IsBot`.

Interpreting the value:

- `0.0000-0.2999`: Very likely human. Standard traffic.
- `0.3000-0.5999`: Ambiguous. Possibly a bot, possibly an unusual browser or corporate proxy.
- `0.6000-0.6999`: Suspicious. Below the default block threshold but worth logging.
- `0.7000-0.8999`: Likely bot. Above the default threshold; action will typically be `Block` or `Throttle`.
- `0.9000-1.0000`: Confirmed bot. High signal from multiple independent detectors.

The probability alone does not determine the action. A high probability with low confidence (few detectors fired) may still result in `Throttle` rather than `Block`.

---

## X-StyloBot-Confidence

**Type:** float string, 4 decimal places, range 0.0000-1.0000
**Always present:** yes

```
X-StyloBot-Confidence: 0.8750
```

How many detectors agreed on the classification, normalized by their weights. A high probability with low confidence means one strong signal fired but others were absent or contradictory. A high probability with high confidence means many independent detectors agree.

Use this alongside `X-StyloBot-Probability` when deciding whether to apply a hard block or a softer response:

- High probability + high confidence: block with confidence.
- High probability + low confidence: consider a challenge instead of a block.
- Low probability + any confidence: allow.

---

## X-StyloBot-BotType

**Type:** string
**Present when:** the request is classified as a bot (`IsBot: true`); empty string for human traffic

```
X-StyloBot-BotType: Scraper
```

The category of bot activity detected. Common values:

| Value | Description |
|---|---|
| `Scraper` | Content scraping. Follows links, fetches many pages. |
| `Scanner` | Port or vulnerability scanning. Probes known paths. |
| `CveProbe` | Actively testing for specific CVE exploits. |
| `AiScraper` | AI training data collection (GPTBot, CCBot, etc.). |
| `SecurityTool` | Commercial security tools (Shodan, Censys, etc.). |
| `Headless` | Headless browser automation (Puppeteer, Playwright without stealth). |
| `Crawler` | Generic web crawler with a declared crawler User-Agent. |
| `DataHarvester` | Targeted extraction of structured data (prices, contacts, etc.). |

This field is empty for human traffic and for bot traffic where the type cannot be determined from the signals available.

---

## X-StyloBot-BotName

**Type:** string
**Present when:** the sidecar has enough information to identify a specific bot instance; empty for human traffic and unidentified bots

```
X-StyloBot-BotName: Shadowreaper-7
```

A deterministic name assigned to this bot's fingerprint. The same bot always receives the same name across requests, sessions, and even after IP rotation, because the name is derived from the stable behavioral fingerprint rather than the IP address.

This makes it easy to track a specific bot actor across your logs:

```bash
grep "Shadowreaper-7" /var/log/app.log
```

The name format is `<Adjective><Noun>-<number>`. The number is a disambiguation suffix for fingerprints that map to the same name hash. This field is empty for human traffic and for bots that have not yet accumulated enough signal to generate a stable fingerprint.

---

## X-StyloBot-RiskBand

**Type:** string enum
**Always present:** yes

```
X-StyloBot-RiskBand: High
```

An ordinal risk classification that combines bot probability, confidence, and threat score into a single label. Values in ascending order from safest to most dangerous:

| Value | Meaning |
|---|---|
| `Unknown` | Insufficient signal. First request from a new fingerprint, or detectors did not run. |
| `VeryLow` | Strong human indicators. Normal browser with consistent headers, TLS fingerprint, and behavioral patterns. |
| `Low` | Probably human with minor anomalies. Unusual browser or proxy configuration but no bot signals. |
| `Elevated` | Suspicious. Some bot signals present but below the classification threshold. Monitor. |
| `Medium` | Moderate bot confidence. Multiple weak signals. Consider challenging. |
| `High` | High bot confidence. Clear bot characteristics. Block or challenge. |
| `VeryHigh` | Confirmed bot with high confidence. Strong signals from multiple independent detectors. Block. |
| `Verified` | Confirmed real browser. Passed browser attestation (TLS fingerprint matches declared UA, behavioral patterns consistent with real user). This is a positive signal, not a threat. |

The `Verified` band is the only value that represents a trust upgrade rather than a risk level. It appears when the sidecar has strong evidence that the request is from a genuine user browser.

---

## X-StyloBot-Action

**Type:** string enum
**Always present:** yes

```
X-StyloBot-Action: Block
```

The sidecar's recommended response policy for this request. Values:

| Value | Caddy behavior | Recommended app behavior |
|---|---|---|
| `Allow` | Forward to app | Serve normally |
| `Throttle` | Forward to app | Add an artificial delay (1-5 seconds) |
| `Challenge` | Forward to app | Serve a CAPTCHA or proof-of-work page |
| `Block` | Returns HTTP `on_block` status (default 403) if `on_block` is non-zero; otherwise forwards | Return 403 if `on_block` is 0 in Caddy |

When `on_block` is non-zero in your Caddyfile, Caddy enforces the `Block` action automatically and the request never reaches your app. When `on_block` is `0` (observe-only mode), your app receives all requests and is responsible for enforcing all four actions.

---

## X-StyloBot-ThreatScore

**Type:** float string, 4 decimal places, range 0.0000-1.0000
**Always present:** yes

```
X-StyloBot-ThreatScore: 0.7410
```

A threat intelligence score distinct from the bot probability. While bot probability measures whether the request looks like a bot, the threat score measures whether the request looks malicious:

- CVE probe traffic targeting known vulnerability paths
- Requests from IP ranges associated with botnets or APT infrastructure
- SQL injection, path traversal, or command injection in query parameters
- Signals correlated with known attack campaigns

A request can have a low bot probability but a high threat score (for example, a human-operated penetration tester). It can also have a high bot probability but a low threat score (for example, a benign SEO crawler).

Values above 0.5 are worth logging. Values above 0.8 indicate high-confidence threat activity.

---

## X-StyloBot-ThreatBand

**Type:** string enum
**Always present:** yes

```
X-StyloBot-ThreatBand: Elevated
```

An ordinal classification of the threat score into five levels:

| Value | Threat score range | Meaning |
|---|---|---|
| `None` | 0.0000-0.1999 | No threat signals detected. |
| `Low` | 0.2000-0.3999 | Weak threat signals. Reconnaissance-level activity or automated scanning with no specific exploit attempts. |
| `Elevated` | 0.4000-0.5999 | Moderate threat. Known bad IP range or suspicious path patterns. Investigate. |
| `High` | 0.6000-0.7999 | Clear threat signals. CVE probes, injection attempts, or correlation with known attack campaigns. |
| `Critical` | 0.8000-1.0000 | Active attack. High-confidence malicious activity. Block immediately and alert. |

---

## Processing time

The `DetectResponse` protobuf message includes a `processing_time_ms` field, but this is not currently injected as a header. It appears in the sidecar's structured logs and in the REST API response when using `/api/v1/detect` directly.

What constitutes fast detection:

- Under 1ms: fast path only (most requests). UserAgent, Header, IP, and behavioral detectors all complete in under 1ms each on a warmed process.
- 1-5ms: additional detectors ran (session analysis, entity resolution, content sequence).
- 5-20ms: slow path included (ProjectHoneypot DNS lookup) or LLM escalation is configured and triggered.
- Over 20ms: investigate. Check sidecar logs for which detectors ran and whether SQLite is blocking.

The Caddy middleware has a configurable `timeout` (default 50ms). If the sidecar does not respond within that window, the request forwards unchanged (fail-open). The 50ms default gives the sidecar approximately 50x its typical response time as slack for system noise and garbage collection pauses.
