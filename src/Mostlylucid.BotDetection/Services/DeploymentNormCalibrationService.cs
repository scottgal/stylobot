using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Logs population-calibration warm-up state at startup and announces when the
///     deployment norm has been established. During warm-up, population-calibrated
///     detectors (Http2Fingerprint, Header) emit neutral contributions rather than
///     bot signals — this service makes that behaviour visible in the log so operators
///     are not confused by missing penalties on the first N requests.
/// </summary>
public sealed class DeploymentNormCalibrationService : BackgroundService
{
    private readonly DeploymentNormTracker _tracker;
    private readonly int _warmupRequests;
    private readonly ILogger<DeploymentNormCalibrationService> _logger;

    public DeploymentNormCalibrationService(
        DeploymentNormTracker tracker,
        IOptions<BotDetectionOptions> options,
        ILogger<DeploymentNormCalibrationService> logger)
    {
        _tracker = tracker;
        _warmupRequests = options.Value.PopulationWarmupRequests;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Population norm calibration started — penalties suppressed for first {WarmupRequests} requests " +
            "while deployment norms (HTTP/2 rate, Accept header prevalence, etc.) are established. " +
            "Override via BotDetection:PopulationWarmupRequests",
            _warmupRequests);

        // Poll until warm-up completes; 1-second resolution is fine since this is purely informational.
        try
        {
            while (_tracker.IsWarmingUp && !stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (!stoppingToken.IsCancellationRequested)
            _logger.LogInformation(
                "Population norm calibration complete — {ObservedRequests} requests observed, " +
                "deployment-specific penalties now active",
                _tracker.TotalRequests);
    }
}
