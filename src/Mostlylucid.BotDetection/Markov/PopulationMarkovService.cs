using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     Periodic Markov maintenance unit. Flushes cohort updates from
///     <see cref="MarkovTracker"/> on every tick and logs aggregated state on a
///     longer snapshot cadence.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(CohortFlushIntervalSeconds)</c> loop (default 5 s);
///         now subscribes to <see cref="TickCadence.Tick10s"/>. The default
///         cohort-flush cadence stretches from every 5 s to every 10 s --
///         <see cref="MarkovTracker.FlushCohortUpdates"/> is idempotent and the
///         buffered updates simply accumulate one extra tick before draining;
///         no behavioural change beyond the latency increase. Snapshot logging
///         honours the configured <c>SnapshotIntervalSeconds</c> via a
///         last-emitted gate inside the tick handler.
///     </para>
/// </summary>
public sealed class PopulationMarkovService : IDisposable
{
    private readonly ILogger<PopulationMarkovService> _logger;
    private readonly MarkovTracker _tracker;
    private readonly MarkovOptions _options;
    private readonly IDisposable _subscription;
    private DateTime _lastSnapshotUtc = DateTime.MinValue;
    private int _disposed;

    public PopulationMarkovService(
        ILogger<PopulationMarkovService> logger,
        MarkovTracker tracker,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator scheduleCoordinator)
    {
        ArgumentNullException.ThrowIfNull(scheduleCoordinator);
        _logger = logger;
        _tracker = tracker;
        _options = options.Value.Markov;

        _subscription = scheduleCoordinator.Subscribe(
            TickCadence.Tick10s,
            "PopulationMarkovService",
            CostHint.Low,
            OnTickAsync);
    }

    /// <summary>
    ///     One tick of Markov maintenance. Flushes cohort updates and emits a
    ///     snapshot log line once every <see cref="MarkovOptions.SnapshotIntervalSeconds"/>.
    ///     Public so tests can drive a single beat deterministically.
    /// </summary>
    public Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return Task.CompletedTask;

        try
        {
            // Flush pending cohort updates to baselines.
            _tracker.FlushCohortUpdates();

            // Periodic snapshot check on the configured cadence.
            var snapshotInterval = TimeSpan.FromSeconds(Math.Max(1, _options.SnapshotIntervalSeconds));
            if (now.UtcDateTime - _lastSnapshotUtc >= snapshotInterval)
            {
                var stats = _tracker.GetStats();
                _logger.LogInformation(
                    "Markov state: {Signatures} signatures, {Cohorts} cohorts, " +
                    "global baseline: {Nodes} nodes / {Edges} active edges",
                    stats.ActiveSignatures, stats.CohortCount,
                    stats.GlobalBaselineNodes, stats.GlobalBaselineEdges);

                // TODO: Snapshot to TimescaleDB via IMarkovSnapshotStore
                // when PostgreSQL storage is wired up.

                _lastSnapshotUtc = now.UtcDateTime;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Error in PopulationMarkovService tick");
        }

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