# Response Detection: Signal Architecture

## Overview

The response detection system uses a **two-tier signal architecture**:

1. **Operation-Level**: Per-request signals (request + response coordinators share same sink)
2. **Signature-Level**: Cross-request signals (keyed by signature, ephemeral LRU)

## Signal Flow Diagram

```
┌───────────────────────────────────────────────────────────────┐
│                   OPERATION TIER                              │
│                 (per-request, short-lived)                    │
├───────────────────────────────────────────────────────────────┤
│                                                               │
│  [RequestCoordinator]                                         │
│   │ Lifetime: request start → request detection complete     │
│   │ Dies: as soon as request-side complete                   │
│   ├──> emits: request.* signals                              │
│   │    - request.ip.datacenter                               │
│   │    - request.ua.suspicious                               │
│   │    - request.risk = 0.72                                 │
│   │    - response.trigger.mode = "Blocking"                  │
│   │    - response.trigger.thoroughness = "Deep"              │
│   │                                                           │
│   ▼                                                           │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │         OperationSignalSink                             │ │
│  │         (scoped to request, SHARED)                     │ │
│  │                                                         │ │
│  │  All signals for THIS operation:                       │ │
│  │  - request.* (from RequestCoordinator)                 │ │
│  │  - response.* (from ResponseCoordinator)               │ │
│  │                                                         │ │
│  │  Dies when: response sent                              │ │
│  └─────────────────────────────────────────────────────────┘ │
│   ▲                                                           │
│   │                                                           │
│  [ResponseCoordinator]                                        │
│   │ Lifetime: response start → response sent                 │
│   │ Reads from: SAME OperationSignalSink                     │
│   │ Mode: already decided (Blocking or Async)                │
│   ├──> emits: response.* signals                             │
│   │    - response.status = 404                               │
│   │    - response.pattern = "honeypot"                       │
│   │    - response.score = 0.85                               │
│   │                                                           │
│   └──> creates: OperationSummarySignal                       │
│        (aggregate of request + response)                     │
│                                                               │
└───────────────────┬───────────────────────────────────────────┘
                    │
                    │ OperationSummarySignal sent to:
                    ▼
┌───────────────────────────────────────────────────────────────┐
│                   SIGNATURE TIER                              │
│                 (cross-request, long-lived)                   │
├───────────────────────────────────────────────────────────────┤
│                                                               │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │         GlobalSignalSink                                │ │
│  │         (process-level, persistent)                     │ │
│  │                                                         │ │
│  │  Signals keyed by signature:                           │ │
│  │  - operation.complete.{signature}                      │ │
│  │  - signature.behavior.{signature}                      │ │
│  │                                                         │ │
│  │  Lifetime: process lifetime                            │ │
│  │  Cleanup: TTL-based eviction                           │ │
│  └──────────────────┬──────────────────────────────────────┘ │
│                     │                                         │
│                     │ filtered by signature                   │
│                     ▼                                         │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │  SignatureProfileAtom[signature]                       │ │
│  │  (keyed by signature, ephemeral LRU)                   │ │
│  │                                                         │ │
│  │  Window: last 100 OperationSummarySignals              │ │
│  │                                                         │ │
│  │  Lanes (parallel analysis):                            │ │
│  │   ├─ Behavioral Lane (entropy, timing)                 │ │
│  │   ├─ Spectral Lane (FFT, periodicity)                  │ │
│  │   ├─ Reputation Lane (history, learning)               │ │
│  │   └─ Content Lane (response patterns)                  │ │
│  │                                                         │ │
│  │  Emits: SignatureBehaviorSignal                        │ │
│  │    - aberrationScore = 0.92                            │ │
│  │    - waveform = "scan"                                 │ │
│  │    - recommendation = "block"                          │ │
│  │                                                         │ │
│  │  Lifetime: until evicted (LRU or TTL)                  │ │
│  │  When evicted: sink dies with it                       │ │
│  └─────────────────────────────────────────────────────────┘ │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

## Two Tiers Explained

### Tier 1: Operation-Level (Per-Request)

**Purpose**: Coordinate detection for a SINGLE HTTP request/response cycle

**Coordinators**:

- `RequestCoordinator` - Request-side detection
- `ResponseCoordinator` - Response-side detection

**Signal Sink**: `OperationSignalSink` (scoped, shared between request/response)

**Key Insight**: Request and response coordinators are **siblings**, not parent/child. They share the **same sink
instance**.

**Lifecycle**:

```
T=0ms:   Request arrives
         → OperationSignalSink created

T=2ms:   RequestCoordinator starts
         → Emits to OperationSignalSink
         → Dies when request detection complete

T=50ms:  ResponseCoordinator starts
         → Reads from SAME OperationSignalSink (sees request.* signals)
         → Emits response.* signals to SAME sink
         → Dies when response sent

T=55ms:  OperationSignalSink dies (no longer needed)
```

**Code Example**:

```csharp
// In middleware, create operation-scoped sink
var operationSink = new SignalSink(
    maxCapacity: 1000,
    maxAge: TimeSpan.FromMinutes(1)); // Dies when request completes

// Request coordinator uses it
var requestCoordinator = new RequestCoordinator(operationSink);
await requestCoordinator.DetectAsync(context);

// Response coordinator uses SAME sink
var responseCoordinator = new ResponseCoordinator(
    operationSink,  // ← Same instance
    globalSink);
await responseCoordinator.DetectAsync(context, responseSignal);

// Sink dies here (scoped lifetime)
```

### Tier 2: Signature-Level (Cross-Request)

**Purpose**: Track behavior patterns across MULTIPLE requests for a signature

**Coordinator**: `SignatureProfileAtom` (keyed by signature hash)

**Signal Sink**: `GlobalSignalSink` (process-level, persistent)

**Key Insight**: Each signature gets its own window of operations (last 100). Signature atoms are **ephemeral** - when
evicted from LRU, the sink dies too.

**Lifecycle**:

```
T=0:      SignatureProfileAtom["abc123"] created
          → Subscribes to GlobalSignalSink (filtered by signature)

T=1s:     Operation 1 completes
          → OperationSummarySignal sent to GlobalSignalSink
          → SignatureProfileAtom["abc123"] receives it
          → Adds to window (now 1 operation)

T=5s:     Operation 2 completes
          → Window now has 2 operations
          → Lanes analyze window

T=10s:    Operation 3 completes
          → Window now has 3 operations
          → Behavioral Lane detects scan pattern
          → Emits SignatureBehaviorSignal (aberration = 0.85)

T=20m:    No new operations from signature "abc123"
          → TTL expires or LRU evicts
          → SignatureProfileAtom["abc123"] destroyed
          → Its sink dies with it
```

**Code Example**:

```csharp
// Global sink (process-level, singleton)
var globalSink = new SignalSink(
    maxCapacity: 100000,
    maxAge: TimeSpan.FromHours(24));

// Signature profile atom (keyed by signature, ephemeral)
var signatureAtom = new SignatureProfileAtom(
    signature: "abc123",
    globalSink: globalSink,
    windowSize: 100);

// Record operation summary
await signatureAtom.RecordOperationAsync(operationSummary);

// Lanes run in parallel
var behavioralResult = await signatureAtom.Behavioral Lane.AnalyzeAsync();
var spectralResult = await signatureAtom.SpectralLane.AnalyzeAsync();
var reputationResult = await signatureAtom.ReputationLane.AnalyzeAsync();

// Aggregate and emit
var behavior = Aggregatelanes(behavioralResult, spectralResult, reputationResult);
globalSink.Raise($"signature.behavior.{signature}", behavior);
```

## Signal Types

### Operation-Level Signals

**Request Signals** (emitted by RequestCoordinator):

```csharp
operationSink.Raise("request.ip.datacenter", new { name = "AWS" });
operationSink.Raise("request.ua.suspicious", true);
operationSink.Raise("request.path.honeypot", "/.git/config");
operationSink.Raise("request.risk", 0.72);
operationSink.Raise("response.trigger.mode", "Blocking");
operationSink.Raise("response.trigger.thoroughness", "Deep");
```

**Response Signals** (emitted by ResponseCoordinator):

```csharp
operationSink.Raise("response.status", 404);
operationSink.Raise("response.pattern.honeypot", true);
operationSink.Raise("response.score", 0.85);
operationSink.Raise("response.detector.StatusCode", new { ... });
operationSink.Raise("response.detector.PatternMatcher", new { ... });
```

**Lifetime**: Dies when operation completes (response sent)

### Global Signals

**Operation Summary** (sent from operation → global):

```csharp
globalSink.Raise($"operation.complete.{signature}", new OperationSummarySignal
{
    Signature = "abc123",
    RequestId = "req-456",
    Timestamp = DateTimeOffset.UtcNow,
    Path = "/.git/config",
    Method = "GET",
    StatusCode = 404,
    RequestBotProbability = 0.72,
    ResponseScore = 0.85,
    ProcessingTimeMs = 45,
    TriggerSignals = new Dictionary<string, object>
    {
        ["honeypot.hit"] = true,
        ["datacenter.ip"] = "AWS"
    }
});
```

**Signature Behavior** (emitted by SignatureProfileAtom):

```csharp
globalSink.Raise($"signature.behavior.{signature}", new SignatureBehaviorSignal
{
    Signature = "abc123",
    AberrationScore = 0.92,
    Waveform = "scan",  // or "burst", "steady", etc.
    Recommendation = "block",
    Window = new
    {
        OperationCount = 47,
        Unique404Paths = 23,
        HoneypotHits = 3,
        AverageInterval = 2.5,
        PathEntropy = 4.2
    }
});
```

**Lifetime**: Persists in global sink until TTL expires

## Lanes

**Lanes** are parallel analysis streams that share signals but not state.

### Operation-Level Lanes (Future)

These would read from `OperationSignalSink`:

1. **Content Lane**: Real-time pattern matching
2. **Heuristic Lane**: Quick risk scoring
3. **AI Lane**: LLM-based semantic analysis

All run in parallel during response analysis.

### Signature-Level Lanes

These read from `GlobalSignalSink` (filtered by signature):

```csharp
public class SignatureProfileAtom
{
    private readonly LinkedList<OperationSummarySignal> _window;
    private readonly BehavioralLane _behavioralLane;
    private readonly SpectralLane _spectralLane;
    private readonly ReputationLane _reputationLane;
    private readonly ContentLane _contentLane;

    public async Task RecordOperationAsync(OperationSummarySignal operation)
    {
        // Add to window
        _window.AddLast(operation);
        while (_window.Count > 100)
            _window.RemoveFirst();

        // Run lanes in parallel
        var laneTasks = new[]
        {
            _behavioralLane.AnalyzeAsync(_window),    // Timing, entropy, Markov
            _spectralLane.AnalyzeAsync(_window),      // FFT, periodicity
            _reputationLane.AnalyzeAsync(_window),    // Historical scoring
            _contentLane.AnalyzeAsync(_window)        // Response patterns
        };

        var results = await Task.WhenAll(laneTasks);

        // Aggregate lane results
        var behavior = AggregateLaneResults(results);

        // Emit to global sink
        _globalSink.Raise($"signature.behavior.{_signature}", behavior);
    }
}
```

**Key Point**: Lanes share signals (via sink), not direct state. Each lane maintains its own internal state for its
analysis.

## Cross-Correlation: Request ↔ Response

Response detectors can read request-side signals from the **same OperationSignalSink**:

```csharp
public class HoneypotResponseDetector : ResponseDetectorBase
{
    public override async Task<ResponseDetectionContribution?> DetectAsync(
        ResponseBlackboardState state,
        CancellationToken cancellationToken)
    {
        // Read request-side signal (same sink!)
        var honeypotHit = state.GetRequestSignal<bool>("request.path.honeypot");
        var datacenterIp = state.GetRequestSignal<string>("request.ip.datacenter");

        if (honeypotHit && state.Signal.StatusCode == 404)
        {
            var score = datacenterIp != null ? 0.95 : 0.85;

            return CreateContribution(
                category: "Honeypot",
                score: score,
                reason: $"Honeypot path + 404 + datacenter IP ({datacenterIp})");
        }

        return null;
    }
}
```

**How it works**:

1. RequestCoordinator emits `request.path.honeypot = true` to OperationSignalSink
2. RequestCoordinator dies
3. ResponseCoordinator starts, uses **same sink instance**
4. ResponseDetector reads `request.path.honeypot` from sink (still there)
5. Combines with response data (404 status) for enriched detection

## Ephemeral Semantics

### Operation-Scoped Sink

```csharp
// Created per-request
var operationSink = new SignalSink(
    maxCapacity: 1000,
    maxAge: TimeSpan.FromMinutes(1));

// Both coordinators use it
requestCoordinator.Use(operationSink);
responseCoordinator.Use(operationSink);

// Dies when request completes (scoped lifetime)
// No manual cleanup needed
```

### Signature-Scoped Atom

```csharp
// Created per-signature (LRU cache)
var signatureCache = new SlidingCacheAtom<string, SignatureProfileAtom>(
    factory: async (signature, ct) => new SignatureProfileAtom(signature, globalSink),
    slidingExpiration: TimeSpan.FromMinutes(20),
    maxSize: 5000,  // Track up to 5000 signatures
    signals: globalSink);

// When evicted from LRU:
// - SignatureProfileAtom["old-signature"] destroyed
// - Its window (last 100 ops) freed
// - Lane state freed
// - Ephemeral semantics: dies cleanly
```

## Performance Characteristics

### Operation-Level

| Mode         | Latency      | Use Case                      |
|--------------|--------------|-------------------------------|
| **Blocking** | 5-50ms added | Honeypots, critical paths     |
| **Async**    | 0ms added    | Normal requests (most common) |

**Memory**: ~5KB per operation (signals + contributions), freed when response sent

### Signature-Level

| Window Size | Memory per Signature | Eviction           |
|-------------|----------------------|--------------------|
| 100 ops     | ~500KB               | LRU + TTL (20 min) |
| 200 ops     | ~1MB                 | LRU + TTL (20 min) |

**CPU**: Lanes run in parallel, ~10-50ms per analysis (async, not blocking requests)

## Summary

| Aspect           | Operation-Level                   | Signature-Level                           |
|------------------|-----------------------------------|-------------------------------------------|
| **Coordinators** | Request, Response (siblings)      | SignatureProfileAtom                      |
| **Sink**         | OperationSignalSink (scoped)      | GlobalSignalSink (persistent)             |
| **Lifetime**     | Per-request (ms)                  | Per-signature (minutes-hours)             |
| **Keyed by**     | Request ID                        | Signature hash                            |
| **Lanes**        | Content, Heuristic, AI            | Behavioral, Spectral, Reputation, Content |
| **Signals**      | request.*, response.*             | operation.complete, signature.behavior    |
| **Shared?**      | Yes (request/response share sink) | Yes (all signatures share global sink)    |
| **Ephemeral?**   | Yes (dies with request)           | Yes (dies with LRU eviction)              |
| **Purpose**      | THIS operation                    | Patterns across operations                |

**Key Design**:

- Request and response coordinators are **equal partners** sharing a sink, not parent/child
- Blocking vs Async decided **VERY EARLY** (during request detection)
- Signature atoms are **ephemeral** - when evicted, everything dies cleanly
- Signals enable **cross-correlation** without tight coupling
