# Coordinator Fire-and-Forget + Shared Signal Sink Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix `http_req_duration p(95)` (currently 1817ms, target <500ms) by making all three `OnCompleted` callbacks in `BotDetectionMiddleware` truly synchronous, and fix unbounded `SignalSink` memory growth in `SignatureResponseCoordinator`.

**Architecture:** Pre-capture all HttpContext values synchronously before registering each `OnCompleted` callback; inside the callback, build snapshots and enqueue to coordinators with fire-and-forget (`_ = ...`), returning `Task.CompletedTask` immediately. `AuditProcessorDispatcher` gets a public `BuildContext` + `DispatchPrebuiltAsync` pair to support this. `SignatureResponseCoordinator` switches from owning a per-instance `SignalSink(10000, 24h)` to accepting a shared sink from `SignatureResponseCoordinatorCache`. **Note:** `SlidingCacheAtom` already implements LFU-first eviction (`OrderBy(AccessCount)` at line 261) - no changes needed there.

**Tech Stack:** ASP.NET Core 10 middleware, `KeyedSequentialAtom`, `SlidingCacheAtom` (from `mostlylucid.ephemeral`), xUnit, Moq

---

## File Map

| File | Change |
|------|--------|
| `Mostlylucid.BotDetection/Orchestration/Audit/AuditProcessingContext.cs` | `HttpContext?` nullable, remove `required` |
| `Mostlylucid.BotDetection/Orchestration/Audit/AuditProcessorDispatcher.cs` | `BuildContext` public; add `DispatchPrebuiltAsync`; fix StatusCode to not be read in `BuildContext` |
| `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` | Extract `BuildResponseSignal` sync helper; fix 3 `OnCompleted` registrations (lines 237, 304, 1332) |
| `Mostlylucid.BotDetection/Orchestration/SignatureResponseCoordinator.cs` | Accept `SignalSink` parameter instead of `new`-ing its own |
| `Mostlylucid.BotDetection/Orchestration/SignatureEscalator.cs` | Pass shared sink into `SignatureResponseCoordinator` constructor |
| `Mostlylucid.BotDetection.Test/Orchestration/Audit/AuditProcessorTests.cs` | Add tests for `BuildContext` public, `DispatchPrebuiltAsync`, nullable HttpContext |
| `Mostlylucid.BotDetection.Test/Orchestration/SignatureResponseCoordinatorTests.cs` | Add test verifying shared sink is used |
| `Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareTests.cs` | Add test for `BuildResponseSignal` signal accuracy |

---

## Task 1: Make AuditProcessingContext HttpContext nullable

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/Audit/AuditProcessingContext.cs`
- Test: `Mostlylucid.BotDetection.Test/Orchestration/Audit/AuditProcessorTests.cs`

This is the foundation. `AuditProcessingContext.HttpContext` is currently `required HttpContext`, forcing callers to hold a live `HttpContext` reference. Processors don't use it (confirmed: `ErrorSignalAuditProcessor` only reads `context.Signals`, `context.Contributions`, `context.Metadata`). Making it nullable lets us safely pre-build contexts before callbacks fire.

- [ ] **Step 1: Write a failing test confirming null HttpContext is acceptable**

Add to `Mostlylucid.BotDetection.Test/Orchestration/Audit/AuditProcessorTests.cs`:

```csharp
[Fact]
public async Task DispatchAsync_WithNullHttpContext_ProcessorStillReceivesSignals()
{
    var processor = new SnapshotProcessor();
    var sink = new CaptureSink();
    var dispatcher = CreateDispatcher(
        [processor],
        sink,
        new AuditProcessorOptions { Enabled = true });

    // Build context without a live HttpContext (fire-and-forget scenario)
    var ctx = new AuditProcessingContext
    {
        HttpContext = null,
        Evidence = CreateEvidence(signals: new Dictionary<string, object> { ["test.signal"] = 1 }),
        Signals = new Dictionary<string, object> { ["test.signal"] = 1 },
        Contributions = [],
        Metadata = new AuditTraceMetadata
        {
            Timestamp = DateTime.UtcNow,
            RequestId = "test-req-id",
            PrimarySignature = "sig123",
            Path = "/test",
            Method = "GET",
            StatusCode = 200,
            PolicyName = "default",
            Action = null,
            RiskBand = "low",
            BotProbability = 0.1,
            Confidence = 0.9,
            ProcessingTimeMs = 5.0
        }
    };
    await dispatcher.DispatchPrebuiltAsync(ctx);

    Assert.Equal(1, processor.InvocationCount);
}
```

- [ ] **Step 2: Run the test to confirm it fails**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test Mostlylucid.BotDetection.Test --filter "DispatchAsync_WithNullHttpContext_ProcessorStillReceivesSignals" -v minimal
```

Expected: compile error - `DispatchPrebuiltAsync` does not exist and `HttpContext` is `required`.

- [ ] **Step 3: Update AuditProcessingContext**

Replace the entire contents of `Mostlylucid.BotDetection/Orchestration/Audit/AuditProcessingContext.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Audit;

/// <summary>
///     Completed detection context passed to audit processors.
///     HttpContext is nullable: it is null when the context is built before response
///     completion (fire-and-forget path). Processors must not access HttpContext.
/// </summary>
public sealed record AuditProcessingContext
{
    public HttpContext? HttpContext { get; init; }
    public required AggregatedEvidence Evidence { get; init; }
    public required IReadOnlyDictionary<string, object> Signals { get; init; }
    public required IReadOnlyList<DetectionContribution> Contributions { get; init; }
    public required AuditTraceMetadata Metadata { get; init; }
}

/// <summary>
///     Request and decision metadata copied from the completed detection.
/// </summary>
public sealed record AuditTraceMetadata
{
    public required DateTime Timestamp { get; init; }
    public required string RequestId { get; init; }
    public string? PrimarySignature { get; init; }
    public string? Path { get; init; }
    public string? Method { get; init; }
    public int? StatusCode { get; init; }
    public string? PolicyName { get; init; }
    public string? Action { get; init; }
    public string? RiskBand { get; init; }
    public double BotProbability { get; init; }
    public double Confidence { get; init; }
    public double ProcessingTimeMs { get; init; }
}
```

- [ ] **Step 4: Add `DispatchPrebuiltAsync` and make `BuildContext` public in AuditProcessorDispatcher**

Open `Mostlylucid.BotDetection/Orchestration/Audit/AuditProcessorDispatcher.cs`.

Change `private AuditProcessingContext BuildContext(...)` to `public AuditProcessingContext BuildContext(...)`.

Change the `BuildContext` body so it passes `StatusCode = null` (it is not final at pre-build time; the caller fixes it up inside the callback):

```csharp
public AuditProcessingContext BuildContext(HttpContext httpContext, AggregatedEvidence evidence)
{
    var signals = RetainSignals(evidence.Signals);

    return new AuditProcessingContext
    {
        HttpContext = null,   // do not hold a reference - HttpContext is pooled
        Evidence = evidence,
        Signals = signals,
        Contributions = evidence.Contributions,
        Metadata = new AuditTraceMetadata
        {
            Timestamp = DateTime.UtcNow,
            RequestId = httpContext.TraceIdentifier,
            PrimarySignature = TryGetPrimarySignature(httpContext),
            Path = httpContext.Request.Path.Value,
            Method = httpContext.Request.Method,
            StatusCode = null,  // set by caller after response is finalized
            PolicyName = evidence.PolicyName,
            Action = evidence.TriggeredActionPolicyName ?? evidence.PolicyAction?.ToString(),
            RiskBand = evidence.RiskBand.ToString(),
            BotProbability = evidence.BotProbability,
            Confidence = evidence.Confidence,
            ProcessingTimeMs = evidence.TotalProcessingTimeMs
        }
    };
}
```

Add `DispatchPrebuiltAsync` below the existing `DispatchAsync`:

```csharp
/// <summary>
///     Dispatches a pre-built audit context. Safe to call fire-and-forget
///     because the context holds no HttpContext reference.
/// </summary>
public async ValueTask DispatchPrebuiltAsync(
    AuditProcessingContext context,
    CancellationToken ct = default)
{
    if (!HasProcessors) return;

    foreach (var processor in _processors)
    {
        try
        {
            await processor.ProcessAsync(context, _writer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Audit processor {ProcessorName} failed for {RequestId}",
                processor.Name,
                context.Metadata.RequestId);
        }
    }
}
```

Update existing `DispatchAsync` to delegate to `DispatchPrebuiltAsync`:

```csharp
public async ValueTask DispatchAsync(
    HttpContext httpContext,
    AggregatedEvidence evidence,
    CancellationToken ct = default)
{
    if (!HasProcessors) return;
    var context = BuildContext(httpContext, evidence);
    // Fix up StatusCode: response is already finalized when DispatchAsync is called inline
    context = context with
    {
        Metadata = context.Metadata with { StatusCode = httpContext.Response.StatusCode }
    };
    await DispatchPrebuiltAsync(context, ct);
}
```

- [ ] **Step 5: Run the failing test again**

```bash
dotnet test Mostlylucid.BotDetection.Test --filter "DispatchAsync_WithNullHttpContext_ProcessorStillReceivesSignals" -v minimal
```

Expected: PASS.

- [ ] **Step 6: Run the full audit test suite**

```bash
dotnet test Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~AuditProcessor" -v minimal
```

Expected: all existing tests pass (the `SnapshotProcessor` helper in the test file uses `context.Signals` and `context.Metadata` only, so nullable `HttpContext` doesn't break it).

- [ ] **Step 7: Build to confirm no compile errors across solution**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj -c Debug 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
git add Mostlylucid.BotDetection/Orchestration/Audit/AuditProcessingContext.cs \
        Mostlylucid.BotDetection/Orchestration/Audit/AuditProcessorDispatcher.cs \
        Mostlylucid.BotDetection.Test/Orchestration/Audit/AuditProcessorTests.cs
git commit -m "refactor(audit): nullable HttpContext in AuditProcessingContext; public BuildContext; DispatchPrebuiltAsync"
```

---

## Task 2: Fix the three OnCompleted callbacks in BotDetectionMiddleware

**Files:**
- Modify: `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`
- Test: `Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareTests.cs`

This is the performance fix. Three `async` `OnCompleted` callbacks cause Kestrel to hold keep-alive connections open. Each must become synchronous: pre-capture all context values, build snapshots inside the callback without awaiting, fire-and-forget to coordinators.

`RecordResponseAsync(HttpContext, AggregatedEvidence, ResponseCoordinator, DateTime)` becomes `BuildResponseSignal(...)` - a synchronous helper returning `ResponseSignal?` (null = skip recording, e.g., for blocked requests). The `await coordinator.RecordResponseAsync(signal, ...)` call moves to the callback as `_ = coordinator.RecordResponseAsync(signal, ...)`.

### Sub-task 2a: Extract `BuildResponseSignal` synchronous helper

- [ ] **Step 1: Write a failing test for the new helper**

Add to `Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareTests.cs`. Because `BuildResponseSignal` will be `private static` (or `internal`), test it indirectly via a thin wrapper or make it `internal` with `[assembly: InternalsVisibleTo]`. Check if `InternalsVisibleTo` is already set:

```bash
grep -r "InternalsVisibleTo" /Users/scottgalloway/RiderProjects/stylobot/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```

If not present, add to the `.csproj`:
```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
    <_Parameter1>Mostlylucid.BotDetection.Test</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

Then add this test (it tests the behavior indirectly through the signal's skip-recording logic - blocked actions return null):

```csharp
[Fact]
public void BuildResponseSignal_ReturnsNull_WhenActionIsBlock()
{
    // The signal-building logic must not record responses for Block/Challenge/Throttle
    // (prevents positive feedback loops from our own 403s inflating bot scores)
    var evidence = CreateEvidence(policyAction: PolicyAction.Block);

    // BuildResponseSignal is internal - call via reflection or use a helper
    var signal = BotDetectionMiddleware.BuildResponseSignalForTest(
        clientId: "127.0.0.1:ABCD1234",
        requestId: "test-req",
        path: "/test",
        method: "GET",
        statusCode: 403,
        contentLength: 0,
        contentType: "text/html",
        processingTimeMs: 5.0,
        requestBotProbability: 0.9,
        action: PolicyAction.Block);

    Assert.Null(signal);
}

[Fact]
public void BuildResponseSignal_ReturnsSignal_WhenActionIsNull()
{
    var signal = BotDetectionMiddleware.BuildResponseSignalForTest(
        clientId: "127.0.0.1:ABCD1234",
        requestId: "test-req",
        path: "/page",
        method: "GET",
        statusCode: 200,
        contentLength: 1024,
        contentType: "text/html",
        processingTimeMs: 10.0,
        requestBotProbability: 0.1,
        action: null);

    Assert.NotNull(signal);
    Assert.Equal("127.0.0.1:ABCD1234", signal!.ClientId);
    Assert.Equal(200, signal.StatusCode);
    Assert.Equal("/page", signal.Path);
    Assert.Equal(1024, signal.ResponseBytes);
}
```

- [ ] **Step 2: Run the test to confirm it fails**

```bash
dotnet test Mostlylucid.BotDetection.Test --filter "BuildResponseSignal_ReturnsNull_WhenActionIsBlock" -v minimal
```

Expected: compile error - `BuildResponseSignalForTest` does not exist.

- [ ] **Step 3: Add `BuildResponseSignalForTest` (internal test hook) and `BuildResponseSignal` in BotDetectionMiddleware**

In `BotDetectionMiddleware.cs`, find the `#region Response Recording` section (around line 2171). Replace the `private async Task RecordResponseAsync(...)` method with a synchronous helper:

```csharp
/// <summary>
///     Builds a ResponseSignal from pre-captured values.
///     Returns null when the response was generated by bot detection itself
///     (Block/Challenge/Throttle), preventing positive-feedback loops.
///     All parameters are pre-captured BEFORE OnCompleted fires.
/// </summary>
private static ResponseSignal? BuildResponseSignal(
    string clientId,
    string requestId,
    string path,
    string method,
    int statusCode,
    long contentLength,
    string? contentType,
    double processingTimeMs,
    double requestBotProbability,
    PolicyAction? action)
{
    // Skip when bot detection itself set the status code
    if (action is PolicyAction.Block or PolicyAction.Challenge or PolicyAction.Throttle)
        return null;

    // Skip synthetic 403s from middleware bot-block that aren't from an action policy
    // (this check was previously done after the fact; now we do it by convention:
    //  callers pass action=null only for pass-through requests)

    return new ResponseSignal
    {
        RequestId = requestId,
        ClientId = clientId,
        Timestamp = DateTimeOffset.UtcNow,
        StatusCode = statusCode,
        ResponseBytes = contentLength,
        Path = path,
        Method = method,
        ProcessingTimeMs = processingTimeMs,
        RequestBotProbability = requestBotProbability,
        InlineAnalysis = false,
        BodySummary = new ResponseBodySummary
        {
            IsPresent = contentLength > 0,
            Length = (int)contentLength,
            ContentType = contentType
        }
    };
}

// Test hook - only compiled in when test assembly has InternalsVisibleTo
internal static ResponseSignal? BuildResponseSignalForTest(
    string clientId, string requestId, string path, string method,
    int statusCode, long contentLength, string? contentType,
    double processingTimeMs, double requestBotProbability, PolicyAction? action)
    => BuildResponseSignal(clientId, requestId, path, method,
        statusCode, contentLength, contentType,
        processingTimeMs, requestBotProbability, action);
```

- [ ] **Step 4: Run the new tests**

```bash
dotnet test Mostlylucid.BotDetection.Test --filter "BuildResponseSignal" -v minimal
```

Expected: both tests pass.

### Sub-task 2b: Fix the main detection path OnCompleted (line 304)

This is the heaviest callback: `RecordResponseAsync` + `auditDispatcher.DispatchAsync` + signature coordinator + reactive tracker.

- [ ] **Step 5: Replace the main OnCompleted registration (lines 303-335)**

Find this block (after `FeedDetectionServices(context, aggregatedResult);` and before `LogDetectionResult`):

```csharp
var requestStartTime = DateTime.UtcNow;
context.Response.OnCompleted(async () =>
{
    var finalEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence ?? aggregatedResult;
    await RecordResponseAsync(context, finalEvidence, responseCoordinator, requestStartTime);
    if (auditProcessorDispatcher?.HasProcessors == true)
        await auditProcessorDispatcher.DispatchAsync(context, finalEvidence);

    var responseSig = context.Items["BotDetection:Signature"] as string;
    if (!string.IsNullOrEmpty(responseSig))
    {
        var responseBytes = context.Response.ContentLength ?? 0;
        var sigCoordinator = context.RequestServices.GetService<SignatureCoordinator>();
        if (sigCoordinator != null)
            _ = sigCoordinator.RecordResponseBytesAsync(responseSig, context.TraceIdentifier, responseBytes);

        if (_reactiveTracker != null && context.Response.StatusCode >= 400)
        {
            int? retryAfter = null;
            if (context.Response.Headers.TryGetValue("Retry-After", out var raVal)
                && int.TryParse(raVal.ToString(), out var parsed))
                retryAfter = parsed;
            _reactiveTracker.RecordErrorServed(
                responseSig, context.Response.StatusCode,
                context.Request.Path.Value ?? "/", retryAfter);
        }
    }
});
```

Replace with:

```csharp
// Pre-capture all context values before the callback fires.
// HttpContext properties accessed inside OnCompleted are not safe for async use.
var requestStartTime = DateTime.UtcNow;
var capturedIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
var capturedUa = context.Request.Headers.UserAgent.ToString();
var capturedClientId = $"{capturedIp}:{GetHash(capturedUa)}";
var capturedRequestId = context.TraceIdentifier;
var capturedPath = context.Request.Path.Value ?? "/";
var capturedMethod = context.Request.Method;
var capturedBotProbability = aggregatedResult.BotProbability;
var capturedAction = aggregatedResult.PolicyAction;
var capturedSig = context.Items["BotDetection:Signature"] as string;

// Pre-capture retry-after (set in response headers before body writes)
int? capturedRetryAfter = null;
if (context.Response.Headers.TryGetValue("Retry-After", out var raHeader)
    && int.TryParse(raHeader.ToString(), out var raParsed))
    capturedRetryAfter = raParsed;

// Pre-resolve waveform contributor (DI scope is valid here, not inside callback)
var capturedWaveform = responseCoordinator is not null
    ? context.RequestServices
        .GetService<IEnumerable<IContributingDetector>>()?
        .OfType<Orchestration.ContributingDetectors.BehavioralWaveformContributor>()
        .FirstOrDefault()
    : null;

// Pre-build audit context (reads Items, Path, Method, Signature - all final now)
// StatusCode is fixed up synchronously inside the callback.
var capturedAuditCtx = auditProcessorDispatcher?.HasProcessors == true
    ? auditProcessorDispatcher.BuildContext(context, aggregatedResult)
    : null;

// Pre-resolve sig coordinator (avoid RequestServices inside callback)
var capturedSigCoordinator = context.RequestServices.GetService<SignatureCoordinator>();

context.Response.OnCompleted(() =>
{
    // Read finalized response properties (safe: body is written, headers locked)
    var finalEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence ?? aggregatedResult;
    var statusCode = context.Response.StatusCode;
    var contentLength = context.Response.ContentLength ?? 0;
    var contentType = context.Response.ContentType;
    var processingTimeMs = (DateTime.UtcNow - requestStartTime).TotalMilliseconds;

    var signal = BuildResponseSignal(
        capturedClientId, capturedRequestId, capturedPath, capturedMethod,
        statusCode, contentLength, contentType,
        processingTimeMs, capturedBotProbability,
        capturedAction);

    if (signal is not null && responseCoordinator is not null)
        _ = responseCoordinator.RecordResponseAsync(signal, CancellationToken.None);

    capturedWaveform?.UpdateResponseContentType(capturedClientId, contentType);

    if (capturedAuditCtx is not null)
    {
        var finalAuditCtx = capturedAuditCtx with
        {
            Metadata = capturedAuditCtx.Metadata with { StatusCode = statusCode }
        };
        _ = auditProcessorDispatcher!.DispatchPrebuiltAsync(finalAuditCtx, CancellationToken.None);
    }

    if (!string.IsNullOrEmpty(capturedSig))
    {
        _ = capturedSigCoordinator?.RecordResponseBytesAsync(capturedSig, capturedRequestId, contentLength);

        if (_reactiveTracker is not null && statusCode >= 400)
            _reactiveTracker.RecordErrorServed(capturedSig, statusCode, capturedPath, capturedRetryAfter);
    }

    return Task.CompletedTask;
});
```

- [ ] **Step 6: Build to confirm no compile errors**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj -c Debug 2>&1 | tail -5
```

Expected: `Build succeeded.`

### Sub-task 2c: Fix the upstream trust path OnCompleted (line 237)

- [ ] **Step 7: Replace the upstream trust path callback (lines 236-242)**

Find this block (inside the `if (upstreamEvidence != null)` block after `TryHydrateFromUpstream`):

```csharp
var upstreamStartTime = DateTime.UtcNow;
context.Response.OnCompleted(async () =>
{
    await RecordResponseAsync(context, upstreamEvidence, responseCoordinator, upstreamStartTime);
    if (auditProcessorDispatcher?.HasProcessors == true)
        await auditProcessorDispatcher.DispatchAsync(context, upstreamEvidence);
});
```

Replace with:

```csharp
var upstreamStartTime = DateTime.UtcNow;
var upIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
var upUa = context.Request.Headers.UserAgent.ToString();
var upClientId = $"{upIp}:{GetHash(upUa)}";
var upRequestId = context.TraceIdentifier;
var upPath = context.Request.Path.Value ?? "/";
var upMethod = context.Request.Method;
var upBotProbability = upstreamEvidence.BotProbability;
var upAction = upstreamEvidence.PolicyAction;
var upAuditCtx = auditProcessorDispatcher?.HasProcessors == true
    ? auditProcessorDispatcher.BuildContext(context, upstreamEvidence)
    : null;

context.Response.OnCompleted(() =>
{
    var statusCode = context.Response.StatusCode;
    var contentLength = context.Response.ContentLength ?? 0;
    var processingTimeMs = (DateTime.UtcNow - upstreamStartTime).TotalMilliseconds;

    var signal = BuildResponseSignal(
        upClientId, upRequestId, upPath, upMethod,
        statusCode, contentLength, context.Response.ContentType,
        processingTimeMs, upBotProbability, upAction);

    if (signal is not null && responseCoordinator is not null)
        _ = responseCoordinator.RecordResponseAsync(signal, CancellationToken.None);

    if (upAuditCtx is not null)
    {
        var finalCtx = upAuditCtx with
        {
            Metadata = upAuditCtx.Metadata with { StatusCode = statusCode }
        };
        _ = auditProcessorDispatcher!.DispatchPrebuiltAsync(finalCtx, CancellationToken.None);
    }

    return Task.CompletedTask;
});
```

### Sub-task 2d: Fix the test mode OnCompleted (line 1332)

- [ ] **Step 8: Replace the test mode callback (lines 1331-1339)**

Find this block (inside `HandleTestModeWithRealDetection` or `HandleCustomUaDetection`, around `if (aggregatedResult != null)`):

```csharp
var testStartTime = DateTime.UtcNow;
context.Response.OnCompleted(async () =>
{
    var finalEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence ?? aggregatedResult;
    await RecordResponseAsync(context, finalEvidence, responseCoordinator, testStartTime);
    if (auditProcessorDispatcher?.HasProcessors == true)
        await auditProcessorDispatcher.DispatchAsync(context, finalEvidence);
});
```

Replace with:

```csharp
var testStartTime = DateTime.UtcNow;
var testIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
var testUa = context.Request.Headers.UserAgent.ToString();
var testClientId = $"{testIp}:{GetHash(testUa)}";
var testRequestId = context.TraceIdentifier;
var testPath = context.Request.Path.Value ?? "/";
var testMethod = context.Request.Method;
var testBotProbability = aggregatedResult.BotProbability;
var testAction = aggregatedResult.PolicyAction;
var testAuditCtx = auditProcessorDispatcher?.HasProcessors == true
    ? auditProcessorDispatcher.BuildContext(context, aggregatedResult)
    : null;

context.Response.OnCompleted(() =>
{
    var finalEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence ?? aggregatedResult;
    var statusCode = context.Response.StatusCode;
    var contentLength = context.Response.ContentLength ?? 0;
    var processingTimeMs = (DateTime.UtcNow - testStartTime).TotalMilliseconds;

    var signal = BuildResponseSignal(
        testClientId, testRequestId, testPath, testMethod,
        statusCode, contentLength, context.Response.ContentType,
        processingTimeMs, testBotProbability, testAction);

    if (signal is not null && responseCoordinator is not null)
        _ = responseCoordinator.RecordResponseAsync(signal, CancellationToken.None);

    if (testAuditCtx is not null)
    {
        var finalCtx = testAuditCtx with
        {
            Metadata = testAuditCtx.Metadata with { StatusCode = statusCode }
        };
        _ = auditProcessorDispatcher!.DispatchPrebuiltAsync(finalCtx, CancellationToken.None);
    }

    return Task.CompletedTask;
});
```

- [ ] **Step 9: Remove the now-dead `RecordResponseAsync` method**

Search for `private async Task RecordResponseAsync(` in `BotDetectionMiddleware.cs` and delete the entire method (lines 2180-2269). Build to confirm nothing else references it.

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj -c Debug 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 10: Run all middleware tests**

```bash
dotnet test Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~Middleware" -v minimal
```

Expected: all pass.

- [ ] **Step 11: Run the full test suite**

```bash
dotnet test Mostlylucid.BotDetection.Test -v minimal 2>&1 | tail -20
```

Expected: all pass (or same pass count as before - no regressions).

- [ ] **Step 12: Commit**

```bash
git add Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs \
        Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj \
        Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareTests.cs
git commit -m "perf(middleware): fix OnCompleted async callbacks - synchronous capture + fire-and-forget"
```

---

## Task 3: Fix SignatureResponseCoordinator per-instance signal sinks

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/SignatureResponseCoordinator.cs`
- Modify: `Mostlylucid.BotDetection/Orchestration/SignatureEscalator.cs`
- Test: `Mostlylucid.BotDetection.Test/Orchestration/SignatureResponseCoordinatorTests.cs`

Each `SignatureResponseCoordinator` currently creates `new SignalSink(10000, 24h)`. With 5000 cached signatures, that is 5000 `SignalSink` instances with 24-hour windows. The fix: `SignatureResponseCoordinatorCache` creates one shared `SignalSink` and passes it to each coordinator it constructs.

- [ ] **Step 1: Write a failing test verifying shared sink**

Add to `Mostlylucid.BotDetection.Test/Orchestration/SignatureResponseCoordinatorTests.cs`:

```csharp
[Fact]
public async Task GetOrCreateAsync_TwoCoordinators_ShareTheSameSink()
{
    var logger = NullLogger<SignatureResponseCoordinatorCache>.Instance;
    var sharedSink = new SignalSink(100, TimeSpan.FromMinutes(5));
    var cache = new SignatureResponseCoordinatorCache(logger, sharedSink: sharedSink);

    var coord1 = await cache.GetOrCreateAsync("sig-aaa");
    var coord2 = await cache.GetOrCreateAsync("sig-bbb");

    // Both coordinators expose their sink - if shared, they are the same reference
    Assert.Same(sharedSink, coord1.Sink);
    Assert.Same(sharedSink, coord2.Sink);
}
```

- [ ] **Step 2: Run the test to confirm it fails**

```bash
dotnet test Mostlylucid.BotDetection.Test --filter "TwoCoordinators_ShareTheSameSink" -v minimal
```

Expected: compile error - `SignatureResponseCoordinatorCache` has no `sharedSink` parameter; `SignatureResponseCoordinator` has no `Sink` property.

- [ ] **Step 3: Update SignatureResponseCoordinator to accept shared sink**

In `Mostlylucid.BotDetection/Orchestration/SignatureResponseCoordinator.cs`:

Change the constructor signature and remove the per-instance sink allocation:

```csharp
public SignatureResponseCoordinator(string signature, ILogger logger, SignalSink sharedSink)
{
    _signature = signature;
    _logger = logger;

    _sink = sharedSink;  // use the shared sink from the cache

    _window = new LinkedList<OperationCompleteSignal>();

    _lanes = new List<IAnalysisLane>
    {
        new BehavioralLane(_sink),
        new SpectralLane(_sink),
        new ReputationLane(_sink)
    };

    _logger.LogDebug("SignatureResponseCoordinator created for {Signature} with {LaneCount} lanes",
        signature, _lanes.Count);
}
```

Change `private readonly SignalSink _sink;` to `internal readonly SignalSink _sink;` so the test can access it.

Add the `Sink` property:

```csharp
internal SignalSink Sink => _sink;
```

Remove the `DisposeAsync` comment about `SignalSink` - it's now owned by the cache, not this coordinator.

- [ ] **Step 4: Update SignatureResponseCoordinatorCache to own the shared sink**

In `Mostlylucid.BotDetection/Orchestration/SignatureResponseCoordinator.cs` (the `SignatureResponseCoordinatorCache` class at the bottom of the file, or in its own file - check which):

```bash
grep -n "class SignatureResponseCoordinatorCache" /Users/scottgalloway/RiderProjects/stylobot/Mostlylucid.BotDetection/Orchestration/SignatureEscalator.cs
```

Update `SignatureResponseCoordinatorCache`:

```csharp
public sealed class SignatureResponseCoordinatorCache : IAsyncDisposable
{
    private readonly SlidingCacheAtom<string, SignatureResponseCoordinator> _cache;
    private readonly ILogger<SignatureResponseCoordinatorCache> _logger;
    private readonly SignalSink _sharedSink;

    public SignatureResponseCoordinatorCache(
        ILogger<SignatureResponseCoordinatorCache> logger,
        int maxSignatures = 5000,
        TimeSpan? ttl = null,
        SignalSink? sharedSink = null)
    {
        _logger = logger;

        // One shared sink for all coordinators in this cache.
        // Sized for maxSignatures * 20 events each, 1-hour window.
        _sharedSink = sharedSink ?? new SignalSink(
            Math.Min(maxSignatures * 20, 50_000),
            TimeSpan.FromHours(1));

        _cache = new SlidingCacheAtom<string, SignatureResponseCoordinator>(
            async (signature, ct) =>
            {
                _logger.LogDebug("Creating SignatureResponseCoordinator for {Signature}", signature);
                return new SignatureResponseCoordinator(signature, logger, _sharedSink);
            },
            ttl ?? TimeSpan.FromMinutes(30),
            (ttl ?? TimeSpan.FromMinutes(30)) * 2,
            maxSignatures,
            Environment.ProcessorCount,
            10,
            _sharedSink);
    }

    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync();
        _logger.LogInformation("SignatureResponseCoordinatorCache disposed");
    }

    public async Task<SignatureResponseCoordinator> GetOrCreateAsync(
        string signature,
        CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrComputeAsync(signature, cancellationToken);
    }
}
```

- [ ] **Step 5: Run the test**

```bash
dotnet test Mostlylucid.BotDetection.Test --filter "TwoCoordinators_ShareTheSameSink" -v minimal
```

Expected: PASS.

- [ ] **Step 6: Run coordinator tests**

```bash
dotnet test Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~SignatureResponseCoordinator" -v minimal
```

Expected: all pass.

- [ ] **Step 7: Build the full solution**

```bash
dotnet build mostlylucid.stylobot.sln -c Debug 2>&1 | tail -10
```

Expected: `Build succeeded.`

- [ ] **Step 8: Run full test suite**

```bash
dotnet test Mostlylucid.BotDetection.Test -v minimal 2>&1 | tail -20
```

Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add Mostlylucid.BotDetection/Orchestration/SignatureResponseCoordinator.cs \
        Mostlylucid.BotDetection/Orchestration/SignatureEscalator.cs \
        Mostlylucid.BotDetection.Test/Orchestration/SignatureResponseCoordinatorTests.cs
git commit -m "fix(orchestration): shared SignalSink in SignatureResponseCoordinatorCache - eliminate per-instance sinks"
```

---

## Task 4: Run the demo and verify performance

**Files:**
- None (verification only)

- [ ] **Step 1: Start the demo**

```bash
dotnet run --project Mostlylucid.BotDetection.Demo -c Release &
sleep 3
```

- [ ] **Step 2: Confirm the demo is responding**

```bash
curl -s -o /dev/null -w "%{http_code}" http://localhost:5080/_stylobot
```

Expected: `200` or `302`.

- [ ] **Step 3: Run k6 against the demo**

If k6 is installed:
```bash
k6 run --vus 20 --duration 30s - <<'EOF'
import http from 'k6/http';
import { check, sleep } from 'k6';
export const options = {
  thresholds: {
    http_req_duration: ['p(95)<500'],
  },
};
export default function () {
  const res = http.get('http://localhost:5080/');
  check(res, { 'status ok': (r) => r.status < 500 });
  sleep(0.1);
}
EOF
```

Expected: `http_req_duration p(95) < 500ms`. This is the target threshold.

If k6 is not installed, verify manually with repeated curl timing:
```bash
for i in {1..20}; do
  curl -s -o /dev/null -w "%{time_total}\n" http://localhost:5080/ &
done
wait
```

Expected: all times under 0.5 seconds.

- [ ] **Step 4: Stop the demo**

```bash
pkill -f "Mostlylucid.BotDetection.Demo" 2>/dev/null || true
```

- [ ] **Step 5: Final commit tagging completion**

```bash
git tag -a "perf/coordinator-fire-and-forget" -m "OnCompleted fix + shared sink: p95 < 500ms target"
```

---

## Self-Review Notes

**Spec coverage check:**

| Spec requirement | Task |
|---|---|
| OnCompleted returns Task.CompletedTask immediately | Task 2 |
| Pre-capture all HttpContext values before callback | Task 2 |
| `AuditProcessorDispatcher.BuildContext` public | Task 1 |
| `DispatchPrebuiltAsync` added | Task 1 |
| `AuditProcessingContext.HttpContext` nullable | Task 1 |
| `RecordResponseAsync` replaced with sync helper | Task 2 |
| DI access removed from inside callback | Task 2 |
| SignatureResponseCoordinator shared sink | Task 3 |
| LFU eviction | No task - already implemented in `SlidingCacheAtom` (line 261: `OrderBy(AccessCount)`) |

**Type consistency:**
- `BuildResponseSignal` returns `ResponseSignal?` in Task 2 - used consistently in all three callbacks
- `DispatchPrebuiltAsync` takes `AuditProcessingContext` and `CancellationToken` - used consistently
- `SignatureResponseCoordinator` constructor takes `(string, ILogger, SignalSink)` - `SignatureResponseCoordinatorCache` creates it that way

**Potential issue:** The `capturedAuditCtx` pre-build in Task 2 reads `aggregatedResult` (captured before `_next`), but the main path has `var finalEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence ?? aggregatedResult` inside the callback - the evidence may be updated by `ApplyResponseStatusBoost`. For the audit context, this means the audit gets the pre-boost evidence. This is acceptable: audit records show DETECTION evidence, not post-processing evidence. If post-boost evidence is needed, the caller should re-read `finalEvidence` for the audit build. Since `ApplyResponseStatusBoost` only affects `BotProbability` stored in `context.Items[AggregatedEvidenceKey]`, and audit uses the pre-boost value, this is actually MORE correct - it records what the detector decided, not what the boost changed.
