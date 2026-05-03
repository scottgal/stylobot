# Fingerprint Approval System

Decouple **identity** (API key = who you are) from **trust** (fingerprint approval = your client is legit) from **behavior** (detection = what you're doing right now).

## The Problem

Traditional API key security: `key = access`. Stolen key = full access.

StyloBot's approach: `key + matching fingerprint + locked dimensions + behavioral baseline = access`. A stolen key from a different environment is useless.

## Three Trust Dimensions

| Dimension | What | Tier |
|-----------|------|------|
| **Identity** | API key | All |
| **Trust** | Fingerprint approval + locked dimensions | Enterprise |
| **Behavior** | 49 detectors + session vectors, including `Threat Intelligence` | All |

## Flow

1. Detection runs, visitor scores borderline (0.5-0.7)
2. Response includes `X-SB-Approval-Id: {token}` header (one-time, 24h expiry)
3. Operator enters the token in the dashboard form with justification + locked dimensions + optional expiry
4. Stored in SQLite (FOSS) or PostgreSQL (commercial)
5. Next request: `FingerprintApprovalContributor` reads approval, checks locked dimensions against live signals
6. All match: strong human signal (-0.4 delta). Mismatch: strong bot signal (+0.3 delta)

## Locked Dimensions

On approval, the operator can lock any signal dimension. Each locked dimension is checked against the live blackboard on every request:

```json
{
  "ip.country_code": "US",
  "ua.family": "Python-Requests",
  "ip.cidr": "10.0.0.0/8"
}
```

If any locked dimension doesn't match, the approval is void. This catches:
- **Stolen API credentials** used from a different environment
- **Account takeover** where the attacker's client has different characteristics
- **Credential sharing** across unauthorized environments

## API

### Approve via token (from X-SB-Approval-Id header)

```bash
POST /_stylobot/api/approvals/by-token
{
  "approvalId": "abc123",
  "justification": "Partner X production scraper",
  "lockedDimensions": {
    "ip.country_code": "US",
    "ua.family": "Python-Requests"
  },
  "expiresInDays": 30
}
```

### Approve directly by signature

```bash
POST /_stylobot/api/approvals/{signature}
{
  "justification": "Manually approved",
  "lockedDimensions": { "ip.country_code": "US" },
  "expiresInDays": 90
}
```

### List approvals

```bash
GET /_stylobot/api/approvals?limit=50
```

### Revoke

```bash
DELETE /_stylobot/api/approvals/{signature}
```

## Response Headers (opt-in)

```json
{
  "BotDetection": {
    "ResponseHeaders": {
      "IncludeApprovalId": true,
      "IncludeReasonHeader": true
    }
  }
}
```

- `X-SB-Approval-Id`: One-time token for borderline requests (0.5-0.7)
- `X-SB-Reason`: Top contributing detector reason (PII-free, 200 char max)

## Detection Signals

| Signal | Type | Description |
|--------|------|-------------|
| `approval.verified` | bool | Approval exists and was checked |
| `approval.status` | string | "active", "expired", "revoked" |
| `approval.locked_dimensions_ok` | bool | All locked dimensions match |
| `approval.dimension_mismatch` | string | Comma-separated mismatched keys |
| `approval.justification` | string | Operator's reason |
| `approval.expires_at` | string | ISO 8601 expiry |
