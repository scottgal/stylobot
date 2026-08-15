using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Scheduling;

/// <summary>
///     THE irreducible bootstrap watchdog for <see cref="ScheduleCoordinator"/>.
///     <para>
///         <b>No other service should follow this pattern.</b> Per
///         <c>feedback_no_background_services</c>, every periodic unit in the
///         project subscribes to <see cref="IScheduleCoordinator"/> ticks rather
///         than inheriting <see cref="BackgroundService"/>. This watchdog is the
///         deliberate exception, justified by the Wave 2 migration plan's
///         "biggest risk identified": with 50+ subscribers depending on the
///         coordinator, a silent cadence-loop crash means mass detection
///         blindness with no in-process recovery. The watchdog forces the
///         process to exit so a process supervisor (K8s, systemd, Docker
///         restart=always) brings it back up.
///     </para>
///     <para>
///         The watchdog observes per-cadence freshness by subscribing to each
///         non-1s cadence and recording its most recent tick timestamp. Every
///         <see cref="ScheduleCoordinatorOptions.WatchdogCheckInterval"/> it
///         walks each observed cadence and checks "is last tick older than
///         <c>period * StalenessMultiplier</c>?" If so, it logs a critical
///         event and calls <see cref="IHostApplicationLifetime.StopApplication"/>.
///     </para>
///     <para>
///         <b>Tick1s is intentionally excluded</b> from the watch list. Its
///         period is so short relative to GC pauses and scheduler hiccups that
///         a 2x staleness threshold would generate false positives on a
///         healthy host. The four observed cadences (Tick10s, Tick1m, Tick5m,
///         Tick1h) provide enough coverage to detect a stuck coordinator: if
///         any one of them silently stops, the per-cadence loop has crashed.
///     </para>
///     <para>
///         All thresholds are on <see cref="ScheduleCoordinatorOptions"/>
///         (staleness multiplier, check interval, startup grace) per the
///         project's "all settings configurable" rule. Operators can turn the
///         watchdog off entirely via
///         <see cref="ScheduleCoordinatorOptions.EnableWatchdog"/>.
///     </para>
/// </summary>
internal sealed class ScheduleCoordinatorWatchdog : BackgroundService
{
    private static readonly (TickCadence Cadence, TimeSpan Period)[] WatchedCadences =
    {
        (TickCadence.Tick10s, TimeSpan.FromSeconds(10)),
        (TickCadence.Tick1m,  TimeSpan.FromMinutes(1)),
        (TickCadence.Tick5m,  TimeSpan.FromMinutes(5)),
        (TickCadence.Tick1h,  TimeSpan.FromHours(1)),
    };

    private readonly IScheduleCoordinator _coordinator;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly ScheduleCoordinatorOptions _options;
    private readonly ILogger<ScheduleCoordinatorWatchdog> _logger;

    private readonly Dictionary<TickCadence, long> _lastTickUtcTicks = new();
    private readonly List<IDisposable> _subscriptions = new();
    private long _startedAtUtcTicks;

    // Fault-storm bookkeeping: per-subscriber FaultCount observed at the
    // previous check + the set of keys whose baseline was already captured
    // (the first observation of a subscriber only records the baseline, it
    // never triggers — a subscriber that was already faulting before the
    // watchdog started must not trip the storm check on first sight).
    private readonly Dictionary<string, int> _lastFaultCounts = new();
    private readonly HashSet<string> _faultBaselineSeen = new();

    public ScheduleCoordinatorWatchdog(
        IScheduleCoordinator coordinator,
        IHostApplicationLifetime lifetime,
        IOptions<ScheduleCoordinatorOptions> options,
        ILogger<ScheduleCoordinatorWatchdog> logger,
        TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator;
        _lifetime = lifetime;
        _options = options.Value ?? new ScheduleCoordinatorOptions();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Subscribe at construction (canonical Wave 1 shape) so tests that
        // call EvaluateOnce directly see the captured handlers without having
        // to spin ExecuteAsync. Disabled? No subscription, no observation, no
        // firing -- ExecuteAsync exits early below.
        if (_options.EnableWatchdog)
            SubscribeToWatchedCadences();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableWatchdog)
        {
            _logger.LogInformation(
                "ScheduleCoordinatorWatchdog disabled (BotDetection:Scheduling:EnableWatchdog=false).");
            return;
        }

        _startedAtUtcTicks = _timeProvider.GetUtcNow().UtcTicks;

        _logger.LogInformation(
            "ScheduleCoordinatorWatchdog: monitoring cadences {Cadences} at interval={Interval}, staleness={Multiplier}x, startupGrace={Grace}",
            string.Join(",", WatchedCadences.Select(c => c.Cadence)),
            _options.WatchdogCheckInterval,
            _options.WatchdogStalenessMultiplier,
            _options.WatchdogStartupGrace);

        try
        {
            using var timer = new PeriodicTimer(_options.WatchdogCheckInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                EvaluateOnce(_timeProvider.GetUtcNow());
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        finally
        {
            DisposeSubscriptions();
        }
    }

    /// <summary>
    ///     Walk each watched cadence and trigger
    ///     <see cref="IHostApplicationLifetime.StopApplication"/> if any has
    ///     gone silent past its staleness threshold. Internal so tests can
    ///     drive the check deterministically without spinning a timer.
    /// </summary>
    internal void EvaluateOnce(DateTimeOffset now)
    {
        var startedAt = Volatile.Read(ref _startedAtUtcTicks);
        if (startedAt == 0)
        {
            // ExecuteAsync hasn't run yet -- this is a direct test invocation;
            // anchor to "now" so the grace window is measured against the
            // first evaluation call rather than process start.
            startedAt = now.UtcTicks;
            Volatile.Write(ref _startedAtUtcTicks, startedAt);
        }

        var startupGraceUntil = new DateTimeOffset(startedAt, TimeSpan.Zero) + _options.WatchdogStartupGrace;
        if (now < startupGraceUntil) return;

        foreach (var (cadence, period) in WatchedCadences)
        {
            var threshold = TimeSpan.FromTicks((long)(period.Ticks * _options.WatchdogStalenessMultiplier));
            long lastTicks;
            lock (_lastTickUtcTicks)
            {
                _lastTickUtcTicks.TryGetValue(cadence, out lastTicks);
            }

            // If we never observed a tick AND the grace window is over, the
            // cadence has been silent since boot -- treat that as a fault.
            // Anchor the "since when" to the startup instant so the elapsed
            // calculation is honest.
            var lastSeenTicks = lastTicks == 0 ? startedAt : lastTicks;
            var sinceLast = now - new DateTimeOffset(lastSeenTicks, TimeSpan.Zero);
            if (sinceLast > threshold)
            {
                _logger.LogCritical(
                    "ScheduleCoordinatorWatchdog: cadence {Cadence} silent for {Elapsed} (>{Threshold}); forcing process exit so the supervisor can restart.",
                    cadence, sinceLast, threshold);
                _lifetime.StopApplication();
                return; // one stop is enough; don't log per-cadence ftl spam
            }
        }

        if (TryDetectFaultStorm())
        {
            _lifetime.StopApplication();
        }
    }

    /// <summary>
    ///     Fault-storm detection: a subscriber that throws on EVERY tick makes
    ///     its work silently dead while the cadence itself looks healthy (the
    ///     2026-08-15 staging 01:29 freeze class -- ticks flowing, the sweep's
    ///     aggregate persist never ran). The silence check above cannot see
    ///     that; this check watches each watched cadence's subscriber
    ///     <c>FaultCount</c> grow between checks and reports a storm past
    ///     <see cref="ScheduleCoordinatorOptions.WatchdogFaultStormThreshold"/>.
    ///     Returns <c>true</c> when a storm is detected (the caller stops the
    ///     application). The first observation of a subscriber only records the
    ///     baseline and can never trigger.
    /// </summary>
    private bool TryDetectFaultStorm()
    {
        var threshold = _options.WatchdogFaultStormThreshold;
        if (threshold <= 0) return false;

        var watched = WatchedCadences.Select(c => c.Cadence).ToHashSet();
        var storm = false;
        lock (_lastFaultCounts)
        {
            foreach (var meta in _coordinator.Snapshot())
            {
                if (!watched.Contains(meta.Cadence)) continue;
                var key = $"{meta.Cadence}:{meta.SubscriberName}";
                if (!_faultBaselineSeen.Add(key))
                {
                    var prev = _lastFaultCounts.GetValueOrDefault(key, 0);
                    var delta = meta.FaultCount - prev;
                    if (delta >= threshold)
                    {
                        _logger.LogCritical(
                            "ScheduleCoordinatorWatchdog: subscriber {Subscriber} on {Cadence} faulted {Delta}x since the last check (>{Threshold}); forcing process exit so the supervisor can restart.",
                            meta.SubscriberName, meta.Cadence, delta, threshold);
                        storm = true;
                    }
                }
                _lastFaultCounts[key] = meta.FaultCount;
            }
        }
        return storm;
    }

    private void SubscribeToWatchedCadences()
    {
        foreach (var (cadence, _) in WatchedCadences)
        {
            var captured = cadence;
            var handle = _coordinator.Subscribe(
                captured,
                $"ScheduleCoordinatorWatchdog.{captured}",
                CostHint.Low,
                (now, _) =>
                {
                    lock (_lastTickUtcTicks) _lastTickUtcTicks[captured] = now.UtcTicks;
                    return Task.CompletedTask;
                });
            _subscriptions.Add(handle);
        }
    }

    private void DisposeSubscriptions()
    {
        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); }
            catch { /* coordinator already torn down */ }
        }
        _subscriptions.Clear();
    }

    public override void Dispose()
    {
        DisposeSubscriptions();
        base.Dispose();
    }
}