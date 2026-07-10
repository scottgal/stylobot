using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins the orchestrator lifetime split that fixes the tail-latency collapse under load
///     (deploy- isolated load test 2026-07-10: p50 15ms, p95 5s). The expensive
///     DetectorOrchestrator + ~70-atom Register() wiring must be built ONCE in a singleton
///     <see cref="DetectionEngine"/>; only the small per-request <see cref="Mostlylucid.Ephemeral.SignalSink"/>
///     stays per-request on the scoped <see cref="BotDetectionOrchestrator"/> wrapper.
/// </summary>
public class DetectionEngineSingletonTests
{
    private static ServiceProvider Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:DatabasePath"] = string.Empty
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task DetectionEngine_is_a_singleton_built_once()
    {
        await using var provider = Build();

        var e1 = provider.GetRequiredService<DetectionEngine>();
        var e2 = provider.GetRequiredService<DetectionEngine>();

        Assert.Same(e1, e2); // built once, shared across the whole process
    }

    [Fact]
    public async Task Two_request_scopes_share_the_engine_but_get_their_own_SignalSink()
    {
        await using var provider = Build();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var o1 = scope1.ServiceProvider.GetRequiredService<BotDetectionOrchestrator>();
        var o2 = scope2.ServiceProvider.GetRequiredService<BotDetectionOrchestrator>();

        // Per-request wrapper is scoped -> distinct instances with distinct sinks...
        Assert.NotSame(o1, o2);
        Assert.NotSame(o1.SignalSink, o2.SignalSink);

        // ...but the expensive engine behind them is the SAME singleton (no per-request
        // DetectorOrchestrator rebuild / 70-atom Register storm).
        var engineFromRoot = provider.GetRequiredService<DetectionEngine>();
        var engineInScope1 = scope1.ServiceProvider.GetRequiredService<DetectionEngine>();
        Assert.Same(engineFromRoot, engineInScope1);
    }
}
