using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Logs population-calibration warm-up state at startup and announces when the
///     deployment norm has been established. During warm-up, population-calibrated
///     detectors (Http2Fingerprint, Header) emit neutral contributions rather than
///     bot signals -- this service makes that behaviour visible in the log so operators
///     are not confused by missing penalties on the first N requests.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> polling
///         <see cref="DeploymentNormTracker.IsWarmingUp"/> every second; now
///         subscribes to <see cref="TickCadence.Tick1s"/> on
///         <see cref="IScheduleCoordinator"/>. Once warm-up completes the
///         subscription disposes itself from inside the handler, matching the
///         original "loop exits, log line fires once" semantics. See
///         <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
public sealed class DeploymentNormCalibrationService : IDisposable
{
    private readonly DeploymentNormTracker _tracker;
    private readonly int _warmupRequests;
    private readonly ILogger<DeploymentNormCalibrationService> _logger;
    private readonly IDisposable _subscription;
    private int _completionAnnounced;
    private int _disposed;

    public DeploymentNormCalibrationService(
        DeploymentNormTracker tracker,
        IOptions<BotDetectionOptions> options,
        ILogger<DeploymentNormCalibrationService> logger,
        IScheduleCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _tracker = tracker;
        _warmupRequests = options.Value.PopulationWarmupRequests;
        _logger = logger;

        _logger.LogInformation(
            "Population norm calibration started — penalties suppressed for first {WarmupRequests} requests " +
            "while deployment norms (HTTP/2 rate, Accept header prevalence, etc.) are established. " +
            "Override via BotDetection:PopulationWarmupRequests",
            _warmupRequests);

        _subscription = coordinator.Subscribe(
            TickCadence.Tick1s,
            "DeploymentNormCalibrationService",
            CostHint.Low,
            OnTickAsync);
    }

    /// <summary>
    ///     The coordinator's tick handler. Drives the warm-up watcher body that
    ///     used to live in <c>ExecuteAsync</c>'s 1-second polling loop. Once
    ///     warm-up completes, the handler logs the completion line once, then
    ///     disposes the subscription so subsequent ticks are not delivered.
    ///     Public so tests can drive a single beat deterministically.
    /// </summary>
    public Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return Task.CompletedTask;
        if (_tracker.IsWarmingUp) return Task.CompletedTask;

        // First non-warming tick: announce completion exactly once, then drop
        // the subscription so the coordinator stops fanning ticks into us.
        if (Interlocked.Exchange(ref _completionAnnounced, 1) != 0) return Task.CompletedTask;

        _logger.LogInformation(
            "Population norm calibration complete — {ObservedRequests} requests observed, " +
            "deployment-specific penalties now active",
            _tracker.TotalRequests);

        // Dispose from inside the handler is supported (the coordinator
        // single-flights per subscription so this completes before any
        // re-entry can occur). Defensive try/catch in case the coordinator
        // is already tearing down.
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}