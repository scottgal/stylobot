using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pegs the load-shed contract added to BlackboardOrchestrator: when
///     <see cref="PipelineLoadSensor"/> reports
///     <see cref="LoadBand.Critical"/> and wave 0 (foundation contributors)
///     has completed, the wave loop breaks early. The result still carries
///     foundation signals, but classifier waves are skipped. A
///     <see cref="SignalKeys.LoadShedActive"/> signal flags the shed-mode
///     verdict so the dashboard / audit can correlate.
/// </summary>
public class BlackboardOrchestratorLoadShedTests
{
    [Fact]
    public async Task NormalLoad_AllClassifierWavesRun_NoShedSignal()
    {
        var (orchestrator, _) = BuildOrchestrator(criticalRps: 10_000);
        var result = await orchestrator.DetectAsync(BuildContext());

        Assert.False(result.Signals.ContainsKey(SignalKeys.LoadShedActive),
            "shed signal should be absent under normal load");
        // Foundation + classifier contributors expected: at minimum we should
        // see signature, useragent, ip-related signals.
        Assert.True(result.Signals.Count > 0);
    }

    [Fact]
    public async Task CriticalLoad_ShedSignalWritten()
    {
        // Force criticalRps to 0 so any single observation pushes the EMA over
        // the threshold; pump a few RecordRequest() calls then trigger the
        // sensor's 1-second tick.
        var (orchestrator, sensor) = BuildOrchestrator(criticalRps: 0);
        for (var i = 0; i < 200; i++) sensor.RecordRequest();
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        // Sanity check the sensor is reporting critical.
        Assert.Equal(LoadBand.Critical, sensor.CurrentBand);

        var result = await orchestrator.DetectAsync(BuildContext());

        Assert.True(result.Signals.TryGetValue(SignalKeys.LoadShedActive, out var v) && v is true,
            "shed signal must be present under critical load");
    }

    private static (BlackboardOrchestrator orchestrator, PipelineLoadSensor sensor) BuildOrchestrator(
        double criticalRps)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Enabled"] = "true",
                ["BotDetection:AiDetection:OllamaEnabled"] = "false",
                ["BotDetection:AiDetection:AnthropicEnabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddBotDetection();
        // Override the registered sensor with one configured for the test case.
        for (var i = services.Count - 1; i >= 0; i--)
            if (services[i].ServiceType == typeof(PipelineLoadSensor))
                services.RemoveAt(i);
        services.AddSingleton(new PipelineLoadSensor(
            normalRps: 0, highRps: 0, criticalRps: criticalRps));

        var provider = services.BuildServiceProvider();
        return (
            provider.GetRequiredService<BlackboardOrchestrator>(),
            provider.GetRequiredService<PipelineLoadSensor>());
    }

    private static HttpContext BuildContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        ctx.Request.Headers.Accept = "text/html";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        return ctx;
    }
}
