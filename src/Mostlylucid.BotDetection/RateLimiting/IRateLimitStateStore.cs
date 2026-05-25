namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     Persistence boundary for bucket state. FOSS ships an in-memory
///     implementation (<see cref="MemoryRateLimitStateStore"/>); commercial
///     swaps in a Redis-backed implementation so multi-gateway clusters share
///     the same budget. The interface is intentionally small -- two atomic
///     ops on a single bucket -- so a Redis implementation can be one Lua
///     script.
/// </summary>
public interface IRateLimitStateStore
{
    /// <summary>
    ///     Atomic: refill the bucket since its last-refill timestamp, then try
    ///     to take <paramref name="tokens"/>. Returns true if taken, false if
    ///     insufficient. The bucket is created on first touch with its full
    ///     <paramref name="capacity"/> available.
    /// </summary>
    bool TryAcquire(
        string bucketKey,
        int capacity,
        double tokensPerSecond,
        int tokens);

    /// <summary>
    ///     Read-only snapshot. Does NOT refill. Returns null when no bucket
    ///     for this key has been created yet -- the caller can render the
    ///     "would be capacity / capacity" default.
    /// </summary>
    RateLimitState? Peek(string bucketKey);

    /// <summary>
    ///     Evict buckets idle for longer than <paramref name="idleThreshold"/>.
    ///     Called periodically by a background sweep so the dictionary doesn't
    ///     grow unbounded across millions of unique fingerprints.
    /// </summary>
    int EvictIdle(TimeSpan idleThreshold);
}
