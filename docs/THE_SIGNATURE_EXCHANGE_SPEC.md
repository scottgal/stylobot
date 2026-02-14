# The Signature Exchange

Status: Draft  
Owner: Core Platform  
Target Release: Future

## 1. Summary

The Signature Exchange is a federated sharing system for bot behavioral signatures. It lets StyloBot nodes publish and consume signed, privacy-safe intelligence about abusive traffic patterns.

Goal: improve detection speed and confidence across independent deployments without sharing raw user data or personal identifiers.

## 2. Why This Exists

StyloBot already identifies requests using multi-vector, zero-PII signatures.  
Each deployment learns valuable behavior over time, but that knowledge is local.

The Signature Exchange extends this model:

1. A known abusive behavioral signature seen by one node can be recognized earlier by others.
2. Detection quality improves against evasive traffic that rotates IPs, user agents, and locations.
3. Teams benefit from network intelligence while keeping control of policy decisions.

## 3. Non-Goals

1. No central storage of raw request payloads.
2. No exchange of IP addresses, cookies, emails, or other PII.
3. No automatic hard blocking solely from third-party data.
4. No vendor lock-in transport. Protocol is open and documented.

## 4. Core Concepts

### 4.1 Signature

A signature represents behavioral identity over time, not personal identity.  
It is derived from multiple vectors and resilient to low-effort evasion.

### 4.2 Signature Record

A signed message describing observed behavior for a signature window:

1. Risk estimate
2. Confidence
3. Detector contributions
4. Behavioral patterns across time
5. Optional cluster context

### 4.3 Exchange Node

A StyloBot deployment that can:

1. Produce exchange records from local observations
2. Validate incoming records
3. Merge incoming intelligence into local reputation models

## 5. Product Requirements

1. Opt-in by default and disabled unless explicitly configured.
2. Separate controls for publish and subscribe.
3. Per-peer trust settings and rate limits.
4. Admin visibility into imported and exported intelligence.
5. Replay protection, signature validation, and revocation support.
6. Fully auditable data lineage for every imported decision influence.

## 6. Privacy and Security Requirements

1. Shared payload must be zero-PII.
2. Records must be signed with peer keys (Ed25519 recommended).
3. Transport must use TLS 1.2+.
4. mTLS support for private exchanges.
5. Record TTL and retention limits must be configurable.
6. Imported records must never bypass local policy engine.
7. Abuse protections:
   1. Peer throttling
   2. Reputation weighting
   3. Quarantine mode for suspicious peers

## 7. Data Model (v1)

```json
{
  "version": "1.0",
  "recordId": "uuid",
  "source": {
    "nodeId": "stylo-node-123",
    "pubKeyId": "key-2026-01"
  },
  "signature": {
    "primary": "sig_abc123",
    "algorithm": "hmac-sha256",
    "vectorSchema": "v3"
  },
  "observationWindow": {
    "startUtc": "2026-02-14T10:00:00Z",
    "endUtc": "2026-02-14T10:05:00Z"
  },
  "classification": {
    "isBot": true,
    "riskBand": "High",
    "botProbability": 0.92,
    "confidence": 0.84
  },
  "behavior": {
    "velocityScore": 0.91,
    "sequenceAnomalyScore": 0.77,
    "requestPatternHash": "rp_789",
    "detectorSummary": [
      { "detector": "Behavioral", "delta": 0.45 },
      { "detector": "Header", "delta": 0.20 },
      { "detector": "ClientSide", "delta": 0.18 }
    ]
  },
  "policyHint": {
    "recommendedAction": "Challenge",
    "reasonCodes": ["BEHAVIORAL_SPIKE", "HEADER_MISMATCH"]
  },
  "ttlSeconds": 86400,
  "issuedAtUtc": "2026-02-14T10:05:05Z",
  "signatureEnvelope": {
    "alg": "Ed25519",
    "kid": "key-2026-01",
    "sig": "base64..."
  }
}
```

## 8. Protocol (v1)

### 8.1 Discovery

Peers are statically configured in v1:

1. URL
2. Node ID
3. Public key
4. Optional mTLS cert requirements

### 8.2 Publish API

`POST /exchange/v1/signatures`

1. Accept batch payloads.
2. Idempotency via `recordId`.
3. Response includes accepted, rejected, and reasons.

### 8.3 Subscribe API

`GET /exchange/v1/signatures?since={cursor}`

1. Pull model in v1.
2. Cursor-based pagination.
3. Backfill window capped by peer policy.

### 8.4 Health and Metadata

1. `GET /exchange/v1/health`
2. `GET /exchange/v1/capabilities`

## 9. Decision Integration

Imported intelligence flows into local orchestration as a weighted contributor.

Rules:

1. Imported record creates an `ExchangeReputation` signal.
2. Weight is bounded and decays over time.
3. Local evidence always has higher priority than external hints.
4. Final action still comes from local policy matrix.

## 10. Trust Model

Each peer gets a trust profile:

1. Base trust weight
2. Max influence cap
3. Allowed reason codes
4. Allowed risk bands for import
5. Strike policy for invalid signatures or noisy data

Dynamic trust adjustment:

1. Increase trust when imported signals correlate with local confirmed detections.
2. Decrease trust when imported signals conflict with strong local evidence.

## 11. Admin UX Requirements

1. New Exchange section in dashboard:
   1. Peer status
   2. Records in/out
   3. Reject rates
   4. Trust score
2. Per-peer toggle: enabled, paused, quarantine.
3. Evidence drill-down: show which imported record influenced a decision.
4. Exportable audit logs.

## 12. Operational Requirements

1. Bounded queue sizes and backpressure behavior.
2. Dead-letter queue for malformed records.
3. Retry policy with exponential backoff.
4. Metrics:
   1. `exchange_records_received_total`
   2. `exchange_records_accepted_total`
   3. `exchange_records_rejected_total`
   4. `exchange_peer_trust_score`
   5. `exchange_influence_applied_total`

## 13. Rollout Plan

### Phase 0: Local-only Simulation

1. Emit records to local sink.
2. Validate schema and signing.
3. No decision influence.

### Phase 1: Trusted Pair Pilot

1. One-to-one exchange between controlled nodes.
2. Read-only import and observability.
3. Compare imported hints vs local outcomes.

### Phase 2: Weighted Influence

1. Enable capped influence for low-risk actions.
2. Measure false positive/negative drift.
3. Automatic rollback on quality regression.

### Phase 3: General Availability

1. Multi-peer federation.
2. Full dashboard management.
3. Public protocol docs and reference configs.

## 14. Risk Register

1. Poisoning attempts from compromised peers
2. Signature collision concerns across schema versions
3. Over-reliance on external hints in sparse local traffic
4. Operational complexity in key rotation

Mitigations are mandatory before GA:

1. Strict trust caps
2. Key rotation and revocation endpoint
3. Data quality scoring
4. Automatic quarantine on anomaly thresholds

## 15. Open Questions

1. Should v1 support push (webhook/stream) or remain pull-only?
2. Should vector schema compatibility be hard-fail or negotiated?
3. What is the minimum peer trust score for influence in production?
4. Should cluster-level intelligence be first-class in v1 or v2?

## 16. USP Alignment

The Signature Exchange amplifies StyloBot’s core strengths:

1. Speed with intelligence: import high-signal behavior context before local history is deep.
2. Cross-temporal detection: signatures represent repeated behavior over time, not session snapshots.
3. Zero-PII resilience: matching survives IP/user-agent/location churn because identity is behavioral and multi-vector.
4. Open and operator-controlled: teams keep policy control, transparency, and auditability.

