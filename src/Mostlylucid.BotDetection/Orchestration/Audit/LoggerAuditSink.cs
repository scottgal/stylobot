using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Orchestration.Audit;

public sealed class LoggerAuditSink : IAuditSink
{
    private readonly ILogger<LoggerAuditSink> _logger;
    private readonly LoggerAuditSinkOptions _options;

    public LoggerAuditSink(ILogger<LoggerAuditSink> logger, IOptions<AuditProcessorOptions> options)
    {
        _logger = logger;
        _options = options.Value.Logger;
    }

    public string Name => "logger";

    public ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        if (!_options.Enabled) return ValueTask.CompletedTask;

        _logger.Log(
            ToLogLevel(record.Severity),
            "StyloBot audit {AuditType} from {SourceProcessor} request={RequestId} path={Path} action={Action} risk={RiskBand} probability={BotProbability} confidence={Confidence} signals={Signals} detectorDeltas={DetectorDeltas} properties={Properties}",
            record.Type,
            record.SourceProcessor,
            record.RequestId,
            record.Path,
            record.Action,
            record.RiskBand,
            record.BotProbability,
            record.Confidence,
            record.Signals,
            record.DetectorDeltas,
            record.Properties);

        return ValueTask.CompletedTask;
    }

    private static LogLevel ToLogLevel(string severity)
        => severity.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "information" => LogLevel.Information,
            "info" => LogLevel.Information,
            "warning" => LogLevel.Warning,
            "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ => LogLevel.Information
        };
}
