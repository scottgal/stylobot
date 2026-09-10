using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Middleware;

namespace Mostlylucid.BotDetection.Test.Middleware;

/// <summary>
///     ONE cut for is_bot: the configured <see cref="ClassificationOptions.BotFloor"/>. Three
///     surfaces used to take that classification decision with a literal <c>0.5</c> of their own,
///     so a visitor between 0.5 and BotFloor rendered as a bot on the forwarded-result header and
///     the downstream display model while every aggregate, the stored session <c>is_bot</c> and
///     <c>HttpContext.IsBot()</c> called them human. These tests pin the literal out.
/// </summary>
public sealed class BotFloorIsBotCutTests
{
    private const double Floor = 0.75;

    /// <summary>Below BotFloor but above the old 0.5 literal -- the band that used to disagree.</summary>
    private const double DisagreeingProbability = 0.60;

    private const double AboveFloorProbability = 0.80;

    private static DefaultHttpContext ContextWith(double probability)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<BotDetectionOptions>>(Options.Create(new BotDetectionOptions
        {
            Classification = new ClassificationOptions { BotFloor = Floor }
        }));

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = new AggregatedEvidence
        {
            BotProbability = probability,
            Confidence = 0.9,
            RiskBand = RiskBand.Medium,
            TotalProcessingTimeMs = 1,
            ContributingDetectors = new HashSet<string>(),
            Signals = new Dictionary<string, object>()
        };
        return context;
    }

    [Fact]
    public async Task Edge_forwarded_result_header_uses_BotFloor()
    {
        var middleware = new StyloBotForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new BotDetectionOptions
            {
                ForwardedHeaders = new EdgeForwardedHeadersOptions { EmitOnForwardedRequest = true }
            }),
            NullLogger<StyloBotForwardedHeadersMiddleware>.Instance);

        var below = ContextWith(DisagreeingProbability);
        await middleware.InvokeAsync(below);

        below.Request.Headers[StyloBotEdgeHeaderNames.Result].ToString().Should().Be("false",
            "0.60 is below the configured BotFloor; the old 0.5 literal called this a bot");

        var above = ContextWith(AboveFloorProbability);
        await middleware.InvokeAsync(above);

        above.Request.Headers[StyloBotEdgeHeaderNames.Result].ToString().Should().Be("true");
    }

    [Fact]
    public void Yarp_full_result_header_uses_BotFloor()
    {
        var context = ContextWith(DisagreeingProbability);
        var headers = new Dictionary<string, string>();

        context.AddBotDetectionHeadersFull((name, value) => headers[name] = value);

        headers["X-Bot-Detection-Result"].Should().Be("false",
            "the YARP writer must agree with the edge writer and with the classifier");
    }

    [Fact]
    public async Task Hydrator_is_bot_uses_BotFloor()
    {
        var middleware = new StyloBotForwardedHeadersHydratorMiddleware(_ => Task.CompletedTask);
        var context = ContextWith(0.0);
        context.Request.Headers[StyloBotEdgeHeaderNames.Probability] = "0.600";

        await middleware.InvokeAsync(context);

        var result = context.Items["BotDetectionResult"] as BotDetectionResult;
        result.Should().NotBeNull("the hydrator reconstructs the verdict from the forwarded headers");
        result!.IsBot.Should().BeFalse(
            "0.60 is below the configured BotFloor; re-deriving it here with a literal is a second opinion");
    }

    /// <summary>
    ///     The canonical accessor the rest of the system already used
    ///     (<c>HttpContext.IsBot()</c> → <c>GetBotDetectionResult</c>) must resolve the same floor,
    ///     so a surface that reads it cannot drift from these headers.
    /// </summary>
    [Fact]
    public void Canonical_accessor_and_the_headers_agree()
    {
        var context = ContextWith(DisagreeingProbability);

        context.IsBot().Should().BeFalse();
        context.GetBotFloor().Should().Be(Floor);
    }
}
