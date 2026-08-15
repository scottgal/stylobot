using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     The session-idle sweep's cadence driver (2026-08-15): on each Tick10s it asks
///     the signals-native <see cref="SessionStore"/> to finalize every session whose
///     LAST contribution is older than <see cref="SessionStoreOptions.MaxIdle"/> — the
///     stopped/paused class the gap boundary and the sliding TTL never catch under
///     continuous-ish load (the operator's "the distance since the last request, that
///     is the threshold"). The finalize lands via the store's onEvict Lifecycle chain;
///     this host only drives the cadence. Same shape as the legacy
///     SessionPersistenceService tick.
/// </summary>
public sealed class SessionIdleSweepHostedService : IHostedService, IDisposable
{
    private readonly SessionStore _store;
    private readonly IScheduleCoordinator? _schedule;
    private readonly ILogger<SessionIdleSweepHostedService>? _logger;
    private IDisposable? _tickSub;

    public SessionIdleSweepHostedService(
        SessionStore store,
        IScheduleCoordinator? schedule = null,
        ILogger<SessionIdleSweepHostedService>? logger = null)
    {
        _store = store;
        _schedule = schedule;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Optional so viewer-mode / minimal hosts without a tick fabric still construct.
        if (_schedule is null) return Task.CompletedTask;
        try
        {
            _tickSub = _schedule.Subscribe(
                TickCadence.Tick10s,
                nameof(SessionIdleSweepHostedService),
                CostHint.Low,
                OnTickAsync);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SessionIdleSweepHostedService: failed to subscribe to Tick10s.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tickSub?.Dispose();
        _tickSub = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _tickSub?.Dispose();

    private async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await _store.FinalizeIdleSessionsAsync(now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Session idle sweep failed; retrying next tick");
        }
    }
}
