using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Privacy;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.SiteProfiles;
using Mostlylucid.BotDetection.Test.Helpers;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.SiteProfiles;

/// <summary>
///     Pins the migration of request-scoped threshold consumers
///     (BotDetectionMiddleware, BlackboardOrchestrator) onto the per-request
///     <see cref="EffectiveThresholds"/> stamp: when the item is set on
///     <c>HttpContext.Items[HttpContextItemKeys.EffectiveThresholds]</c> the
///     effective value wins; when it's absent, the consumer falls back to the
///     global <see cref="BotDetectionOptions"/>. Exercised through the
///     middleware's IsBotKey side-effect (site: PopulateContextFromAggregated)
///     which reflects the same "probability >= threshold" comparison every
///     other migrated site uses.
/// </summary>
public class EffectiveThresholdConsumerTests
{
    private const double ProbabilityUnderTest = 0.55;

    private static readonly ILogger<BotDetectionMiddleware> Logger =
        new Mock<ILogger<BotDetectionMiddleware>>().Object;

    // ── Middleware: effective thresholds win over global when stamped ──────

    [Fact]
    public async Task Middleware_UsesEffectiveThreshold_WhenStampedOnItems()
    {
        // Global BotThreshold=0.9 would classify 0.55 as human. The per-domain
        // overlay says 0.5, which flips the same probability to bot.
        var options = new BotDetectionOptions
        {
#pragma warning disable CS0618
            BotThreshold = 0.9,
#pragma warning restore CS0618
        };

        var context = BuildContext();
        context.Items[HttpContextItemKeys.EffectiveThresholds] =
            new EffectiveThresholds(BotThreshold: 0.5, HumanCeiling: 0.3, BotFloor: 0.5);

        await InvokeMiddlewareAsync(context, options, ProbabilityUnderTest);

        Assert.True(context.Items.ContainsKey(BotDetectionMiddleware.IsBotKey));
        Assert.True((bool)context.Items[BotDetectionMiddleware.IsBotKey]!,
            "Effective threshold 0.5 should flip probability 0.55 to bot even though global 0.9 wouldn't.");
    }

    [Fact]
    public async Task Middleware_FallsBackToGlobalThreshold_WhenItemUnset()
    {
        // Same probability, no per-request stamp — the middleware must fall
        // back to _options.BotThreshold. Global 0.9 means 0.55 is human.
        var options = new BotDetectionOptions
        {
#pragma warning disable CS0618
            BotThreshold = 0.9,
#pragma warning restore CS0618
        };

        var context = BuildContext();
        // Deliberately do NOT stamp EffectiveThresholds.

        await InvokeMiddlewareAsync(context, options, ProbabilityUnderTest);

        Assert.True(context.Items.ContainsKey(BotDetectionMiddleware.IsBotKey));
        Assert.False((bool)context.Items[BotDetectionMiddleware.IsBotKey]!,
            "With no per-request stamp, the middleware must fall back to the global BotThreshold (0.9).");
    }

    [Fact]
    public async Task Middleware_GlobalThresholdBelowProbability_FlipsToBotWithoutStamp()
    {
        // Belt-and-braces: with global BotThreshold=0.5 and no stamp, 0.55 must
        // still classify as bot -- i.e. the fallback path preserves the
        // pre-migration behaviour identically.
        var options = new BotDetectionOptions
        {
#pragma warning disable CS0618
            BotThreshold = 0.5,
#pragma warning restore CS0618
        };

        var context = BuildContext();

        await InvokeMiddlewareAsync(context, options, ProbabilityUnderTest);

        Assert.True(context.Items.ContainsKey(BotDetectionMiddleware.IsBotKey));
        Assert.True((bool)context.Items[BotDetectionMiddleware.IsBotKey]!);
    }

    [Fact]
    public async Task Middleware_EffectiveThresholdAbovePrpobability_KeepsHumanEvenIfGlobalIsLower()
    {
        // Global 0.4 would say bot; per-domain overlay pulls the bar up to
        // 0.8, and 0.55 stays human. Confirms per-domain overrides can be
        // MORE permissive than global too.
        var options = new BotDetectionOptions
        {
#pragma warning disable CS0618
            BotThreshold = 0.4,
#pragma warning restore CS0618
        };

        var context = BuildContext();
        context.Items[HttpContextItemKeys.EffectiveThresholds] =
            new EffectiveThresholds(BotThreshold: 0.8, HumanCeiling: 0.3, BotFloor: 0.8);

        await InvokeMiddlewareAsync(context, options, ProbabilityUnderTest);

        Assert.True(context.Items.ContainsKey(BotDetectionMiddleware.IsBotKey));
        Assert.False((bool)context.Items[BotDetectionMiddleware.IsBotKey]!,
            "Effective threshold 0.8 should classify probability 0.55 as human, overriding global 0.4.");
    }

    // ── EffectiveThresholds boxing contract — the pattern every consumer uses ─

    [Fact]
    public void EffectiveThresholds_AsNullableCast_YieldsValue_WhenBoxedStructStored()
    {
        // Every migrated consumer relies on:
        //   context.Items[key] as EffectiveThresholds?
        // returning the value. Pin the contract so a refactor that stores the
        // wrong type (or omits boxing) is caught here, not in a live regression.
        var ctx = BuildContext();
        var thresholds = new EffectiveThresholds(0.5, 0.2, 0.6);
        ctx.Items[HttpContextItemKeys.EffectiveThresholds] = thresholds;

        var roundTripped = ctx.Items[HttpContextItemKeys.EffectiveThresholds] as EffectiveThresholds?;

        Assert.NotNull(roundTripped);
        Assert.Equal(0.5, roundTripped!.Value.BotThreshold);
        Assert.Equal(0.2, roundTripped.Value.HumanCeiling);
        Assert.Equal(0.6, roundTripped.Value.BotFloor);
    }

    [Fact]
    public void EffectiveThresholds_AsNullableCast_YieldsNull_WhenItemUnset()
    {
        var ctx = BuildContext();

        var missing = ctx.Items[HttpContextItemKeys.EffectiveThresholds] as EffectiveThresholds?;

        Assert.Null(missing);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task InvokeMiddlewareAsync(
        HttpContext context,
        BotDetectionOptions options,
        double probability)
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = probability,
            Confidence = 0.9,
            RiskBand = RiskBand.Medium,
            Signals = new Dictionary<string, object>(),
            CategoryBreakdown = new Dictionary<string, CategoryScore>(),
            ContributingDetectors = new HashSet<string>()
        };
        var orchestrator = BuildMockOrchestrator(evidence);
        var policyRegistry = BuildMockPolicyRegistry();
        var actionRegistry = new Mock<IActionPolicyRegistry>().Object;

        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new BotDetectionMiddleware(
            next,
            Logger,
            Options.Create(options),
            new DomainNormalizer(
                Options.Create(new DomainNormalizerOptions()),
                PublicSuffixList.LoadEmbedded()));

        await middleware.InvokeAsync(context, orchestrator, policyRegistry, actionRegistry, null);
    }

    private static HttpContext BuildContext()
    {
        var ctx = MockHttpContext.CreateRealisticBrowser();
        return ctx;
    }

    private static BlackboardOrchestrator BuildMockOrchestrator(AggregatedEvidence evidence)
    {
        var mock = new Mock<BlackboardOrchestrator>(
            Mock.Of<ILogger<BlackboardOrchestrator>>(),
            Options.Create(new BotDetectionOptions()),
            Enumerable.Empty<IContributingDetector>(),
            new PiiHasher(new byte[32]),
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        mock.Setup(o => o.DetectWithPolicyAsync(
                It.IsAny<HttpContext>(),
                It.IsAny<DetectionPolicy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(evidence);

        return mock.Object;
    }

    private static IPolicyRegistry BuildMockPolicyRegistry()
    {
        var mock = new Mock<IPolicyRegistry>();
        mock.Setup(p => p.GetPolicyForPath(It.IsAny<string>())).Returns(DetectionPolicy.Default);
        mock.Setup(p => p.GetPolicy(It.IsAny<string>())).Returns(DetectionPolicy.Default);
        return mock.Object;
    }
}