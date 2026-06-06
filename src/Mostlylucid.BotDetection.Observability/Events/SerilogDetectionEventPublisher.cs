using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration.Telemetry;

namespace Mostlylucid.BotDetection.Observability.Events;

/// <summary>
///     Emits each <see cref="DetectionEvent"/> as one structured log entry on the host's
///     ILogger pipeline. When the host is wired with Serilog, this is what people will
///     call the "StyloBot Serilog sink": properties land in Datadog / Seq / Splunk / Loki
///     with the StyloBot_* prefix so customers can query and dashboard against them.
/// </summary>
public sealed class SerilogDetectionEventPublisher : IDetectionEventPublisher
{
    private readonly ILogger<SerilogDetectionEventPublisher> _logger;

    public SerilogDetectionEventPublisher(ILogger<SerilogDetectionEventPublisher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "serilog";

    public ValueTask PublishAsync(DetectionEvent evt, CancellationToken ct = default)
    {
        if (evt is null) return ValueTask.CompletedTask;

        var level = LevelFor(evt);
        if (!_logger.IsEnabled(level)) return ValueTask.CompletedTask;

#pragma warning disable CA2254 // Template is a const; properties are positional by design.
        _logger.Log(level, DetectionEventLogProperties.MessageTemplate, evt.ToLogArgs());
#pragma warning restore CA2254

        return ValueTask.CompletedTask;
    }

    private static LogLevel LevelFor(DetectionEvent evt)
    {
        if (string.Equals(evt.Action, "block", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;
        if (string.Equals(evt.Action, "challenge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Action, "throttle-tools", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Action, "throttle-stealth", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Action, "throttle-status", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Action, "redirect-honeypot", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Information;
        return evt.IsBot ? LogLevel.Information : LogLevel.Debug;
    }
}
