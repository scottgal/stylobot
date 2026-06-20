using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.AspNetPack.Logging;

public sealed class StyloBotGatewayLogger : ILogger
{
    private readonly string _category;
    private readonly ChannelWriter<LogRecord> _writer;
    private readonly LogLevel _minLevel;

    public StyloBotGatewayLogger(string category, ChannelWriter<LogRecord> writer, LogLevel minLevel)
    {
        _category = category;
        _writer = writer;
        _minLevel = minLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var activity = Activity.Current;
        var fp = activity?.GetBaggageItem("sb-fp");
        var record = new LogRecord(
            TimestampUtc: DateTime.UtcNow,
            Level: logLevel,
            Category: _category,
            Body: formatter(state, exception),
            TraceIdHex: activity?.TraceId.ToHexString(),
            SpanIdHex: activity?.SpanId.ToHexString(),
            FingerprintId: fp,
            Attributes: new Dictionary<string, string>());

        _writer.TryWrite(record);
    }
}
