# Signature Coordinator Architecture

## Overview

The **SignatureCoordinator** is a singleton service that tracks request signatures across multiple HTTP requests to
detect aberrant behavior patterns. It forms the foundation of the new signal-driven, multi-coordinator architecture.

## Architecture: Two-Tier Coordination

```
┌──────────────────────────────────────────────────────────────────────┐
│                      Per-Request Coordinator                          │
│                    (EphemeralDetectionOrchestrator)                   │
│  ┌──────────────────────────────────────────────────────────┐        │
│  │  Request arrives → Contributors run in parallel           │        │
│  │  Contributors emit SIGNALS with signatures                │        │
│  │  Can early exit or escalate based on signals              │        │
│  │  Lives only for the request duration                      │        │
│  └──────────────────────────────────────────────────────────┘        │
│                              ↓ Escalation Signal                      │
└──────────────────────────────────────────────────────────────────────┘
                               ↓
┌──────────────────────────────────────────────────────────────────────┐
│              Cross-Request Singleton Coordinator                      │
│                    (SignatureCoordinator)                             │
│  ┌──────────────────────────────────────────────────────────┐        │
│  │  Tracks 1000 signatures in sliding window (15 min)        │        │
│  │  Each signature = keyed atom with request history         │        │
│  │  Detects aberrations across multiple requests             │        │
│  │  Emits aberration signals when patterns detected          │        │
│  │  Lives for application lifetime (singleton)               │        │
│  └──────────────────────────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────────────────┘
```

## Key Concepts

### 1. Signatures

A **signature** is a privacy-preserving hash of the client identity (IP + salt):

- **Deterministic:** Same IP always produces same signature
- **Non-reversible:** Cannot recover IP from signature
- **Salted:** Configurable salt for multi-tenant isolation
- **Example:** `"A3F5B9C2D8E1F4A7"` (16-character hex)

### 2. Tracking Window

The coordinator maintains a **sliding window** of signatures:

- **Capacity:** 1000 signatures (configurable via `MaxSignaturesInWindow`)
- **Duration:** 15 minutes (configurable via `SignatureWindow`)
- **Eviction:** LRU (Least Recently Used) when capacity is exceeded
- **Per-Signature Limit:** 100 requests per signature (prevents memory exhaustion)

### 3. Signature Atoms

Each tracked signature has its own **SignatureTrackingAtom**:

```csharp
SignatureTrackingAtom
├── Signature Hash (key)
├── Request History (LinkedList<SignatureRequest>)
│   ├── Request 1 (timestamp, path, botProb, signals, detectors)
│   ├── Request 2
│   └── Request N (max 100)
├── Cached Behavior (recomputed on each new request)
│   ├── Path Entropy (Shannon)
│   ├── Timing Coefficient (CV)
│   ├── Average Bot Probability
│   ├── Aberration Score
│   └── IsAberrant flag
└── Semaphore (thread-safe access)
```

### 4. Cross-Request Intelligence

The coordinator analyzes patterns **across requests** for each signature:

#### Path Entropy (Shannon)

```
H = -Σ(p * log2(p))
```

- Detects scanning behavior (high entropy > 3.0)
- Detects repetitive loops (low entropy)

#### Timing Coefficient of Variation

```
CV = σ / μ
```

- Detects too-regular timing (CV < 0.15 = bot-like)
- Natural humans have CV 0.3-0.8

#### Average Bot Probability

- Tracks average detection confidence across all requests
- High average (>0.6) indicates persistent bot behavior

#### Aberration Score

Weighted combination of cross-request metrics:

```csharp
score = 0
if (avgBotProb > 0.6)       score += 0.3 * avgBotProb
if (pathEntropy > 3.0)      score += 0.25
if (timingCV < 0.15)        score += 0.25
if (avgInterval < 2.0)      score += 0.20
return min(score, 1.0)
```

When `aberrationScore >= 0.7` → **Aberration Signal Emitted**

## API

### Recording Requests

```csharp
await signatureCoordinator.RecordRequestAsync(
    signature: "A3F5B9C2D8E1F4A7",
    requestId: "req-12345",
    path: "/api/data",
    botProbability: 0.45,
    signals: signalsDictionary,
    detectorsRan: detectorsHashSet);
```

**What Happens:**

1. Gets or creates `SignatureTrackingAtom` for the signature
2. Records the request in the atom's sliding window
3. Evicts old requests (outside 15-minute window)
4. Enforces max 100 requests per signature
5. Recomputes cross-request behavioral metrics
6. Emits aberration signal if threshold exceeded

### Querying Behavior

```csharp
var behavior = await signatureCoordinator.GetSignatureBehaviorAsync(
    signature: "A3F5B9C2D8E1F4A7");

if (behavior != null)
{
    Console.WriteLine($"Requests: {behavior.RequestCount}");
    Console.WriteLine($"Path Entropy: {behavior.PathEntropy:F2}");
    Console.WriteLine($"Timing CV: {behavior.TimingCoefficient:F2}");
    Console.WriteLine($"Aberration Score: {behavior.AberrationScore:F2}");
    Console.WriteLine($"Is Aberrant: {behavior.IsAberrant}");
}
```

### Listening for Aberrations

```csharp
var aberrationSignals = signatureCoordinator.GetAberrationSignals();

foreach (var signal in aberrationSignals)
{
    var aberration = signal.Payload;
    Console.WriteLine($"Aberrant signature: {aberration.Signature}");
    Console.WriteLine($"Score: {aberration.AberrationScore:F2}");
    Console.WriteLine($"Requests: {aberration.RequestCount}");
    Console.WriteLine($"Reason: {aberration.Reason}");
}
```

### Statistics

```csharp
var (tracked, total, aberrant) = signatureCoordinator.GetStatistics();

Console.WriteLine($"Tracked Signatures: {tracked}");
Console.WriteLine($"Total Requests: {total}");
Console.WriteLine($"Aberrant Signatures: {aberrant}");
```

## Configuration

```json
{
  "BotDetection": {
    "SignatureCoordinator": {
      "MaxSignaturesInWindow": 1000,
      "SignatureWindow": "00:15:00",
      "MaxRequestsPerSignature": 100,
      "MinRequestsForAberrationDetection": 5,
      "AberrationScoreThreshold": 0.7
    }
  }
}
```

### Configuration Options

| Option                              | Description                                    | Default    |
|-------------------------------------|------------------------------------------------|------------|
| `MaxSignaturesInWindow`             | Maximum signatures to track (LRU eviction)     | 1000       |
| `SignatureWindow`                   | Time window for tracking requests              | 15 minutes |
| `MaxRequestsPerSignature`           | Max requests per signature (prevents flooding) | 100        |
| `MinRequestsForAberrationDetection` | Minimum requests before analysis               | 5          |
| `AberrationScoreThreshold`          | Score threshold for aberration                 | 0.7        |

## Memory & Performance

### Memory Footprint

**Per Signature:**

```
SignatureTrackingAtom: ~2 KB base
+ (100 requests × ~1 KB) = ~100 KB per signature
```

**Total Memory:**

```
1000 signatures × 100 KB = ~100 MB maximum
```

**With Eviction:**

- LRU eviction keeps memory bounded
- Signatures outside 15-minute window auto-evicted
- Old requests within signature auto-evicted

### Performance

**RecordRequestAsync:**

- O(1) signature lookup (ConcurrentDictionary)
- O(1) request recording (LinkedList append)
- O(n) behavior computation (n = requests in window, max 100)
- **Total:** <1ms per request for 100-request signatures

**Thread Safety:**

- Per-signature semaphore (no global lock)
- Parallel requests to different signatures = no contention
- Requests to same signature = sequential (but fast)

## Use Cases

### 1. Distributed Scanning Detection

**Pattern:** Bot distributes scanning across time to evade rate limits

```
Signature: "A3F5B9C2D8E1F4A7"
Request 1: /api/users (10:00:00)
Request 2: /.env (10:02:15)
Request 3: /admin (10:04:30)
Request 4: /wp-login.php (10:06:45)
Request 5: /config.php (10:09:00)

Cross-Request Analysis:
- Path Entropy: 4.5 (very high - random scanning)
- Timing CV: 0.02 (too regular - 135s ± 5s)
- Aberration Score: 0.85

→ Aberration Signal Emitted
```

### 2. Slow Scraper Detection

**Pattern:** Bot requests same endpoint repeatedly over time

```
Signature: "B4C5D6E7F8A9B0C1"
Request 1-50: /api/products (over 10 minutes)

Cross-Request Analysis:
- Path Entropy: 0.0 (no variety - same path)
- Average Bot Probability: 0.55 (medium confidence)
- Timing CV: 0.08 (too consistent)
- Aberration Score: 0.75

→ Aberration Signal Emitted
```

### 3. Normal User Pattern

**Pattern:** Human browsing naturally

```
Signature: "C6D7E8F9A0B1C2D3"
Request 1: /home (10:00:00)
Request 2: /products (10:00:15)
Request 3: /product/123 (10:00:45)
Request 4: /cart (10:01:30)
Request 5: /checkout (10:02:00)

Cross-Request Analysis:
- Path Entropy: 1.8 (moderate variety)
- Timing CV: 0.52 (natural variance)
- Average Bot Probability: 0.15
- Aberration Score: 0.20

→ No Aberration (normal behavior)
```

## Integration with Per-Request Orchestrator

The SignatureCoordinator integrates with the per-request detection flow:

```csharp
// At end of per-request detection
var signature = HashSignature(clientIp);

await signatureCoordinator.RecordRequestAsync(
    signature,
    requestId,
    path,
    finalEvidence.BotProbability,
    finalEvidence.Signals,
    finalEvidence.ContributingDetectors);

// Check for cross-request aberrations
var behavior = await signatureCoordinator.GetSignatureBehaviorAsync(signature);

if (behavior?.IsAberrant == true)
{
    // Boost bot probability based on cross-request intelligence
    finalEvidence = finalEvidence with
    {
        BotProbability = Math.Max(
            finalEvidence.BotProbability,
            behavior.AberrationScore)
    };
}
```

## Benefits

### 1. Bypasses Per-Request Evasion

- Bots that look "normal" in single requests
- Distributes scanning to avoid rate limits
- Hides bot behavior behind legitimate patterns

### 2. Temporal Pattern Detection

- Detects regularity across time
- Identifies scanning campaigns
- Tracks persistent bot identities

### 3. Privacy-Preserving

- No IP storage - only hashed signatures
- Configurable salt for isolation
- Time-bounded tracking (15 minutes)
- Automatic eviction

### 4. Memory Efficient

- Bounded capacity (1000 signatures)
- LRU eviction
- Per-signature limits (100 requests)
- Automatic cleanup

### 5. Fast & Scalable

- O(1) lookups
- Per-signature locking (parallel processing)
- Minimal computation per request
- Singleton architecture (shared across all requests)

## Next Steps

This is **Step 1** of the new architecture:

- ✅ **Step 1:** SignatureCoordinator singleton with cross-request tracking
- 🔄 **Step 2:** Signal-based contributor emissions
- 🔄 **Step 3:** Escalation mechanism (per-request → singleton)
- 🔄 **Step 4:** Signal-based early exit in orchestrator
- 🔄 **Step 5:** Update contributors to emit signatures as signals
- 🔄 **Step 6:** End-to-end testing

## See Also

- [Ephemeral Detection Orchestrator](./orchestrator.md) - Per-request coordination
- [Contributing Detectors](./contributing-detectors.md) - Signal emission patterns
- [Signal-Based Architecture](./signals.md) - Signal keys and typed payloads
- [Privacy & Security](./privacy-security.md) - Signature hashing and PII protection
