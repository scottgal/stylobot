using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Bounded ring buffer of <see cref="DegradationSnapshot"/> records,
///     written by <see cref="DegradationHistorySampler"/> on every Tick10s
///     and read by the gateway's site-health REST endpoint.
///     <para>
///         Gateway-local truth-on-instant per <c>project_gateway_data_locality</c>
///         + <c>feedback_no_inmemory_stores</c>'s transient-state carve-out:
///         the ring is process-local ephemeral state, not a persistence
///         layer. Restart loses the window; the next Tick10s starts refilling
///         from the live <see cref="DegradationAtom"/>.
///     </para>
///     <para>
///         Append is lock-free (<see cref="Interlocked.Increment(ref long)"/>
///         on the write cursor); read takes a tiny lock to copy out a stable
///         window snapshot. Sampler and reader never contend on the hot path
///         because the sampler runs at Tick10s cadence and the reader is a
///         REST GET on the dashboard's render path.
///     </para>
/// </summary>
public sealed class DegradationHistoryAtom
{
    private readonly object _gate = new();
    private readonly DegradationSnapshot?[] _ring;
    private long _writeCursor; // monotonic count of writes; index = (cursor - 1) % Capacity

    public DegradationHistoryAtom(IOptions<SiteHealthHistoryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capacity = Math.Max(1, options.Value.Capacity);
        _ring = new DegradationSnapshot?[capacity];
    }

    /// <summary>
    ///     Ring buffer capacity (number of slots; once full, oldest entries
    ///     are overwritten). Useful for tests + the empty-state UI branch.
    /// </summary>
    public int Capacity => _ring.Length;

    /// <summary>
    ///     Append one snapshot. Overwrites the oldest entry when the ring is
    ///     full. Thread-safe — safe to call concurrently with
    ///     <see cref="GetWindow"/>.
    /// </summary>
    public void Append(DegradationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            var idx = (int)(_writeCursor % _ring.Length);
            _ring[idx] = snapshot;
            _writeCursor++;
        }
    }

    /// <summary>
    ///     Snapshot of every entry in the ring whose <see cref="DegradationSnapshot.TimestampUtc"/>
    ///     falls inside <paramref name="window"/> ending at <paramref name="endUtc"/>,
    ///     ordered oldest-first. Returns an empty list when nothing matches
    ///     so callers do not need to null-check.
    /// </summary>
    /// <param name="endUtc">Upper edge of the window (inclusive).</param>
    /// <param name="window">Window duration; ignored when zero / negative (returns everything).</param>
    public IReadOnlyList<DegradationSnapshot> GetWindow(DateTime endUtc, TimeSpan window)
    {
        DegradationSnapshot?[] copy;
        long cursor;
        lock (_gate)
        {
            copy = new DegradationSnapshot?[_ring.Length];
            Array.Copy(_ring, copy, _ring.Length);
            cursor = _writeCursor;
        }

        if (cursor == 0) return Array.Empty<DegradationSnapshot>();

        // Walk oldest-first from the ring. When cursor < capacity, slots
        // [cursor, capacity) are empty placeholders that we skip via the
        // null check.
        var ordered = new List<DegradationSnapshot>((int)Math.Min(cursor, _ring.Length));
        var startCursor = cursor > _ring.Length ? cursor - _ring.Length : 0;
        for (var c = startCursor; c < cursor; c++)
        {
            var snap = copy[(int)(c % _ring.Length)];
            if (snap is null) continue;
            if (window > TimeSpan.Zero && (endUtc - snap.TimestampUtc) > window) continue;
            ordered.Add(snap);
        }

        return ordered;
    }
}