# Response Detection: Escalation Model

## Core Principle: Signal-Based Escalation

**Per-Operation Sinks** (scoped to request) → **Signature-Scoped Coordinators** (with their own sinks)

The **signature** carries state out of operation context into signature coordinators for offline work.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    PER-OPERATION TIER                           │
│                  (scoped to single request)                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  [OperationSignalSink] ◄─── Created per-request                │
│   │                                                             │
│   ├─► [RequestCoordinator] raises signals                      │
│   │    - request.path.honeypot = true                          │
│   │    - request.ip.datacenter = "AWS"                         │
│   │    - request.risk = 0.9                                    │
│   │                                                             │
│   ├─► [ResponseCoordinator] reads + raises signals             │
│   │    - Reads: request.* signals                              │
│   │    - Raises: response.status = 404                         │
│   │    - Raises: response.score = 0.95                         │
│   │                                                             │
│   └─► ESCALATION POINT: End of operation                       │
│        └─► Emit OperationCompleteSignal                        │
│             to SignatureCoordinator[signature]                 │
│                                                                 │
│  Dies when: Response sent (scoped lifetime)                    │
│                                                                 │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     │ Escalation Signal (keyed by signature)
                     │
                     ▼
┌─────────────────────────────────────────────────────────────────┐
│                  SIGNATURE-SCOPED TIER                          │
│               (long-lived, per-signature, LRU)                  │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  [SignatureCoordinator["abc123"]]                              │
│   Has its own sink: SignatureSignalSink["abc123"]              │
│   │                                                             │
│   ├─► Receives: OperationCompleteSignal from operation tier    │
│   │    - Adds to window (last 100 operations)                  │
│   │                                                             │
│   ├─► Runs Lanes (with their own coordinators):                │
│   │    │                                                        │
│   │    ├─► BehavioralLane (has SignatureSink)                  │
│   │    │    - Analyzes timing, entropy, patterns               │
│   │    │    - Emits: behavioral.entropy = 4.2                  │
│   │    │                                                        │
│   │    ├─► SpectralLane (has SignatureSink)                    │
│   │    │    - FFT analysis, periodicity                        │
│   │    │    - Emits: spectral.frequency = 0.5Hz                │
│   │    │                                                        │
│   │    └─► ReputationLane (has SignatureSink)                  │
│   │         - Historical scoring                               │
│   │         - Emits: reputation.score = 0.85                   │
│   │                                                             │
│   └─► Aggregates lane signals → SignatureBehaviorSignal        │
│        - Sends to HeuristicDetector for next request           │
│                                                                 │
│  Dies when: LRU evicts (inactive signature)                    │
│  Its SignatureSink dies with it                                │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## Key Insight: Signature as State Carrier

The **signature** (client hash) is the **key** that carries state from operation context into signature coordinators:

```csharp
// In operation tier
var signature = GetClientHash(context); // e.g., "abc123"

// Escalate to signature tier
operationSink.Raise("operation.complete", new OperationCompleteSignal
{
    Signature = signature,  // ← STATE CARRIER
    RequestId = requestId,
    // ... all operation data
});

// Signature coordinator receives it
signatureCoordinators[signature].ReceiveOperationSignal(signal);
```

## Per-Operation Sink (Scoped)

### Purpose

- Coordinate request + response for THIS operation
- Enable signal-based handoff (no HttpContext dependency)
- Works with YARP (signals are serializable, HttpContext is not)

### Lifecycle

```csharp
public async Task HandleRequestAsync(HttpContext context)
{
    var signature = GetClientHash(context);
    var requestId = context.TraceIdentifier;

    // Create operation-scoped sink
    using var operationSink = new SignalSink(
        maxCapacity: 1000,
        maxAge: TimeSpan.FromMinutes(1));

    // Phase 1: Request Detection
    var requestCoordinator = new RequestCoordinator(operationSink);
    await requestCoordinator.DetectAsync(context, signature, requestId);
    // RequestCoordinator dies, signals persist in operationSink

    // Phase 2: Generate Response
    await _next(context);

    // Phase 3: Response Detection
    var responseCoordinator = new ResponseCoordinator(operationSink);
    var responseSignal = BuildResponseSignal(context, signature, requestId);
    await responseCoordinator.DetectAsync(responseSignal);
    // ResponseCoordinator dies, signals persist in operationSink

    // Phase 4: ESCALATION - Aggregate and send to signature coordinator
    var operationSummary = AggregateOperationSignals(operationSink, signature, requestId);

    // Get signature coordinator (creates if needed, LRU cache)
    var signatureCoordinator = await _signatureCoordinators.GetOrCreateAsync(signature);

    // Escalate!
    await signatureCoordinator.ReceiveOperationAsync(operationSummary);

    // operationSink dies here (scoped using block)
}
```

### Signal Flow

```
T=0ms:   OperationSink created

T=2ms:   RequestCoordinator raises:
         └─► request.path.honeypot = true
         └─► request.ip.datacenter = "AWS"
         └─► request.risk = 0.9

T=5ms:   RequestCoordinator dies
         Signals persist in OperationSink

T=50ms:  ResponseCoordinator reads:
         ├─► Gets: request.path.honeypot (from same sink)
         └─► Gets: request.risk (from same sink)

T=52ms:  ResponseCoordinator raises:
         └─► response.status = 404
         └─► response.score = 0.95

T=55ms:  ResponseCoordinator dies
         All signals in OperationSink

T=56ms:  ESCALATION
         ├─► Aggregate signals from OperationSink
         ├─► Create OperationCompleteSignal
         └─► Send to SignatureCoordinator["abc123"]

T=57ms:  OperationSink dies (scoped)
         Operation tier complete
```

## Signature-Scoped Coordinator

### Purpose

- Maintain cross-request state for a signature
- Run offline analysis (lanes)
- Learn behavior patterns
- Feed back to heuristic

### Its Own Sink

Each signature coordinator has **its own SignalSink**:

```csharp
public class SignatureCoordinator
{
    private readonly string _signature;
    private readonly SignalSink _signatureSink; // OWNED by this coordinator
    private readonly LinkedList<OperationCompleteSignal> _window;

    // Lanes (each can use the signature sink)
    private readonly BehavioralLane _behavioral;
    private readonly SpectralLane _spectral;
    private readonly ReputationLane _reputation;

    public SignatureCoordinator(string signature)
    {
        _signature = signature;

        // This coordinator owns its sink
        _signatureSink = new SignalSink(
            maxCapacity: 10000,
            maxAge: TimeSpan.FromHours(24));

        _window = new LinkedList<OperationCompleteSignal>();

        // Lanes share this sink
        _behavioral = new BehavioralLane(_signatureSink);
        _spectral = new SpectralLane(_signatureSink);
        _reputation = new ReputationLane(_signatureSink);
    }

    public async Task ReceiveOperationAsync(OperationCompleteSignal operation)
    {
        // Add to window
        _window.AddLast(operation);
        while (_window.Count > 100)
            _window.RemoveFirst();

        // Emit to own sink (for lanes to consume)
        _signatureSink.Raise("operation.added", operation);

        // Run lanes in parallel (offline work)
        var laneTasks = new[]
        {
            _behavioral.AnalyzeAsync(_window),
            _spectral.AnalyzeAsync(_window),
            _reputation.AnalyzeAsync(_window)
        };

        await Task.WhenAll(laneTasks);
        // Each lane emits to _signatureSink

        // Aggregate lane signals from signature sink
        var behavior = AggregateLaneSignals(_signatureSink);

        // Emit final behavior (for heuristic feedback)
        _signatureSink.Raise("signature.behavior", behavior);

        // Send to heuristic detector for next request
        await UpdateHeuristicAsync(behavior);
    }

    // When LRU evicts this coordinator:
    public void Dispose()
    {
        _signatureSink.Dispose(); // Sink dies with coordinator
        // Window freed
        // Lanes freed
    }
}
```

### Why Own Sink?

Each signature coordinator has its own sink because:

1. **Isolation**: Signals for signature "abc123" don't pollute signals for "xyz789"
2. **Lifetime**: Sink dies when coordinator evicted (clean ephemeral semantics)
3. **Lane Communication**: Lanes communicate via shared sink (not direct coupling)
4. **Scalability**: Each signature is independent (can parallelize across signatures)

## Escalation Points

### Point 1: Request Complete (Optional Early Escalation)

```csharp
// In RequestCoordinator
public async Task DetectAsync(HttpContext context, string signature, string requestId)
{
    // Detect...
    _operationSink.Raise("request.risk", 0.9);
    _operationSink.Raise("request.honeypot", true);

    // OPTIONAL: Early escalation for high-risk requests
    if (risk > 0.8)
    {
        var earlySignal = new RequestCompleteSignal
        {
            Signature = signature,
            RequestId = requestId,
            Risk = risk,
            Honeypot = true
        };

        // Get signature coordinator
        var sigCoordinator = await _signatureCoordinators.GetOrCreateAsync(signature);

        // Early escalation (before response even generated)
        await sigCoordinator.ReceiveRequestAsync(earlySignal);

        // Signature coordinator can update heuristic immediately
        // Next request from this signature gets boosted score
    }
}
```

**Use Case**: Honeypot hits need immediate escalation (don't wait for response)

### Point 2: Operation Complete (Standard Escalation)

```csharp
// After response sent
var operationSummary = new OperationCompleteSignal
{
    Signature = signature,
    RequestId = requestId,
    Timestamp = DateTimeOffset.UtcNow,

    // From operation sink
    RequestRisk = operationSink.GetLatest<double>("request.risk"),
    ResponseScore = operationSink.GetLatest<double>("response.score"),
    StatusCode = operationSink.GetLatest<int>("response.status"),
    Path = operationSink.GetLatest<string>("request.path"),

    // Aggregate trigger signals
    TriggerSignals = operationSink.Sense("request.*")
        .Concat(operationSink.Sense("response.*"))
        .ToDictionary(e => e.Key, e => e.Payload)
};

// Escalate to signature coordinator
var sigCoordinator = await _signatureCoordinators.GetOrCreateAsync(signature);
await sigCoordinator.ReceiveOperationAsync(operationSummary);

// Operation sink dies here
```

## Lane Example: Behavioral Lane

```csharp
public class BehavioralLane
{
    private readonly SignalSink _signatureSink; // Shared with other lanes

    public BehavioralLane(SignalSink signatureSink)
    {
        _signatureSink = signatureSink;
    }

    public async Task AnalyzeAsync(IReadOnlyList<OperationCompleteSignal> window)
    {
        // Compute timing entropy
        var intervals = ComputeIntervals(window);
        var entropy = ComputeEntropy(intervals);

        // Emit to signature sink (other lanes can read this)
        _signatureSink.Raise("behavioral.entropy", entropy);

        // Compute path diversity
        var pathDiversity = ComputePathDiversity(window);
        _signatureSink.Raise("behavioral.path_diversity", pathDiversity);

        // Detect scan patterns
        var isScan = DetectScanPattern(window);
        _signatureSink.Raise("behavioral.is_scan", isScan);

        // Score
        var behavioralScore = ComputeBehavioralScore(entropy, pathDiversity, isScan);
        _signatureSink.Raise("behavioral.score", behavioralScore);

        return Task.CompletedTask;
    }
}
```

## Signature Coordinator Cache (LRU)

```csharp
public class SignatureCoordinatorCache
{
    private readonly SlidingCacheAtom<string, SignatureCoordinator> _cache;

    public SignatureCoordinatorCache()
    {
        _cache = new SlidingCacheAtom<string, SignatureCoordinator>(
            factory: async (signature, ct) =>
            {
                // Create NEW coordinator with its own sink
                return new SignatureCoordinator(signature);
            },
            slidingExpiration: TimeSpan.FromMinutes(30),
            maxSize: 5000,  // Track 5000 most active signatures
            maxConcurrency: Environment.ProcessorCount,
            sampleRate: 10,
            signals: null); // No external signal needed
    }

    public async Task<SignatureCoordinator> GetOrCreateAsync(string signature)
    {
        return await _cache.GetOrComputeAsync(signature);
    }

    // When signature evicted:
    // - SignatureCoordinator disposed
    // - Its SignatureSink disposed
    // - Window freed
    // - Lanes freed
    // Clean ephemeral semantics!
}
```

## YARP Compatibility

This design works with YARP because **signals are data, not HttpContext**:

```csharp
// In YARP proxy
public class BotDetectionTransform : IHttpTransform
{
    public async Task ApplyAsync(HttpTransformerContext context)
    {
        var signature = GetClientHash(context.HttpContext);
        var requestId = Guid.NewGuid().ToString();

        // Create operation sink (works in YARP)
        var operationSink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));

        // Run request detection
        var requestCoordinator = new RequestCoordinator(operationSink);
        await requestCoordinator.DetectAsync(context.HttpContext, signature, requestId);

        // YARP forwards request to backend...

        // After backend responds:
        var responseSignal = BuildResponseSignal(context.HttpContext, signature, requestId);
        var responseCoordinator = new ResponseCoordinator(operationSink);
        await responseCoordinator.DetectAsync(responseSignal);

        // Escalate to signature coordinator
        var operationSummary = AggregateOperationSignals(operationSink, signature, requestId);
        var sigCoordinator = await _signatureCoordinators.GetOrCreateAsync(signature);
        await sigCoordinator.ReceiveOperationAsync(operationSummary);

        // operationSink disposes
    }
}
```

**Key**: Signals are plain data (POCOs), not HttpContext references. They can be serialized, logged, forwarded, etc.

## Summary

| Tier          | Sink Scope            | Coordinator Lifetime    | Purpose                         |
|---------------|-----------------------|-------------------------|---------------------------------|
| **Operation** | Per-request (scoped)  | Dies when response sent | Request + Response coordination |
| **Signature** | Per-signature (owned) | Dies when LRU evicts    | Cross-request learning, lanes   |

**Escalation**: Operation → Signature via `OperationCompleteSignal` keyed by signature

**State Carrier**: Signature hash carries state from ephemeral operation context to long-lived signature coordinator

**YARP Compatible**: Signals are data, not HttpContext (works across proxy boundaries)

**Ephemeral**: When signature evicted, its coordinator + sink + lanes all die cleanly

**Auto-Specialization**: LRU keeps active (problematic) signatures, evicts inactive ones
