using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Subscribes to <see cref="TickCadence.Tick10s"/> on the
///     <see cref="IScheduleCoordinator"/> and persists a
///     <see cref="DegradationSnapshot"/> of <see cref="DegradationAtom"/>'s
///     current EWMA arms via <see cref="IDashboardEventStore.RecordDegradationSnapshotAsync"/>.
///     <para>
///         Replaces the deleted <c>DegradationHistorySampler</c> +
///         <c>DegradationHistoryAtom</c> pair. The atom ring lost the
///         whole window on restart -- the user could not see what they
///         called "all the data" because nothing was actually persisted.
///         Per <c>feedback_no_inmemory_stores</c> the only acceptable
///         backing for a dashboard history surface is SQLite / Postgres
///         via <see cref="IDashboardEventStore"/>.
///     </para>
///     <para>
///         Per <c>feedback_no_background_services</c>: NOT a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>.
///         The coordinator owns the only cadence loop in the project;
///         this service just subscribes a handler.
///     </para>
/// </summary>
public sealed class DegradationStoreSampler : IDisposable
{
    private readonly DegradationAtom? _atom;
    private readonly IDashboardEventStore _store;
    private readonly ILogger<DegradationStoreSampler> _logger;
    private readonly IDisposable? _subscription;
    private int _disposed;

    /// <summary>
    ///     DI-friendly constructor: resolves DegradationAtom and
    ///     IScheduleCoordinator from IServiceProvider via GetService (not
    ///     GetRequiredService) so they really ARE optional. The earlier
    ///     `DegradationAtom? atom = null` parameter relied on a default
    ///     value that Microsoft.Extensions.DependencyInjection does NOT
    ///     honour — DI sees a nullable parameter and still tries to
    ///     resolve it, throwing if not registered. That broke marketing
    ///     (remote-mode host) on boot per feedback_remote_mode_optional_di.
    /// </summary>
    public DegradationStoreSampler(
        IServiceProvider services,
        IDashboardEventStore store,
        ILogger<DegradationStoreSampler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(services);
        _atom = services.GetService<DegradationAtom>();
        _store = store;
        _logger = logger;

        // atom is optional per feedback_remote_mode_optional_di: marketing-site host
        // does not register DegradationAtom (the gateway owns the response-recording
        // hot path). With no atom, this sampler is inert; gateway's instance does the
        // work and persists the history that the website's dashboard reads via REST.
        if (_atom is null) return;

        // scheduleCoordinator is optional so existing direct-construction tests
        // keep working — tests drive OnTickAsync directly.
        var scheduleCoordinator = services.GetService<IScheduleCoordinator>();
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "DegradationStoreSampler",
                CostHint.Low,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     Direct-construction overload preserved for tests.
    /// </summary>
    public DegradationStoreSampler(
        IDashboardEventStore store,
        ILogger<DegradationStoreSampler> logger,
        DegradationAtom? atom,
        IScheduleCoordinator? scheduleCoordinator)
    {
        ArgumentNullException.ThrowIfNull(store);
        _atom = atom;
        _store = store;
        _logger = logger;

        if (_atom is null) return;

        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "DegradationStoreSampler",
                CostHint.Low,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     The schedule-coordinator tick handler. Public so tests can drive
    ///     a single beat deterministically without spinning up the
    ///     coordinator.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (_atom is null) return; // remote-mode host without DegradationAtom

        try
        {
            // Reuse the same LatencyEmaMs slot for both p50 + p95 until the
            // atom grows distinct percentile estimators; the snapshot shape
            // already has two fields so the future change is additive.
            var latency = _atom.GetSignalValue(DegradationAtom.LatencyEmaMs);
            var snap = new DegradationSnapshot(
                TimestampUtc: now.UtcDateTime,
                Latency5xxRate: _atom.GetSignalValue(DegradationAtom.Error5xxRate),
                Latency4xxRate: _atom.GetSignalValue(DegradationAtom.Error4xxRate),
                Latency429Rate: _atom.GetSignalValue(DegradationAtom.Rate429),
                LatencyP50Ms: latency,
                LatencyP95Ms: latency,
                NotFoundRate: _atom.GetSignalValue(DegradationAtom.NotFoundRate));
            await _store.RecordDegradationSnapshotAsync(snap, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DegradationStoreSampler: tick failed");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}
