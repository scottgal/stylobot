using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Hard rule from operator 2026-06-20: "BOTS ARE NEVER HUMAN."
///     A YAML/Arcjet catalogue match like Bytespider, Googlebot, GPTBot, etc.
///     declares the request a bot regardless of the probability sigmoid.
///     Probability is a confidence axis on TOP of identity, not a gate beneath
///     it. Bytespider hovering at 0.3 probability was surfaced as Human on the
///     staging dashboard while the BotName column said "Bytespider" -- the
///     contradiction this suite forbids.
///
///     Pins both layers:
///       (1) ToAggregatedEvidence: catalogue identity feeds isActuallyBot so
///           PrimaryBotType populates correctly.
///       (2) BuildDetectionFromEvidence: PrimaryBotType beats the
///           probability-only IsBot gate at the dashboard event boundary.
/// </summary>
public class CatalogIdentityIsBotTests
{
    [Fact]
    public void Catalog_identified_Bytespider_below_threshold_still_classifies_as_bot()
    {
        var ledger = new DetectionLedger("test-bytespider-cat");
        // Single weak contribution -- not enough to push botProbability past 0.5.
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Header",
            Category = "Header",
            ConfidenceDelta = -0.2,
            Weight = 0.5,
            Reason = "Header pattern marginal",
        });
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = "Mozilla/5.0 (compatible; Bytespider; spider-feedback@bytedance.com)",
            [SignalKeys.UserAgentBotName] = "Bytespider",
        };

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        // Catalog identifies Bytespider as AiBot; that's the authoritative bot
        // signal, even though botProbability < 0.5.
        Assert.NotNull(evidence.PrimaryBotType);
        Assert.Equal(BotType.AiBot, evidence.PrimaryBotType);
    }

    [Fact]
    public void Dashboard_event_IsBot_true_when_PrimaryBotType_authoritative()
    {
        var middleware = new DetectionBroadcastMiddleware(
            _ => System.Threading.Tasks.Task.CompletedTask,
            NullLogger<DetectionBroadcastMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Response.StatusCode = 200;
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();

        // Hand-crafted evidence: probability below threshold, but authoritative
        // bot type set -- the catalog/orchestrator decided this is an AI bot.
        var evidence = new AggregatedEvidence
        {
            Ledger = new DetectionLedger("test-event"),
            BotProbability = 0.3,
            Confidence = 1.0,
            RiskBand = Mostlylucid.BotDetection.Orchestration.RiskBand.Unknown,
            PrimaryBotType = BotType.AiBot,
            PrimaryBotName = "Bytespider",
            Signals = new Dictionary<string, object>(),
        };

        var detection = middleware.BuildDetectionFromEvidence(ctx, evidence);

        Assert.True(detection.IsBot, "AiBot PrimaryBotType must classify as bot regardless of probability");
        Assert.Equal("AiBot", detection.BotType);
    }

    [Fact]
    public void Dashboard_event_IsBot_false_when_no_authoritative_type_and_low_probability()
    {
        // Sanity: a genuine human (no catalog match, low probability) stays Human.
        var middleware = new DetectionBroadcastMiddleware(
            _ => System.Threading.Tasks.Task.CompletedTask,
            NullLogger<DetectionBroadcastMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Response.StatusCode = 200;
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();

        var evidence = new AggregatedEvidence
        {
            Ledger = new DetectionLedger("test-human"),
            BotProbability = 0.2,
            Confidence = 1.0,
            RiskBand = Mostlylucid.BotDetection.Orchestration.RiskBand.VeryLow,
            PrimaryBotType = null,
            PrimaryBotName = "Mac Chrome 149",
            Signals = new Dictionary<string, object>(),
        };

        var detection = middleware.BuildDetectionFromEvidence(ctx, evidence);

        Assert.False(detection.IsBot, "no catalog match + low probability stays human");
    }

    [Fact]
    public void Dashboard_event_IsBot_true_for_high_probability_even_without_type()
    {
        // Existing behaviour preserved: high probability still pushes IsBot=true
        // when no authoritative type is set. The catalog identity is an
        // ADDITIONAL path to IsBot=true, not a replacement.
        var middleware = new DetectionBroadcastMiddleware(
            _ => System.Threading.Tasks.Task.CompletedTask,
            NullLogger<DetectionBroadcastMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Response.StatusCode = 200;
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();

        var evidence = new AggregatedEvidence
        {
            Ledger = new DetectionLedger("test-high-prob"),
            BotProbability = 0.95,
            Confidence = 1.0,
            RiskBand = Mostlylucid.BotDetection.Orchestration.RiskBand.High,
            PrimaryBotType = null,
            PrimaryBotName = "unknown-pattern",
            Signals = new Dictionary<string, object>(),
        };

        var detection = middleware.BuildDetectionFromEvidence(ctx, evidence);

        Assert.True(detection.IsBot);
    }
}
