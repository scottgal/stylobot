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

    public int MinSessions => _minSessions;

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
        return _windows.AddOrUpdate(
            path,
            _ => new DivergenceWindow(),
            (_, existing) => DateTimeOffset.UtcNow - existing.WindowStart > _windowDuration
                ? new DivergenceWindow()
                : existing);
    }
}
