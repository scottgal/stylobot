using Microsoft.Extensions.Logging;

namespace Mostlylucid.Common.Scheduling;

/// <summary>
///     No-op <see cref="IScheduleCoordinator"/> for tests that construct a
///     migrated singleton (e.g. <c>LocalMeterStream</c>, <c>RemoteMeterStream</c>,
///     <c>MeterTriggerService</c>) directly without standing up the DI graph.
///     <para>
///         Use case: a test wants to drive the service deterministically through
///         its existing test hook (<c>TickOnceAsync</c>, <c>PumpPollForTesting</c>,
///         <c>PumpEvictionForTesting</c>) instead of going through real ticks.
///         The constructor still calls <see cref="Subscribe"/> -- that has to
///         succeed and return a disposable handle, but the handler will never
///         fire because no cadence loop is running.
///     </para>
///     <para>
///         <b>Production callers must NEVER resolve this type.</b> The real
///         <c>ScheduleCoordinator</c> (Mostlylucid.BotDetection.Scheduling) is
///         the one and only coordinator the FOSS DI graph registers. This
///         sibling lives in Mostlylucid.Common so tests get the convenience
///         without taking a test-assembly reference from production code; it's
///         a no-cost alternative to forcing every test to spin a real
///         coordinator just to construct a subscriber.
///     </para>
///     <para>
///         <b>Observability:</b> <see cref="Subscribe"/> emits a one-shot warning
///         per <see cref="TickCadence"/> when an <see cref="ILogger"/> sink has
///         been provided via <see cref="SetWarnSink"/>. This makes shadow
///         registrations (a host accidentally resolving the null sentinel in a
///         production wiring) loud instead of silently zero-data. The warning
///         path is opt-in so test rigs that use the sentinel intentionally
///         don't get spammed; production hosts can wire the sink from their
///         logging factory.
///     </para>
/// </summary>
public sealed class NullScheduleCoordinator : IScheduleCoordinator
{
    /// <summary>Singleton -- the type is stateless so a single shared instance is enough.</summary>
    public static readonly NullScheduleCoordinator Instance = new();

    private static readonly object _gate = new();
    private static ILogger? _warnSink;
    private static readonly HashSet<TickCadence> _warnedCadences = new();

    private NullScheduleCoordinator() { }

    /// <summary>
    ///     Install an <see cref="ILogger"/> that receives a one-shot warning the
    ///     first time <see cref="Subscribe"/> is called against this sentinel for
    ///     each <see cref="TickCadence"/>. Pass <c>null</c> to detach. Idempotent.
    /// </summary>
    /// <remarks>
    ///     Static state: the sentinel is a singleton, so the warn sink is too.
    ///     Production hosts that ever resolve this type by accident want the
    ///     warning emitted exactly once per cadence per process; spamming a log
    ///     line on every Subscribe call would drown the warning's signal.
    /// </remarks>
    public static void SetWarnSink(ILogger? logger)
    {
        lock (_gate)
        {
            _warnSink = logger;
        }
    }

    /// <summary>
    ///     Test-only: clear the "already warned about this cadence" memo so a
    ///     subsequent <see cref="Subscribe"/> call re-emits the warning. Production
    ///     code never needs this; the warning is a one-shot per process.
    /// </summary>
    public static void ResetWarnedCadencesForTesting()
    {
        lock (_gate)
        {
            _warnedCadences.Clear();
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(
        TickCadence cadence,
        string subscriberName,
        CostHint costHint,
        Func<DateTimeOffset, CancellationToken, Task> handler)
    {
        WarnIfFirstFor(cadence, subscriberName);
        return NullSubscription.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<TickSubscriberMetadata> Snapshot() => Array.Empty<TickSubscriberMetadata>();

    private static void WarnIfFirstFor(TickCadence cadence, string subscriberName)
    {
        ILogger? sink;
        bool first;
        lock (_gate)
        {
            sink = _warnSink;
            first = _warnedCadences.Add(cadence);
        }
        if (sink is null || !first) return;

        // The message intentionally names the type so log readers can grep for
        // "NullScheduleCoordinator" and locate every shadow-registration site.
        sink.LogWarning(
            "NullScheduleCoordinator.Subscribe({Cadence}, {SubscriberName}) called -- this is a no-op sentinel; the real ScheduleCoordinator is not registered. Tick handlers WILL NOT FIRE.",
            cadence, subscriberName);
    }

    private sealed class NullSubscription : IDisposable
    {
        public static readonly NullSubscription Instance = new();
        public void Dispose() { }
    }
}