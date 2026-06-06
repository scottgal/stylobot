using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Observability.Signals;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Observability.Test;

public class BlackboardSignalLogBridgeTests
{
    private sealed class CapturingLogger : ILogger<StyloBotSignalCategory>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static EphemeralDetectionOrchestrator NewOrchestrator() =>
        new(NullLogger<EphemeralDetectionOrchestrator>.Instance,
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IContributingDetector>());

    [Fact]
    public async Task Bridge_forwards_global_signals_to_logger()
    {
        var orchestrator = NewOrchestrator();
        var logger = new CapturingLogger();
        var bridge = new BlackboardSignalLogBridge(
            orchestrator,
            logger,
            Options.Create(new BlackboardSignalLogOptions()));

        await bridge.StartAsync(CancellationToken.None);
        orchestrator.RaiseSignalForObservability("error.detector.crash", "wp-scanner");
        await bridge.StopAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Error);
        logger.Entries[0].Message.Should().Contain("error.detector.crash");
    }

    [Fact]
    public async Task Bridge_respects_exclude_prefixes()
    {
        var orchestrator = NewOrchestrator();
        var logger = new CapturingLogger();
        var opts = new BlackboardSignalLogOptions { ExcludePrefixes = { "noise." } };
        var bridge = new BlackboardSignalLogBridge(orchestrator, logger, Options.Create(opts));

        await bridge.StartAsync(CancellationToken.None);
        orchestrator.RaiseSignalForObservability("noise.tick");
        orchestrator.RaiseSignalForObservability("warning.threshold");
        await bridge.StopAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle(e => e.Message.Contains("warning.threshold"));
    }

    [Fact]
    public async Task Bridge_is_inert_when_disabled()
    {
        var orchestrator = NewOrchestrator();
        var logger = new CapturingLogger();
        var bridge = new BlackboardSignalLogBridge(
            orchestrator,
            logger,
            Options.Create(new BlackboardSignalLogOptions { Enabled = false }));

        await bridge.StartAsync(CancellationToken.None);
        orchestrator.RaiseSignalForObservability("warning.x");
        await bridge.StopAsync(CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }
}
