# Detector Weights Audit (April 2026)

Snapshot of every detector's current weight / confidence defaults, compiled from the 49
YAML manifests under `Orchestration/Manifests/detectors/`. This is the baseline for the
weighting refinement pass - **no changes proposed yet**, this is just the map.

## Full table

| Detector | Priority | base / bot / human | neutral / bot / human conf | Notable thresholds |
|---|---:|---|---|---|
| ai | 100 | 0.5 / 0.8 / 0.6 | 0.0 / 0.2 / −0.1 | high_risk 0.8, escalation 0.5 |
| aiscraper | 9 | 1.0 / 2.0 / 1.0 | 0.0 / 0.8 / 0.0 | known_ai_bot 0.95, web_bot_auth 0.95 |
| behavioral | 20 | 1.0 / 1.5 / 1.0 | 0.0 / 0.4 / −0.30 | rate_limit 0.5, rapid_request 0.3 |
| cluster | 850 | 1.0 / 1.5 / 1.0 | 0.0 / 0.3 / −0.1 | product_delta 0.4, network_delta 0.25 |
| geochange | 16 | - / 1.5 / 1.0 | base 0.5 | rapid_drift_conf 0.8, rapid_drift_weight 1.8 |
| haxxor | 7 | 1.0 / 2.0 / 0.0 | 0.0 / 0.85 / 0.0 | sqli 0.95, xss 0.90, max_compound 0.99 |
| header | 10 | 1.0 / 1.2 / 1.2 | 0.0 / 0.2 / −0.15 | missing_header 0.1, order_anomaly 0.15 |
| heuristic | 50 | 1.0 / 1.8 / 1.2 | 0.5 / 0.7 / 0.3 | escalation 0.5, heuristic_weight 2.0 |
| http2 | 13 | 1.0 / 1.6 / 1.4 | 0.0 / 0.35 / −0.25 | bot_fp 0.7, browser_fp −0.2 |
| http3 | 14 | 1.0 / 1.6 / 1.4 | 0.0 / 0.35 / −0.25 | quic_bot 0.6, draft_version_penalty 0.3 |
| inconsistency | 50 | 1.0 / 1.5 / 1.2 | 0.0 / 0.4 / −0.20 | datacenter_browser 0.7, missing_language 0.5 |
| ip | 12 | 1.0 / 1.2 / 1.0 | 0.0 / 0.6 / −0.25 | datacenter 0.6, isp_human 0.15 |
| llm | 40 | 2.0 / 2.5 / 2.0 | 0.5 / 0.8 / 0.2 | high 0.85, low 0.15 |
| securitytool | 8 | 1.0 / 2.0 / 0.0 | 0.0 / 0.95 / 0.0 | strong_signal 0.99, high 0.9 |
| stream-abuse | 35 | 1.0 / 1.6 / 1.0 | 0.0 / 0.5 / −0.1 | handshake_storm 0.65, endpoint_mixing 0.6 |
| timescale | 15 | 0.5 / 1.0 / 0.8 | 0.0 / 0.7 / −0.3 | high_bot_ratio 0.8, low_bot_ratio 0.2 |
| tls | 11 | 1.0 / 1.8 / 1.5 | 0.0 / 0.4 / −0.30 | known_bot_fp 0.85, known_browser_fp −0.15 |
| useragent | 10 | 1.0 / 1.5 / 1.3 | 0.0 / 0.3 / −0.25 | strong_signal 0.85, missing_ua 0.8 |
| versionage | 25 | 1.0 / 1.2 / 1.0 | 0.0 / 0.25 / −0.15 | outdated 0.2, very_outdated 0.4 |
| advancedbehavioral | 25 | - / 1.3 / 1.0 | 0.0 / 0.3 / −0.20 | multi-session pattern analysis |
| behavioralwaveform | 3 | - / 1.5 / 1.0 | 0.0 / 0.5 / −0.15 | spectral entropy, harmonic ratio, dominant frequency |
| cachebehavior | 15 | - / 1.2 / 1.0 | 0.0 / 0.25 / −0.15 | Cache-Control / ETag / conditional request consistency |
| challengeverification | 25 | - / 1.2 / 0.0 | 0.0 / 0.15 / −0.35 | PoW/CAPTCHA result verification |
| claimedidentity | 35 | - / 1.5 / 1.0 | 0.0 / 0.35 / −0.15 | centroid consistency 0.55 threshold |
| clickfraud | 38 | - / 1.5 / 0.0 | 0.0 / 0.30 / 0.0 | datacenter_paid 0.50, headless_paid 0.40 |
| clientside | 18 | - / 1.8 / 1.0 | 0.0 / 0.5 / −0.20 | canvas/WebGL/AudioContext fingerprint |
| contentsequence | 4 | - / 0.5 / 1.0 | 0.0 / 0.25 / −0.10 | machine-speed <20ms, phase divergence |
| cookiebehavior | 20 | - / 1.2 / 0.0 | 0.0 / 0.25 / 0.0 | missing/stale/replayed cookie lifecycle |
| cvefingerprint | 55 | - / 1.5 / 0.0 | 0.0 / 0.30 / −0.10 | YAML-driven CVE exploit fingerprint DB |
| cveprobe | 11 | - / 2.0 / 0.0 | 0.0 / 0.90 / 0.0 | WordPress/Log4j/Spring4Shell probe patterns |
| fastpath | 3 | 0.0 / 0.0 / 0.0 | 0.0 / 1.0 / 0.0 | fast_abort_weight 3.0, allow_max 0.1, abort_min 0.9 |
| fingerprintapproval | 24 | - / 2.0 / 0.0 | 0.0 / 0.30 / −0.40 | enterprise allowlist overrides |
| headercorrelation | 21 | - / 1.2 / 1.0 | 0.0 / 0.5 / −0.10 | Sec-Fetch-* + Accept + UA family correlation |
| heuristiclate | 100 | - / 2.5 / 1.5 | 0.0 / 0.5 / −0.30 | final pass consuming all accumulated evidence |
| multilayercorrelation | 4 | - / 1.5 / 1.0 | 0.0 / 0.5 / −0.20 | TLS + UA + H2 + TCP fingerprint correlation |
| periodicity | 25 | - / 1.4 / 1.0 | 0.0 / 0.4 / −0.10 | rotation cadence, temporal autocorrelation |
| piiquerystring | 8 | - / 2.0 / 0.0 | 0.0 / 0.70 / 0.0 | PII harvesting patterns in query strings |
| projecthoneypot | 15 | - / 1.5 / 0.0 | 0.0 / 0.50 / 0.0 | DNS-based IP reputation (Project Honey Pot) |
| reactivepattern | 32 | - / 1.5 / 1.0 | 0.0 / 0.5 / −0.15 | retry-after compliance, backoff pattern |
| resourcewaterfall | 22 | - / 1.2 / 0.0 | 0.0 / 0.5 / 0.0 | CSS/JS/font load sequence consistency |
| signature | 1 | 0.0 / 0.0 / 0.0 | 0.0 / 0.0 / 0.0 | identity-only; computes PrimarySignature |
| similarity | 60 | - / 1.4 / 1.0 | 0.0 / 0.3 / −0.20 | HNSW cosine similarity to known-bot session vectors |
| tcpip | 11 | - / 1.3 / 1.0 | 0.0 / 0.25 / −0.10 | p0f-style TCP/IP OS fingerprinting |
| reputation | 45 | 1.0 / 1.5 / 1.2 | 0.0 / 0.3 / −0.35 | confirmed_bad_weight 2.5, min_support 3.0 |
| sessionvector | 30 | 0.0 / 1.5 / 1.3 | 0.0 / 0.5 / −0.2 | velocity_anomaly 0.6, dissimilarity 0.3 |
| verifiedbot | 4 | 1.0 / 2.0 / 0.0 | 0.0 / 0.85 / 0.0 | spoofed_ua 0.85, honest_bot 0.3 |
| intent | 40 | 1.0 / 1.0 / 1.0 | 0.0 / 0.0 / 0.0 | similarity 0.75, ambiguous_low 0.3, ambiguous_high 0.7 |
| transport-protocol | 5 | 1.0 / 1.4 / 1.0 | 0.0 / 0.35 / −0.1 | missing_ws 0.6, invalid_ws_ver 0.5 |
| accounttakeover | 25 | 1.0 / 2.0 / 0.5 | 0.0 / 0.85 / −0.1 | stuffing 0.90, brute_force 0.90, drift 0.8 |
| responsebehavior | 12 | 1.0 / 1.5 / 1.3 | 0.0 / 0.5 / −0.15 | honeypot 0.9, auth_severe 0.85, exclusive_404 0.75 |

## High-impact detectors

**`bot_signal ≥ 2.0` (6 detectors):** aiscraper, haxxor, securitytool, accounttakeover, verifiedbot, llm (2.5). These dominate aggregate confidence when they fire - correctly so for definitive signals (known attack payload, confirmed security-tool UA, etc.), but they also *compound* if two fire on the same request.

**`bot_detected ≥ 0.85` (individually authoritative, 7 detectors):** aiscraper (0.8), haxxor (0.85), securitytool (0.95), tls known_bot_fp (0.85), accounttakeover (0.85), verifiedbot (0.85), responsebehavior honeypot (0.9). A single hit on any of these already exceeds the default block threshold of 0.7 without any other contribution.

**No detector has `human_signal > 2.0`.** All human signals top out at 1.5. This is deliberate - we're happy to err on the side of false-negative bots over false-positive humans, and the pipeline trusts bot evidence more than human evidence.

## Double-counting risks (same signal, multiple detectors)

1. **Datacenter IP + browser UA → bot.** Both `ip` (0.6) and `inconsistency` (datacenter_browser 0.7) score this. A request from AWS with Chrome UA fires both, adding ~1.3 combined confidence. Almost certainly intentional layering, but worth checking whether the `inconsistency` check should gate on `ip.is_datacenter` or run independently.
2. **Client fingerprinting trio.** `tls` (JA3/JA4), `http2` (SETTINGS), `http3` (QUIC transport params) all emit "this fingerprint doesn't match a real browser" at 0.35–0.85. A headless Chromium hits all three. May want a one-of-three cap via cross-detector aggregation.
3. **Behavioral quartet.** `behavioral` (rate/pattern), `stream-abuse` (streaming protocols), `sessionvector` (Markov chain), `responsebehavior` (historical) all look at request sequences from different angles. Each firing at 0.3–0.5 can sum to >1.0 on any active bot session. This is *probably* correct aggregation - different signals, same story - but worth verifying with labeled data.
4. **UA analysis pair.** `useragent` (pattern match) and `verifiedbot` (UA spoofing) both key off the UA string. They typically don't fire together (verifiedbot only acts on known-bot UAs), but worth a sanity check.
5. **Reputation trio.** `fastpath` (instant cache), `reputation` (learned bias), `timescale` (90-day history) all pull from reputation state. Different time horizons, same underlying pattern - the fan-out was intentional to let fresh signals override stale ones, but the weight sum (1.5 + 1.5 + 1.0 = 4.0) is the largest cumulative bot signal in the pipeline.

## Priority distribution

| Band | Count | Detectors |
|---|---:|---|
| Identity (1) | 1 | signature |
| Fast-path (3–9) | 9 | fastpath, behavioralwaveform, contentsequence, multilayercorrelation, verifiedbot, transport-protocol, haxxor, piiquerystring, securitytool |
| Low (9–15) | 12 | aiscraper, useragent, header, cveprobe, tls, tcpip, ip, responsebehavior, http2, http3, cachebehavior, projecthoneypot, timescale |
| Mid (16–30) | 16 | geochange, behavioral, cookiebehavior, headercorrelation, clientside, resourcewaterfall, fingerprintapproval, accounttakeover, challengeverification, periodicity, versionage, advancedbehavioral, sessionvector, reactivepattern, claimedidentity, stream-abuse |
| Late (35–60) | 9 | clickfraud, intent, reputationbias, heuristic, inconsistency, llm, cvefingerprint, similarity, ai |
| Final (100) | 1 | heuristiclate |
| Coordinator (850) | 1 | cluster (Wave 2, post-signature aggregation) |

Shape is correct: cheap detectors fire first, expensive ML/LLM/coordinator-driven ones fire late. `heuristiclate` consumes all accumulated evidence before the coordinator wave. `cluster` is the intentional outlier - it runs once per 30s cycle across all active signatures, not per-request.

## Next steps for the weighting pass

1. **Labeled data.** Dashboard's "top signatures" view needs a three-button annotator (bot / human / benign-bot-like-Googlebot). A few days of live labeling gives us hundreds of ground-truth records.
2. **Per-detector precision/recall on labeled set.** Run each detector in isolation against the labeled corpus and compute F1 for the current weight. This surfaces detectors that are pulling their weight vs. noise generators.
3. **Cross-detector interaction matrix.** 49 × 49 matrix of how often pairs fire together on bots vs humans. Pairs with high joint-bot rate + low joint-human rate = the signals doing real work. Pairs with high joint-human rate = sources of false positives.
4. **Targeted weight adjustments.** Probably a handful of 20% nudges, not a wholesale rebalance. Ship each behind shadow mode for a week before promoting.
5. **Per-API-key overrides.** Commercial portal UX for customers to locally adjust weights that don't fit their traffic (e.g., a SaaS with heavy API usage wants `behavioral` weights softened on `/api/*`).

Separate concern, not a weighting issue: **BDF temporal scenarios.** We need tests that replay synthetic sessions with evolving behavior - human→bot transition, bot→human rehabilitation, misclassification decay - so we can assert the reputation state moves in the right direction over time, not just per-request.