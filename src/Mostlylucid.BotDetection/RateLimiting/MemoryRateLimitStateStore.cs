using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     In-memory token-bucket store. One <see cref="Bucket"/> per key, refilled
///     on access via <see cref="Stopwatch.GetTimestamp"/> monotonic time. Atomic
///     via a per-bucket lock (cheaper than CAS-looping double-fields on the hot
///     path and trivially correct).
///     <para>
///     FOSS default. Commercial replaces with a Redis-backed implementation so
///     budgets are shared across a multi-gateway cluster; a Scraper hitting six
///     gateways at 10 req/min each does not get 60 req/min effective.
///     </para>
/// </summary>
public sealed class MemoryRateLimitStateStore : IRateLimitStateStore
{
    private static readonly double TicksPerSecond = System.Diagnostics.Stopwatch.Frequency;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public bool TryAcquire(string bucketKey, int capacity, double tokensPerSecond, int tokens)
    {
        var bucket = _buckets.GetOrAdd(bucketKey, _ => Bucket.CreateFull(capacity));
        lock (bucket.SyncRoot)
        {
            bucket.Refill(tokensPerSecond, capacity);
            if (bucket.Tokens < tokens) return false;
            bucket.Tokens -= tokens;
            return true;
        }
    }

    public RateLimitState? Peek(string bucketKey)
    {
        if (!_buckets.TryGetValue(bucketKey, out var bucket)) return null;
        lock (bucket.SyncRoot)
        {
            // Peek does NOT refill -- the dashboard reads the bucket as it
            // sat at the last acquire, not as it would be if a request fired
            // right now. That makes "tokens remaining" stable across
            // back-to-back reads.
            return new RateLimitState(
                bucket.Tokens,
                bucket.Capacity,
                bucket.LastTokensPerSecond,
                bucket.LastRefillUtc);
        }
    }

    public int EvictIdle(TimeSpan idleThreshold)
    {
        var cutoff = DateTime.UtcNow - idleThreshold;
        var evicted = 0;
        foreach (var kv in _buckets)
        {
            lock (kv.Value.SyncRoot)
            {
                if (kv.Value.LastRefillUtc < cutoff && _buckets.TryRemove(kv.Key, out _))
                    evicted++;
            }
        }
        return evicted;
    }

    private sealed class Bucket
    {
        internal readonly object SyncRoot = new();
        internal double Tokens;
        internal int Capacity;
        internal long LastRefillTicks;
        internal DateTime LastRefillUtc;
        internal double LastTokensPerSecond;

        public static Bucket CreateFull(int capacity) => new()
        {
            Tokens = capacity,
            Capacity = capacity,
            LastRefillTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            LastRefillUtc = DateTime.UtcNow,
            LastTokensPerSecond = 0,
        };

        internal void Refill(double tokensPerSecond, int capacity)
        {
            // Capacity is allowed to grow via config reload -- if the new value
            // is larger, raise the cap; never shrink Tokens for an already-full
            // bucket (operators raising the cap shouldn't unexpectedly reduce
            // the live budget).
            if (capacity > Capacity) Capacity = capacity;
            LastTokensPerSecond = tokensPerSecond;

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var elapsedSeconds = (now - LastRefillTicks) / TicksPerSecond;
            if (elapsedSeconds <= 0) return;

            Tokens = Math.Min(Capacity, Tokens + elapsedSeconds * tokensPerSecond);
            LastRefillTicks = now;
            LastRefillUtc = DateTime.UtcNow;
        }
    }
}
