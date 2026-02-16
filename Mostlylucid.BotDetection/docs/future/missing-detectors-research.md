# Missing Detectors: Research Backlog and Rollout Plan

Date: 2026-02-16

## Purpose

Identify detector capabilities we are likely missing, based on:

1. Current StyloBot detector inventory and default policy wiring.
2. External anti-bot standards and primary-source research.

This is a practical backlog document, not a theoretical wishlist.

## Current State Snapshot

### What is already implemented in code but not active in default policy

The default `DetectionPolicy.Default` fast-path list currently includes:

- `FastPathReputation`
- `TimescaleReputation`
- `UserAgent`
- `Header`
- `Ip`
- `SecurityTool`
- `Behavioral`
- `ClientSide`
- `CacheBehavior`
- `Inconsistency`
- `VersionAge`
- `Heuristic`
- `HeuristicLate`
- `ReputationBias`
- `Geo`
- `GeoClient`

Source: `Mostlylucid.BotDetection/Policies/DetectionPolicy.cs`.

Registered contributors that are not in that default list include:

- `AdvancedBehavioral`
- `AI`
- `AiScraper`
- `BehavioralWaveform`
- `ClusterContributor`
- `Http2Fingerprint`
- `Http3Fingerprint`
- `Llm`
- `MultiLayerCorrelation`
- `ProjectHoneypot`
- `ResponseBehavior`
- `Similarity`
- `TcpIpFingerprint`
- `TlsFingerprint`

Source: `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` and contributor classes under `Mostlylucid.BotDetection/Orchestration/ContributingDetectors`.

### Configuration gap

Several contributors exist without corresponding detector manifests in `Orchestration/Manifests/detectors`, which makes configuration parity and tuning harder.

Examples:

- `AdvancedBehavioral`
- `BehavioralWaveform`
- `CacheBehavior`
- `ClientSide`
- `HeuristicLate`
- `MultiLayerCorrelation`
- `ProjectHoneypot`
- `ResponseBehavior`
- `Similarity`
- `TcpIpFingerprint`

## Priority Backlog: Missing Detector Capabilities

## P0: Add now (highest practical ROI)

### 1) Verified Bot Authenticity Detector (cryptographic bot identity)

Problem:
- UA-based "good bot" detection is spoofable.

Detector idea:
- Accept and verify cryptographic bot assertions (Web Bot Auth / HTTP Message Signatures and, where available, mTLS identity).
- Emit signed-identity confidence signals separate from UA reputation.

Signals:
- `botauth.signature.valid`
- `botauth.identity.provider`
- `botauth.mtls.present`
- `botauth.verification_level`

Why now:
- Directly improves false-positive/false-negative handling for AI crawlers and search bots.

### 2) Reverse-DNS + ASN Verified Crawler Detector

Problem:
- Claimed crawler UAs are often fake.

Detector idea:
- Perform strict reverse DNS and forward-confirmed DNS verification for claimed search crawlers.
- Add ASN consistency checks for verified bot domains.

Signals:
- `crawler.claimed`
- `crawler.rdns.validated`
- `crawler.forward_dns_match`
- `crawler.asn_expected`

Why now:
- Low complexity, very high trust-value signal.

### 3) Attestation Token Detector (Privacy Pass / PAT)

Problem:
- We currently infer humanness from behavior/fingerprints but do not consume modern attestation-token proofs.

Detector idea:
- Validate Privacy Pass / PAT token presence and validity.
- Use as a strong positive human signal and challenge bypass signal.

Signals:
- `attestation.token.present`
- `attestation.token.valid`
- `attestation.token.issuer`

Why now:
- Reduces friction for legitimate traffic and gives stronger anti-automation evidence than heuristic-only paths.

## P1: Next (strong incremental detection quality)

### 4) Client Hints Consistency Detector

Problem:
- Modern clients expose structured hints (`Sec-CH-UA*`), but spoof stacks often drift between UA, hints, and protocol fingerprints.

Detector idea:
- Cross-check UA claims, Client Hints, HTTP/2 + TLS fingerprints, and platform hints.
- Score impossible or unlikely combinations.

Signals:
- `ch.ua_consistent`
- `ch.platform_consistent`
- `ch.mobile_consistent`
- `ch.anomaly_count`

### 5) Interaction-Triggered Fingerprint Stability Detector

Problem:
- One-shot static fingerprint checks are easier to bypass.

Detector idea:
- Collect lightweight active measurements during real user interactions and compare temporal stability.
- Bots that replay synthetic profiles tend to fail multi-step consistency.

Signals:
- `fp.interaction.phase_count`
- `fp.temporal_stability_score`
- `fp.interaction_anomaly`

### 6) Credential Stuffing Campaign Detector (route-aware)

Problem:
- Generic bot detection misses targeted account-abuse patterns on auth endpoints.

Detector idea:
- Add route-scoped contributor for login/identity endpoints:
  - failure burst shape,
  - username/email spray patterns,
  - distributed low-and-slow attempts.

Signals:
- `auth.fail_rate_window`
- `auth.distinct_account_attempts`
- `auth.spray_pattern_detected`

## P2: Later (valuable but needs infra/tuning)

### 7) Autonomous System Reputation Drift Detector

Problem:
- Static datacenter lists are coarse.

Detector idea:
- Track time-decayed behavior quality by ASN and subnet clusters to detect new abusive infrastructure quickly.

Signals:
- `asn.bot_rate_ema`
- `asn.risk_trend`
- `asn.sample_count`

### 8) Signed Automation Allowlist Detector (enterprise integrations)

Problem:
- Legitimate automation (partner APIs, RPA, internal jobs) gets mixed with hostile automation.

Detector idea:
- Require tenant-scoped signed claims or mTLS identities for approved non-human clients.
- Distinguish "authorized automation" from unknown bots.

Signals:
- `automation.allowlisted`
- `automation.identity_strength`
- `automation.policy_scope_match`

## Recommended Rollout Sequence

1. Activate existing dormant contributors in a non-blocking policy:
   - `ProjectHoneypot`, `ResponseBehavior`, `Http2Fingerprint`, `Http3Fingerprint`, `TlsFingerprint`, `TcpIpFingerprint`, `Similarity`, `BehavioralWaveform`, `MultiLayerCorrelation`.
2. Add P0 detectors with log-only actions first.
3. Add manifests for currently unmanifested contributors to unify tuning and telemetry.
4. Promote high-precision signals into strict/auth policies after false-positive review.

## Success Criteria

- Reduced spoofed-good-bot acceptance rate.
- Lower challenge rate for verified legitimate automation/humans.
- Improved precision on login-route abuse.
- Higher confidence scores from cross-layer consistency (not single-signal spikes).

## External References (Primary Sources)

- Cloudflare JA4 signals and inter-request intelligence:
  - https://blog.cloudflare.com/ja4-signals/
- Cloudflare bot verification and Web Bot Auth:
  - https://developers.cloudflare.com/bots/concepts/bot/verified-bots/
  - https://developers.cloudflare.com/bots/additional-configurations/bot-verification/machine-validation/
- IETF Web Bot Auth architecture draft:
  - https://www.ietf.org/archive/id/draft-ietf-httpbis-bot-auth-architecture-04.html
- HTTP Message Signatures (for signed request identity patterns):
  - https://www.rfc-editor.org/rfc/rfc9421
- Privacy Pass architecture:
  - https://www.rfc-editor.org/rfc/rfc9578.html
- Google crawler verification guidance (reverse DNS + IP validation):
  - https://developers.google.com/search/docs/crawling-indexing/verifying-googlebot
- HTTP Client Hints:
  - https://www.rfc-editor.org/rfc/rfc8942
- Interaction-driven browser fingerprinting research:
  - https://research.google/pubs/browser-fingerprinting-via-active-measurements-in-real-user-interactions/
- OWASP automated threat categories (credential stuffing reference):
  - https://owasp.org/www-project-automated-threats-to-web-applications/
