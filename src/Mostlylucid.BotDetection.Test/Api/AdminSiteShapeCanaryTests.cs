using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Api;

/// <summary>
///     C6 canary: the admin console's own request shape must stay below the block threshold of
///     the endpoint it reads.
///     <para>
///         Why: `/api/v1/config/*` carries `WithApiBotPolicy(BlockThreshold = 0.95)` while the
///         commercial groups the AdminSite also calls do not. If our own client ever scores at or
///         above 0.95 there, the config reads 403 the moment enforcement is switched on. That is a
///         latent break, not a bypass question — the remedy would be to fix what makes our client
///         look like a bot, never to exempt its key.
///     </para>
///     <para>
///         The shape under test is exactly what `GatewayControlClient` produces: GET, `Accept:
///         application/json`, an API-key context, a private peer IP, and **no User-Agent at all**
///         (its `ConfigureHeaders` sets only the key and Accept). Measured at 0.30 on the real
///         wired pipeline when this canary was written — well clear of 0.95, which is why the
///         latent break is recorded as conditional rather than live.
///     </para>
///     <para>
///         FIDELITY CAVEAT, stated so the number is not over-read: this drives `AddBotDetection`
///         with a bare container, not the gateway's deployed configuration (transport trust,
///         health endpoints, policy stack). It isolates the CLIENT's contribution; the deployed
///         classification is only measurable against a running gateway. If this canary ever
///         fails, that is a real signal to re-measure there before assuming the threshold bites.
///     </para>
/// </summary>
public sealed class AdminSiteShapeCanaryTests
{
    /// <summary>The endpoint group's per-endpoint block threshold (`WithApiBotPolicy`).</summary>
    private const double EndpointBlockThreshold = 0.95;

    [Fact]
    public async Task The_admin_sites_own_request_shape_stays_below_the_config_endpoints_block_threshold()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:DatabasePath"] = Path.Combine(Path.GetTempPath(), $"sb-canary-{Guid.NewGuid():N}.db"),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpContextAccessor();
        services.AddBotDetection();

        await using var provider = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = provider };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/v1/config/manifests";
        ctx.Request.Host = new HostString("gateway:8080");
        ctx.Request.Headers.Accept = "application/json";
        ctx.Request.Headers["X-SB-Api-Key"] = "canary-not-a-real-key";
        // Mimic ApiKeyContextMiddleware (this harness runs the orchestrator, not the pipeline).
        ctx.Items["BotDetection.ApiKeyContext"] = new ApiKeyContext
        {
            KeyName = "admin-site-control",
            DisabledDetectors = Array.Empty<string>(),
            WeightOverrides = new Dictionary<string, double>(),
            DetectionPolicyName = null,
        };
        // A cluster pod IP — the AdminSite's peer address as the gateway sees it.
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.42.0.15");
        ctx.TraceIdentifier = "canary-" + Guid.NewGuid().ToString("N")[..8];

        var orchestrator = provider.GetRequiredService<BotDetectionOrchestrator>();
        var evidence = await orchestrator.DetectAsync(ctx);

        // The shape is faithful: no UA is the load-bearing part of it.
        Assert.True(evidence.Signals.ContainsKey("ua.empty"), "the canary must exercise a UA-less request");

        Assert.True(evidence.BotProbability < EndpointBlockThreshold,
            $"the AdminSite's own shape scored {evidence.BotProbability:F4}, at or above the config " +
            $"endpoint's {EndpointBlockThreshold} block threshold (type={evidence.PrimaryBotType}, " +
            $"justification='{evidence.RiskJustification}'). If this fires, our own client would be " +
            "refused once enforcement is on — fix what makes it look like a bot (an honest User-Agent " +
            "is the first candidate), never exempt its key.");
    }
}
