using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Observability.Events;
using Mostlylucid.BotDetection.Orchestration.Telemetry;

namespace Mostlylucid.BotDetection.Observability.Test;

public class ObservabilityServiceCollectionExtensionsTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    [Fact]
    public void Registers_SerilogDetectionEventPublisher_by_default()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDetectionEventPublisher, NullDetectionEventPublisher>();
        services.AddLogging();

        services.AddStyloBotObservability(EmptyConfig());

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IDetectionEventPublisher>()
            .Should().BeOfType<SerilogDetectionEventPublisher>();
    }

    [Fact]
    public void PublishDetectionEventsToSerilog_false_leaves_existing_publisher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDetectionEventPublisher, NullDetectionEventPublisher>();
        services.AddLogging();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BotDetection:Observability:PublishDetectionEventsToSerilog"] = "false"
        }).Build();

        services.AddStyloBotObservability(config);

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IDetectionEventPublisher>()
            .Should().BeOfType<NullDetectionEventPublisher>();
    }
}
