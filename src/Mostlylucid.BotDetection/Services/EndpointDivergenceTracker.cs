using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Tracks per-endpoint divergence rate in a rolling window (default 1 hour).
///     When the divergence rate exceeds the threshold (default 40%) across at least
///     <see cref="MinSessions"/> sessions, <see cref="IsStale"/> returns true —
///     indicating the page content likely changed rather than bots arriving.
///     In-memory only. Loss on restart is acceptable (staleness lasts at most one restart cycle).
/// </summary>
public sealed class EndpointDivergenceTracker
{
    public readonly record struct EndpointStats(int TotalSessions, int DivergenceCount);

    private sealed class DivergenceWindow
    {
        private int _totalSessions;
        private int _divergenceCount;
        public DateTimeOffset WindowStart { get; } = DateTimeOffset.UtcNow;
        public int TotalSessions => _totalSessions;
        public int DivergenceCount => _divergenceCount;
        public void IncrementSession() => Interlocked.Increment(ref _totalSessions);
        public void IncrementDivergence() => Interlocked.Increment(ref _divergenceCount);
    }

    private readonly ConcurrentDictionary<string, DivergenceWindow> _windows = new();
    private readonly TimeSpan _windowDuration;
    private readonly double _stalenessRateThreshold;
    private readonly int _minSessions;

    // Keyed by raw request path — unbounded cardinality (crawlers probe endless URLs).
    // Cap the key count and evict the oldest windows (earliest WindowStart) on overflow.
    // Loss of a window merely resets staleness tracking for that path, which is
    // already an accepted outcome across restarts.
    private const int MaxWindows = 10_000;
    private readonly object _evictLock = new();

    public int MinSessions => _minSessions;

    /// <summary>Test hook: number of resident per-path windows.</summary>
    internal int WindowCount => _windows.Count;

    public EndpointDivergenceTracker(
        TimeSpan? windowDuration = null,
        double stalenessRateThreshold = 0.40,
        int minSessions = 10)
    {
        _windowDuration = windowDuration ?? TimeSpan.FromHours(1);
        _stalenessRateThreshold = stalenessRateThreshold;
        _minSessions = minSessions;
    }

    /// <summary>Record a new session starting at this path (document hit).</summary>
    public void RecordSession(string path)
        => GetOrRefreshWindow(path).IncrementSession();

    /// <summary>Record a divergence event at this path.</summary>
    public void RecordDivergence(string path)
        => GetOrRefreshWindow(path).IncrementDivergence();

    /// <summary>
    ///     Returns true when the divergence rate exceeds the threshold AND at least
    ///     <see cref="MinSessions"/> sessions have been observed in the current window.
    /// </summary>
    public bool IsStale(string path)
    {
        if (!_windows.TryGetValue(path, out var window))
            return false;
        if (window.TotalSessions < _minSessions)
            return false;
        var rate = (double)window.DivergenceCount / window.TotalSessions;
        return rate >= _stalenessRateThreshold;
    }

    /// <summary>Get current stats for a path (for diagnostics / tests).</summary>
    public EndpointStats GetStats(string path)
    {
        if (!_windows.TryGetValue(path, out var window))
            return new EndpointStats(0, 0);
        return new EndpointStats(window.TotalSessions, window.DivergenceCount);
    }

    /// <summary>Reset divergence tracking for a path (called after centroid rebuild).</summary>
    public void Reset(string path) => _windows.TryRemove(path, out _);

    private DivergenceWindow GetOrRefreshWindow(string path)
    {
        var window = _windows.AddOrUpdate(
            path,
            _ => new DivergenceWindow(),
            (_, existing) => DateTimeOffset.UtcNow - existing.WindowStart > _windowDuration
                ? new DivergenceWindow()
                : existing);
        EvictOldestIfNeeded();
        return window;
    }

    /// <summary>
    ///     Caps the number of resident path windows. On overflow, evicts the oldest slice
    ///     (earliest WindowStart) down to ~90% of <see cref="MaxWindows"/> so the next
    ///     insert doesn't immediately re-trigger. Single-threaded via <see cref="_evictLock"/>.
    /// </summary>
    private void EvictOldestIfNeeded()
    {
        if (_windows.Count <= MaxWindows) return;
        if (!Monitor.TryEnter(_evictLock)) return;
        try
        {
            if (_windows.Count <= MaxWindows) return;
            var target = MaxWindows - MaxWindows / 10; // trim to 90%
            var overflow = _windows.Count - target;
            if (overflow <= 0) return;

            var snapshot = _windows.ToArray();
            Array.Sort(snapshot, static (a, b) => a.Value.WindowStart.CompareTo(b.Value.WindowStart));
            var removed = 0;
            foreach (var kv in snapshot)
            {
                if (removed >= overflow) break;
                if (_windows.TryRemove(kv.Key, out _)) removed++;
            }
        }
        finally
        {
            Monitor.Exit(_evictLock);
        }
    }
}
