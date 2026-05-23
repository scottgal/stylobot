using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     Per-fingerprint sliding-window counter for honeypot engagements.
///     The <see cref="HoneypotResponseActionPolicy"/> consults this before
///     applying jitter; when the cap is exceeded the policy drops to an
///     immediate fast 403 instead of holding a jittered connection. This
///     bounds resource use under hammer-attack patterns and reduces the
///     timing information leaked to the attacker.
/// </summary>
/// <remarks>
///     <para>
///         Two sliding windows are tracked per key: a 60-second short window
///         and a 3600-second long window. The implementation uses a ring of
///         timestamps per key with binary-search-based pruning so the
///         RecordAndCheck call is O(log n) on the buffer size with cheap
///         amortised allocation.
///     </para>
///     <para>
///         Bounded to <see cref="MaxKeys"/> distinct fingerprints with LRU
///         eviction so an attacker rotating identities can't OOM the store.
///     </para>
/// </remarks>
public sealed class HoneypotRateLimiter
{
    public const int MaxKeys = 50_000;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>
    ///     Records a honeypot engagement and returns true when the caller
    ///     SHOULD continue serving the jittered fake response (under both
    ///     caps), false when the caller should immediately fast-403.
    /// </summary>
    /// <param name="key">Per-fingerprint or per-IP identifier.</param>
    /// <param name="maxPerMinute">Per-minute cap. 0 disables.</param>
    /// <param name="maxPerHour">Per-hour cap. 0 disables.</param>
    public bool RecordAndCheck(string key, int maxPerMinute, int maxPerHour)
    {
        if (string.IsNullOrEmpty(key)) return true;
        if (maxPerMinute <= 0 && maxPerHour <= 0) return true;

        // Eviction is a tiny race here -- worst case a few extra entries.
        if (_windows.Count > MaxKeys) EvictColdest();

        var window = _windows.GetOrAdd(key, _ => new Window());
        return window.RecordAndCheck(DateTime.UtcNow, maxPerMinute, maxPerHour);
    }

    /// <summary>
    ///     Counts current hits in each window for the given key. Used by
    ///     the dashboard read surface.
    /// </summary>
    public (int Minute, int Hour) Snapshot(string key)
    {
        if (!_windows.TryGetValue(key, out var window)) return (0, 0);
        return window.Snapshot(DateTime.UtcNow);
    }

    private void EvictColdest()
    {
        // Drop ~10% with the oldest LastTouchUtc. Not perfect LRU but good
        // enough for a counter store.
        var target = MaxKeys - (MaxKeys / 10);
        if (_windows.Count <= target) return;

        var coldest = _windows.OrderBy(kv => kv.Value.LastTouchUtc)
            .Take(_windows.Count - target)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var k in coldest) _windows.TryRemove(k, out _);
    }

    private sealed class Window
    {
        // Timestamps stored in chronological insertion order. Pruning trims
        // from the head; new entries append to the tail. We keep at most an
        // hour's worth of entries -- anything older is dropped on each call.
        private readonly object _lock = new();
        private readonly List<DateTime> _hits = new(16);

        public DateTime LastTouchUtc { get; private set; } = DateTime.UtcNow;

        public bool RecordAndCheck(DateTime now, int maxPerMinute, int maxPerHour)
        {
            lock (_lock)
            {
                LastTouchUtc = now;
                Prune(now);

                _hits.Add(now);

                if (maxPerMinute > 0)
                {
                    var minuteCutoff = now.AddSeconds(-60);
                    var minuteCount = CountSince(minuteCutoff);
                    if (minuteCount > maxPerMinute) return false;
                }
                if (maxPerHour > 0)
                {
                    if (_hits.Count > maxPerHour) return false;
                }
                return true;
            }
        }

        public (int Minute, int Hour) Snapshot(DateTime now)
        {
            lock (_lock)
            {
                Prune(now);
                var minute = CountSince(now.AddSeconds(-60));
                return (minute, _hits.Count);
            }
        }

        private void Prune(DateTime now)
        {
            var cutoff = now.AddSeconds(-3600);
            if (_hits.Count == 0 || _hits[0] >= cutoff) return;
            var keep = 0;
            while (keep < _hits.Count && _hits[keep] < cutoff) keep++;
            if (keep > 0) _hits.RemoveRange(0, keep);
        }

        private int CountSince(DateTime cutoff)
        {
            // Binary search to the lower bound.
            var lo = 0;
            var hi = _hits.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (_hits[mid] < cutoff) lo = mid + 1;
                else hi = mid;
            }
            return _hits.Count - lo;
        }
    }
}
