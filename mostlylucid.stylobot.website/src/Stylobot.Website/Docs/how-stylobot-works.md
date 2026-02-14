# How StyloBot Works

This page explains the runtime model behind StyloBot decisions.

## 1. Request enters middleware

For each incoming request, StyloBot creates a detection context and starts detector execution according to policy.

## 2. Detectors produce evidence

Detectors emit contributions such as:

- Signal values (for example: missing headers, suspicious UA traits, inconsistent browser fingerprint)
- Confidence deltas
- Human-readable reasons

These contributions are not final verdicts by themselves.

## 2.5 Signature computation and matching

StyloBot converts request evidence into a privacy-safe signature and matches it against previously seen signatures.
This is the core cross-temporal mechanism.

A visitor is treated as a signature, not a raw IP address, user agent string, or geolocation point.
Because identity is signature-based, matching stays useful even when bots rotate IPs, mutate UAs, or shift locations.

The signature is multi-vector and zero-PII.
Each vector contributes to fuzzy matching across time windows, giving higher behavioral resolution than single-request scoring.

## 3. Aggregation computes probability and confidence

StyloBot aggregates detector contributions into:

- `botProbability`: likelihood request is automated
- `confidence`: quality and consistency of evidence
- `riskBand`: coarse action category for policy mapping

The dashboard and response surfaces show both score and reasons so operators can audit outcomes.

## 4. Policy maps risk to action

Policies map the aggregated result to runtime behavior:

- `Allow`: pass through
- `Throttle`: slow request rate
- `Challenge`: require challenge/CAPTCHA style check
- `Block`: reject request

Policy selection can be path-based, environment-based, or deployment-mode based.

## 5. Observability and learning

Results are emitted to:

- Dashboard feed (`/_stylobot`)
- API endpoints (`/_stylobot/api/*`)
- Optional logs/telemetry sinks

Depending on configuration, learning/reputation components may adjust future weighting based on repeated patterns.

## Temporal intelligence (mostlylucid.ephemeral)

StyloBot uses `mostlylucid.ephemeral` capabilities to reason across time, not just per-request snapshots.
This enables temporal pattern memory for:

- Request cadence shifts
- Burst windows and cool-down patterns
- Repeated signature behavior over rolling windows
- Cross-request correlation for higher-confidence classification

This is the core reason StyloBot can keep low-latency decisions while improving behavioral resolution over time.

## Cross-request cluster detection

Beyond per-signature behavioral analysis, StyloBot discovers coordinated bot activity across multiple signatures:

- **Bot Product clusters**: Multiple signatures exhibiting the same behavioral fingerprint (same bot software running from different IPs)
- **Bot Network clusters**: Temporally correlated signatures with moderate similarity (coordinated campaigns, botnets)
- **Country reputation**: Per-country bot detection rates with time-decay, so a country's reputation recovers when bot traffic stops

Cluster detection uses label propagation on a similarity graph with FFT-based spectral analysis to detect shared timing patterns (C2 heartbeats, cron schedules). Only confirmed bot signatures enter clustering, ensuring zero false positives on human traffic.

## Confidence vs probability

- High probability + high confidence: strong basis for challenge/block.
- High probability + low confidence: suspicious, but often safer to throttle/challenge first.
- Low probability + high confidence: typically safe allow.

Use confidence to control enforcement aggressiveness.

## Fast path vs heavier analysis

Fast-path detectors are designed for low latency and run inline per request.
Heavier enrichment/analytics (when enabled) run without stalling baseline request flow.

## Practical tuning loop

1. Run in observe mode.
2. Collect top reasons for false positives and misses.
3. Tune thresholds and policy mapping.
4. Roll out stronger actions gradually by risk band.

## Related docs

- [Detectors In Depth](/docs/detectors-in-depth)
- [Live Demo Guide](/docs/live-demo)
- [Deploy on Server](/docs/deploy-on-server)
