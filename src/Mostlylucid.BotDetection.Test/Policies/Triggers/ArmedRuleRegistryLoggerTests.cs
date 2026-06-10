using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Triggers;

namespace Mostlylucid.BotDetection.Test.Policies.Triggers;

/// <summary>
///     Wave 5: <see cref="ArmedRuleRegistry"/> now accepts an optional
///     <see cref="ILogger"/> so a faulting transition subscriber surfaces at
///     Warning instead of being silently swallowed. Pin both halves of the
///     contract: the loop still survives a throwing subscriber, AND the logger
///     captures a Warning entry naming the rule id.
/// </summary>
public sealed class ArmedRuleRegistryLoggerTests
{
    [Fact]
    public void Faulting_subscriber_is_isolated_and_logged_at_warning()
    {
        var recorder = new RecordingLogger();
        var registry = new ArmedRuleRegistry(recorder);

        var ruleId = Guid.NewGuid();
        var observedTransitions = 0;

        registry.OnTransition += (_, _) => throw new InvalidOperationException("boom");
        registry.OnTransition += (_, _) => Interlocked.Increment(ref observedTransitions);

        // Must not throw even though the first subscriber faults; the second
        // subscriber still runs because the registry hand-isolates each
        // delegate in the invocation list.
        registry.RaiseTransition(ruleId, nowArmed: true, at: DateTimeOffset.UnixEpoch);

        Assert.Equal(1, observedTransitions);
        Assert.Equal(1, recorder.WarningCount);
        Assert.Contains(recorder.Warnings, w => w.Contains(ruleId.ToString()));
        Assert.Contains(recorder.Warnings, w => w.Contains("InvalidOperationException"));
    }

    [Fact]
    public void Null_logger_constructor_still_isolates_subscriber_faults()
    {
        // Default-shape registry (no ILogger plumbed) keeps the historical
        // silent-swallow behaviour. The loop must still survive.
        var registry = new ArmedRuleRegistry();
        registry.OnTransition += (_, _) => throw new InvalidOperationException("boom");
        registry.RaiseTransition(Guid.NewGuid(), nowArmed: true, at: DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingLogger : ILogger<ArmedRuleRegistry>
    {
        private int _warningCount;
        private readonly List<string> _warnings = new();
        private readonly object _gate = new();

        public int WarningCount => Volatile.Read(ref _warningCount);
        public IReadOnlyList<string> Warnings
        {
            get { lock (_gate) return _warnings.ToArray(); }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            Interlocked.Increment(ref _warningCount);
            var rendered = formatter(state, exception);
            // Include the exception's type-name so the test can assert on it
            // without the formatter having to spell it out.
            if (exception is not null) rendered += " " + exception.GetType().FullName;
            lock (_gate) _warnings.Add(rendered);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
