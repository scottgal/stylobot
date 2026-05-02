# Coordinator Architecture: Fire-and-Forget + LFU Design

**Goal:** Fix four architectural drifts that inflate `http_req_duration` and cause unbounded memory growth in coordinator infrastructure.

**Context:** After fixing HNSW rebuild-under-lock (gateway_processing_ms p95 now 1.3ms), the remaining p95 failure is `http_req_duration p(95) > 500ms` (measured 1817ms). Root cause: three `OnCompleted` callbacks in `BotDetectionMiddleware` are `async` lambdas that `await` coordinator work; Kestrel holds keep-alive connections open until callbacks complete; k6 reuses connections so each request pays the previous request's callback cost.

---

## Problems

### 1. OnCompleted callbacks are blocking

Three locations in `BotDetectionMiddleware.cs` register `async` `OnCompleted` callbacks:
- Line 237: upstream trust path
- Line 304: main detection path
- Test mode handlers

All three `await RecordResponseAsync(context, ...)` which itself `await coordinator.RecordResponseAsync(signal, ...)`. Kestrel awaits every `OnCompleted` callback before releasing the keep-alive connection slot. Result: p95 = 1817ms.

There are two correctness issues beyond the latency:
- `RecordResponseAsync` calls `context.RequestServices.GetService<IEnumerable<IContributingDetector>>()` inside the callback. The DI scope is tearing down at that point.
- `AuditProcessorDispatcher.DispatchAsync(context, ...)` accepts a live `HttpContext` reference inside a fire-and-forget callback. HttpContext is pooled/recycled after response completion.

### 2. AuditProcessorDispatcher not fire-and-forget ready

`BuildContext` is `private`. `DispatchAsync` takes a live `HttpContext`. `AuditProcessingContext` has `required HttpContext HttpContext`. Processors downstream do not actually use `context.HttpContext` (confirmed in `ErrorSignalAuditProcessor`), but the `required` reference forces a live context to be passed.

`BuildContext` reads `httpContext.Response.StatusCode` which is NOT finalized until `OnCompleted` fires, so it cannot be called entirely before the callback. It must be called partially before (all except StatusCode) and the StatusCode fixed up synchronously inside the callback.

### 3. SlidingCacheAtom uses LRU eviction

`ResponseCoordinator._clientCache` and `SignatureCoordinator` caches use LRU. Active bots with burst-pause-burst patterns get evicted during the pause, forcing cold-path DB reads on next burst. LFU keeps frequently-touched entries (active bots) warm through access pauses.

### 4. Per-instance signal sinks in SignatureResponseCoordinator

Each `SignatureResponseCoordinator` instance owns `new SignalSink(10000, 24h)`. At 1000 cached signatures, that is 1000 separate signal windows with 24-hour TTL. Memory grows linearly with unique signatures and never compacts. Signal sinks must be shared at the escalator level.

---

## Design

### Rule 1: OnCompleted is a capture-and-enqueue zone. No awaiting. No DI access.

All values needed by coordinators are captured synchronously BEFORE `OnCompleted` is registered. Inside the callback: synchronous reads of finalized response properties (StatusCode, ContentLength - safe after body flush), build immutable snapshot, fire-and-forget enqueue, return `Task.CompletedTask`.

The private `RecordResponseAsync(HttpContext, AggregatedEvidence, ...)` method becomes a synchronous `BuildResponseSignal(...)` helper. Async work stays inside the coordinator's `KeyedSequentialAtom` pipeline (which is already correct).

**What to pre-capture before registering the callback:**
- `clientId` - computed from IP + UA hash (sync)
- `requestId` - `context.TraceIdentifier`
- `path` - `context.Request.Path.Value`
- `method` - `context.Request.Method`
- `requestBotProbability` - `evidence.BotProbability`
- `action` - `evidence.PolicyAction`
- `responseSig` - `context.Items["BotDetection:Signature"] as string`
- `waveform` - `BehavioralWaveformContributor` resolved from DI BEFORE callback
- `auditCtx` - `auditProcessorDispatcher.BuildContext(context, evidence)` BEFORE callback (StatusCode gets fixed up synchronously inside callback via `with` record copy)
- `retryAfter` - parsed from `Retry-After` response header (already set before body flush)

**Inside the callback (all synchronous + fire-and-forget):**
```csharp
context.Response.OnCompleted(() =>
{
    var statusCode = context.Response.StatusCode;      // sync read, finalized
    var contentLength = context.Response.ContentLength ?? 0;

    if (action is PolicyAction.Block or PolicyAction.Challenge or PolicyAction.Throttle)
        return Task.CompletedTask;

    var signal = new ResponseSignal { RequestId = requestId, ClientId = clientId,
        StatusCode = statusCode, ResponseBytes = contentLength, Path = path, ... };

    _ = coordinator.RecordResponseAsync(signal, CancellationToken.None);
    waveform?.UpdateResponseContentType(clientId, context.Response.ContentType);  // sync

    if (auditCtx != null)
    {
        var finalCtx = auditCtx with { Metadata = auditCtx.Metadata with { StatusCode = statusCode } };
        _ = auditProcessorDispatcher!.DispatchPrebuiltAsync(finalCtx, CancellationToken.None);
    }

    if (responseSig != null)
    {
        _ = sigCoordinator?.RecordResponseBytesAsync(responseSig, requestId, contentLength);
        if (statusCode >= 400)
            _reactiveTracker?.RecordErrorServed(responseSig, statusCode, path, retryAfter);
    }

    return Task.CompletedTask;
});
```

### Rule 2: AuditProcessorDispatcher exposes pre-build and pre-dispatch

- `BuildContext(HttpContext, AggregatedEvidence)` becomes `public`
- Add `DispatchPrebuiltAsync(AuditProcessingContext, CancellationToken)` that dispatches without touching `HttpContext`
- `AuditProcessingContext.HttpContext` changes from `required HttpContext` to `HttpContext?` (nullable, not required)

The `AuditTraceMetadata.StatusCode` field is already `int?`. The `with` record copy inside the callback sets it after the response is finalized.

### Rule 3: LFU eviction in SlidingCacheAtom

Add `EvictionPolicy` enum (`Lru`, `Lfu`) to `SlidingCacheAtom` in `mostlylucid.ephemeral`. LFU implementation: each cache entry carries a frequency counter incremented on every `GetOrComputeAsync` hit. On eviction (at-capacity insert), the entry with lowest frequency is evicted (ties broken by last-access time). Frequency is NOT decayed on TTL extension - it accumulates over the entry's lifetime.

`ResponseCoordinator` and `SignatureCoordinator` registrations pass `EvictionPolicy.Lfu`.

### Rule 4: Shared signal sink at escalator level

`SignatureResponseCoordinatorCache` (or its owner) creates ONE `SignalSink` sized appropriately for its window. When constructing each `SignatureResponseCoordinator`, it passes the shared sink reference rather than `new SignalSink(10000, 24h)` per instance. Individual coordinator instances still use the shared sink's `Raise` method keyed by their signature ID.

---

## File Map

| File | Change |
|------|--------|
| `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` | 3 OnCompleted registrations refactored to sync capture + fire-and-forget; `RecordResponseAsync` renamed to `BuildResponseSignal` returning `ResponseSignal?`; waveform pre-captured; DI access removed from callback |
| `Mostlylucid.BotDetection/Orchestration/Audit/AuditProcessorDispatcher.cs` | `BuildContext` made `public`; add `DispatchPrebuiltAsync`; remove `HttpContext` parameter from dispatch path |
| `Mostlylucid.BotDetection/Orchestration/Audit/AuditProcessingContext.cs` | `HttpContext HttpContext` becomes `HttpContext? HttpContext` (not required) |
| `mostlylucid.ephemeral` SlidingCacheAtom | Add `EvictionPolicy` enum and LFU path |
| `SignatureResponseCoordinatorCache` (and related escalator owner) | Single shared `SignalSink`; pass to coordinator constructors |

`ResponseCoordinator` internals, `KeyedSequentialAtom` configuration, and `SignatureCoordinator` processing logic are correct as-is. Changes are calling-side only.

---

## What Does Not Change

- `ResponseCoordinator.RecordResponseAsync(ResponseSignal, CancellationToken)` signature - already correct
- `KeyedSequentialAtom` per-key sequential processing configuration - correct
- HNSW similarity search classes - already fixed (rebuild-off-lock)
- Detection pipeline - no changes
- SQLite persistence layer - no changes; coordinators continue to write async via their atom pipelines
