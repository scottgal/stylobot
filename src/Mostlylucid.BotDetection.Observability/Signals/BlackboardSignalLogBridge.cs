using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Observability.Signals;

/// <summary>
///     Subscribes to the orchestrator's global blackboard signal stream and emits each
///     signal as a structured log entry on <c>ILogger&lt;StyloBotSignalCategory&gt;</c>.
///     Drops signals matching ExcludePrefixes; restricts to IncludePrefixes when configured.
///     Level inference is heuristic based on the signal name prefix:
///       fatal/critical -&gt; Critical, error -&gt; Error, warn/warning -&gt; Warning,
///       debug -&gt; Debug, trace -&gt; Trace, anything else -&gt; Information.
/// </summary>
public sealed class BlackboardSignalLogBridge : IHostedService, IDisposable
{
    private readonly IDetectionOrchestrator _orchestrator;
    private readonly ILogger<StyloBotSignalCategory> _logger;
    private readonly BlackboardSignalLogOptions _options;
    private IDisposable? _subscription;

    public BlackboardSignalLogBridge(
        IDetectionOrchestrator orchestrator,
        ILogger<StyloBotSignalCategory> logger,
        IOptions<BlackboardSignalLogOptions> options)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value ?? new BlackboardSignalLogOptions();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return Task.CompletedTask;

        _subscription = _orchestrator.SubscribeToSignals(OnSignal);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnSignal(SignalEvent evt)
    {
        var name = evt.Signal ?? string.Empty;
        if (_options.ExcludePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return;
        if (_options.IncludePrefixes.Count > 0 &&
            !_options.IncludePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return;

        var level = InferLevel(name);
        if (!_logger.IsEnabled(level)) return;

        var eventId = new EventId(unchecked((int)(evt.OperationId % int.MaxValue)), evt.Key ?? evt.Signal);
        _logger.Log(
            level,
            eventId,
            "StyloBot signal: {Signal} op={OperationId} key={SignalKey}",
            evt.Signal,
            evt.OperationId,
            evt.Key ?? string.Empty);
    }

    private static LogLevel InferLevel(string name)
    {
        if (string.IsNullOrEmpty(name)) return LogLevel.Information;
        var first = name.Split('.', ':')[0].ToLowerInvariant();
        return first switch
        {
            "fatal" or "critical" => LogLevel.Critical,
            "error" => LogLevel.Error,
            "warn" or "warning" => LogLevel.Warning,
            "debug" => LogLevel.Debug,
            "trace" => LogLevel.Trace,
            _ => LogLevel.Information
        };
    }
}
