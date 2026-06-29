using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.AspNetPack.Logging;

/// <summary>
///     <see cref="ILoggerProvider"/> that captures the host's own <c>ILogger</c>
///     output (gateway startup, exceptions, <c>Yarp.ReverseProxy.*</c> upstream
///     errors, etc.) at the configured <see cref="LogSinkProviderOptions.MinLevel"/>
///     and fans each record out to every registered
///     <see cref="IGatewayLogIngestSink"/>. NO new store, NO channel, NO drainer:
///     the sink interface is the seam between FOSS capture and whichever atom
///     the commercial pack chose to hold the dashboard's read surface in.
///     <para>
///         Filtering is min-level only by design. Category include/exclude
///         allowlists belong on <see cref="Configuration.LogSinkOptions.AllowedCategories"/>
///         (which governs the SDK shipper), not here: the gateway's value to
///         operators IS surfacing whatever the framework chose to emit at the
///         operator-visible severity, and a separate category gate would silently
///         drop unfamiliar third-party categories without warning.
///     </para>
///     <para>
///         When the host registers zero <see cref="IGatewayLogIngestSink"/>
///         services (FOSS-only deployment, no commercial OtelMesh), the provider
///         resolves an empty collection and every log call short-circuits at
///         the fan-out step — operationally identical to never registering the
///         provider, no resource cost beyond an empty IEnumerable walk.
///     </para>
/// </summary>
public sealed class LogSinkLoggerProvider : ILoggerProvider
{
    private readonly IReadOnlyList<IGatewayLogIngestSink> _sinks;
    private readonly LogSinkProviderOptions _opts;
    private readonly ConcurrentDictionary<string, LogSinkLogger> _loggers = new();

    public LogSinkLoggerProvider(IEnumerable<IGatewayLogIngestSink> sinks, LogSinkProviderOptions opts)
    {
        // Materialise once: the sink list is a startup-time DI registration,
        // not a hot-reloadable set, and walking a snapshot array per call is
        // cheaper than re-enumerating the IEnumerable each time.
        _sinks = sinks?.ToArray() ?? Array.Empty<IGatewayLogIngestSink>();
        _opts  = opts ?? new LogSinkProviderOptions();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new LogSinkLogger(name, _sinks, _opts));

    public void Dispose() => _loggers.Clear();
}

/// <summary>
///     Per-category <see cref="ILogger"/> the provider hands out. Holds the
///     category name (so each emitted <see cref="LogRecord"/> carries the right
///     <c>Category</c> field without the provider re-allocating on every emit)
///     plus a reference to the shared sink list and options. Allocation-light:
///     the only per-call allocations are the formatted body string and the
///     <see cref="LogRecord"/> itself.
/// </summary>
internal sealed class LogSinkLogger : ILogger
{
    private readonly string _category;
    private readonly IReadOnlyList<IGatewayLogIngestSink> _sinks;
    private readonly LogSinkProviderOptions _opts;

    public LogSinkLogger(string category, IReadOnlyList<IGatewayLogIngestSink> sinks, LogSinkProviderOptions opts)
    {
        _category = category;
        _sinks    = sinks;
        _opts     = opts;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && logLevel >= _opts.MinLevel;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        if (_sinks.Count == 0) return;

        // Pull the fingerprint baggage off Activity.Current so logs that
        // happen inside a request scope land on the right per-fp ring. The
        // baggage key matches what StyloBotGatewayLogger uses (the OTLP
        // shipper); keeping them aligned means a fingerprint-tagged warning
        // shows up in the same ring regardless of which path emitted it.
        var activity = Activity.Current;
        var fp = activity?.GetBaggageItem("sb-fp");

        // Build the body once; if formatter throws (rare, but Serilog-style
        // structured loggers can), swallow the entry rather than crash the
        // emitting code path.
        string body;
        try
        {
            body = formatter(state, exception);
        }
        catch
        {
            return;
        }

        // Append a brief exception summary when present so operators on the
        // dashboard tile see "what blew up" without having to cross-reference
        // a separate file log. Stack frames stay in the file/console sinks.
        if (exception is not null)
        {
            var sep = string.IsNullOrEmpty(body) ? string.Empty : " | ";
            body = $"{body}{sep}{exception.GetType().Name}: {exception.Message}";
        }

        var entry = new GatewayLogEntry(
            TimestampUtc: DateTime.UtcNow,
            Level: logLevel,
            Category: _category,
            Body: body,
            TraceIdHex: activity?.TraceId.ToHexString(),
            SpanIdHex: activity?.SpanId.ToHexString(),
            FingerprintId: fp);

        // Fan out to every sink. Each sink swallows its own exceptions per
        // the IGatewayLogIngestSink contract -- a faulty sink must not take
        // down a logging call -- but we double-belt around it here too.
        for (var i = 0; i < _sinks.Count; i++)
        {
            try { _sinks[i].Record(entry); }
            catch { /* sink contract bans throws; defend against impl bugs */ }
        }
    }
}
