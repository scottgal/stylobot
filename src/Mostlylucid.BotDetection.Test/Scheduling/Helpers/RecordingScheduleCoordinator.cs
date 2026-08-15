using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Test.Scheduling.Helpers;

/// <summary>
///     <see cref="IScheduleCoordinator"/> test stand-in that records every
///     subscription so tests can assert on names/cadences without standing up
///     the real coordinator's cadence loops. The captured handlers are
///     accessible so tests can drive ticks directly via
///     <see cref="Recorded.Handler"/>.
///     <para>
///         <b>Wave 2 migration helper.</b> Promoted from private nested classes
///         in <c>WaveTwoBootstrapTests</c> and <c>MeterSignalsAtomTests</c> --
///         per the Wave 2 migration plan, every per-service migration PR needs
///         the same recording-coordinator shape to assert that the migrated
///         service called <see cref="IScheduleCoordinator.Subscribe"/> with the
///         right name / cadence / cost hint.
///     </para>
///     <para>
///         The two shapes merged here:
///         <list type="bullet">
///             <item>
///                 <see cref="Snapshot"/> returns <see cref="TickSubscriberMetadata"/>
///                 records, matching the original <c>WaveTwoBootstrapTests</c>
///                 shape used to assert "did the migrated service subscribe?".
///             </item>
///             <item>
///                 <see cref="Subscriptions"/> returns <see cref="Recorded"/>
///                 entries with the captured handler delegate, matching the
///                 original <c>MeterSignalsAtomTests</c> shape used to drive
///                 the handler directly under a frozen clock.
///             </item>
///         </list>
///     </para>
/// </summary>
public sealed class RecordingScheduleCoordinator : IScheduleCoordinator
{
    private readonly object _gate = new();
    private readonly List<Recorded> _subs = new();

    /// <summary>
    ///     Every <see cref="Subscribe"/> call captured in order. Disposed
    ///     subscriptions stay in the list with <see cref="Recorded.Disposed"/>
    ///     set to <c>true</c> so tests can assert dispose-on-teardown.
    /// </summary>
    public IReadOnlyList<Recorded> Subscriptions
    {
        get { lock (_gate) return _subs.ToArray(); }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(
        TickCadence cadence,
        string subscriberName,
        CostHint costHint,
        Func<DateTimeOffset, CancellationToken, Task> handler)
    {
        var rec = new Recorded(cadence, subscriberName, costHint, handler);
        lock (_gate) _subs.Add(rec);
        return rec;
    }

    /// <inheritdoc />
    public IReadOnlyList<TickSubscriberMetadata> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<TickSubscriberMetadata>(_subs.Count);
            foreach (var rec in _subs)
                result.Add(new TickSubscriberMetadata(rec.Cadence, rec.Name, rec.Hint, null, null, 0, rec.FaultCount));
            return result;
        }
    }

    /// <summary>
    ///     Test hook: simulate a faulted subscriber by setting its cumulative
    ///     fault count (surfaced via <see cref="Snapshot"/>). Returns
    ///     <c>false</c> if no subscription with that name exists.
    /// </summary>
    public bool SetFaults(string subscriberName, int faultCount)
    {
        lock (_gate)
        {
            var rec = _subs.FirstOrDefault(r => r.Name == subscriberName);
            if (rec is null) return false;
            rec.FaultCount = faultCount;
            return true;
        }
    }

    /// <summary>
    ///     A single captured <see cref="Subscribe"/> call. Disposing the
    ///     instance flips <see cref="Disposed"/> but leaves the record in the
    ///     coordinator's <see cref="Subscriptions"/> list so tests can assert
    ///     teardown.
    /// </summary>
    public sealed class Recorded : IDisposable
    {
        /// <summary>Cadence the subscriber registered against.</summary>
        public TickCadence Cadence { get; }

        /// <summary>Subscriber name (used in log lines + assertions).</summary>
        public string Name { get; }

        /// <summary>Cost hint passed at subscription.</summary>
        public CostHint Hint { get; }

        /// <summary>The handler captured at subscription -- tests invoke this directly to drive ticks.</summary>
        public Func<DateTimeOffset, CancellationToken, Task> Handler { get; }

        /// <summary>Simulated cumulative fault count, surfaced via <see cref="Snapshot"/>.</summary>
        public int FaultCount { get; set; }

        /// <summary><c>true</c> once the subscription handle has been disposed.</summary>
        public bool Disposed { get; private set; }

        internal Recorded(TickCadence c, string n, CostHint h, Func<DateTimeOffset, CancellationToken, Task> handler)
        {
            Cadence = c;
            Name = n;
            Hint = h;
            Handler = handler;
        }

        /// <inheritdoc />
        public void Dispose() => Disposed = true;
    }
}