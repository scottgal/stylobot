using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Pins the four layered defences of <see cref="IdentityProcessingCoordinator"/>:
///     keyed coalesce, priority scheduling, admission control (per-fp + global drop-oldest),
///     and the circuit breaker.
/// </summary>
public sealed class IdentityProcessingCoordinatorTests : IAsyncLifetime
{
    private IdentityProcessingCoordinator _coord = null!;
    private CancellationTokenSource _cts = null!;

    private IdentityCoordinatorOptions DefaultOptions() => new()
    {
        MaxQueueDepth = 10,
        MaxQueuedPerFingerprint = 2,
        CoalesceWindowMs = 200,
        BreakerTripThreshold = 0.80,
        BreakerResetThreshold = 0.30,
        BreakerTripHoldSeconds = 1,
        BreakerResetHoldSeconds = 1,
        // Tests pin dispatch ordering. WorkerCount=1 makes the worker-loop deterministic;
        // parallelism is verified in its own dedicated test below.
        WorkerCount = 1
    };

    private void Make(IdentityCoordinatorOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var options = Options.Create(new BotDetectionOptions
        {
            Identity = new IdentityOptions { Coordinator = opts }
        });
        _coord = new IdentityProcessingCoordinator(NullLogger<IdentityProcessingCoordinator>.Instance, options);
        _cts = new CancellationTokenSource();
        _ = _coord.StartAsync(_cts.Token);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_coord is null) return;
        _cts.Cancel();
        await _coord.StopAsync(CancellationToken.None);
        _coord.Dispose();
        _cts.Dispose();
    }

    [Fact]
    public async Task RunAsync_HappyPath_ExecutesAndReturnsResult()
    {
        Make();
        var (result, outcome) = await _coord.RunAsync(
            "fp-1", IdentitySlowPathKind.Pass2Match, riskScore: 0.5,
            ct => Task.FromResult<string?>("done"), CancellationToken.None);

        Assert.Equal(SlowPathDispatchOutcome.Executed, outcome);
        Assert.Equal("done", result);
    }

    [Fact]
    public async Task RunAsync_SecondCallForSameFp_DuringInflight_Coalesces()
    {
        Make();
        var firstStarted = new TaskCompletionSource<bool>();
        var firstCanFinish = new TaskCompletionSource<bool>();

        var firstTask = _coord.RunAsync(
            "fp-1", IdentitySlowPathKind.Pass2Match, riskScore: 0.5,
            async ct => { firstStarted.SetResult(true); await firstCanFinish.Task; return "first"; },
            CancellationToken.None);

        await firstStarted.Task;

        var (secondResult, secondOutcome) = await _coord.RunAsync(
            "fp-1", IdentitySlowPathKind.Pass2Match, riskScore: 0.5,
            ct => Task.FromResult<string?>("second"), CancellationToken.None);

        Assert.Equal(SlowPathDispatchOutcome.Coalesced, secondOutcome);
        Assert.Null(secondResult);

        firstCanFinish.SetResult(true);
        var (firstResult, firstOutcome) = await firstTask;
        Assert.Equal(SlowPathDispatchOutcome.Executed, firstOutcome);
        Assert.Equal("first", firstResult);
    }

    [Fact]
    public async Task RunAsync_HighRiskScore_DequeuesBeforeLowRiskScore()
    {
        Make();
        // Block the worker by holding an in-flight call for fp-blocker.
        var blockerStarted = new TaskCompletionSource<bool>();
        var blockerRelease = new TaskCompletionSource<bool>();
        var blocker = _coord.RunAsync(
            "fp-blocker", IdentitySlowPathKind.Pass2Match, 0.5,
            async ct => { blockerStarted.SetResult(true); await blockerRelease.Task; return "blocker"; },
            CancellationToken.None);
        await blockerStarted.Task;

        // Enqueue low-risk first, then high-risk. The high-risk should dequeue first.
        var executionOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var lowRisk = _coord.RunAsync(
            "fp-low", IdentitySlowPathKind.Pass2Match, riskScore: 0.1,
            ct => { executionOrder.Enqueue("low"); return Task.FromResult<string?>("low"); },
            CancellationToken.None);
        var highRisk = _coord.RunAsync(
            "fp-high", IdentitySlowPathKind.Pass2Match, riskScore: 0.95,
            ct => { executionOrder.Enqueue("high"); return Task.FromResult<string?>("high"); },
            CancellationToken.None);

        // Release the blocker so the worker drains the queue.
        blockerRelease.SetResult(true);
        await Task.WhenAll(blocker, lowRisk, highRisk);

        Assert.Equal(new[] { "high", "low" }, executionOrder.ToArray());
    }

    [Fact]
    public async Task RunAsync_OperatorTriggered_OutranksRegularRiskScore()
    {
        Make();
        var blockerStarted = new TaskCompletionSource<bool>();
        var blockerRelease = new TaskCompletionSource<bool>();
        var blocker = _coord.RunAsync(
            "fp-blocker", IdentitySlowPathKind.Pass2Match, 0.5,
            async ct => { blockerStarted.SetResult(true); await blockerRelease.Task; return "blocker"; },
            CancellationToken.None);
        await blockerStarted.Task;

        var executionOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var highRisk = _coord.RunAsync(
            "fp-h", IdentitySlowPathKind.Pass2Match, riskScore: 0.99,
            ct => { executionOrder.Enqueue("high-risk-pass2"); return Task.FromResult<string?>("h"); },
            CancellationToken.None);
        var operatorTriggered = _coord.RunAsync(
            "fp-op", IdentitySlowPathKind.OperatorReverify, riskScore: 0.0,
            ct => { executionOrder.Enqueue("operator"); return Task.FromResult<string?>("op"); },
            CancellationToken.None);

        blockerRelease.SetResult(true);
        await Task.WhenAll(blocker, highRisk, operatorTriggered);

        Assert.Equal(new[] { "operator", "high-risk-pass2" }, executionOrder.ToArray());
    }

    [Fact]
    public async Task RunAsync_PerFpQueuedCap_ShedsAdditional()
    {
        Make();
        var blockerStarted = new TaskCompletionSource<bool>();
        var blockerRelease = new TaskCompletionSource<bool>();
        // Hold the worker on a different fp so the queue accumulates.
        var blocker = _coord.RunAsync(
            "fp-blocker", IdentitySlowPathKind.Pass2Match, 0.5,
            async ct => { blockerStarted.SetResult(true); await blockerRelease.Task; return "b"; },
            CancellationToken.None);
        await blockerStarted.Task;

        // Cap is 2 — enqueue three for the same fp.
        var first = _coord.RunAsync("fp-x", IdentitySlowPathKind.Pass2Match, 0.5,
            ct => Task.FromResult<string?>("1"), CancellationToken.None);
        var second = _coord.RunAsync("fp-x", IdentitySlowPathKind.Pass2Match, 0.5,
            ct => Task.FromResult<string?>("2"), CancellationToken.None);
        var (thirdResult, thirdOutcome) = await _coord.RunAsync("fp-x", IdentitySlowPathKind.Pass2Match, 0.5,
            ct => Task.FromResult<string?>("3"), CancellationToken.None);

        Assert.Equal(SlowPathDispatchOutcome.SheddedSamePerFpCap, thirdOutcome);
        Assert.Null(thirdResult);

        blockerRelease.SetResult(true);
        await Task.WhenAll(blocker, first, second);
    }

    [Fact]
    public async Task RunAsync_QueueFull_DropsOldestLowestPriority()
    {
        Make(new IdentityCoordinatorOptions
        {
            MaxQueueDepth = 3,
            MaxQueuedPerFingerprint = 10,
            CoalesceWindowMs = 50,
            BreakerTripThreshold = 0.99,
            BreakerResetThreshold = 0.10,
            BreakerTripHoldSeconds = 60,
            BreakerResetHoldSeconds = 60
        });

        var blockerStarted = new TaskCompletionSource<bool>();
        var blockerRelease = new TaskCompletionSource<bool>();
        var blocker = _coord.RunAsync(
            "fp-blocker", IdentitySlowPathKind.Pass2Match, 0.5,
            async ct => { blockerStarted.SetResult(true); await blockerRelease.Task; return "b"; },
            CancellationToken.None);
        await blockerStarted.Task;

        // Fill the queue with one low-risk and two high-risk items.
        var low = _coord.RunAsync("fp-low", IdentitySlowPathKind.Pass2Match, 0.05,
            ct => Task.FromResult<string?>("low"), CancellationToken.None);
        var hi1 = _coord.RunAsync("fp-h1", IdentitySlowPathKind.Pass2Match, 0.9,
            ct => Task.FromResult<string?>("h1"), CancellationToken.None);
        var hi2 = _coord.RunAsync("fp-h2", IdentitySlowPathKind.Pass2Match, 0.9,
            ct => Task.FromResult<string?>("h2"), CancellationToken.None);

        // Queue is now full (3 items). Adding a fourth should drop the lowest-priority (low).
        var hi3 = _coord.RunAsync("fp-h3", IdentitySlowPathKind.Pass2Match, 0.9,
            ct => Task.FromResult<string?>("h3"), CancellationToken.None);

        var (lowResult, lowOutcome) = await low;
        Assert.Equal(SlowPathDispatchOutcome.SheddedQueueFull, lowOutcome);
        Assert.Null(lowResult);

        blockerRelease.SetResult(true);
        await Task.WhenAll(blocker, hi1, hi2, hi3);
    }

    [Fact]
    public async Task RunAsync_WithMultipleWorkers_DispatchesDifferentFingerprintsInParallel()
    {
        // WorkerCount > 1 should let two slow ops on different fps overlap. We measure by
        // starting both and asserting both reach their "started" markers before either
        // finishes — proves the second isn't queued behind the first.
        Make(new IdentityCoordinatorOptions
        {
            MaxQueueDepth = 10,
            MaxQueuedPerFingerprint = 2,
            CoalesceWindowMs = 50,
            BreakerTripThreshold = 0.99,
            BreakerResetThreshold = 0.30,
            BreakerTripHoldSeconds = 60,
            BreakerResetHoldSeconds = 60,
            WorkerCount = 4
        });

        var aStarted = new TaskCompletionSource<bool>();
        var bStarted = new TaskCompletionSource<bool>();
        var releaseAll = new TaskCompletionSource<bool>();

        var aTask = _coord.RunAsync("fp-a", IdentitySlowPathKind.Pass2Match, 0.5,
            async ct => { aStarted.SetResult(true); await releaseAll.Task; return "a"; },
            CancellationToken.None);
        var bTask = _coord.RunAsync("fp-b", IdentitySlowPathKind.Pass2Match, 0.5,
            async ct => { bStarted.SetResult(true); await releaseAll.Task; return "b"; },
            CancellationToken.None);

        // Both should reach "started" within a generous timeout if running in parallel.
        var bothStarted = Task.WhenAll(aStarted.Task, bStarted.Task);
        var winner = await Task.WhenAny(bothStarted, Task.Delay(2000));
        Assert.Same(bothStarted, winner);

        releaseAll.SetResult(true);
        await Task.WhenAll(aTask, bTask);
    }

    [Fact]
    public async Task GetDiagnostics_TracksCounters()
    {
        Make();
        await _coord.RunAsync("fp-1", IdentitySlowPathKind.Pass2Match, 0.5,
            ct => Task.FromResult<string?>("ok"), CancellationToken.None);

        var diag = _coord.GetDiagnostics();
        Assert.True(diag.TotalEnqueued >= 1);
        Assert.True(diag.TotalExecuted >= 1);
        Assert.False(diag.BreakerOpen);
    }
}
