using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Scheduling;

/// <summary>
///     Canonical tick.* signal source for the FOSS project.
///     <para>
///         <b>Architectural exception:</b> this is the one and only
///         <see cref="IHostedService"/> the project promotes by convention.
///         Per the <c>feedback_no_background_services</c> memory, the ~39
///         <see cref="BackgroundService"/> instances the project had
///         accumulated are each spinning their own timer loop -- the convention
///         is to centralise that into ONE coordinator that emits stable
///         <see cref="TickCadence"/> ticks at wall-clock boundaries and let
///         units subscribe via <see cref="Subscribe"/>.
///     </para>
///     <para>
///         Wave 1 of the architectural-drift remediation ships this coordinator
///         and its interface. Wave 2 migrates
///         <c>MeterTriggerService</c>, <c>LocalMeterStream</c> eviction, and
///         <c>RemoteMeterStream</c> poll to subscribe here instead of inheriting
///         <see cref="BackgroundService"/>. Nothing else should inherit
///         <see cref="BackgroundService"/> after that point.
///     </para>
///     <para>
///         Cadences fire on stable wall-clock boundaries -- <see cref="TickCadence.Tick1m"/>
///         at the top of every UTC minute, not "60s after the last fire" -- so
///         dashboards and metrics across runs see aligned timestamps. Each
///         subscription is single-flight (no re-entry while the previous tick is
///         still running), fault-isolated (one subscriber's throw does not stop
///         siblings), and cancellation-aware on shutdown.
///     </para>
///     <para>
///         The coordinator does NOT publish its ticks on the existing blackboard
///         signal sink. v1 keeps subscription direct-callback; the spec author's
///         note: "subscribe against ticks" without prescribing the bus. A future
///         wave can mirror ticks onto the blackboard if the diagnostics value
///         outweighs the second hop.
///     </para>
/// </summary>
public sealed class ScheduleCoordinator : IScheduleCoordinator, IHostedService, IAsyncDisposable
{
    private readonly ScheduleCoordinatorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScheduleCoordinator> _logger;

    // One list per cadence. CopyOnWrite via lock; reads snapshot the reference
    // and walk without contention. Subscriber counts are tiny (tens, not thousands),
    // a ConcurrentBag-style replacement would be over-engineered.
    private readonly object _subscribersGate = new();
    private Dictionary<TickCadence, List<Subscription>> _subscribers;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<Task> _cadenceLoops = new();
    private int _running; // 0 / 1, set via Interlocked

    /// <summary>
    ///     Production constructor. <paramref name="timeProvider"/> is left
    ///     nullable so the default DI registration can omit it; tests inject
    ///     <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c> directly.
    /// </summary>
    public ScheduleCoordinator(
        IOptions<ScheduleCoordinatorOptions> options,
        ILogger<ScheduleCoordinator> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value ?? new ScheduleCoordinatorOptions();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _subscribers = new Dictionary<TickCadence, List<Subscription>>
        {
            [TickCadence.Tick1s]  = new(),
            [TickCadence.Tick10s] = new(),
            [TickCadence.Tick1m]  = new(),
            [TickCadence.Tick5m]  = new(),
            [TickCadence.Tick1h]  = new()
        };
    }

    /// <inheritdoc />
    public IDisposable Subscribe(
        TickCadence cadence,
        string subscriberName,
        CostHint costHint,
        Func<DateTimeOffset, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(subscriberName))
            throw new ArgumentException("Subscriber name is required.", nameof(subscriberName));

        var sub = new Subscription(cadence, subscriberName, costHint, handler);

        lock (_subscribersGate)
        {
            // CopyOnWrite so the live cadence loop walks an immutable list.
            var snapshot = new Dictionary<TickCadence, List<Subscription>>(_subscribers);
            var bucket = new List<Subscription>(snapshot[cadence]) { sub };
            snapshot[cadence] = bucket;
            _subscribers = snapshot;
        }

        _logger.LogDebug(
            "ScheduleCoordinator: subscribed {Name} ({Hint}) to {Cadence}",
            subscriberName, costHint, cadence);

        return new Unsubscriber(this, sub);
    }

    /// <inheritdoc />
    public IReadOnlyList<TickSubscriberMetadata> Snapshot()
    {
        var snapshot = _subscribers;
        var result = new List<TickSubscriberMetadata>(capacity: 16);
        foreach (var (cadence, list) in snapshot)
        {
            foreach (var sub in list)
                result.Add(sub.Snapshot());
        }
        return result;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return Task.CompletedTask;

        _logger.LogInformation("ScheduleCoordinator starting (Tick1s={T1s}, Tick10s={T10s}, Tick1m={T1m}, Tick5m={T5m}, Tick1h={T1h})",
            _options.EnableTick1s, _options.EnableTick10s, _options.EnableTick1m,
            _options.EnableTick5m, _options.EnableTick1h);

        if (_options.EnableTick1s)  _cadenceLoops.Add(RunCadenceLoop(TickCadence.Tick1s,  TimeSpan.FromSeconds(1)));
        if (_options.EnableTick10s) _cadenceLoops.Add(RunCadenceLoop(TickCadence.Tick10s, TimeSpan.FromSeconds(10)));
        if (_options.EnableTick1m)  _cadenceLoops.Add(RunCadenceLoop(TickCadence.Tick1m,  TimeSpan.FromMinutes(1)));
        if (_options.EnableTick5m)  _cadenceLoops.Add(RunCadenceLoop(TickCadence.Tick5m,  TimeSpan.FromMinutes(5)));
        if (_options.EnableTick1h)  _cadenceLoops.Add(RunCadenceLoop(TickCadence.Tick1h,  TimeSpan.FromHours(1)));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Always cancel the shutdown CTS, even if StartAsync was never called.
        // This lets the test pump (TickOnceAsync) handlers observe shutdown
        // cancellation through their handler-bound linked token.
        if (!_shutdownCts.IsCancellationRequested)
        {
            _logger.LogInformation("ScheduleCoordinator stopping.");
            _shutdownCts.Cancel();
        }

        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        try
        {
            await Task.WhenAll(_cadenceLoops).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScheduleCoordinator: cadence loop fault on shutdown.");
        }
        finally
        {
            _cadenceLoops.Clear();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _running) == 1)
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _shutdownCts.Dispose();
    }

    // ---- Internal test hook --------------------------------------------------

    /// <summary>
    ///     Pump one tick of the given cadence synchronously through the
    ///     subscriber list. Tests call this instead of waiting on the
    ///     <see cref="PeriodicTimer"/> so they run deterministically.
    /// </summary>
    /// <remarks>
    ///     <c>internal</c> + <c>InternalsVisibleTo("Mostlylucid.BotDetection.Test")</c>.
    ///     Mirrors the <c>MeterTriggerService.TickOnceAsync</c> pattern already
    ///     used in the codebase.
    /// </remarks>
    internal Task TickOnceAsync(TickCadence cadence, CancellationToken ct = default)
    {
        var enabled = cadence switch
        {
            TickCadence.Tick1s  => _options.EnableTick1s,
            TickCadence.Tick10s => _options.EnableTick10s,
            TickCadence.Tick1m  => _options.EnableTick1m,
            TickCadence.Tick5m  => _options.EnableTick5m,
            TickCadence.Tick1h  => _options.EnableTick1h,
            _ => false
        };
        if (!enabled) return Task.CompletedTask;

        var now = _timeProvider.GetUtcNow();
        return FireTickAsync(cadence, now, ct);
    }

    /// <summary>
    ///     Build the cancellation token a subscriber handler observes. The
    ///     handler sees the linked union of the per-tick token (if any) and the
    ///     coordinator's shutdown token, so <see cref="StopAsync"/> reliably
    ///     interrupts in-flight handlers regardless of whether the tick was
    ///     dispatched by the cadence loop or by the test hook.
    /// </summary>
    private CancellationTokenSource LinkWithShutdown(CancellationToken outer) =>
        CancellationTokenSource.CreateLinkedTokenSource(outer, _shutdownCts.Token);

    // ---- Cadence loop --------------------------------------------------------

    private async Task RunCadenceLoop(TickCadence cadence, TimeSpan period)
    {
        var ct = _shutdownCts.Token;
        try
        {
            // Initial alignment: wait until the next wall-clock boundary.
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var nextBoundary = NextBoundary(nowUtc, period);
            var initialDelay = nextBoundary - nowUtc;
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, ct).ConfigureAwait(false);

            using var timer = new PeriodicTimer(period, _timeProvider);
            // First fire happens immediately after the alignment delay, so
            // emit a tick BEFORE the first WaitForNextTickAsync (otherwise
            // the first real tick fires `period` after alignment, defeating
            // the point).
            await FireTickSafelyAsync(cadence, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await FireTickSafelyAsync(cadence, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScheduleCoordinator: cadence loop {Cadence} crashed; ticks lost until restart.", cadence);
        }
    }

    /// <summary>
    ///     Compute the next wall-clock boundary after <paramref name="nowUtc"/>
    ///     for a given period. For sub-day periods this is "round up to the next
    ///     multiple of <paramref name="period"/> measured from midnight UTC".
    /// </summary>
    internal static DateTime NextBoundary(DateTime nowUtc, TimeSpan period)
    {
        if (period <= TimeSpan.Zero) return nowUtc;
        // Anchor at the start of the current UTC day; align periods to that anchor.
        var anchor = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
        var elapsed = nowUtc - anchor;
        var periodsSinceAnchor = (long)Math.Floor(elapsed.Ticks / (double)period.Ticks);
        var nextTickElapsed = TimeSpan.FromTicks((periodsSinceAnchor + 1) * period.Ticks);
        return anchor + nextTickElapsed;
    }

    private async Task FireTickSafelyAsync(TickCadence cadence, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await FireTickAsync(cadence, now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScheduleCoordinator: tick {Cadence} fan-out failed.", cadence);
        }
    }

    private Task FireTickAsync(TickCadence cadence, DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = _subscribers;
        if (!snapshot.TryGetValue(cadence, out var bucket) || bucket.Count == 0)
            return Task.CompletedTask;

        // Resource-aware pipelining: when MaxConcurrentSubscribersPerTick is
        // bounded, throttle the fan-out with a semaphore. Default int.MaxValue
        // degenerates to plain Task.Run-style fan-out (a SemaphoreSlim sized
        // int.MaxValue still works -- but we skip the wrapper to keep the hot
        // path free of an unnecessary allocation).
        var perTickLimit = _options.MaxConcurrentSubscribersPerTick;
        if (perTickLimit <= 0) perTickLimit = int.MaxValue;

        var tasks = new List<Task>(bucket.Count);

        if (perTickLimit >= bucket.Count)
        {
            foreach (var sub in bucket)
                tasks.Add(InvokeSubscriberAsync(sub, now, ct));
        }
        else
        {
            // Bounded fan-out. SemaphoreSlim is released after each handler completes;
            // subscribers fire as soon as a slot opens. High-cost subscribers stagger
            // naturally because the slow ones occupy slots longer.
            var gate = new SemaphoreSlim(perTickLimit, perTickLimit);
            foreach (var sub in bucket)
            {
                tasks.Add(InvokeBoundedAsync(sub, now, gate, ct));
            }
        }

        return Task.WhenAll(tasks);
    }

    private async Task InvokeBoundedAsync(
        Subscription sub, DateTimeOffset now, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await InvokeSubscriberAsync(sub, now, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task InvokeSubscriberAsync(Subscription sub, DateTimeOffset now, CancellationToken ct)
    {
        // Single-flight gate: if the previous invocation is still running, skip
        // this tick. CompareExchange 0 -> 1 makes this lock-free.
        if (Interlocked.CompareExchange(ref sub.BusyFlag, 1, 0) != 0)
        {
            var skips = Interlocked.Increment(ref sub.OverlapSkipCount);
            var every = _options.OverlapWarnEveryNth;
            if (every > 0 && skips % every == 0)
            {
                _logger.LogWarning(
                    "ScheduleCoordinator: subscriber {Name} on {Cadence} has been busy for >2 ticks (skips={Skips}).",
                    sub.Name, sub.Cadence, skips);
            }
            return;
        }

        var startedAt = _timeProvider.GetUtcNow();
        using var linked = LinkWithShutdown(ct);
        try
        {
            await sub.Handler(now, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            _logger.LogDebug(oce, "ScheduleCoordinator: subscriber {Name} on {Cadence} cancelled.", sub.Name, sub.Cadence);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref sub.FaultCount);
            _logger.LogWarning(ex,
                "ScheduleCoordinator: subscriber {Name} on {Cadence} threw at tick={TickAt}.",
                sub.Name, sub.Cadence, now);
        }
        finally
        {
            var endedAt = _timeProvider.GetUtcNow();
            sub.LastInvokedAtTicks = startedAt.UtcTicks;
            sub.LastDurationTicks = (endedAt - startedAt).Ticks;
            Volatile.Write(ref sub.BusyFlag, 0);
        }
    }

    // ---- Subscription state --------------------------------------------------

    /// <summary>
    ///     Per-subscription state. Fields are exposed for in-class Interlocked
    ///     access; <see cref="Snapshot"/> projects them to the immutable
    ///     <see cref="TickSubscriberMetadata"/> record.
    /// </summary>
    private sealed class Subscription
    {
        public readonly TickCadence Cadence;
        public readonly string Name;
        public readonly CostHint Hint;
        public readonly Func<DateTimeOffset, CancellationToken, Task> Handler;

        public int BusyFlag;              // 0 idle, 1 busy
        public int OverlapSkipCount;
        public int FaultCount;
        public long LastInvokedAtTicks;   // 0 = never
        public long LastDurationTicks;    // 0 = never

        public Subscription(
            TickCadence cadence,
            string name,
            CostHint hint,
            Func<DateTimeOffset, CancellationToken, Task> handler)
        {
            Cadence = cadence;
            Name = name;
            Hint = hint;
            Handler = handler;
        }

        public TickSubscriberMetadata Snapshot()
        {
            var invokedTicks = Volatile.Read(ref LastInvokedAtTicks);
            var durTicks = Volatile.Read(ref LastDurationTicks);
            return new TickSubscriberMetadata(
                Cadence,
                Name,
                Hint,
                invokedTicks == 0 ? null : new DateTimeOffset(invokedTicks, TimeSpan.Zero),
                invokedTicks == 0 ? null : TimeSpan.FromTicks(durTicks),
                Volatile.Read(ref OverlapSkipCount),
                Volatile.Read(ref FaultCount));
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly ScheduleCoordinator _owner;
        private readonly Subscription _sub;
        private int _disposed;

        public Unsubscriber(ScheduleCoordinator owner, Subscription sub)
        {
            _owner = owner;
            _sub = sub;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            lock (_owner._subscribersGate)
            {
                var snapshot = new Dictionary<TickCadence, List<Subscription>>(_owner._subscribers);
                if (snapshot.TryGetValue(_sub.Cadence, out var bucket))
                {
                    var replacement = new List<Subscription>(bucket.Count);
                    foreach (var existing in bucket)
                        if (!ReferenceEquals(existing, _sub)) replacement.Add(existing);
                    snapshot[_sub.Cadence] = replacement;
                    _owner._subscribers = snapshot;
                }
            }
        }
    }
}