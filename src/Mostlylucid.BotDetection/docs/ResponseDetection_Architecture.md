# Response Detection Architecture

## Core Principles

The response detection system follows a **two-tier coordinator architecture**:

1. **Operation-Level**: Short-lived coordinators per HTTP request/response
2. **Signature-Level**: Long-lived coordinators per client signature (cross-request)

These tiers communicate through **signal sinks**, not direct dependencies.

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                          HTTP Request                               │
└────────────────────┬────────────────────────────────────────────────┘
                     │
                     ▼
        ┌────────────────────────────┐
        │  RequestCoordinator        │  (operation-level, dies fast)
        │  Lifetime: request begin   │
        │           → request done    │
        └──────────┬─────────────────┘
                   │ emits: request.* signals
                   ▼
        ┌──────────────────────────────┐
        │  OperationSignalSink         │  (per-request, shared)
        │  - request.path              │
        │  - request.ip.datacenter     │
        │  - request.ua.suspicious     │
        │  - request.risk = 0.72       │
        │  - response.trigger.mode     │  ◄─ ResponseAnalysisContext
        │  - response.trigger.depth    │
        └──────────┬───────────────────┘
                   │ armed by request signals
                   ▼
        ┌────────────────────────────┐
        │  ResponseCoordinator       │  (operation-level, queued by signals)
        │  Lifetime: response start  │
        │           → response sent  │
        │  Mode: BLOCKING or ASYNC   │  ◄─ determined VERY early
        └──────────┬─────────────────┘
                   │ emits: response.* signals
                   │ - response.status = 404
                   │ - response.pattern = honeypot
                   │ - response.score = 0.85
                   ▼
        ┌──────────────────────────────┐
        │  OperationSignalSink         │  (same sink, shared)
        └──────────┬───────────────────┘
                   │ end-of-operation
                   ▼
        ┌──────────────────────────────┐
        │  OperationSummarySignal      │  (aggregated)
        │  {                           │
        │    signature: "abc123",      │
        │    requestScore: 0.72,       │
        │    responseScore: 0.85,      │
        │    path: "/.git/config",     │
        │    status: 404,              │
        │    processingMs: 45          │
        │  }                           │
        └──────────┬───────────────────┘
                   │ relayed to global
                   ▼
        ┌──────────────────────────────┐
        │  GlobalSignalSink            │  (process-level, persistent)
        │  (all operations, all sigs)  │
        └──────────┬───────────────────┘
                   │ filtered by signature
                   ▼
        ┌────────────────────────────────────────┐
        │  SignatureProfileAtom[signature]       │  (keyed by signature)
        │  Maintains window: last 100 ops        │
        │  Lanes:                                │
        │   ├─ Behavioral Lane (entropy, timing) │
        │   ├─ Spectral Lane (FFT, periodicity)  │
        │   ├─ Reputation Lane (history)         │
        │   └─ Content Lane (response patterns)  │
        └────────────┬───────────────────────────┘
                     │ emits
                     ▼
        ┌──────────────────────────────┐
        │  SignatureBehaviorSignal     │
        │  {                           │
        │    signature: "abc123",      │
        │    aberrationScore: 0.92,    │
        │    waveform: "scan",         │
        │    recommendation: "block"   │
        │  }                           │
        └──────────────────────────────┘
                     │
                     ▼ feeds back to heuristic
        ┌──────────────────────────────┐
        │  Next Request from signature │
        │  → starts with prior = 0.92  │
        └──────────────────────────────┘
```

## Two Coordinator Tiers

### Tier 1: Operation-Level Coordinators

**Characteristics**:

- **Lifetime**: Single HTTP request/response cycle
- **Scope**: One operation (request → response)
- **Signal Sink**: `OperationSignalSink` (scoped, dies with operation)
- **Purpose**: Coordinate request-side and response-side detection for THIS operation

#### RequestCoordinator

```csharp
// Short-lived, dies as soon as request detection completes
public class RequestCoordinator
{
    private readonly OperationSignalSink _operationSink; // shared with ResponseCoordinator

    public async Task DetectAsync(HttpContext context)
    {
        // Run request-side detectors
        // Emit signals: request.ip.datacenter, request.ua.suspicious, etc.

        // Create ResponseAnalysisContext (signals to ResponseCoordinator)
        var responseContext = new ResponseAnalysisContext
        {
            Mode = isHoneypot ? Blocking : Async, // DECIDED EARLY
            Thoroughness = riskScore > 0.8 ? Deep : Standard
        };

        context.Items[ResponseAnalysisContext.Key] = responseContext;

        // Emit trigger signal
        _operationSink.Raise("response.trigger", new
        {
            Mode = responseContext.Mode,
            Thoroughness = responseContext.Thoroughness
        });

        // DIE - request coordinator is done
    }
}
```

#### ResponseCoordinator

```csharp
// Short-lived, queued by signals from RequestCoordinator
public class ResponseCoordinator
{
    private readonly OperationSignalSink _operationSink; // SAME sink as RequestCoordinator

    public async Task DetectAsync(HttpContext context, ResponseSignal signal)
    {
        // Read ResponseAnalysisContext (prepared by RequestCoordinator)
        var analysisContext = context.Items[ResponseAnalysisContext.Key];

        // Mode already decided (BLOCKING or ASYNC)
        if (analysisContext.Mode == Blocking)
        {
            // INLINE: analyze before sending response
            await AnalyzeInlineAsync(signal);
        }
        else
        {
            // ASYNC: queue for background processing
            _ = Task.Run(() => AnalyzeAsyncAsync(signal));
        }

        // Emit response signals to SAME operation sink
        _operationSink.Raise("response.status", signal.StatusCode);
        _operationSink.Raise("response.score", computedScore);

        // DIE - response coordinator is done
    }
}
```

**Key Point**: Request and response coordinators are **separate objects** with **separate lifecycles** but share the *
*same OperationSignalSink**.

### Tier 2: Signature-Level Coordinators

**Characteristics**:

- **Lifetime**: Minutes/hours (bounded by window)
- **Scope**: All operations for a signature (cross-request)
- **Signal Sink**: `GlobalSignalSink` (process-level, persistent)
- **Purpose**: Maintain rolling window of operations per signature, detect patterns

#### SignatureProfileAtom

```csharp
// Long-lived, keyed by signature hash
public class SignatureProfileAtom
{
    private readonly string _signature;
    private readonly LinkedList<OperationSummary> _operationWindow; // last 100 ops
    private readonly GlobalSignalSink _globalSink;

    // Lanes (parallel analysis streams)
    private readonly BehavioralLane _behavioralLane;
    private readonly SpectralLane _spectralLane;
    private readonly ReputationLane _reputationLane;
    private readonly ContentLane _contentLane;

    public async Task RecordOperationAsync(OperationSummarySignal summary)
    {
        // Add to window
        _operationWindow.AddLast(summary);

        // Evict old operations
        while (_operationWindow.Count > 100)
            _operationWindow.RemoveFirst();

        // Run lanes in parallel (each lane analyzes the window)
        var laneTasks = new[]
        {
            _behavioralLane.AnalyzeAsync(_operationWindow),
            _spectralLane.AnalyzeAsync(_operationWindow),
            _reputationLane.AnalyzeAsync(_operationWindow),
            _contentLane.AnalyzeAsync(_operationWindow)
        };

        await Task.WhenAll(laneTasks);

        // Aggregate lane results into signature behavior
        var behavior = Aggregatelanes();

        // Emit to global sink
        _globalSink.Raise("signature.behavior", new SignatureBehaviorSignal
        {
            Signature = _signature,
            AberrationScore = behavior.AberrationScore,
            Waveform = behavior.Waveform,
            Recommendation = behavior.Recommendation
        });
    }
}
```

**Key Point**: SignatureProfileAtom is **NOT** a per-request coordinator. It's a **per-signature state machine** that
accumulates operation summaries over time.

## Lanes

**Lanes** are parallel analysis streams that share signals but don't share state.

### Operation-Level Lanes

These read from `OperationSignalSink`:

1. **Content Lane**: Pattern matching, semantic analysis
2. **Heuristic Lane**: Quick risk scoring
3. **AI Lane** (Enterprise): LLM-based analysis

All operate on the SAME operation, in parallel, reading the same sink.

### Signature-Level Lanes

These read from `GlobalSignalSink` (filtered by signature):

1. **Behavioral Lane**: Entropy, timing patterns, Markov chains
2. **Spectral Lane**: FFT, periodicity detection
3. **Reputation Lane**: Historical scoring, learned patterns
4. **Content Lane**: Response pattern aggregation

All operate on the SAME signature window, in parallel.

## Signal Flow

### 1. Operation Signals (per-request)

```csharp
// OperationSignalSink (scoped to request)
sink.Raise("request.path", "/admin");
sink.Raise("request.ip.datacenter", "AWS");
sink.Raise("request.risk", 0.72);
sink.Raise("response.trigger.mode", "Blocking");
sink.Raise("response.status", 404);
sink.Raise("response.pattern", "honeypot");
sink.Raise("response.score", 0.85);
```

**Lifetime**: Dies when response completes

### 2. Operation Summary Signal (end-of-operation)

```csharp
// Aggregated at end of operation, sent to GlobalSignalSink
var summary = new OperationSummarySignal
{
    Signature = clientHash,
    RequestScore = 0.72,
    ResponseScore = 0.85,
    Path = "/.git/config",
    StatusCode = 404,
    ProcessingMs = 45,
    Timestamp = DateTime.UtcNow
};

globalSink.Raise("operation.complete", summary);
```

**Lifetime**: Persists in GlobalSignalSink

### 3. Signature Behavior Signal (cross-request)

```csharp
// Emitted by SignatureProfileAtom after analyzing window
var behavior = new SignatureBehaviorSignal
{
    Signature = "abc123",
    AberrationScore = 0.92,
    Waveform = "scan", // scan, burst, steady, etc.
    Recommendation = "block",
    Window = new
    {
        OperationCount = 47,
        Unique404Paths = 23,
        HoneypotHits = 3
    }
};

globalSink.Raise("signature.behavior", behavior);
```

**Lifetime**: Persists until signature evicted from cache

## Blocking vs Async Decision (VERY EARLY)

The key insight: **Mode is decided during request detection**, not response detection.

```csharp
// In RequestCoordinator (EARLY)
public async Task DetectAsync(HttpContext context)
{
    // Run first wave detectors (Priority 0-10)
    var isHoneypot = path.StartsWith("/.git/");
    var isDatacenter = await CheckDatacenter(ip);
    var riskScore = ComputeEarlyRisk();

    // DECIDE MODE NOW (not later)
    var mode = isHoneypot ? Blocking : Async;
    var thoroughness = riskScore > 0.8 ? Deep : Standard;

    // Store decision for ResponseCoordinator
    var analysisContext = new ResponseAnalysisContext
    {
        Mode = mode,                  // ◄─ DECIDED HERE
        Thoroughness = thoroughness,  // ◄─ DECIDED HERE
        EnableStreaming = mode == Blocking && thoroughness == Deep
    };

    context.Items[ResponseAnalysisContext.Key] = analysisContext;

    // Emit signal
    _operationSink.Raise("response.trigger", new
    {
        Mode = mode,
        Thoroughness = thoroughness,
        DecidedAt = "RequestCoordinator",
        DecisionTimeMs = stopwatch.ElapsedMilliseconds
    });

    // Request coordinator dies here
}
```

Later, when response starts:

```csharp
// In ResponseCoordinator (reads pre-made decision)
public async Task DetectAsync(HttpContext context, ResponseSignal signal)
{
    var analysisContext = context.Items[ResponseAnalysisContext.Key];

    // Mode already decided (NO logic here, just read)
    if (analysisContext.Mode == Blocking)
    {
        // INLINE: block request until analysis completes
        await AnalyzeBlockingAsync(signal, analysisContext);
    }
    else
    {
        // ASYNC: queue for background, return immediately
        _ = QueueAsyncAnalysis(signal, analysisContext);
    }
}
```

**Performance**: Decision made in ~2-5ms (early request detection), not during response generation.

## Example: Honeypot Hit Flow

```
T=0ms: Request arrives: GET /.git/config

T=2ms: RequestCoordinator starts
       ├─> PathDetector (Priority 0)
       │   └─> Detects: honeypot path
       ├─> DECISION: Mode = Blocking, Thoroughness = Deep
       ├─> ResponseAnalysisContext created
       └─> Signal: response.trigger { mode: "Blocking", thoroughness: "Deep" }

T=5ms: RequestCoordinator DIES (fast)

T=10-50ms: Application generates response (404 page)

T=50ms: ResponseCoordinator starts (QUEUED by response.trigger signal)
        ├─> Reads: Mode = Blocking (decided earlier)
        ├─> BLOCKS until analysis completes
        ├─> Runs: StatusCodeDetector, HoneypotDetector, PatternDetector (parallel)
        ├─> Score: 0.95 (honeypot + 404 + datacenter IP)
        └─> Signal: response.score = 0.95

T=55ms: Response sent to client (blocked for 5ms for inline analysis)

T=56ms: ResponseCoordinator DIES

T=57ms: OperationSummarySignal created and sent to GlobalSignalSink
        {
          signature: "abc123",
          requestScore: 0.72,
          responseScore: 0.95,
          path: "/.git/config",
          status: 404
        }

T=58ms: SignatureProfileAtom["abc123"] receives summary
        ├─> Adds to window (now 47 operations for this signature)
        ├─> Runs lanes: Behavioral, Spectral, Reputation, Content
        ├─> Detects: scan pattern (23 unique 404 paths, 3 honeypot hits)
        ├─> AberrationScore: 0.92
        └─> Signal: signature.behavior { recommendation: "block" }

T=60ms: HeuristicDetector updated (feeds back for next request)
        signature["abc123"] = 0.92

---

T=1000ms: Next request from same signature

T=1002ms: HeuristicDetector runs FIRST (Priority 0)
          ├─> Reads: prior score = 0.92 (from signature behavior)
          ├─> Contribution: +0.92 (VERY HIGH)
          └─> Policy: BLOCK immediately (no response needed)

T=1003ms: Request blocked (1ms total latency)
```

## Summary

| Aspect           | Operation-Level                         | Signature-Level                           |
|------------------|-----------------------------------------|-------------------------------------------|
| **Coordinators** | RequestCoordinator, ResponseCoordinator | SignatureProfileAtom                      |
| **Lifetime**     | Per-request (ms-seconds)                | Per-signature (minutes-hours)             |
| **Signal Sink**  | OperationSignalSink (scoped)            | GlobalSignalSink (persistent)             |
| **Purpose**      | Detect THIS request/response            | Detect patterns across requests           |
| **Keyed by**     | Request ID                              | Signature hash                            |
| **Lanes**        | Content, Heuristic, AI                  | Behavioral, Spectral, Reputation, Content |
| **Death**        | When response sent                      | When signature evicted (TTL/LRU)          |
| **Signals**      | Fine-grained (request.*, response.*)    | Aggregated (signature.behavior)           |

**Key Design**: Coordinators share signals, not state. Request/Response coordinators are siblings (same sink), not
parent/child.
