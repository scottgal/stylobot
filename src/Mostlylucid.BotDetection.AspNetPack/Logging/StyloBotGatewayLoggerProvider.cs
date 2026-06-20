using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.AspNetPack.Configuration;

namespace Mostlylucid.BotDetection.AspNetPack.Logging;

public sealed class StyloBotGatewayLoggerProvider : ILoggerProvider
{
    private readonly IOptions<LogSinkOptions> _opts;
    private readonly ConcurrentDictionary<string, StyloBotGatewayLogger> _loggers = new();

    public Channel<LogRecord> Channel { get; }

    public StyloBotGatewayLoggerProvider(IOptions<LogSinkOptions> opts)
    {
        _opts = opts;
        Channel = System.Threading.Channels.Channel.CreateBounded<LogRecord>(
            new BoundedChannelOptions(opts.Value.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true, SingleWriter = false
            });
    }

    public ILogger CreateLogger(string categoryName)
    {
        if (!CategoryAllowed(categoryName)) return NullLogger.Instance;
        return _loggers.GetOrAdd(categoryName,
            cat => new StyloBotGatewayLogger(cat, Channel.Writer, _opts.Value.MinLevel));
    }

    private bool CategoryAllowed(string category)
    {
        var allowed = _opts.Value.AllowedCategories;
        if (allowed.Length == 0) return false;
        foreach (var pattern in allowed)
        {
            if (pattern == "*") return true;
            if (pattern.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = pattern[..^2];
                if (category == prefix || category.StartsWith(prefix + ".", StringComparison.Ordinal))
                    return true;
            }
            else if (category == pattern) return true;
        }
        return false;
    }

    public void Dispose()
    {
        Channel.Writer.TryComplete();
        _loggers.Clear();
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f) { }
    }
}
