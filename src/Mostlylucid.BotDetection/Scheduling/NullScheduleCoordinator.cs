namespace Mostlylucid.BotDetection.Scheduling;

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
///         <see cref="ScheduleCoordinator"/> is the one and only coordinator the
///         FOSS DI graph registers. This sibling lives in the same FOSS namespace
///         so tests get the convenience without taking a test-assembly reference
///         from production code; it's a no-cost alternative to forcing every test
///         to spin a real coordinator just to construct a subscriber.
///     </para>
/// </summary>
public sealed class NullScheduleCoordinator : IScheduleCoordinator
{
    /// <summary>Singleton -- the type is stateless so a single shared instance is enough.</summary>
    public static readonly NullScheduleCoordinator Instance = new();

    private NullScheduleCoordinator() { }

    /// <inheritdoc />
    public IDisposable Subscribe(
        TickCadence cadence,
        string subscriberName,
        CostHint costHint,
        Func<DateTimeOffset, CancellationToken, Task> handler)
        => NullSubscription.Instance;

    /// <inheritdoc />
    public IReadOnlyList<TickSubscriberMetadata> Snapshot() => Array.Empty<TickSubscriberMetadata>();

    private sealed class NullSubscription : IDisposable
    {
        public static readonly NullSubscription Instance = new();
        public void Dispose() { }
    }
}