# Response Detection: Ephemeral Semantics & Signal Sinks

## Core Principle: Sinks vs Atoms

**IMPORTANT**: Signal sinks are **scoped, not ephemeral**. Only the **atoms that raise signals** are ephemeral.

```
┌─────────────────────────────────────────────────────────────┐
│ SignalSink (long-lived, scoped)                            │
│ - Operation-scoped: dies when request completes            │
│ - Process-scoped: lives for process lifetime               │
│                                                             │
│ Contains signals raised by atoms:                          │
│  ┌──────────────────────────────────────────────────────┐  │
│  │ Atom 1 (ephemeral, LRU evicted)                      │  │
│  │ - Raises signals                                     │  │
│  │ - Dies when evicted from LRU                         │  │
│  │ - Signals persist in sink!                           │  │
│  └──────────────────────────────────────────────────────┘  │
│                                                             │
│  ┌──────────────────────────────────────────────────────┐  │
│  │ Atom 2 (ephemeral, LRU evicted)                      │  │
│  │ - Raises different signals                           │  │
│  │ - Also dies when evicted                             │  │
│  │ - Its signals also persist!                          │  │
│  └──────────────────────────────────────────────────────┘  │
│                                                             │
│ Important ones naturally persist via LRU!                  │
└─────────────────────────────────────────────────────────────┘
```

## Sink Lifecycles

### Operation-Scoped Sink

**Lifetime**: One HTTP request/response cycle

```csharp
// Created when request arrives
public async Task HandleRequestAsync(HttpContext context)
{
    // Create operation-scoped sink
    var operationSink = new SignalSink(
        maxCapacity: 1000,
        maxAge: TimeSpan.FromMinutes(1)); // But will die sooner (scoped)

    // Request coordinator raises signals INTO this sink
    var requestCoordinator = new RequestCoordinator(operationSink);
    await requestCoordinator.DetectAsync(context);
    // RequestCoordinator dies here (ephemeral atom)
    // But signals it raised persist in operationSink

    // Response coordinator raises signals INTO SAME sink
    var responseCoordinator = new ResponseCoordinator(operationSink, globalSink);
    await responseCoordinator.DetectAsync(context, responseSignal);
    // ResponseCoordinator dies here (ephemeral atom)
    // But signals it raised ALSO persist in operationSink

    // operationSink dies HERE (scoped lifetime)
    // All signals for this operation are now gone
}
```

**Key Point**: The sink is **scoped** (not ephemeral). Coordinators are **ephemeral atoms** that raise signals into the
sink.

### Process-Scoped Sink (Global)

**Lifetime**: Entire process (singleton)

```csharp
// Created at startup (singleton)
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Global sink lives for entire process
        var globalSink = new SignalSink(
            maxCapacity: 100000,
            maxAge: TimeSpan.FromHours(24));

        services.AddSingleton(globalSink);
    }
}
```

**Key Point**: The global sink is **long-lived**. Signature atoms that raise signals into it are **ephemeral** (LRU
evicted).

## Ephemeral Atoms

### What is Ephemeral?

**Atoms** (not sinks) are ephemeral:

1. **RequestCoordinator** - Dies when request detection completes
2. **ResponseCoordinator** - Dies when response sent
3. **SignatureProfileAtom** - Dies when evicted from LRU cache

**Signals they raised**: Persist in sinks (scoped by sink lifetime)

### SignatureProfileAtom Example

```csharp
// LRU cache of signature atoms (atoms are ephemeral)
var signatureCache = new SlidingCacheAtom<string, SignatureProfileAtom>(
    factory: async (signature, ct) =>
    {
        // Create NEW atom for this signature
        var atom = new SignatureProfileAtom(
            signature: signature,
            globalSink: globalSink);  // Sink is SHARED, not owned by atom

        return atom;
    },
    slidingExpiration: TimeSpan.FromMinutes(20),
    maxSize: 5000);  // LRU: keep 5000 most active signatures

// Get atom for signature "abc123"
var atom1 = await signatureCache.GetOrComputeAsync("abc123");
atom1.RecordOperation(operation1);
// Atom raises signal to globalSink: "operation.complete.abc123"

// ... time passes, atom1 stays in cache (active) ...

atom1.RecordOperation(operation2);
// Atom raises another signal: "operation.complete.abc123"

// ... more time passes, signature "abc123" becomes inactive ...

// NEW signature "xyz789" arrives, cache is full
var atom2 = await signatureCache.GetOrComputeAsync("xyz789");
// LRU evicts atom1 (signature "abc123")
// atom1 dies (ephemeral)
// BUT: signals it raised to globalSink persist!

// Later, query global sink:
var signals = globalSink.Sense(new SignalKey("operation.complete.abc123"));
// Returns all signals from atom1, even though atom1 is dead!
```

**Key Insight**: The atom dies, but signals it raised persist in the sink until **sink's own TTL** expires.

## LRU Behavior: Important Atoms Persist

Atoms with **high activity** stay in the LRU cache naturally:

```
Signature Activity:
- "scanner-bot-1": 1000 requests/hour → STAYS in LRU (top of cache)
- "normal-user-1": 10 requests/hour → Might get evicted
- "normal-user-2": 1 request/hour → Likely evicted

LRU Cache (max 5000 signatures):
┌─────────────────────────────────────┐
│ scanner-bot-1 ◄─ High activity      │
│ scraper-bot-2 ◄─ High activity      │
│ api-client-1  ◄─ Medium activity    │
│ normal-user-1 ◄─ Low activity       │
│ ...                                 │
│ normal-user-4999 ◄─ Very low        │
│ normal-user-5000 ◄─ About to evict │
└─────────────────────────────────────┘

New request from "new-scanner-bot":
→ Cache full, evict normal-user-5000 (LRU)
→ normal-user-5000 atom dies
→ scanner-bot-1 stays (frequently accessed)
```

**Auto-Specialization**: The cache naturally focuses on **problematic signatures** (high volume scanners, bots) while
letting inactive users evict.

## Signal Persistence

### Signals Outlive Atoms

```
T=0s:    SignatureAtom["abc123"] created
         └─> Raises signal: operation.complete.abc123 { score: 0.8 }

T=5s:    SignatureAtom["abc123"]
         └─> Raises signal: operation.complete.abc123 { score: 0.9 }

T=20m:   LRU evicts SignatureAtom["abc123"]
         └─> Atom dies (freed from memory)

T=20m+1s: Query globalSink for "abc123" signals
          └─> Returns both signals (score: 0.8, score: 0.9)
          └─> Signals persist until sink TTL (24 hours)

T=24h:   Sink TTL expires
         └─> Signals finally deleted
```

### Why This Matters

**Heuristic Learning**: Even if atom dies, signals persist:

```csharp
// Signature "bot-123" was active, then went quiet
// Its atom got evicted (LRU), BUT:

var signals = globalSink.Sense(new SignalKey("operation.complete.bot-123"));
// Still returns historical signals!

// When "bot-123" comes back later:
var atom = await signatureCache.GetOrComputeAsync("bot-123");
// NEW atom created, but can read OLD signals from sink:

var priorBehavior = atom.LoadHistoricalBehavior(globalSink);
// Reads signals from dead atom → learns from history
```

**Result**: Bots that go quiet then return are **still recognized**.

## Operation Flow with Scoped Sink

### Example: Honeypot Request

```
T=0ms:   Request arrives: GET /.git/config

T=1ms:   Create OperationSignalSink (scoped to this request)

T=2ms:   RequestCoordinator created (ephemeral atom)
         ├─> Raises to operationSink: request.path.honeypot = true
         ├─> Raises to operationSink: request.risk = 0.9
         └─> Dies (atom freed)

T=3ms:   Signals persist in operationSink:
         - request.path.honeypot = true
         - request.risk = 0.9

T=50ms:  ResponseCoordinator created (ephemeral atom)
         ├─> Reads from operationSink: request.path.honeypot = true
         ├─> Raises to operationSink: response.status = 404
         ├─> Raises to operationSink: response.score = 0.95
         └─> Dies (atom freed)

T=51ms:  Signals now in operationSink:
         - request.path.honeypot = true
         - request.risk = 0.9
         - response.status = 404
         - response.score = 0.95

T=52ms:  Create OperationSummarySignal from operationSink
         └─> Send to globalSink: operation.complete.abc123

T=53ms:  OperationSignalSink dies (scoped lifetime)
         └─> All operation signals freed

T=54ms:  SignatureAtom["abc123"] receives signal from globalSink
         └─> Records in window (last 100 operations)
```

**Key Points**:

- **Coordinators die** (ephemeral atoms)
- **operationSink dies** (scoped)
- **globalSink persists** (process-scoped)
- **SignatureAtom may die later** (LRU evicted)
- **Signals in globalSink persist** (until TTL)

## Signature Atom Architecture

```csharp
public class SignatureProfileAtom
{
    private readonly string _signature;
    private readonly SignalSink _globalSink; // NOT owned, just referenced
    private readonly LinkedList<OperationSummarySignal> _window;

    // Lanes (ephemeral with atom)
    private readonly BehavioralLane _behavioralLane;
    private readonly SpectralLane _spectralLane;
    private readonly ReputationLane _reputationLane;

    public SignatureProfileAtom(string signature, SignalSink globalSink)
    {
        _signature = signature;
        _globalSink = globalSink; // Reference to shared sink
        _window = new LinkedList<OperationSummarySignal>();

        _behavioralLane = new BehavioralLane();
        _spectralLane = new SpectralLane();
        _reputationLane = new ReputationLane();

        // Load historical signals from sink (even if previous atom died)
        LoadHistoricalSignals();
    }

    private void LoadHistoricalSignals()
    {
        // Read signals from global sink (may be from dead atom)
        var historicalSignals = _globalSink.Sense(
            new SignalKey($"operation.complete.{_signature}"));

        foreach (var signal in historicalSignals.OrderBy(s => s.Timestamp))
        {
            if (signal.Payload is OperationSummarySignal summary)
            {
                _window.AddLast(summary);
            }
        }

        // Trim to window size
        while (_window.Count > 100)
            _window.RemoveFirst();
    }

    public async Task RecordOperationAsync(OperationSummarySignal operation)
    {
        // Add to window
        _window.AddLast(operation);
        while (_window.Count > 100)
            _window.RemoveFirst();

        // Run lanes (ephemeral with atom)
        var laneTasks = new[]
        {
            _behavioralLane.AnalyzeAsync(_window),
            _spectralLane.AnalyzeAsync(_window),
            _reputationLane.AnalyzeAsync(_window)
        };

        await Task.WhenAll(laneTasks);

        // Emit to global sink (signal persists even if atom dies)
        _globalSink.Raise(
            new SignalKey($"signature.behavior.{_signature}"),
            new SignatureBehaviorSignal { /* ... */ });
    }

    // When atom dies (LRU eviction):
    // - _window freed
    // - _behavioralLane freed
    // - _spectralLane freed
    // - _reputationLane freed
    // - BUT: signals in _globalSink persist!
}
```

**Lifecycle**:

1. Atom created → Loads historical signals from sink
2. Atom processes operations → Emits signals to sink
3. Atom evicted → Frees memory (window + lanes)
4. Signals persist in sink → Available for next atom

## Summary

| Component                | Lifetime             | Scope          | Ephemeral?                |
|--------------------------|----------------------|----------------|---------------------------|
| **OperationSignalSink**  | Per-request          | Request-scoped | No (scoped)               |
| **GlobalSignalSink**     | Process              | Process-scoped | No (long-lived)           |
| **RequestCoordinator**   | Request detection    | Atom           | Yes (dies fast)           |
| **ResponseCoordinator**  | Response analysis    | Atom           | Yes (dies after response) |
| **SignatureProfileAtom** | Until LRU eviction   | Atom           | Yes (LRU evicted)         |
| **Signals in sink**      | Until sink TTL/scope | Sink-owned     | No (persist in sink)      |

**Key Insight**: Atoms are ephemeral (LRU evicted), but the **signals they raise persist** in sinks. Important
signatures naturally stay in LRU, unimportant ones evict, but their signals remain available for learning.

## Auto-Specialization via LRU

The LRU cache provides **automatic specialization**:

**High-Activity Signatures** (bots, scanners):

- Generate many operations → Frequent cache access
- Stay at top of LRU → Never evicted
- Atom stays in memory → Fast analysis

**Low-Activity Signatures** (normal users):

- Generate few operations → Infrequent cache access
- Fall to bottom of LRU → Evicted
- Atom freed → Memory saved
- Signals persist → Learning retained

**Result**: System naturally focuses resources on problematic traffic while learning from all traffic.
