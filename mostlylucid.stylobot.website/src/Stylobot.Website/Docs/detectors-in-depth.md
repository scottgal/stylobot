# Detectors In Depth

This page describes the primary detector categories StyloBot uses and how to interpret their output.

## Detector model

A detector contributes evidence, not a unilateral verdict.
Final outcomes come from combined contributions across detectors.

StyloBot evaluates a visitor as a multi-vector, zero-PII signature.
Detectors feed those vectors, and the system performs fuzzy matching across time windows.
That is why detector output should be interpreted as part of a temporal profile, not as isolated request flags.

## Core detectors

## User-Agent Detection
- Purpose: identify known bots, scanners, and suspicious automation clients from UA patterns.
- Typical signals: known bot signatures, malformed UA, impossible browser identifiers.
- Operator note: spoofing is common, so do not rely on UA alone.

## Header Detection
- Purpose: find protocol/header patterns that diverge from normal browser traffic.
- Typical signals: missing expected headers, abnormal `Accept` combinations, inconsistent sec-ch headers.
- Operator note: some API/mobile clients are naturally sparse; use route-level policy.

## IP / Network Detection
- Purpose: classify network origin and risk context.
- Typical signals: data center/cloud ranges, known proxy/VPN traits, reputation lookups.
- Operator note: cloud IP does not automatically mean malicious traffic.

## Behavioral Detection
- Purpose: detect suspicious request cadence and navigation behavior over time.
- Typical signals: bursty request rates, repetitive endpoint sweeps, low-latency robotic timing.
- Operator note: high-traffic legitimate clients can look bot-like without proper allowlisting and policy scopes.

## Advanced Behavioral Detection
- Purpose: identify higher-order behavior anomalies not captured by basic rate checks.
- Typical signals: traversal strategies, sequence anomalies, sustained suspicious pattern drift.
- Operator note: best tuned with production-like traffic windows.

## Cache Behavior Detection
- Purpose: detect requests that intentionally bypass or abuse cache semantics.
- Typical signals: cache-busting patterns, repeated anti-cache query churn.
- Operator note: some build systems and CDNs generate noisy cache patterns.

## Security Tool Detection
- Purpose: identify known offensive/security scanning tool fingerprints.
- Typical signals: nikto/sqlmap-style signatures, scanner UA/header motifs.
- Operator note: useful for separating benign user traffic from probing.

## Client-Side Fingerprinting
- Purpose: increase confidence for browser-origin traffic with signed browser fingerprints.
- Typical signals: webdriver/headless traits, browser capability inconsistencies, token validity.
- Operator note: keep this optional and policy-driven for privacy and compatibility requirements.

## Version Age Detection
- Purpose: detect outdated or impossible browser/OS combinations often used by automation stacks.
- Typical signals: stale major versions, impossible OS/browser pairings.
- Operator note: long-lived enterprise environments may legitimately run older stacks.

## Inconsistency Detection
- Purpose: correlate cross-signal contradictions that are hard to fake consistently.
- Typical signals: UA claims modern browser but headers/fingerprint mismatch that claim.
- Operator note: high-value signal for escalation because it catches spoofing gaps.

## Reputation / Fast Path Match
- Purpose: quickly classify known repeat patterns with low latency.
- Typical signals: known signature state, prior confidence-backed outcomes.
- Operator note: monitor drift and decay to avoid permanently stale classifications.

## Optional AI / Heuristic Classification
- Purpose: add classification support for edge cases and evolving patterns.
- Typical signals: feature-vector interpretation over aggregated request evidence.
- Operator note: keep explainability and fallback behavior explicit in policy.

## Cross-request detectors

## Cluster Detection
- Purpose: discover coordinated bot networks and bot product families across multiple signatures using label propagation and FFT spectral analysis.
- Typical signals: cluster membership type (BotProduct or BotNetwork), cluster label (e.g., "Rapid-Scraper", "Burst-Campaign"), spectral entropy, harmonic ratio, community affinity score.
- How it works: A background service periodically clusters confirmed bot signatures (probability >= 0.5) using a 12-dimensional feature vector with weighted similarity. Label propagation discovers groups; FFT cross-correlation detects shared C2 timing patterns. Only bot traffic is clustered, so there are zero false positives on human traffic.
- Operator note: cluster results update every 60 seconds or when 20 new bots are detected. Tune `SimilarityThreshold` (default: 0.7) and `MinClusterSize` (default: 3) for your traffic profile.

## Country Reputation
- Purpose: track per-country bot detection rates with time decay so countries recover reputation when bot traffic stops.
- Typical signals: `geo.country_bot_rate` (0-1), `geo.country_bot_rank` (ordinal position).
- How it works: Uses exponential moving average with a configurable time constant (default: 168 hours / 1 week). Countries need a minimum sample size (default: 10) before rates are reported, preventing noisy signals from low-traffic origins.
- Operator note: country reputation is a supporting signal, not a blocking signal. It increases resolution on borderline calls.

## What to do with detector output

Use detector reasons to answer:

1. Which signal families are driving action?
2. Are we seeing consistent evidence or one noisy detector?
3. Should this route use a stricter or looser policy?

## Tuning checklist by detector

- Confirm detector is meaningful on the target route family.
- Verify false-positive profile from live traffic samples.
- Adjust action mapping before detector weighting when possible.
- Prefer progressive escalation (`Allow` -> `Throttle` -> `Challenge` -> `Block`).

## Deep technical references (GitHub)

- [User-Agent Detection](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/user-agent-detection.md)
- [Header Detection](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/header-detection.md)
- [IP Detection](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/ip-detection.md)
- [Behavioral Analysis](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/behavioral-analysis.md)
- [Advanced Behavioral Detection](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/advanced-behavioral-detection.md)
- [Cache Behavior Detection](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/cache-behavior-detection.md)
- [Security Tools Detection](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/security-tools-detection.md)
- [Client-Side Fingerprinting](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/client-side-fingerprinting.md)
- [Version Age Detection](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/version-age-detection.md)
- [AI Detection](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/ai-detection.md)
- [Cluster Detection & Country Reputation](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/cluster-detection.md)
- [Training Data API](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/training-data-api.md)
- [Action Policies](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/action-policies.md)
