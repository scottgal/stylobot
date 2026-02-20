# Connected Signature Exchange Spec

Status: Draft  
Owner: Core Platform  
Scope: Stylobot.net-hosted shared signature feed for connected instances

## Purpose

Define a production-ready spec for:

1. Current detection endpoints exposed by Stylobot.net
2. New connected-instance exchange endpoints
3. Delta synchronization (`last accessed` / cursor) behavior
4. Merge reputation logic that can promote signatures into a local list
5. Privacy and security controls that keep the system zero-PII

## Current endpoint baseline (Stylobot.net)

These are available in the website host today (subject to environment/config):

1. `GET /bot-detection/check`
2. `GET /bot-detection/stats`
3. `GET /bot-detection/health`
4. `POST /bot-detection/feedback`
5. `POST /bot-detection/fingerprint`
6. `GET /_stylobot`
7. `GET /_stylobot/api/summary`
8. `GET /_stylobot/api/detections`
9. `GET /_stylobot/api/signatures`
10. `GET /_stylobot/api/timeseries`
11. `GET /_stylobot/api/countries`
12. `GET /_stylobot/api/clusters`
13. `GET /_stylobot/api/topbots`
14. `GET /_stylobot/api/useragents`
15. `GET /_stylobot/api/export`
16. `GET /_stylobot/api/me`

Notes:

1. `/bot-detection/*` diagnostics are controlled by `StyloBot:ExposeDiagnostics`.
2. Dashboard data endpoints are protected by dashboard authorization and bot blocking logic.

## Current endpoint contracts (vCurrent)

### `GET /bot-detection/check`

Purpose:

1. Per-request detection verdict and explainability payload

Core fields:

1. `isBot`, `isHuman`
2. `botProbability`, `humanProbability`, `confidence`
3. `riskBand`, `recommendedAction`
4. `detectorsRan`, `contributions`, `signals` (PII-filtered)
5. `processingTimeMs`

### `GET /bot-detection/stats`

Purpose:

1. Aggregate counters and latency summary

Core fields:

1. `totalRequests`, `botsDetected`, `botPercentage`
2. `verifiedBots`, `maliciousBots`
3. `averageProcessingTimeMs`
4. `botTypeBreakdown`

### `GET /bot-detection/health`

Purpose:

1. Service health and basic throughput signal

Core fields:

1. `status`
2. `service`
3. `totalRequests`
4. `averageResponseMs`

### `POST /bot-detection/feedback`

Purpose:

1. Operator feedback loop for false-positive / false-negative review

Request:

1. `outcome`: `Human` or `Bot`
2. `requestId` (optional)
3. `notes` (optional, max length constrained)

### `POST /bot-detection/fingerprint`

Purpose:

1. Receives client-side fingerprint telemetry for correlation

Requirements:

1. Header `X-ML-BotD-Token`
2. JSON body matching browser fingerprint schema

Response:

1. `received`
2. `id`

### `GET /_stylobot/api/*`

Purpose:

1. Dashboard and analytics feeds (`summary`, `detections`, `signatures`, `timeseries`, `countries`, `clusters`, `topbots`, `useragents`, `export`)

Rules:

1. Authorization required by dashboard policy
2. Confirmed bots are denied for high-value data feeds
3. Output is operational telemetry, not raw request PII

## New capability: connected signature exchange

Connected instances can pull shared signature intelligence from Stylobot.net and merge it into local detection state.

Goals:

1. Faster detection on new deployments with shallow local history
2. Shared protection against repeat bot campaigns
3. Zero-PII exchange payloads
4. Local policy remains authoritative

Non-goals:

1. No sharing raw IP, raw User-Agent, cookies, or session IDs
2. No forced blocking based only on remote data
3. No unlimited historical replay

## Proposed exchange API surface (v1)

All routes below are under `/exchange/v1`.

### 1) `GET /exchange/v1/health`

Lightweight availability check for connected clients.

### 2) `GET /exchange/v1/capabilities`

Returns:

1. Supported schema versions
2. Max page size
3. Cursor retention window
4. Signature algorithms
5. Recommended polling interval

### 3) `GET /exchange/v1/signatures?cursor={cursor}&limit={n}`

Primary delta feed endpoint.

Behavior:

1. Returns only records after `cursor`
2. Sorted by server sequence (`exchangeOffset`)
3. Includes `nextCursor` and `hasMore`
4. Returns `410 Gone` if cursor is outside retention window

### 4) `GET /exchange/v1/signatures?sinceUtc={iso8601}&limit={n}`

Fallback delta mode when cursor is unavailable.

Behavior:

1. Returns records with `updatedAtUtc > sinceUtc`
2. Lower fidelity than cursor mode for high-volume streams
3. Intended only for recovery/bootstrap paths

### 5) `POST /exchange/v1/ack`

Optional checkpoint endpoint for observability.

Request fields:

1. `nodeId`
2. `cursor`
3. `receivedAtUtc`
4. `acceptedCount`
5. `rejectedCount`

Purpose:

1. Lets Stylobot.net show per-peer lag and feed health
2. Supports operator troubleshooting

## Exchange record contract (PII-safe)

```json
{
  "version": "1.0",
  "recordId": "f9d4a4cb-9d66-49d1-8f13-50a7f2de6022",
  "exchangeOffset": 1844221,
  "publishedAtUtc": "2026-02-20T10:20:11Z",
  "updatedAtUtc": "2026-02-20T10:20:11Z",
  "sourceNodeId": "stylobot-net",
  "exchangeSignature": "exsig_6f4d5d...d20a",
  "schemaVersion": "sbx.v1",
  "classification": {
    "botProbability": 0.93,
    "confidence": 0.87,
    "riskBand": "High",
    "recommendedActionHint": "Challenge"
  },
  "summary": {
    "reasonCodes": ["UA_AUTOMATION_STACK", "BEHAVIORAL_BURST", "HEADER_INCONSISTENT"],
    "detectors": ["UserAgent", "Behavioral", "Header"],
    "requestCount": 49
  },
  "reputation": {
    "sourceTrustHint": 0.92,
    "evidenceCount": 49
  },
  "ttlSeconds": 1209600,
  "signatureEnvelope": {
    "alg": "Ed25519",
    "kid": "exchange-key-2026-01",
    "sig": "base64..."
  }
}
```

Never included:

1. Raw IP address
2. Raw User-Agent
3. Cookie values
4. Session IDs
5. Query strings or body payload fragments

## Delta sync model (`last accessed`)

Each connected node stores:

1. `lastCursor`
2. `lastSuccessfulSyncUtc`
3. `lastAttemptUtc`
4. `lastAckedOffset`

Recommended client loop:

1. Call `GET /exchange/v1/signatures?cursor=...`
2. Validate signature envelope and schema
3. Upsert locally by `recordId` (idempotent)
4. Commit `lastCursor = nextCursor` only after durable write
5. Optionally `POST /exchange/v1/ack`

If `410 Gone` is returned:

1. Fall back to `sinceUtc=lastSuccessfulSyncUtc`
2. If still out of range, run bounded bootstrap pull

## Merge reputation system (local promotion model)

Imported signatures should not instantly become local enforced signatures.  
Use a staged merge model:

1. `Imported`: accepted but zero policy influence
2. `Candidate`: soft influence only (challenge/throttle hints)
3. `PromotedLocal`: merged into local signature list
4. `Quarantined`: invalid/conflicting/noisy source data

Per-signature local state:

1. `localEvidenceScore` (`0..1`)
2. `externalConsensusScore` (`0..1`)
3. `trustedSourceCount`
4. `maxSourceTrust`
5. `conflictScore` (`0..1`, local-human contradictions)
6. `mergedReputation` (`0..1`)
7. `lastMergedUtc`

Suggested scoring:

`mergedReputation = clamp(0, 1, 0.60*localEvidenceScore + 0.30*externalConsensusScore + 0.10*maxSourceTrust - 0.35*conflictScore)`

Guardrails:

1. External influence cap: max `0.35` contribution when no local evidence exists
2. Promotion to `PromotedLocal` requires `mergedReputation >= 0.82`
3. Promotion to `PromotedLocal` requires `confidence >= 0.75`
4. Promotion to `PromotedLocal` requires `trustedSourceCount >= 2` (or `>=1` with `maxSourceTrust >= 0.95` plus at least one local hit)
5. Promotion to `PromotedLocal` requires no strong local-human contradiction in the last 14 days
6. Auto-demotion if merged score decays below `0.65`

Result:

1. Multiple trusted sources corroborating the same signature increases local reputation
2. High enough merged reputation can promote into your local list
3. Local evidence still outranks remote evidence

## PII and security controls

Data minimization:

1. Exchange payload carries only derived, non-reversible signatures and summary signals
2. Path-like values are generalized (token/ID stripping) before hashing
3. Strict allowlist schema (reject unknown sensitive fields)

Transport and auth:

1. TLS required
2. API key or mTLS per peer
3. Payload signing (`Ed25519`) with key rotation (`kid`)
4. Replay protection (`recordId`, `publishedAtUtc`, skew window)

Governance:

1. Per-peer trust profiles and rate limits
2. Full audit log: imported record to influenced decision
3. Retention TTL + expiry sweeps
4. Peer quarantine on invalid-signature or anomaly thresholds

## Useful enhancements for v1.1+

1. Signed peer quality score endpoint (`precision`, `conflict rate`, `false positive drift`)
2. Topic feeds (`ai-scrapers`, `credential-stuffing`, `scanner`)
3. Region/industry partitions for relevance
4. Optional push channel (`SSE/WebSocket`) in addition to pull
5. Provenance graph in dashboard: why a signature got promoted

## Rollout plan

1. Phase 0: schema + docs + synthetic replay tests
2. Phase 1: read-only pull + no policy influence
3. Phase 2: candidate influence with strict cap
4. Phase 3: controlled local promotion (`PromotedLocal`) with rollback toggles

## Acceptance criteria

1. No PII fields accepted or emitted by exchange endpoints
2. Delta sync resumes from cursor without duplication
3. Merge promotion is deterministic and auditable
4. Local policy can disable or cap external influence instantly
5. Dashboard shows peer lag, import counts, rejects, promotions, quarantines
