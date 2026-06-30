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
///     Integration test for the per-endpoint action-policy override path.
///     Resolver unit tests (<see cref="EndpointActionPolicyResolverTests"/>)
///     prove the resolver returns the right name; THIS file proves the
///     middleware actually calls it AND threads the result into
///     <see cref="AggregatedEvidence.TriggeredActionPolicyName"/> so the
///     matching action policy fires. Without this, a future middleware
///     refactor could silently break the wiring while the resolver tests
///     stay green.
/// </summary>
public class BotDetectionMiddlewareEndpointOverrideTests
{
    [Fact]
    public async Task BotAction_Attribute_Override_FiresMatchingActionPolicy()
    {
        var recording = new RecordingActionPolicy("recording-override");
        var registry = BuildRealRegistry(recording);
        var middleware = NewMiddleware();
        var ctx = NewContext(metadata: new BotActionAttribute("recording-override"));

        await middleware.InvokeAsync(ctx,
            NewOrchestrator(BotEvidence()).Object,
            NewDetectionRegistry().Object,
            registry,
            responseCoordinator: null!);

        Assert.Equal(1, recording.HitCount);
    }

    [Fact]
    public async Task BotPolicy_ActionPolicy_Override_FiresMatchingActionPolicy()
    {
        var recording = new RecordingActionPolicy("recording-override");
        var registry = BuildRealRegistry(recording);
        var middleware = NewMiddleware();
        var ctx = NewContext(metadata:
            new BotPolicyAttribute("strict") { ActionPolicy = "recording-override" });

        await middleware.InvokeAsync(ctx,
            NewOrchestrator(BotEvidence()).Object,
            NewDetectionRegistry().Object,
            registry,
            responseCoordinator: null!);

        Assert.Equal(1, recording.HitCount);
    }

    [Fact]
    public async Task NoEndpointAttribute_DoesNotInvokeOverridePolicy()
    {
        // Regression: an endpoint without any BotAction / BotPolicy.ActionPolicy
        // attribute must NOT have a stale override applied. Verifies the
        // wiring guards against accidental fallthrough.
        var recording = new RecordingActionPolicy("recording-override");
        var registry = BuildRealRegistry(recording);
        var middleware = NewMiddleware();
        var ctx = NewContext(metadata: null);

        await middleware.InvokeAsync(ctx,
            NewOrchestrator(BotEvidence()).Object,
            NewDetectionRegistry().Object,
            registry,
            responseCoordinator: null!);

        Assert.Equal(0, recording.HitCount);
    }

    [Fact]
    public async Task EvidenceWithPreExistingTriggeredPolicy_OverrideDoesNotReplaceIt()
    {
        // Higher-precedence overrides (honeypot, API-key trust, internal-network)
        // run before the endpoint resolver and set TriggeredActionPolicyName
        // themselves. The resolver must NOT clobber a pre-set name -- it only
        // fires when the slot is empty.
        var honeypotPolicy = new RecordingActionPolicy("honeypot-stub");
        var endpointPolicy = new RecordingActionPolicy("endpoint-override");
        var registry = BuildRealRegistry(honeypotPolicy, endpointPolicy);
        var middleware = NewMiddleware();
        var ctx = NewContext(metadata: new BotActionAttribute("endpoint-override"));

        var evidence = BotEvidence() with { TriggeredActionPolicyName = "honeypot-stub" };
        await middleware.InvokeAsync(ctx,
            NewOrchestrator(evidence).Object,
            NewDetectionRegistry().Object,
            registry,
            responseCoordinator: null!);

        Assert.Equal(1, honeypotPolicy.HitCount);
        Assert.Equal(0, endpointPolicy.HitCount);
    }

    // === Helpers ===========================================================

    private static BotDetectionMiddleware NewMiddleware()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        return new BotDetectionMiddleware(
            next,
            NullLogger<BotDetectionMiddleware>.Instance,
            Options.Create(new BotDetectionOptions()),
            new FossLicenseState());
    }

    private static HttpContext NewContext(object? metadata)
    {
        // Middleware calls context.RequestServices.GetService<...>() on its
        // hot path; a minimal ServiceProvider keeps that safe.
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        ctx.Request.Path = "/";
        ctx.Request.Headers.UserAgent = "Mozilla/5.0";
        if (metadata is not null)
        {
            ctx.SetEndpoint(new Endpoint(
                requestDelegate: _ => Task.CompletedTask,
                metadata: new EndpointMetadataCollection(metadata),
                displayName: "test"));
        }
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
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
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
        public RecordingActionPolicy(string name) => Name = name;
        public string Name { get; }
        public ActionType ActionType => ActionType.Block;
        public PolicyIntent Intent => PolicyIntent.Block;
        public int HitCount { get; private set; }

        public Task<ActionResult> ExecuteAsync(HttpContext c, AggregatedEvidence e, CancellationToken ct = default)
        {
            HitCount++;
            return Task.FromResult(ActionResult.Blocked(403, "recording stub"));
        }
    }
}
