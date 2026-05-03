using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Orchestration.Telemetry;

/// <summary>
///     Dispatches detection reports to all registered <see cref="IFleetReporter"/> implementations.
///     Fire-and-forget: reporter failures never block the request pipeline.
///     FOSS default: no reporters registered → all calls are no-ops.
/// </summary>
public sealed class FleetReportDispatcher
{
    private readonly IReadOnlyList<IFleetReporter> _reporters;
    private readonly ILogger<FleetReportDispatcher> _logger;

    public FleetReportDispatcher(
        IEnumerable<IFleetReporter> reporters,
        ILogger<FleetReportDispatcher> logger)
    {
        _reporters = reporters.ToList();
        _logger = logger;
    }

    /// <summary>Whether any reporters are registered (fast-path check to avoid building reports).</summary>
    public bool HasReporters => _reporters.Count > 0;

    /// <summary>
    ///     Dispatch a report to all reporters. Failures are logged and swallowed.
    ///     Intended to be called on the request-completion path, so this method never throws.
    /// </summary>
    public async ValueTask DispatchAsync(DetectionReport report, CancellationToken ct = default)
    {
        if (_reporters.Count == 0) return;

        foreach (var reporter in _reporters)
        {
            try
            {
                await reporter.ReportAsync(report, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Request completing/cancelled - normal, don't log
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Fleet reporter {Name} failed to process report for {RequestId}",
                    reporter.Name, report.RequestId);
            }
        }
    }
}