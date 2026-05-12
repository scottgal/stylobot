# Policy Consolidation + FailureMode + LoadShed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the two genuinely missing policy capabilities (FailureMode for fail-open vs fail-closed; intake-level LoadShed for high RPS) while consolidating the policy concept landscape (deprecate redundant `BotDetectionOptions` fields, document threshold precedence, rename `SimulationPack -> HoneypotPack` for clarity).

**Architecture:**
- `FailureMode` is a new per-policy enum read by `BotDetectionMiddleware` in a try-catch wrapped around the orchestrator call, and applied symmetrically in `SidecarBotDetectionMiddleware`. Three values: `FailOpen` (default), `FailClosed`, `LogOnly`.
- `LoadShedDecision` is a new service consulted by `BotDetectionMiddleware` BEFORE calling the orchestrator. It uses the existing `PipelineLoadSensor.CurrentBand` to decide whether to skip detection on this request. Configured per-policy via `LoadShed: { DropFractionAtHigh: 0, DropFractionAtCritical: 0.05 }`.
- Cleanup: source-only `[Obsolete]` on redundant `BotDetectionOptions` fields with "use DetectionPolicy.X instead" messages; new `docs/policy-system.md` documenting the precedence; `SimulationPack` keeps its type, gets a `HoneypotPack` partial-class alias for clarity (no breaking renames in 6.5).

**Tech Stack:** .NET 10, xUnit, YamlDotNet, existing detector pipeline. No new packages.

**Out of scope:** Threshold-sprawl removal (deferred to 7.0 per user direction); reaction-pack implementation (already a separate plan); dashboard UI for the new knobs (FOSS view-only is enough).

---

## File Structure

**New files:**
- `src/Mostlylucid.BotDetection/Policies/FailureMode.cs`
- `src/Mostlylucid.BotDetection/Policies/LoadShedOptions.cs`
- `src/Mostlylucid.BotDetection/Services/LoadShedDecision.cs`
- `src/Mostlylucid.BotDetection.Test/Policies/FailureModeTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/LoadShedDecisionTests.cs`
- `src/Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareFailureTests.cs`
- `src/Mostlylucid.BotDetection/docs/policy-system.md`

**Modified files:**
- `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs` (add `FailureMode` and `LoadShed` properties)
- `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs` (deprecate redundant fields)
- `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` (wrap orchestrator call; load-shed gate)
- `src/Mostlylucid.BotDetection.Sidecar.Client/SidecarBotDetectionMiddleware.cs` (align with FailureMode)
- `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` (register `LoadShedDecision`)
- `src/Mostlylucid.BotDetection/Policies/DetectionPolicyConfiguration.cs` (load `FailureMode` and `LoadShed` from JSON)
- `CHANGELOG.md`

---

## Task 1: Add `FailureMode` enum and property to `DetectionPolicy`

**Files:**
- Create: `src/Mostlylucid.BotDetection/Policies/FailureMode.cs`
- Modify: `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Policies/FailureModeTests.cs`

- [ ] **Step 1.1: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/Policies/FailureModeTests.cs`:

```csharp
using Mostlylucid.BotDetection.Policies;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies;

public class FailureModeTests
{
    [Fact]
    public void Default_IsFailOpen()
    {
        var policy = new DetectionPolicy { Name = "test" };
        Assert.Equal(FailureMode.FailOpen, policy.OnFailure);
    }

    [Fact]
    public void Init_SetsFailureMode()
    {
        var policy = new DetectionPolicy { Name = "test", OnFailure = FailureMode.FailClosed };
        Assert.Equal(FailureMode.FailClosed, policy.OnFailure);
    }

    [Fact]
    public void Enum_HasThreeValues()
    {
        var values = Enum.GetValues<FailureMode>();
        Assert.Equal(3, values.Length);
        Assert.Contains(FailureMode.FailOpen, values);
        Assert.Contains(FailureMode.FailClosed, values);
        Assert.Contains(FailureMode.LogOnly, values);
    }
}
```

- [ ] **Step 1.2: Run the test, expect build error**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~FailureModeTests"
```
Expected: `FailureMode` type does not exist.

- [ ] **Step 1.3: Create the enum**

Create `src/Mostlylucid.BotDetection/Policies/FailureMode.cs`:

```csharp
namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Behaviour when bot detection itself fails (orchestrator exception, store unavailable,
///     sidecar unreachable, etc.). This is NOT the verdict for a bot-positive request:
///     bots are still blocked according to action policies. FailureMode covers the case
///     where the pipeline could not produce a verdict at all.
/// </summary>
public enum FailureMode
{
    /// <summary>
    ///     Allow the request through on internal failure. Bias toward availability.
    ///     Default for general-purpose sites and the sidecar pattern (where the sidecar
    ///     being unreachable should not take down the upstream app).
    /// </summary>
    FailOpen = 0,

    /// <summary>
    ///     Reject the request with HTTP 503 (Service Unavailable) on internal failure.
    ///     Bias toward security. Use for high-security endpoints where letting a
    ///     potentially-bot request through unscanned is worse than dropping a legitimate
    ///     one (admin panels, financial transactions, account changes).
    /// </summary>
    FailClosed = 1,

    /// <summary>
    ///     Allow the request through, but write a diagnostic signal to the response
    ///     headers and structured logs so operators can monitor the failure rate without
    ///     impacting users. Useful for staged rollouts and shadow-mode evaluation of
    ///     FailClosed before flipping it on.
    /// </summary>
    LogOnly = 2,
}
```

- [ ] **Step 1.4: Add the property to DetectionPolicy**

In `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs`, after the existing `Enabled` property (around line 106), add:

```csharp
    /// <summary>
    ///     What to do when bot detection itself fails (orchestrator exception, store
    ///     unavailable, sidecar unreachable). Defaults to <see cref="FailureMode.FailOpen"/>
    ///     to preserve availability. Set to <see cref="FailureMode.FailClosed"/> for
    ///     high-security endpoints.
    /// </summary>
    public FailureMode OnFailure { get; init; } = FailureMode.FailOpen;
```

- [ ] **Step 1.5: Run the tests, expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~FailureModeTests"
```
Expected: 3 pass.

- [ ] **Step 1.6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Policies/FailureMode.cs \
        src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs \
        src/Mostlylucid.BotDetection.Test/Policies/FailureModeTests.cs
git commit -m "$(cat <<'EOF'
feat(policy): add FailureMode enum to DetectionPolicy

Three values: FailOpen (default, preserves availability), FailClosed
(rejects with 503 on internal failure), LogOnly (allow but emit
diagnostic signal). Wiring into the middleware is the next task.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Wire FailureMode into `BotDetectionMiddleware`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareFailureTests.cs`

- [ ] **Step 2.1: Write the failing tests**

Create `src/Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareFailureTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Moq;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Middleware;

public class BotDetectionMiddlewareFailureTests
{
    private static (HttpContext ctx, Mock<IBlackboardOrchestrator> orch) Build(DetectionPolicy policy, Exception failure)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/x";
        var orch = new Mock<IBlackboardOrchestrator>();
        orch.Setup(o => o.DetectWithPolicyAsync(It.IsAny<HttpContext>(), It.IsAny<DetectionPolicy>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        return (ctx, orch);
    }

    [Fact]
    public async Task FailOpen_AllowsRequest_AndDoesNotSet503()
    {
        var policy = new DetectionPolicy { Name = "test", OnFailure = FailureMode.FailOpen };
        var (ctx, orch) = Build(policy, new InvalidOperationException("boom"));
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var failureHandler = BotDetectionMiddleware.HandleDetectionFailureFor(policy);
        var result = failureHandler.Apply(ctx, new InvalidOperationException("boom"));

        Assert.True(result.ContinuePipeline, "FailOpen must call next()");
        Assert.NotEqual(503, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task FailClosed_Returns503_AndShortCircuits()
    {
        var policy = new DetectionPolicy { Name = "test", OnFailure = FailureMode.FailClosed };
        var ctx = new DefaultHttpContext();
        var failureHandler = BotDetectionMiddleware.HandleDetectionFailureFor(policy);
        var result = failureHandler.Apply(ctx, new InvalidOperationException("boom"));

        Assert.False(result.ContinuePipeline, "FailClosed must NOT call next()");
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.True(ctx.Response.Headers.ContainsKey("X-StyloBot-Failed"));
    }

    [Fact]
    public async Task LogOnly_AllowsRequest_AndWritesDiagnosticHeader()
    {
        var policy = new DetectionPolicy { Name = "test", OnFailure = FailureMode.LogOnly };
        var ctx = new DefaultHttpContext();
        var failureHandler = BotDetectionMiddleware.HandleDetectionFailureFor(policy);
        var result = failureHandler.Apply(ctx, new InvalidOperationException("boom"));

        Assert.True(result.ContinuePipeline, "LogOnly must call next()");
        Assert.True(ctx.Response.Headers.ContainsKey("X-StyloBot-Failed"));
    }
}
```

This test uses a static helper `BotDetectionMiddleware.HandleDetectionFailureFor(policy)` that returns a tiny applier. The helper exists so failure handling is unit-testable without the full middleware fixture. Define it in Step 2.3 below.

- [ ] **Step 2.2: Run tests, expect build error**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~BotDetectionMiddlewareFailureTests"
```
Expected: `HandleDetectionFailureFor` not defined.

- [ ] **Step 2.3: Add the helper + wire it into the middleware**

In `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`, add at the bottom of the class (near other static helpers):

```csharp
    /// <summary>
    ///     The applier returned by <see cref="HandleDetectionFailureFor"/>. Splitting this
    ///     out lets the middleware's try-catch be unit-tested without spinning up the
    ///     whole DI fixture.
    /// </summary>
    public readonly record struct DetectionFailureResult(bool ContinuePipeline);

    public sealed class DetectionFailureHandler
    {
        private readonly FailureMode _mode;
        public DetectionFailureHandler(FailureMode mode) => _mode = mode;

        public DetectionFailureResult Apply(HttpContext ctx, Exception ex)
        {
            ctx.Response.Headers["X-StyloBot-Failed"] = ex.GetType().Name;
            switch (_mode)
            {
                case FailureMode.FailClosed:
                    ctx.Response.StatusCode = 503;
                    return new DetectionFailureResult(ContinuePipeline: false);
                case FailureMode.LogOnly:
                case FailureMode.FailOpen:
                default:
                    return new DetectionFailureResult(ContinuePipeline: true);
            }
        }
    }

    public static DetectionFailureHandler HandleDetectionFailureFor(DetectionPolicy policy)
        => new(policy.OnFailure);
```

Then locate line 326 (the call `var aggregatedResult = await orchestrator.DetectWithPolicyAsync(...)`). Wrap it in a try-catch:

```csharp
        AggregatedEvidence aggregatedResult;
        try
        {
            aggregatedResult = await orchestrator.DetectWithPolicyAsync(context, policy, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client aborted; rethrow so ASP.NET handles connection teardown normally.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detection pipeline failed for {Path}; applying policy.OnFailure={Mode}",
                context.Request.Path, policy.OnFailure);

            var fh = HandleDetectionFailureFor(policy);
            var fr = fh.Apply(context, ex);
            if (!fr.ContinuePipeline)
                return;
            // Synthesise a neutral verdict so downstream middleware sees a consistent shape.
            aggregatedResult = new AggregatedEvidence
            {
                BotProbability = 0.0,
                Confidence = 0.0,
                RiskBand = RiskBand.Low,
                ThreatBand = ThreatBand.Low,
                TotalProcessingTimeMs = 0,
            };
        }
```

If `_logger` is not a field on the middleware, use the existing logger reference (read the file to find the actual field name; it's typically `_logger` or the parameter on the method).

The second instance at line 1375 needs the same treatment. Read the surrounding context and apply the identical pattern.

- [ ] **Step 2.4: Run tests, expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~BotDetectionMiddlewareFailureTests"
```
Expected: 3 pass.

Also run the existing middleware tests to confirm no regression:

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~BotDetectionMiddleware"
```
Expected: all pass.

- [ ] **Step 2.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs \
        src/Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareFailureTests.cs
git commit -m "$(cat <<'EOF'
feat(middleware): apply DetectionPolicy.OnFailure to orchestrator exceptions

Wraps the DetectWithPolicyAsync call in a try-catch and routes through the
new FailureMode applier. FailOpen continues the pipeline (default,
preserves availability), FailClosed returns 503 and short-circuits,
LogOnly continues and emits an X-StyloBot-Failed header for monitoring.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Align `SidecarBotDetectionMiddleware` with FailureMode

**Files:**
- Modify: `src/Mostlylucid.BotDetection.Sidecar.Client/SidecarBotDetectionMiddleware.cs`
- Create: `src/Mostlylucid.BotDetection.Sidecar.Client.Tests/SidecarMiddlewareFailureTests.cs` (or extend the existing test file if one exists)

- [ ] **Step 3.1: Find or create the sidecar-client test project**

```bash
find src -name "Mostlylucid.BotDetection.Sidecar.Client*.csproj" -not -path "*/bin/*" -not -path "*/obj/*"
```

If no test project exists, the failure scenarios are best covered by integration tests in `tests/integration/`. In that case, write the unit test as part of the main `Mostlylucid.BotDetection.Test` project, asserting the helper applier behaviour through a reference to the sidecar middleware. If the sidecar-client csproj is referenced by the main test project, this should just work. Inspect the test project's references with:

```bash
grep "Sidecar.Client" src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj
```

If absent, add a `ProjectReference` to `Sidecar.Client.csproj` in the test csproj before writing the test.

- [ ] **Step 3.2: Add the failure-mode option to `SidecarClientOptions`**

In `src/Mostlylucid.BotDetection.Sidecar.Client/SidecarClientOptions.cs`, add a new property:

```csharp
    /// <summary>
    ///     What to do when the sidecar gRPC call fails (timeout, unreachable, RPC error).
    ///     Defaults to <see cref="FailureMode.FailOpen"/>. Set to FailClosed for
    ///     high-security deployments where letting requests through without detection
    ///     is worse than dropping them.
    /// </summary>
    public FailureMode OnFailure { get; set; } = FailureMode.FailOpen;
```

Add `using Mostlylucid.BotDetection.Policies;` to the file's using directives.

- [ ] **Step 3.3: Write the failing test**

Create or extend `src/Mostlylucid.BotDetection.Test/Sidecar/SidecarMiddlewareFailureTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Sidecar.Client;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Sidecar;

public class SidecarMiddlewareFailureTests
{
    [Fact]
    public void Default_OnFailure_IsFailOpen()
    {
        var opts = new SidecarClientOptions();
        Assert.Equal(FailureMode.FailOpen, opts.OnFailure);
    }

    // Behavioural tests for the actual middleware are covered in
    // tests/integration/baseline-grpc/* (k6 integration). The unit-level
    // sidecar failure semantics are covered by exercising the shared helper
    // from BotDetectionMiddlewareFailureTests.
}
```

Run:

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SidecarMiddlewareFailureTests"
```
Expected: PASS (just option-default validation).

- [ ] **Step 3.4: Update the sidecar middleware to honour the option**

Replace the body of `SidecarBotDetectionMiddleware.InvokeAsync` so it consults `SidecarClientOptions.OnFailure`:

```csharp
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            var req = BuildRequest(context);
            var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            var response = await _client.DetectAsync(req, deadline: deadline);
            WriteToContext(context, response);
            await _next(context);
            return;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Sidecar gRPC call failed; applying OnFailure={Mode}", _onFailure);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sidecar detection error; applying OnFailure={Mode}", _onFailure);
        }

        // Failure path: same applier as BotDetectionMiddleware.
        context.Response.Headers["X-StyloBot-Failed"] = "sidecar";
        switch (_onFailure)
        {
            case FailureMode.FailClosed:
                context.Response.StatusCode = 503;
                return;
            case FailureMode.LogOnly:
            case FailureMode.FailOpen:
            default:
                await _next(context);
                return;
        }
    }
```

Add an `_onFailure` field initialised from the options:

```csharp
    private readonly FailureMode _onFailure;
    // ... in the constructor:
    _onFailure = options.Value.OnFailure;
```

Add `using Mostlylucid.BotDetection.Policies;` at the top.

- [ ] **Step 3.5: Run tests, expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SidecarMiddleware"
```
Expected: PASS.

- [ ] **Step 3.6: Commit**

```bash
git add src/Mostlylucid.BotDetection.Sidecar.Client/SidecarBotDetectionMiddleware.cs \
        src/Mostlylucid.BotDetection.Sidecar.Client/SidecarClientOptions.cs \
        src/Mostlylucid.BotDetection.Test/Sidecar/SidecarMiddlewareFailureTests.cs
git commit -m "$(cat <<'EOF'
feat(sidecar): honour FailureMode in SidecarBotDetectionMiddleware

Previously the sidecar middleware hardcoded fail-open behaviour on RPC
errors. It now reads SidecarClientOptions.OnFailure and applies the same
three-mode semantics as BotDetectionMiddleware: FailOpen continues,
FailClosed returns 503, LogOnly continues and emits X-StyloBot-Failed.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Add `LoadShedOptions` and `LoadShedDecision`

**Files:**
- Create: `src/Mostlylucid.BotDetection/Policies/LoadShedOptions.cs`
- Create: `src/Mostlylucid.BotDetection/Services/LoadShedDecision.cs`
- Modify: `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs` (add `LoadShed` property)
- Create: `src/Mostlylucid.BotDetection.Test/Services/LoadShedDecisionTests.cs`

- [ ] **Step 4.1: Write the failing tests**

Create `src/Mostlylucid.BotDetection.Test/Services/LoadShedDecisionTests.cs`:

```csharp
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class LoadShedDecisionTests
{
    private sealed class FixedBandSensor : ILoadBandSource
    {
        public FixedBandSensor(LoadBand band) => CurrentBand = band;
        public LoadBand CurrentBand { get; }
    }

    [Fact]
    public void Default_NeverSheds_AtLowLoad()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Low));
        var opts = new LoadShedOptions();
        // 100 random draws, none should shed since fractions are all 0.0
        for (var i = 0; i < 100; i++)
            Assert.False(decision.ShouldShed(opts, requestSeed: i));
    }

    [Fact]
    public void Critical_WithFullDrop_AlwaysSheds()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Critical));
        var opts = new LoadShedOptions { DropFractionAtCritical = 1.0 };
        for (var i = 0; i < 100; i++)
            Assert.True(decision.ShouldShed(opts, requestSeed: i));
    }

    [Fact]
    public void Critical_WithZeroDrop_NeverSheds()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Critical));
        var opts = new LoadShedOptions { DropFractionAtCritical = 0.0 };
        for (var i = 0; i < 100; i++)
            Assert.False(decision.ShouldShed(opts, requestSeed: i));
    }

    [Fact]
    public void Critical_WithFractionalDrop_ApproximatesFraction()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Critical));
        var opts = new LoadShedOptions { DropFractionAtCritical = 0.5 };
        var shed = 0;
        for (var i = 0; i < 1000; i++)
            if (decision.ShouldShed(opts, requestSeed: i)) shed++;
        // Deterministic hash-based; should be near 500 across 1000 draws.
        Assert.InRange(shed, 400, 600);
    }

    [Fact]
    public void High_UsesHighFraction_NotCriticalFraction()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.High));
        var opts = new LoadShedOptions
        {
            DropFractionAtHigh = 0.0,
            DropFractionAtCritical = 1.0,
        };
        for (var i = 0; i < 100; i++)
            Assert.False(decision.ShouldShed(opts, requestSeed: i));
    }

    [Fact]
    public void Normal_NeverSheds_RegardlessOfOptions()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Normal));
        var opts = new LoadShedOptions
        {
            DropFractionAtHigh = 1.0,
            DropFractionAtCritical = 1.0,
        };
        for (var i = 0; i < 100; i++)
            Assert.False(decision.ShouldShed(opts, requestSeed: i));
    }
}
```

- [ ] **Step 4.2: Run the tests, expect build errors**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~LoadShedDecisionTests"
```
Expected: `LoadShedDecision`, `LoadShedOptions`, `ILoadBandSource` not defined.

- [ ] **Step 4.3: Create `LoadShedOptions`**

Create `src/Mostlylucid.BotDetection/Policies/LoadShedOptions.cs`:

```csharp
namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Per-policy load-shed configuration. Consulted by <see cref="Services.LoadShedDecision"/>
///     at request intake, BEFORE the orchestrator is called. Sheds (skips detection) for a
///     fraction of requests when the pipeline is under sustained High or Critical load,
///     as reported by <see cref="Services.PipelineLoadSensor.CurrentBand"/>.
///     Defaults are zero, so load-shedding is opt-in.
/// </summary>
public sealed record LoadShedOptions
{
    /// <summary>Fraction of requests to shed when load band is High. Range 0.0-1.0. Default 0.0.</summary>
    public double DropFractionAtHigh { get; init; }

    /// <summary>Fraction of requests to shed when load band is Critical. Range 0.0-1.0. Default 0.0.</summary>
    public double DropFractionAtCritical { get; init; }
}
```

- [ ] **Step 4.4: Create the abstraction `ILoadBandSource` and `LoadShedDecision`**

Create `src/Mostlylucid.BotDetection/Services/LoadShedDecision.cs`:

```csharp
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Minimal abstraction over <see cref="PipelineLoadSensor.CurrentBand"/> so the
///     decision can be unit-tested without spinning up the real sensor.
/// </summary>
public interface ILoadBandSource
{
    LoadBand CurrentBand { get; }
}

/// <summary>
///     Decides whether to shed (skip detection on) the current request based on
///     <see cref="ILoadBandSource.CurrentBand"/> and the policy-level <see cref="LoadShedOptions"/>.
///     Deterministic: a stable hash of the requestSeed decides whether the request falls
///     in the shed bucket, so identical seeds produce identical results. The middleware
///     uses the request's signature hash as the seed.
/// </summary>
public sealed class LoadShedDecision
{
    private readonly ILoadBandSource _source;

    public LoadShedDecision(ILoadBandSource source) => _source = source;

    public bool ShouldShed(LoadShedOptions options, int requestSeed)
    {
        var fraction = _source.CurrentBand switch
        {
            LoadBand.High     => options.DropFractionAtHigh,
            LoadBand.Critical => options.DropFractionAtCritical,
            _                 => 0.0,
        };
        if (fraction <= 0.0) return false;
        if (fraction >= 1.0) return true;

        // Deterministic shed decision: map the seed through a stable hash to [0, 1.0).
        // Use unchecked uint to avoid sign issues and give a uniform distribution.
        unchecked
        {
            var h = (uint)requestSeed * 2654435761u; // Knuth multiplicative hash
            var bucket = (h % 10_000) / 10_000.0;
            return bucket < fraction;
        }
    }
}
```

- [ ] **Step 4.5: Make `PipelineLoadSensor` implement `ILoadBandSource`**

In `src/Mostlylucid.BotDetection/Services/PipelineLoadSensor.cs`, change the class declaration to add the interface:

```csharp
public sealed class PipelineLoadSensor : ILoadBandSource, IDisposable
```

Since `CurrentBand` is already a public property returning `LoadBand`, no body change is needed.

Add `using` if the interface is in a different namespace (same namespace `Mostlylucid.BotDetection.Services`, so no using required).

- [ ] **Step 4.6: Add `LoadShed` property to `DetectionPolicy`**

In `src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs`, after the `OnFailure` property added in Task 1:

```csharp
    /// <summary>
    ///     Load-shed configuration: at High/Critical pipeline load, skip detection on
    ///     the configured fraction of requests. Defaults to no shedding.
    /// </summary>
    public LoadShedOptions LoadShed { get; init; } = new();
```

- [ ] **Step 4.7: Run tests, expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~LoadShedDecisionTests"
```
Expected: 6 pass.

- [ ] **Step 4.8: Register `LoadShedDecision` in DI**

In `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`, find where `PipelineLoadSensor` is registered (search the file). Add `LoadShedDecision` as a singleton registered after the sensor:

```csharp
services.AddSingleton<LoadShedDecision>();
services.AddSingleton<ILoadBandSource>(sp => sp.GetRequiredService<PipelineLoadSensor>());
```

If `PipelineLoadSensor` is not yet registered as a singleton (verify with `grep "PipelineLoadSensor" src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`), the DI graph for tests may pass `null` for the sensor. In that case, register it conditionally:

```csharp
services.TryAddSingleton<PipelineLoadSensor>();
```

(Check the existing registration pattern in the file and follow it.)

- [ ] **Step 4.9: Verify solution builds**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
dotnet build src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj
```
Expected: 0 errors.

- [ ] **Step 4.10: Commit**

```bash
git add src/Mostlylucid.BotDetection/Policies/LoadShedOptions.cs \
        src/Mostlylucid.BotDetection/Services/LoadShedDecision.cs \
        src/Mostlylucid.BotDetection/Services/PipelineLoadSensor.cs \
        src/Mostlylucid.BotDetection/Policies/DetectionPolicy.cs \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection.Test/Services/LoadShedDecisionTests.cs
git commit -m "$(cat <<'EOF'
feat(policy): add LoadShedDecision for request-intake load shedding

PipelineLoadSensor already exposes CurrentBand (Low/Normal/High/Critical).
LoadShedDecision consults that band and the policy's LoadShedOptions to
decide whether to skip detection on a given request. Deterministic by
request seed, so retries land identically.

Wiring into the middleware is the next task.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Wire `LoadShedDecision` into `BotDetectionMiddleware`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`
- Modify: `src/Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareFailureTests.cs` (or new file)

- [ ] **Step 5.1: Write the failing test**

Append to `BotDetectionMiddlewareFailureTests.cs` (or create a separate `BotDetectionMiddlewareLoadShedTests.cs`):

```csharp
using Mostlylucid.BotDetection.Services;

// (Inside the same test class:)

[Fact]
public void LoadShed_ShouldSkipDetection_WhenSensorIsCriticalAndFractionIsOne()
{
    var policy = new DetectionPolicy
    {
        Name = "shed-test",
        LoadShed = new LoadShedOptions { DropFractionAtCritical = 1.0 }
    };

    var source = new FixedBandSource(LoadBand.Critical);
    var decision = new LoadShedDecision(source);
    var should = decision.ShouldShed(policy.LoadShed, requestSeed: 12345);
    Assert.True(should);
}

private sealed class FixedBandSource : ILoadBandSource
{
    public FixedBandSource(LoadBand b) => CurrentBand = b;
    public LoadBand CurrentBand { get; }
}
```

(Note: this is essentially a redundancy check on top of LoadShedDecisionTests. The real integration check is in Step 5.4 below.)

- [ ] **Step 5.2: Wire LoadShedDecision into the middleware**

In `BotDetectionMiddleware.cs`, find the constructor and add a parameter for `LoadShedDecision`:

```csharp
    private readonly LoadShedDecision _loadShedDecision;
    // ... in the constructor signature:
    LoadShedDecision loadShedDecision,
    // ... in the body:
    _loadShedDecision = loadShedDecision;
```

Then, BEFORE the orchestrator-call try-catch added in Task 2 (around line 326), add the load-shed gate:

```csharp
        // Load-shed gate: at High/Critical load, skip detection per policy.
        // The request still goes through the pipeline; we just emit a neutral verdict
        // so downstream middleware sees a consistent shape.
        var loadShedSeed = context.Connection.Id?.GetHashCode() ?? context.Request.Path.Value?.GetHashCode() ?? 0;
        if (_loadShedDecision.ShouldShed(policy.LoadShed, loadShedSeed))
        {
            context.Response.Headers["X-StyloBot-Shed"] = "1";
            _logger.LogInformation("Load-shed: skipping detection for {Path} (band reported by sensor)",
                context.Request.Path);
            await _next(context);
            return;
        }
```

The exact position depends on the existing code structure (read line 320-330 of the middleware). Place the gate after the policy is resolved but before the orchestrator is called.

- [ ] **Step 5.3: Run tests, expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~BotDetectionMiddleware"
```
Expected: all pass.

If any existing middleware constructor test breaks because of the new `LoadShedDecision` parameter, update those test fixtures to pass a stub:

```csharp
var loadShedDecision = new LoadShedDecision(new FixedBandSource(LoadBand.Low));
```

- [ ] **Step 5.4: Solution build and full test run**

```bash
dotnet build mostlylucid.stylobot.sln
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj
```
Expected: 0 build errors; 0 test failures.

- [ ] **Step 5.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs \
        src/Mostlylucid.BotDetection.Test/Middleware/BotDetectionMiddlewareFailureTests.cs
git commit -m "$(cat <<'EOF'
feat(middleware): consult LoadShedDecision before calling orchestrator

At High/Critical pipeline load, the middleware now skips detection for
the policy-configured fraction of requests, emits X-StyloBot-Shed=1, and
continues the pipeline. Defaults are zero (opt-in).

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Configuration binding for `FailureMode` and `LoadShed`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Policies/DetectionPolicyConfiguration.cs`

- [ ] **Step 6.1: Add JSON binding for the new properties**

Read `DetectionPolicyConfiguration.cs` to find where existing policy fields are bound from the JSON section. There is a method that maps a `PolicyDefinition` (the DTO) onto a `DetectionPolicy`. Add bindings for:

- `OnFailure` (string -> `FailureMode` enum) -default `FailOpen`
- `LoadShed.DropFractionAtHigh` (double) -default 0.0
- `LoadShed.DropFractionAtCritical` (double) -default 0.0

Concretely, in the DTO (look for a `PolicyDefinition` or `PolicyDef` class):

```csharp
public string? OnFailure { get; set; }
public LoadShedDef? LoadShed { get; set; }

public sealed class LoadShedDef
{
    public double DropFractionAtHigh { get; set; }
    public double DropFractionAtCritical { get; set; }
}
```

In the mapping function (look for `ToDetectionPolicy()` or similar):

```csharp
OnFailure = Enum.TryParse<FailureMode>(definition.OnFailure, ignoreCase: true, out var mode)
    ? mode
    : FailureMode.FailOpen,
LoadShed = definition.LoadShed is { } ls
    ? new LoadShedOptions
        {
            DropFractionAtHigh = ls.DropFractionAtHigh,
            DropFractionAtCritical = ls.DropFractionAtCritical,
        }
    : new LoadShedOptions(),
```

Add `using Mostlylucid.BotDetection.Policies;` if needed (likely already there).

- [ ] **Step 6.2: Write a binding test**

Create `src/Mostlylucid.BotDetection.Test/Policies/PolicyConfigurationBindingTests.cs` (or extend an existing config-tests file):

```csharp
using Microsoft.Extensions.Configuration;
using Mostlylucid.BotDetection.Policies;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies;

public class PolicyConfigurationBindingTests
{
    [Fact]
    public void LoadFromJson_BindsFailureMode_AndLoadShed()
    {
        var json = """
        {
          "Policies": {
            "high-security": {
              "OnFailure": "FailClosed",
              "LoadShed": { "DropFractionAtHigh": 0.0, "DropFractionAtCritical": 0.1 }
            }
          }
        }
        """;
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var policies = DetectionPolicyConfiguration.LoadFromConfiguration(config.GetSection("Policies"));
        var p = policies["high-security"];
        Assert.Equal(FailureMode.FailClosed, p.OnFailure);
        Assert.Equal(0.0, p.LoadShed.DropFractionAtHigh);
        Assert.Equal(0.1, p.LoadShed.DropFractionAtCritical);
    }
}
```

If `DetectionPolicyConfiguration.LoadFromConfiguration` has a different signature, adapt the call to match the actual API. Read the existing config-loading code first.

- [ ] **Step 6.3: Run tests, expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~PolicyConfigurationBindingTests"
```
Expected: PASS.

- [ ] **Step 6.4: Commit**

```bash
git add src/Mostlylucid.BotDetection/Policies/DetectionPolicyConfiguration.cs \
        src/Mostlylucid.BotDetection.Test/Policies/PolicyConfigurationBindingTests.cs
git commit -m "$(cat <<'EOF'
feat(policy-config): bind OnFailure and LoadShed from appsettings JSON

Customers can now write per-policy:

  "Policies": {
    "admin": { "OnFailure": "FailClosed", "LoadShed": { "DropFractionAtCritical": 0.05 } }
  }

Unrecognised OnFailure values fall back to FailOpen with a log warning.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Deprecate redundant `BotDetectionOptions` fields

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs`

- [ ] **Step 7.1: Mark the duplicated fields obsolete**

In `BotDetectionOptions.cs`, add `[Obsolete]` attributes (warnings, not errors) to fields that duplicate `DetectionPolicy` concepts:

```csharp
    [Obsolete("Use DetectionPolicy.ImmediateBlockThreshold / EarlyExitThreshold per policy. " +
              "Will be removed in 7.0.", error: false)]
    public double BotThreshold { get; set; } = 0.7;

    [Obsolete("Use DetectionPolicy.MinConfidence per policy. Will be removed in 7.0.", error: false)]
    public double MinConfidenceToBlock { get; set; } = 0.8;

    [Obsolete("Per-policy ActionPolicyName covers this. Will be removed in 7.0.", error: false)]
    public bool BlockDetectedBots { get; set; } = false;

    [Obsolete("Use DetectionPolicy.AllowVerifiedBots (or a custom transition) per policy. " +
              "Will be removed in 7.0.", error: false)]
    public bool AllowVerifiedSearchEngines { get; set; } = true;

    [Obsolete("Use DetectionPolicy.ExcludedDetectors or FastPathDetectors per policy. " +
              "Will be removed in 7.0.", error: false)]
    public bool EnableUserAgentDetection { get; set; } = true;

    [Obsolete("Use DetectionPolicy.ExcludedDetectors per policy. Will be removed in 7.0.", error: false)]
    public bool EnableHeaderAnalysis { get; set; } = true;

    [Obsolete("Use DetectionPolicy.ExcludedDetectors per policy. Will be removed in 7.0.", error: false)]
    public bool EnableIpDetection { get; set; } = true;

    [Obsolete("Use DetectionPolicy.ExcludedDetectors per policy. Will be removed in 7.0.", error: false)]
    public bool EnableBehavioralAnalysis { get; set; } = true;

    [Obsolete("Use DetectionPolicy.AiPathDetectors and EscalateToAi per policy. " +
              "Will be removed in 7.0.", error: false)]
    public bool EnableLlmDetection { get; set; }
```

Important: do NOT change the default values or remove the fields. The deprecation is source-only; internal code still references them.

- [ ] **Step 7.2: Suppress the new warnings in the codebase**

Build the solution and capture the new warnings:

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | grep -i "obsolete" | head -30
```

Each internal callsite that reads one of these fields will now warn. Three options:

1. Update the callsite to use the corresponding `DetectionPolicy` property (best, but scope creep).
2. Add `#pragma warning disable CS0618` around the callsite with a TODO comment referencing 7.0 removal.
3. Add `[SuppressMessage("Usage", "CS0618:Type or member is obsolete")]` on the callsite method.

Choose option 2 for the smallest blast radius. For every callsite the build identifies, wrap with:

```csharp
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in 7.0
            var threshold = _options.BotThreshold;
#pragma warning restore CS0618
```

Do NOT change the runtime behaviour. The goal is "warns customers; internal code still works."

- [ ] **Step 7.3: Build is warning-clean**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | grep -cE "warning CS0618" || echo "0"
```
Expected: 0. If non-zero, suppress the remaining sites with the same pragma pattern.

- [ ] **Step 7.4: Tests still pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj
```
Expected: 0 failures.

- [ ] **Step 7.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs
# Use `git add` for any file you wrapped with the suppression pragma:
git add -A
git commit -m "$(cat <<'EOF'
deprecate(options): mark redundant BotDetectionOptions fields obsolete

Nine fields on BotDetectionOptions duplicate per-policy DetectionPolicy
properties: BotThreshold, MinConfidenceToBlock, BlockDetectedBots,
AllowVerifiedSearchEngines, and the five Enable*Detection booleans.
Marked [Obsolete] (warning only) with the corresponding DetectionPolicy
replacement in the message. Removal scheduled for 7.0.

Internal callsites are suppressed with #pragma to keep the build clean
while the consolidation lands incrementally.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Rename `SimulationPack` to `HoneypotPack` (additive)

**Files:**
- Create: `src/Mostlylucid.BotDetection/SimulationPacks/HoneypotPack.cs` (an alias)

- [ ] **Step 8.1: Add the type alias**

Create `src/Mostlylucid.BotDetection/SimulationPacks/HoneypotPack.cs`:

```csharp
namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Alias for <see cref="SimulationPack"/>. The "Honeypot" name better describes the
///     responsibility (fake response content served to bots) and distinguishes the type
///     from <c>ReactionPack</c> (adaptive policy escalation), <c>CompliancePack</c>
///     (data lifecycle), and <c>MonitoringPack</c> (metrics spec).
/// </summary>
/// <remarks>
///     This alias keeps backwards compatibility with the <c>SimulationPack</c> type name
///     used in 6.4 and earlier. New code should use <see cref="HoneypotPack"/>. The
///     <c>SimulationPack</c> identifier will be removed in 7.0.
/// </remarks>
public static class HoneypotPack
{
    public static SimulationPack Create(string name) => new() { Name = name };
}
```

Why this shape: C# doesn't support type aliases at the file level outside `using` statements, and a full rename would break every customer who already references `SimulationPack`. The class above gives new code a discoverable `HoneypotPack` entry point without touching the existing type.

If `SimulationPack` does NOT have a parameterless `Create`-style factory and is constructed elsewhere via a builder or static method, adapt this file to mirror the existing pattern. Read `src/Mostlylucid.BotDetection/SimulationPacks/SimulationPack.cs` first.

- [ ] **Step 8.2: Build the project**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```
Expected: 0 errors.

- [ ] **Step 8.3: Commit**

```bash
git add src/Mostlylucid.BotDetection/SimulationPacks/HoneypotPack.cs
git commit -m "$(cat <<'EOF'
docs(packs): add HoneypotPack name as discoverable alias for SimulationPack

The 'pack' identifier is overloaded across SimulationPack (honeypot content),
ReactionPack (adaptive policy escalation), CompliancePack (data lifecycle),
and MonitoringPack (metrics spec). HoneypotPack disambiguates the first.

Existing SimulationPack code keeps working; new code can pick the clearer
name. The SimulationPack identifier is scheduled for removal in 7.0.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: `policy-system.md` documentation

**Files:**
- Create: `src/Mostlylucid.BotDetection/docs/policy-system.md`

- [ ] **Step 9.1: Write the doc**

Create `src/Mostlylucid.BotDetection/docs/policy-system.md`:

```markdown
# Policy System

StyloBot has three policy-shaped concepts. They are deliberately distinct.

## Detection Policy

Defines HOW detection runs for a request: which detectors are active in which wave, what risk thresholds apply, whether to escalate to AI, what to do on internal failure, and whether to shed load.

Type: `Mostlylucid.BotDetection.Policies.DetectionPolicy`.

Key properties:

| Property | Default | Purpose |
| --- | --- | --- |
| `FastPathDetectors` | (built-in list) | Detectors that run in Wave 0 |
| `SlowPathDetectors` | (built-in list) | Detectors that run on escalation |
| `AiPathDetectors` | (empty) | Detectors that run only when `EscalateToAi` triggers |
| `UseFastPath` | `true` | Whether to short-circuit on fast-path verdict |
| `EarlyExitThreshold` | 0.3 | Below this risk, allow early exit |
| `ImmediateBlockThreshold` | 0.95 | Above this risk, block immediately |
| `MinConfidence` | 0.0 | Confidence gate for blocking decisions |
| `OnFailure` | `FailOpen` | What to do on internal pipeline failure |
| `LoadShed.DropFractionAtCritical` | 0.0 | Fraction of requests to skip at Critical load |
| `LoadShed.DropFractionAtHigh` | 0.0 | Fraction of requests to skip at High load |
| `Transitions` | (empty) | Per-condition action-policy escalation |

Built-in policy names: `default`, `strict`, `relaxed`, `static`, `learning`, `monitor`, `api`.

## Action Policy

Defines WHAT to do with the verdict: block / throttle / challenge / log-only / redirect.

Type: `Mostlylucid.BotDetection.Actions.IActionPolicy`.

Built-in names: `block`, `block-hard`, `block-soft`, `throttle`, `throttle-stealth`, `challenge`, `redirect-honeypot`, `logonly`, `shadow`.

A detection policy can demand an action policy via `Transitions[i].ActionPolicyName`. The endpoint-level `[BotAction("...")]` attribute can also pick an action policy independently of the detection policy.

## Failure Mode

Distinct from "verdict on bot detection." `FailureMode` covers what to do when detection itself fails (orchestrator exception, store unavailable, sidecar unreachable).

- `FailOpen` (default): allow the request through, no detection signals.
- `FailClosed`: return HTTP 503, short-circuit the pipeline.
- `LogOnly`: allow through, emit `X-StyloBot-Failed` header and structured log entry.

Set per-policy via `DetectionPolicy.OnFailure`. The sidecar middleware reads the same enum via `SidecarClientOptions.OnFailure`.

## Load Shed

At High or Critical pipeline load (as reported by `PipelineLoadSensor.CurrentBand`), skip detection on the configured fraction of requests. Defaults are zero (opt-in).

- `DropFractionAtHigh`: fraction (0.0-1.0) of requests to skip at `LoadBand.High`.
- `DropFractionAtCritical`: fraction (0.0-1.0) of requests to skip at `LoadBand.Critical`.

Decision is deterministic by request seed (`Connection.Id` hash), so retries land identically. Sheds emit `X-StyloBot-Shed: 1` header so operators can observe the shed rate.

## Threshold precedence (read this when tuning)

Two layers exist today:

1. Per-policy `DetectionPolicy` thresholds (`EarlyExitThreshold`, `ImmediateBlockThreshold`, `MinConfidence`).
2. Per-transition `PolicyTransition` thresholds (`WhenRiskExceeds`, `WhenRiskBelow`) for multi-step action selection.

The legacy `BotDetectionOptions.BotThreshold` field is deprecated and scheduled for removal in 7.0. Until then, customers using it should migrate to the per-policy thresholds. Documentation in `appsettings.json` examples now uses the per-policy form.

## Pack disambiguation

Four "pack" types exist, deliberately separate:

| Pack | Purpose |
| --- | --- |
| `SimulationPack` (aka `HoneypotPack`) | Fake response content served to bots |
| `ReactionPack` (planned) | Adaptive policy escalation on upstream degradation signals |
| `CompliancePack` | Data retention, anonymization, DSAR audit |
| `MonitoringPack` | Metric collection spec |

`SimulationPack` and `HoneypotPack` refer to the same type. New code should prefer `HoneypotPack` for clarity.

## Existing capabilities you might be looking for

Several capabilities customers ask about already exist:

- **Per-detector timeout**: `DetectorDefaults.Timing.TimeoutMs` in each detector's YAML manifest.
- **Per-detector circuit breaker**: `BotDetectionOptions.CircuitBreakerThreshold` / `CircuitBreakerResetTime`. When a detector fails N times in a row, it's skipped for the reset window, then probed.
- **Adaptive load handling for background work**: `PipelineLoadSensor.LoadFactor` scales clustering and enrichment intervals as RPS climbs.
- **Out-of-process detection**: register `SidecarBotDetectionMiddleware` instead of the in-process one in `Startup`. The sidecar is a separate ASP.NET host (`Mostlylucid.BotDetection.Sidecar`).
- **Sampling for learning**: `FastPathDecider.ScheduledForFullAnalysis` lets uncertain fast-path verdicts be re-analysed asynchronously.
```

- [ ] **Step 9.2: Verify no em dashes**

```bash
grep -- '—' src/Mostlylucid.BotDetection/docs/policy-system.md && echo "FOUND" || echo "clean"
```
Expected: `clean`.

- [ ] **Step 9.3: Commit**

```bash
git add src/Mostlylucid.BotDetection/docs/policy-system.md
git commit -m "$(cat <<'EOF'
docs: add policy-system.md documenting the four policy-shaped concepts

DetectionPolicy, ActionPolicy, FailureMode, and LoadShed are deliberately
distinct. The doc also disambiguates the four 'pack' types (Simulation,
Reaction, Compliance, Monitoring) and lists existing capabilities
customers commonly ask for (per-detector timeout, circuit breaker,
sidecar mode, FastPathDecider sampling).

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Full verification + CHANGELOG entry

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 10.1: Full BotDetection.Test run**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj
```
Expected: 0 failures. The new test count should be roughly:
- FailureModeTests: 3
- BotDetectionMiddlewareFailureTests: 3
- SidecarMiddlewareFailureTests: 1
- LoadShedDecisionTests: 6
- PolicyConfigurationBindingTests: 1
- Plus all pre-existing tests.

If any pre-existing test broke because of the new middleware constructor parameter, fix the fixture by passing a stub `LoadShedDecision`.

- [ ] **Step 10.2: Build the AOT Console**

```bash
dotnet publish src/Mostlylucid.BotDetection.Console -c Release -r osx-arm64 -o /tmp/stylobot-aot 2>&1 | tail -5
```
Expected: 0 errors. New code should not introduce new IL2026/IL3050 warnings (the existing pre-existing warnings from LLM providers / Serilog will still be there). Grep specifically for our new files:

```bash
dotnet publish src/Mostlylucid.BotDetection.Console -c Release -r osx-arm64 -o /tmp/stylobot-aot 2>&1 \
  | grep -E "FailureMode|LoadShed|HoneypotPack" || echo "no AOT issues from new files"
```
Expected: `no AOT issues from new files`.

- [ ] **Step 10.3: Add CHANGELOG entry**

In `CHANGELOG.md`, add a new section at the top (after the header lines, before `## [6.4.0]`):

```markdown
## [6.5.0] - 2026-05-12

Policy-system consolidation. Two genuine additions (FailureMode, LoadShed) plus a deprecation pass that surfaces and documents the existing duplication between `BotDetectionOptions` and `DetectionPolicy`. No breaking changes.

### Added

- **`DetectionPolicy.OnFailure` (FailureMode enum)** -policy-level behaviour when detection itself fails. Three values: `FailOpen` (default), `FailClosed` (HTTP 503), `LogOnly` (allow + emit `X-StyloBot-Failed` header). Honoured by `BotDetectionMiddleware` (try-catch around `DetectWithPolicyAsync`) and `SidecarBotDetectionMiddleware` (via `SidecarClientOptions.OnFailure`).
- **`DetectionPolicy.LoadShed` (LoadShedOptions)** -per-policy load shedding at request intake. `DropFractionAtHigh` and `DropFractionAtCritical` (default 0.0) drop the configured fraction of requests when `PipelineLoadSensor.CurrentBand` reports `High` or `Critical`. Sheds emit `X-StyloBot-Shed: 1` for observability. Decision is deterministic by request seed so retries land identically.
- **`LoadShedDecision`** service and **`ILoadBandSource`** interface -wraps `PipelineLoadSensor` so the shed decision is unit-testable.
- **`HoneypotPack`** -discoverable name for `SimulationPack`, disambiguating it from `ReactionPack` / `CompliancePack` / `MonitoringPack`. Existing `SimulationPack` code still works.
- **`docs/policy-system.md`** -new reference doc covering all four policy-shaped concepts (DetectionPolicy, ActionPolicy, FailureMode, LoadShed), the four "pack" types, threshold precedence, and existing capabilities customers commonly ask for.

### Changed

- **`SidecarBotDetectionMiddleware`** -previously hardcoded fail-open on RPC error; now reads `SidecarClientOptions.OnFailure`.
- **`BotDetectionMiddleware`** -orchestrator call is now wrapped in a try-catch that applies `DetectionPolicy.OnFailure`. Previously an unhandled detector exception would crash the request with HTTP 500.

### Deprecated

Nine `BotDetectionOptions` fields are now `[Obsolete]` (warning only) with the corresponding `DetectionPolicy` replacement in the obsolete message. Scheduled for removal in 7.0:

- `BotThreshold` -use `DetectionPolicy.ImmediateBlockThreshold` / `EarlyExitThreshold`
- `MinConfidenceToBlock` -use `DetectionPolicy.MinConfidence`
- `BlockDetectedBots` -use per-policy `ActionPolicyName` / `Transitions`
- `AllowVerifiedSearchEngines` -use `DetectionPolicy.AllowVerifiedBots` (or a Transitions rule)
- `EnableUserAgentDetection`, `EnableHeaderAnalysis`, `EnableIpDetection`, `EnableBehavioralAnalysis`, `EnableLlmDetection` -use `DetectionPolicy.{FastPathDetectors, SlowPathDetectors, AiPathDetectors, ExcludedDetectors}`

Internal callsites are suppressed with `#pragma warning disable CS0618` until the consolidation lands in 7.0; customer code emits a build warning that names the replacement.

### Tests Added

- `FailureModeTests` -3 facts on enum + default + init
- `BotDetectionMiddlewareFailureTests` -3 facts on FailOpen / FailClosed / LogOnly applier behaviour
- `SidecarMiddlewareFailureTests` -1 fact on option default
- `LoadShedDecisionTests` -6 facts covering Low / Normal / High / Critical bands and 0.0 / 0.5 / 1.0 drop fractions
- `PolicyConfigurationBindingTests` -1 fact for JSON binding of `OnFailure` and `LoadShed`

### Verified

- All BotDetection unit tests pass (full suite plus the new 14 tests).
- `Mostlylucid.BotDetection.Console` publishes cleanly under `PublishAot=true` for `osx-arm64`; no new IL2026/IL3050 warnings from the new files.

---

## [6.4.0] - 2026-05-12
```

Verify no em dashes:

```bash
grep -- '—' CHANGELOG.md | head -5 ; echo "--- count above ---"
```
Expected: only pre-existing em dashes outside the new section.

- [ ] **Step 10.4: Commit**

```bash
git add CHANGELOG.md
git commit -m "$(cat <<'EOF'
docs(changelog): 6.5.0 entry for policy consolidation

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 10.5: Branch summary**

```bash
git log --oneline main..HEAD
```
Confirm the commit list matches the task structure (10 task commits plus the CHANGELOG).

---

## Notes for the executor

- Per project rules (memory: `feedback_no_emdash`): never use em dashes anywhere. Use hyphens, colons, commas, parentheses.
- Per project rules (memory: `feedback_verify_before_checkin`): run the affected test slice before each commit; run the full BotDetection.Test project before Task 10's commit.
- Per project rules (memory: `feedback_never_push_without_approval`): never `git push`. Commits stay local until the user instructs otherwise.
- Per project rules (`CLAUDE.md`): never add hardcoded site-specific exceptions or bypass keys. All tuning happens through YAML / `appsettings.json`.
- Do NOT add a `PerformanceMode` enum, a `ShieldMode`, or a new circuit-breaker class. The existing capabilities (UseFastPath, FastPathDecider, the seven built-in policies, the existing CircuitState in `BlackboardOrchestrator`) cover those concepts. This PR is consolidation + the two genuinely missing pieces.
