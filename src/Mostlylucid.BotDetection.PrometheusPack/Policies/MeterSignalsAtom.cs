using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.PrometheusPack.Policies;

/// <summary>
///     Singleton atom that holds the canonical meter-signals snapshot for the
///     policy hot path. The atom subscribes to
///     <see cref="TickCadence.Tick10s"/> via the project's
///     <see cref="IScheduleCoordinator"/> and, on each tick, rebuilds the
///     <c>meter.{name}.current</c> / <c>.p50</c> / <c>.p99</c> /
///     <c>.delta_1m</c> / <c>.delta_5m</c> dictionary by ONE pass over the
///     catalogued meters. Per-request consumers read the
///     <see cref="CurrentSnapshot"/> reference lock-free via
///     <see cref="Volatile.Read"/> -- the hot path is O(N) dictionary copy
///     instead of O(N) awaited <see cref="IMeterStream.GetAsync"/> calls.
/// </summary>
/// <remarks>
///     <para>
///         <b>Architectural note:</b> this is the first non-per-key
///         atom-shaped state in PrometheusPack. The
///         <see cref="MeterSummaryAtom"/> family is per-meter; this atom is a
///         single global aggregate that fans those summaries out into the
///         policy-evaluator signal vocabulary once per Tick10s window.
///     </para>
///     <para>
///         <b>Wave 4 architectural-drift remediation:</b> before this atom,
///         <see cref="MeterSnapshotSignalContributor"/> issued two awaited
///         <see cref="IMeterStream.GetAsync"/> calls per catalogued meter on
///         every <c>DefaultPolicyResolver.EffectiveWithContextAsync</c>
///         invocation -- i.e. per request. With
///         <see cref="LocalMeterStreamOptions.MaxTrackedMeters"/> at the
///         default of 128 that is ~256 awaits per request. The atom collapses
///         that into one dictionary copy per request and one full snapshot
///         rebuild every 10s.
///     </para>
///     <para>
///         <b>Atomic-swap:</b> the snapshot reference is rebuilt off the hot
///         path and published via <see cref="Volatile.Write"/>. Readers see
///         either the previous or the new dictionary in full -- never a
///         half-built dictionary -- and never block. The previous reference is
///         freed by the GC after the last in-flight read completes.
///     </para>
///     <para>
///         <b>Safe defaults:</b> if the catalog is empty or
///         <see cref="IMeterStream"/> throws, the snapshot stays an empty
///         dictionary -- never null. The contributor that reads from this atom
///         falls back to its empty no-op, the catalog source returns an empty
///         descriptor list, and the per-request signal bag is unaffected.
///     </para>
/// </remarks>
public sealed class MeterSignalsAtom : IDisposable
{
    /// <summary>
    ///     Default bucket count for both the 1m and 5m windows. Six buckets
    ///     matches the per-request contributor's previous behaviour
    ///     (<see cref="MeterSnapshotSignalContributor.WindowBuckets"/>) so
    ///     the <c>delta_*</c> facet values stay numerically comparable
    ///     across the Wave 4 refactor.
    ///
    ///     <para>
    ///         <b>Wave 5:</b> the live value is now read from
    ///         <see cref="MeterSignalsAtomOptions.WindowBuckets"/>; this
    ///         constant is kept as the documented default and as the
    ///         compatibility shim for callers that referenced
    ///         <c>MeterSignalsAtom.WindowBuckets</c> directly.
    ///     </para>
    /// </summary>
    internal const int WindowBuckets = 6;

    /// <summary>Tick cadence the snapshot rebuilds on.</summary>
    public static readonly TickCadence RebuildCadence = TickCadence.Tick10s;

    private static readonly IReadOnlyDictionary<string, object?> EmptySnapshot =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static readonly IReadOnlyList<MeterCatalogEntry> EmptyCatalog = Array.Empty<MeterCatalogEntry>();

    private readonly IMeterStream _stream;
    private readonly ILogger<MeterSignalsAtom> _logger;
    private readonly IDisposable _subscription;
    private readonly int _windowBuckets;

    // Reference-swap via Volatile.Read/Write. Snapshot is never mutated in place;
    // each tick builds a fresh dictionary and publishes the new reference.
    private IReadOnlyDictionary<string, object?> _snapshot = EmptySnapshot;
    private IReadOnlyList<MeterCatalogEntry> _catalog = EmptyCatalog;
    private int _disposed;

    /// <summary>
    ///     Production constructor. Subscribes to
    ///     <see cref="TickCadence.Tick10s"/> immediately so the snapshot rebuild
    ///     fires as soon as the coordinator's cadence loop starts.
    /// </summary>
    /// <remarks>
    ///     The Wave 5 <see cref="MeterSignalsAtomOptions"/> shim is optional;
    ///     when an <see cref="IOptions{TOptions}"/> isn't registered the atom
    ///     falls back to <see cref="WindowBuckets"/>.
    /// </remarks>
    public MeterSignalsAtom(
        IMeterStream stream,
        IScheduleCoordinator coordinator,
        ILogger<MeterSignalsAtom>? logger = null,
        IOptions<MeterSignalsAtomOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(coordinator);

        _stream = stream;
        _logger = logger ?? NullLogger<MeterSignalsAtom>.Instance;
        var configured = options?.Value.WindowBuckets;
        _windowBuckets = configured is > 0 ? configured.Value : WindowBuckets;

        _subscription = coordinator.Subscribe(
            RebuildCadence,
            "MeterSignalsAtom.Rebuild",
            CostHint.Low,
            (_, ct) => RebuildAsync(ct));
    }

    /// <summary>
    ///     The current meter-signals snapshot. O(1) lock-free read; safe to
    ///     call from the request hot path. Returns an empty dictionary -- not
    ///     null -- when the atom has never ticked or the catalog is empty.
    /// </summary>
    public IReadOnlyDictionary<string, object?> CurrentSnapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    ///     The catalog projection used by
    ///     <see cref="MeterSignalCatalogSource"/>. Snapshotted on the same
    ///     tick as <see cref="CurrentSnapshot"/> so the editor autocomplete
    ///     vocabulary stays aligned with what the runtime can actually
    ///     resolve. Returns an empty list -- not null -- when the catalog is
    ///     empty or the meter stream is unhealthy.
    /// </summary>
    public IReadOnlyList<MeterCatalogEntry> CurrentCatalog => Volatile.Read(ref _catalog);

    /// <summary>
    ///     Force-rebuild the snapshot on the calling thread. Used by tests to
    ///     drive the atom deterministically without waiting on a tick from a
    ///     real coordinator. Production callers MUST go through the tick
    ///     subscription -- pulling a rebuild on a hot path defeats the point.
    /// </summary>
    public Task RebuildNowAsync(CancellationToken ct = default) => RebuildAsync(ct);

    private async Task RebuildAsync(CancellationToken ct)
    {
        if (_disposed != 0) return;

        IReadOnlyList<MeterCatalogEntry> catalog;
        try
        {
            catalog = await _stream.ListAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A faulty meter stream must not break the snapshot reader. Leave
            // the previously-published snapshot in place so the predicate
            // evaluator continues to see whatever the last successful tick
            // surfaced.
            _logger.LogDebug(ex, "MeterSignalsAtom: stream ListAsync threw; keeping previous snapshot");
            return;
        }

        if (catalog.Count == 0)
        {
            Volatile.Write(ref _catalog, EmptyCatalog);
            Volatile.Write(ref _snapshot, EmptySnapshot);
            return;
        }

        // Pre-size the dictionary against the worst case (histograms contribute
        // 5 keys, other kinds contribute 3) so we don't pay for resizes mid-build.
        var capacity = 0;
        for (var i = 0; i < catalog.Count; i++)
            capacity += catalog[i].Kind == MeterKind.Histogram ? 5 : 3;

        var next = new Dictionary<string, object?>(capacity, StringComparer.Ordinal);

        foreach (var entry in catalog)
        {
            ct.ThrowIfCancellationRequested();

            // 1m window -- current + delta_1m + (p50/p99 for histograms).
            MeterTimeSeries? oneMinute = null;
            try
            {
                oneMinute = await _stream
                    .GetAsync(entry.Name, TimeSpan.FromMinutes(1), _windowBuckets, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MeterSignalsAtom: 1m GetAsync threw for {Name}; skipping", entry.Name);
            }

            if (oneMinute is not null)
            {
                next[$"meter.{entry.Name}.current"]  = oneMinute.Current;
                next[$"meter.{entry.Name}.delta_1m"] = SumValues(oneMinute);
                if (entry.Kind == MeterKind.Histogram)
                {
                    next[$"meter.{entry.Name}.p50"] = oneMinute.P50;
                    next[$"meter.{entry.Name}.p99"] = oneMinute.P99;
                }
            }

            // 5m window -- delta_5m only; current/p50/p99 are point-in-time and
            // the 1m series already captured them.
            MeterTimeSeries? fiveMinute = null;
            try
            {
                fiveMinute = await _stream
                    .GetAsync(entry.Name, TimeSpan.FromMinutes(5), _windowBuckets, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MeterSignalsAtom: 5m GetAsync threw for {Name}; skipping", entry.Name);
            }

            if (fiveMinute is not null)
                next[$"meter.{entry.Name}.delta_5m"] = SumValues(fiveMinute);
        }

        // Publish atomically. Readers crossing the swap see either the old or
        // the new dictionary -- never a partial result.
        Volatile.Write(ref _catalog, catalog);
        Volatile.Write(ref _snapshot, next);
    }

    private static double SumValues(MeterTimeSeries ts)
    {
        double sum = 0;
        for (var i = 0; i < ts.Values.Count; i++)
            sum += ts.Values[i];
        return sum;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}
