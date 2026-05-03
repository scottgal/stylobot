# Multi-Factor Signature System

## Overview

The bot detection system uses **multi-factor signatures** to track patterns while handling real-world scenarios like
dynamic IPs, browser updates, and ISP changes, all while maintaining **zero-PII architecture**.

## The Problem

Traditional single-factor signatures have limitations:

```
❌ Single Factor (IP only):
- Problem: Dynamic ISPs change IPs frequently
- Result: Same user looks like different users (false negatives)

❌ Single Factor (UA only):
- Problem: Multiple users can have identical UAs
- Result: Different users look like same user (false positives)

❌ Composite (IP + UA):
- Problem: Either factor changing breaks the match
- Result: Legitimate user detected as "new" after IP change
```

## The Solution: Multi-Factor Signatures

Generate **multiple independent signatures** and use **partial matching** with configurable thresholds:

```
✅ Multi-Factor Approach:
- Primary: HMAC(IP + UA) - main identity
- IP: HMAC(IP) - handles UA changes
- UA: HMAC(UA) - handles IP changes
- ClientSide: HMAC(Canvas + WebGL + AudioContext) - browser fingerprint
- Plugin: HMAC(Plugins + Extensions + Fonts) - stable config

Match Rules:
- Exact Match: All factors match → 100% same client
- IP Changed: UA + ClientSide + Plugins match → Dynamic ISP (90% confidence)
- UA Changed: IP + ClientSide + Plugins match → Browser update (85% confidence)
- Minimum: Require 2+ factors to match (avoid false positives)
```

## Signature Factors

### 1. Primary Signature (IP + UA)

```
SignatureId: HMAC-SHA256(IP + UA)
```

**Purpose**: Main identity, backwards compatible with traditional approach

**When it matches**: Same client, same network, same browser

**Stability**: Low (changes with IP or browser updates)

**Example**:

```
IP: "203.0.113.42"
UA: "Mozilla/5.0 Chrome/120.0.0.0"
→ PrimarySignature: "XpK3nR8vMq-_Wd7"
```

### 2. IP Signature

```
IpSignature: HMAC-SHA256(IP)
```

**Purpose**: Track client across user-agent changes (browser updates)

**When it matches**: Same network location, different browser

**Stability**: Medium (stable within ISP session, days-weeks for static IPs)

**Use Case**:

- User updates Chrome 119 → 120
- UA changes, but IP remains same
- IP signature still matches → likely same user

### 3. UA Signature

```
UaSignature: HMAC-SHA256(UserAgent)
```

**Purpose**: Track client across IP changes (dynamic ISPs, mobile networks)

**When it matches**: Same browser/device, different network

**Stability**: Medium-High (stable until browser updates)

**Use Case**:

- Mobile user moves between WiFi and cellular
- IP changes frequently, but UA remains same
- UA signature still matches → likely same device

### 4. Client-Side Signature

```
ClientSideSignature: HMAC-SHA256(Canvas + WebGL + AudioContext + Screen + Timezone + ...)
```

**Purpose**: Browser fingerprint - most stable identifier

**When it matches**: Same browser on same device (hardware-based)

**Stability**: High (survives IP and UA changes)

**Components**:

- Canvas fingerprint (GPU rendering variations)
- WebGL fingerprint (GPU capabilities)
- AudioContext fingerprint (audio processing)
- Screen resolution
- Timezone offset
- Installed fonts
- Plugin list

**Use Case**:

- User travels (IP changes across countries)
- Browser auto-updates (UA changes)
- Client-side signature remains stable → same physical device

**Implementation**:

```javascript
// Client-side JS sends fingerprint in header
const fingerprint = await generateBrowserFingerprint();
fetch('/api', {
  headers: {
    'X-Client-Fingerprint': fingerprint
  }
});
```

### 5. Plugin Signature

```
PluginSignature: HMAC-SHA256(Plugins + Extensions + Fonts + AcceptLanguage + AcceptEncoding)
```

**Purpose**: Browser configuration fingerprint

**When it matches**: Same browser installation with same extensions

**Stability**: High (rarely changes)

**Components**:

- Installed plugins/extensions
- Installed fonts
- Accept-Language header
- Accept-Encoding header
- DNT (Do Not Track) preference

**Use Case**:

- User has unique plugin configuration (e.g., specific ad blocker + privacy extensions)
- Even if IP and UA change, plugin signature remains stable
- High confidence same user

### 6. IP Subnet Signature

```
IpSubnetSignature: HMAC-SHA256(IP /24 subnet)
```

**Purpose**: Network-level grouping (same ISP/datacenter)

**When it matches**: Same /24 subnet (e.g., 203.0.113.0/24)

**Stability**: Medium (stable within ISP allocation)

**Use Case**:

- Detect coordinated attacks from same datacenter
- Group traffic from same corporate network
- Less specific than full IP, more stable

## Matching Algorithm

### Match Rules

```csharp
// Require minimum 2 factors to match (configurable)
const int MINIMUM_FACTORS = 2;

// Match confidence calculation
if (matchedFactors.Contains("Primary"))
    confidence = 1.0;  // Exact match

else if (matchedFactors.Contains("ClientSide") && matchedFactors.Count >= 3)
    confidence = 0.95; // Very strong (browser fingerprint + 2 others)

else if (matchedFactors.Contains("IP") && matchedFactors.Contains("UA"))
    confidence = 1.0;  // Equivalent to primary

else if (matchedFactors.Contains("UA") && matchedFactors.Contains("ClientSide"))
    confidence = 0.90; // Dynamic IP, same device

else if (matchedFactors.Contains("IP") && matchedFactors.Contains("ClientSide"))
    confidence = 0.85; // Browser update, same network

else if (matchedFactors.Count >= 2)
    confidence = 0.70; // Partial match (2 factors)

else
    confidence = 0.0;  // No match or too weak
```

### Match Types

```csharp
public enum MatchType
{
    Weak,            // 0-1 factors match
    Partial,         // 2 factors match (different ones)
    Exact,           // All factors match
    ClientIdentity,  // Client-side fingerprint + 2 others
    NetworkIdentity  // IP subnet + 2 others
}
```

## Real-World Scenarios

### Scenario 1: Dynamic ISP (Mobile User)

```
Initial Request:
  IP: 203.0.113.42
  UA: Mozilla/5.0 Chrome/120
  ClientSide: canvas_abc123
  → Primary: hash_1, IP: hash_ip1, UA: hash_ua1, Client: hash_client1

10 Minutes Later (IP changes due to mobile network):
  IP: 198.51.100.88  ← CHANGED
  UA: Mozilla/5.0 Chrome/120
  ClientSide: canvas_abc123
  → Primary: hash_2, IP: hash_ip2, UA: hash_ua1, Client: hash_client1

Match Result:
  ✅ UA matches
  ✅ ClientSide matches
  ✅ Plugin matches
  ❌ IP different
  → Confidence: 90% (Dynamic IP, same device)
  → MatchType: ClientIdentity
```

### Scenario 2: Browser Update

```
Initial Request:
  IP: 203.0.113.42
  UA: Mozilla/5.0 Chrome/119  ← OLD VERSION
  ClientSide: canvas_abc123
  → Primary: hash_1, IP: hash_ip1, UA: hash_ua1, Client: hash_client1

After Browser Update:
  IP: 203.0.113.42
  UA: Mozilla/5.0 Chrome/120  ← UPDATED
  ClientSide: canvas_abc123
  → Primary: hash_2, IP: hash_ip1, UA: hash_ua2, Client: hash_client1

Match Result:
  ✅ IP matches
  ✅ ClientSide matches
  ✅ Plugin matches
  ❌ UA different (but expected)
  → Confidence: 85% (Browser update, same network)
  → MatchType: ClientIdentity
```

### Scenario 3: False Positive Avoidance

```
User A:
  IP: 203.0.113.42 (Corporate network)
  UA: Mozilla/5.0 Chrome/120 (Standard)
  ClientSide: canvas_corporate_default
  → Primary: hash_A

User B (Same company):
  IP: 203.0.113.42 (Same corporate network)  ← SAME IP
  UA: Mozilla/5.0 Chrome/120 (Same version)  ← SAME UA
  ClientSide: canvas_different_gpu  ← DIFFERENT HARDWARE
  → Primary: hash_B

Match Result:
  ❌ Primary different (IP+UA composite differs in details)
  ✅ IP matches
  ✅ UA matches
  ❌ ClientSide different
  → Confidence: 0% (Only 2 factors, but ClientSide MISMATCH)
  → MatchType: Weak
  → Result: Correctly identified as different users!
```

### Scenario 4: Bot with Rotating Proxies

```
Bot Request 1:
  IP: 203.0.113.1 (Datacenter)
  UA: HeadlessChrome/120
  ClientSide: None (headless = no canvas)
  Plugin: None
  → Primary: hash_bot1, IP: hash_ip1, UA: hash_ua_headless

Bot Request 2 (10 seconds later):
  IP: 198.51.100.50 (Different datacenter)  ← ROTATED
  UA: HeadlessChrome/120
  ClientSide: None
  Plugin: None
  → Primary: hash_bot2, IP: hash_ip2, UA: hash_ua_headless

Match Result:
  ❌ Primary different
  ❌ IP different
  ✅ UA matches (same bot UA)
  ❌ ClientSide absent
  ❌ Plugin absent
  → Confidence: 0% (Only 1 factor - UA)
  → MatchType: Weak
  → Result: Detected as different requests (correct!)
  → BUT: Pattern emerges (same UA, multiple datacenter IPs, no client-side)
```

## Database Storage (Zero-PII)

### What Gets Stored

```sql
CREATE TABLE bot_signatures (
  signature_id TEXT PRIMARY KEY,  -- HMAC(IP+UA) - non-reversible

  -- Multi-factor signatures (JSONB)
  signatures JSONB NOT NULL,  -- {
                              --   "primary": "hash",
                              --   "ip": "hash",
                              --   "ua": "hash",
                              --   "clientSide": "hash",
                              --   "plugin": "hash"
                              -- }

  -- Non-PII metadata
  request_path TEXT,
  request_method TEXT,
  bot_probability REAL,
  confidence REAL,
  risk_band TEXT,
  timestamp TIMESTAMPTZ,

  -- Privacy-safe signals (JSONB)
  signals_json JSONB,  -- { "is_datacenter": true, "honeypot_hit": false, ... }

  -- REMOVED (PII):
  -- remote_ip TEXT,    ❌ NEVER store raw IP
  -- user_agent TEXT    ❌ NEVER store raw UA
);

-- Index on multi-factor signatures for fast lookups
CREATE INDEX idx_signatures_gin ON bot_signatures USING GIN (signatures);
```

### What NEVER Gets Stored

```
❌ Raw IP address (PII)
❌ Raw User-Agent string (PII)
❌ Any personally identifying information

✅ ONLY non-reversible HMAC-SHA256 hashes
✅ ONLY privacy-safe signals (datacenter flags, bot scores, etc.)
```

## Pattern Detection Queries

### Find All Requests from Same Client (Any Factor Match)

```sql
-- Find requests where ANY signature matches
SELECT *
FROM bot_signatures
WHERE
  signatures->>'primary' = $1 OR
  signatures->>'ip' = $2 OR
  signatures->>'ua' = $3 OR
  signatures->>'clientSide' = $4
ORDER BY timestamp DESC;
```

### Find Dynamic IP Pattern

```sql
-- Find requests with same UA+ClientSide but different IPs
SELECT
  COUNT(DISTINCT signatures->>'ip') as ip_count,
  signatures->>'ua' as ua_sig,
  signatures->>'clientSide' as client_sig
FROM bot_signatures
GROUP BY signatures->>'ua', signatures->>'clientSide'
HAVING COUNT(DISTINCT signatures->>'ip') > 5
  AND signatures->>'clientSide' IS NOT NULL;
-- Result: Legitimate mobile users (high IP churn, stable client-side)
```

### Find Bot Rotation Pattern

```sql
-- Find requests with same UA, multiple IPs, NO client-side
SELECT
  signatures->>'ua' as ua_sig,
  COUNT(DISTINCT signatures->>'ip') as ip_count,
  COUNT(*) as request_count,
  bool_or(signatures->>'clientSide' IS NULL) as no_client_fp
FROM bot_signatures
WHERE timestamp > NOW() - INTERVAL '1 hour'
GROUP BY signatures->>'ua'
HAVING COUNT(DISTINCT signatures->>'ip') > 10
  AND bool_or(signatures->>'clientSide' IS NULL) = true;
-- Result: Bots with proxy rotation (high IP churn, no client-side)
```

## Security Properties

### Zero-PII Architecture

```
✅ All signatures are HMAC-SHA256 hashes with secret key
✅ Non-reversible without the secret key
✅ Consistent (same input → same hash)
✅ Collision-resistant (128-bit truncated hash)
✅ Key rotation supported (HKDF daily derivation)
```

### Key Management

```csharp
// Master key (256-bit, stored in Key Vault)
byte[] masterKey = GetFromKeyVault("bot-detection-master-key");

// Daily key derivation (prevents long-term correlation)
var hasher = PiiHasher.WithDailyRotation(masterKey, DateTime.UtcNow);

// Per-tenant isolation (SaaS deployments)
var hasher = PiiHasher.ForTenant(masterKey, "tenant-abc-123");
```

### Can You Reverse the Hashes?

```
Q: Can you get the original IP from the hash?
A: NO - HMAC is cryptographically non-reversible

Q: Can you brute-force common IPs?
A: Extremely difficult:
   - HMAC requires secret key (unknown to attacker)
   - 2^32 possible IPv4 addresses = 4 billion attempts
   - Each attempt requires HMAC computation (slow)
   - Key rotation makes old hashes invalid

Q: Can you correlate across deployments?
A: NO - Each deployment uses different master key

Q: Can you correlate across time?
A: NO - Daily key rotation prevents long-term tracking
```

## Configuration

### Enable Multi-Factor Signatures

```csharp
// In Program.cs
builder.Services.AddSingleton(sp =>
{
    var masterKey = GetSecretKey(); // From Key Vault
    return new PiiHasher(masterKey);
});

builder.Services.AddSingleton<MultiFactorSignatureService>();

// Configure minimum factors for match
builder.Services.Configure<SignatureMatchOptions>(options =>
{
    options.MinimumFactorsToMatch = 2;  // Require 2+ factors
    options.ClientSideWeight = 1.5;      // Boost client-side confidence
    options.PluginWeight = 1.2;          // Boost plugin confidence
});
```

### Client-Side Integration (Optional)

```html
<script src="/bot-detection/fingerprint.js"></script>
<script>
// Generate browser fingerprint and send in header
(async () => {
  const fingerprint = await BotDetection.generateFingerprint();

  // Send with all requests
  fetch.defaultHeaders = {
    'X-Client-Fingerprint': fingerprint.hash,
    'X-Browser-Plugins': fingerprint.plugins
  };
})();
</script>
```

## Migration from Single-Factor

### Backwards Compatibility

```csharp
// Old code (single factor - IP only)
var signature = ComputeSignatureHash(httpContext);  // Returns HMAC(IP)

// New code (multi-factor)
var signatures = multiFactorService.GenerateSignatures(httpContext);
var signature = signatures.PrimarySignature;  // Returns HMAC(IP+UA)

// Migration: Both work, new system is superset
```

### Database Migration

```sql
-- Add new column for multi-factor signatures
ALTER TABLE bot_signatures
ADD COLUMN signatures JSONB;

-- Migrate existing single-factor to multi-factor format
UPDATE bot_signatures
SET signatures = jsonb_build_object(
  'primary', signature_id,  -- Old signature becomes primary
  'ip', signature_id         -- Assume it was IP-only
)
WHERE signatures IS NULL;

-- Deprecate old PII columns
ALTER TABLE bot_signatures
ALTER COLUMN remote_ip SET DEFAULT NULL;
ALTER TABLE bot_signatures
ALTER COLUMN user_agent SET DEFAULT NULL;
```

## Summary

**Multi-factor signatures solve real-world challenges:**

1. **Dynamic IPs**: UA + ClientSide match → same user, different network ✅
2. **Browser Updates**: IP + ClientSide match → same user, updated browser ✅
3. **False Positives**: Require 2+ factors → avoid matching different users ✅
4. **Bot Rotation**: Pattern detection (high IP churn + no client-side) ✅
5. **Zero-PII**: ALL signatures are HMAC hashes, NO raw data stored ✅

**Key Benefits:**

- Handles legitimate user behavior (mobile, updates)
- Detects sophisticated bots (proxy rotation)
- Maintains privacy (non-reversible hashes)
- Enables pattern detection (multi-factor correlation)
- Configurable thresholds (tune for your use case)
