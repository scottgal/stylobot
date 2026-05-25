namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     A token-bucket request-rate limiter keyed on a composite subject set.
///     One bucket per <c>(subjects, scope)</c> pair: capacity = BurstSize,
///     refill at RequestsPerMinute / 60 tokens per second.
///     <para>
///     <see cref="TryAcquire"/> is non-blocking. Returns true if a token was
///     available; false if the bucket was empty. The caller dispatches the
///     rule's OverLimitAction on a false return -- typically a fast 429 or a
///     soft block. No <c>Task.Delay</c>: the limiter is a budget, not a
///     latency knob.
///     </para>
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    ///     Try to consume <paramref name="tokens"/> from the bucket keyed by
    ///     <paramref name="subjects"/>. Returns true when the bucket had room;
    ///     false when it would have gone negative. Always non-blocking.
    /// </summary>
    bool TryAcquire(IReadOnlyList<RateLimitSubject> subjects, RateLimitConfig config, int tokens = 1);

    /// <summary>
    ///     Diagnostic snapshot for the dashboard. Never blocks, never mutates
    ///     bucket state -- the operator can poll for "tokens remaining" without
    ///     refilling the bucket.
    /// </summary>
    RateLimitState GetState(IReadOnlyList<RateLimitSubject> subjects, RateLimitConfig config);
}

/// <summary>
///     Static parameters for a token bucket. Carried alongside the subjects
///     so the same limiter instance can serve many distinct configs without
///     a per-config registration step.
/// </summary>
public sealed record RateLimitConfig(int RequestsPerMinute, int BurstSize)
{
    /// <summary>Refill rate in tokens per second.</summary>
    public double TokensPerSecond => RequestsPerMinute / 60.0;
}

/// <summary>
///     Read-only snapshot of a bucket's current state for dashboard rendering.
/// </summary>
public sealed record RateLimitState(
    double TokensRemaining,
    int Capacity,
    double TokensPerSecond,
    DateTime LastRefillUtc);
