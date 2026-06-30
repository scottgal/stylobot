using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Middleware;

/// <summary>
///     Regression-locks the bug where the middleware called the downstream
///     pipeline after an action policy returned <c>Continue=false</c>. A
///     throttle/block policy that writes a 429 to the response and then has
///     YARP forward-proxy invoked on top of it throws
///     "The response has already started" and 5xx's every actioned request.
///     The fix is to short-circuit on <c>!actionResult.Continue</c>; the
///     test driver uses a <see cref="RequestDelegate"/> that throws when
///     invoked, which is what catches the pre-fix bug.
/// </summary>
public class BotDetectionMiddlewareActionContinueTests
{
    [Fact]
    public async Task PerBotType_FallbackPolicy_ContinueFalse_DoesNotInvokeNext()
    {
        // Scraper -> "throttle-aggressive" via the default BotTypeActionPolicies map.
        // The stub returns Blocked(429) which has Continue=false.
        var throttle = new RecordingActionPolicy("throttle-aggressive", continueDownstream: false);
        var registry = BuildRealRegistry(throttle);
        var nextInvoked = false;
        var middleware = NewMiddleware(_ =>
        {
            nextInvoked = true;
            throw new InvalidOperationException(
                "_next must NOT be invoked after an action policy returned Continue=false");
        });

        var ctx = NewContext();
        await middleware.InvokeAsync(ctx,
            NewOrchestrator(BotEvidence()).Object,
            NewDetectionRegistry().Object,
            registry,
            responseCoordinator: null!);

        Assert.Equal(1, throttle.HitCount);
        Assert.False(nextInvoked);
    }

    [Fact]
    public async Task PerBotType_FallbackPolicy_ContinueTrue_InvokesNext()
    {
        // logonly-style policies return Allowed (Continue=true). The middleware
        // must STILL call _next so the downstream handler can produce a real
        // response.
        var allow = new RecordingActionPolicy("throttle-aggressive", continueDownstream: true);
        var registry = BuildRealRegistry(allow);
        var nextInvoked = false;
        var middleware = NewMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        var ctx = NewContext();
        await middleware.InvokeAsync(ctx,
            NewOrchestrator(BotEvidence()).Object,
            NewDetectionRegistry().Object,
            registry,
            responseCoordinator: null!);

        Assert.Equal(1, allow.HitCount);
        Assert.True(nextInvoked);
    }

    [Fact]
    public async Task ExplicitTriggeredActionPolicy_ContinueFalse_DoesNotInvokeNext()
    {
        // Regression-lock: the explicit-TriggeredActionPolicyName path
        // already had a correct `if (!actionResult.Continue) return;` guard
        // but no test drove a throwing _next behind it.
        var explicitPolicy = new RecordingActionPolicy("recording-explicit", continueDownstream: false);
        var registry = BuildRealRegistry(explicitPolicy);
        var nextInvoked = false;
        var middleware = NewMiddleware(_ =>
        {
            nextInvoked = true;
            throw new InvalidOperationException(
                "_next must NOT be invoked after an action policy returned Continue=false");
        });

        var evidence = BotEvidence() with { TriggeredActionPolicyName = "recording-explicit" };
        var ctx = NewContext();
        await middleware.InvokeAsync(ctx,
            NewOrchestrator(evidence).Object,
            NewDetectionRegistry().Object,
            registry,
            responseCoordinator: null!);

        Assert.Equal(1, explicitPolicy.HitCount);
        Assert.False(nextInvoked);
    }

    // === Helpers ===========================================================

    private static BotDetectionMiddleware NewMiddleware(RequestDelegate next) =>
        new BotDetectionMiddleware(
            next,
            NullLogger<BotDetectionMiddleware>.Instance,
            Options.Create(new BotDetectionOptions()),
            new FossLicenseState());

    private static HttpContext NewContext()
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        ctx.Request.Path = "/";
        ctx.Request.Headers.UserAgent = "Mozilla/5.0";
        return ctx;
    }

    private static AggregatedEvidence BotEvidence() => new()
    {
        Ledger = new DetectionLedger("test"),
        BotProbability = 0.95,
        Confidence = 1.0,
        RiskBand = RiskBand.VeryHigh,
        PrimaryBotType = BotType.Scraper,
        CategoryBreakdown = new Dictionary<string, CategoryScore>(),
        Signals = new Dictionary<string, object>(),
        ContributingDetectors = new HashSet<string>(),
        FailedDetectors = new HashSet<string>(),
    };

    private static Mock<BlackboardOrchestrator> NewOrchestrator(AggregatedEvidence evidence)
    {
        var m = new Mock<BlackboardOrchestrator>(
            Mock.Of<ILogger<BlackboardOrchestrator>>(),
            Options.Create(new BotDetectionOptions()),
            Enumerable.Empty<IContributingDetector>(),
            new PiiHasher(new byte[32]),
            null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        m.Setup(o => o.DetectWithPolicyAsync(
                It.IsAny<HttpContext>(),
                It.IsAny<DetectionPolicy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(evidence);
        return m;
    }

    private static Mock<IPolicyRegistry> NewDetectionRegistry()
    {
        var m = new Mock<IPolicyRegistry>();
        m.Setup(p => p.GetPolicyForPath(It.IsAny<string>())).Returns(DetectionPolicy.Default);
        m.Setup(p => p.GetPolicy(It.IsAny<string>())).Returns(DetectionPolicy.Default);
        return m;
    }

    private static IActionPolicyRegistry BuildRealRegistry(params IActionPolicy[] extras) =>
        new ActionPolicyRegistry(
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>(),
            extras,
            NullLogger<ActionPolicyRegistry>.Instance);

    private sealed class RecordingActionPolicy : IActionPolicy
    {
        private readonly bool _continueDownstream;

        public RecordingActionPolicy(string name, bool continueDownstream)
        {
            Name = name;
            _continueDownstream = continueDownstream;
        }

        public string Name { get; }
        public ActionType ActionType => _continueDownstream ? ActionType.LogOnly : ActionType.Block;
        public PolicyIntent Intent => _continueDownstream ? PolicyIntent.Pass : PolicyIntent.Block;
        public int HitCount { get; private set; }

        public Task<ActionResult> ExecuteAsync(HttpContext c, AggregatedEvidence e, CancellationToken ct = default)
        {
            HitCount++;
            return Task.FromResult(_continueDownstream
                ? ActionResult.Allowed("recording stub")
                : ActionResult.Blocked(429, "recording stub"));
        }
    }
}