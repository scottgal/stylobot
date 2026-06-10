using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Policies.Throttle;

/// <summary>
///     Process-wide registry of <see cref="TokenBucket"/>s keyed by a scope
///     string -- typically the stringified <see cref="Rules.PolicyScope"/> of
///     the throttle rule. The single instance is registered as a singleton so
///     concurrent requests on the same throttle rule share the bucket.
///
///     <para>
///         <see cref="TryAcquire"/> mints (or reuses) a bucket for
///         <paramref name="scopeKey"/> at <paramref name="requestsPerSecond"/>
///         capacity and refill rate, then attempts to take a token. Returns
///         <c>true</c> when the request is admitted; <c>false</c> when the
///         caller should respond with 429 + <c>Retry-After: 1</c>.
///     </para>
///
///     <para>
///         Buckets are never evicted -- they're tiny (one double + a clock)
///         and the scope-key cardinality is bounded by the authored rule
///         corpus. A future pass can wire LFU decay if rule churn ever makes
///         the dictionary memory-relevant.
///     </para>
/// </summary>
public sealed class ThrottleBucketRegistry
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     Attempt to admit one request against the bucket for
    ///     <paramref name="scopeKey"/>. Refills the bucket linearly toward
    ///     <paramref name="requestsPerSecond"/> capacity since the previous
    ///     call, then deducts one token. Returns <c>true</c> when a token was
    ///     available, <c>false</c> otherwise.
    /// </summary>
    /// <param name="scopeKey">Stable bucket identity. The throttle rule's scope, stringified.</param>
    /// <param name="requestsPerSecond">Bucket capacity and refill rate. Must be &gt; 0.</param>
    /// <param name="now">Wall-clock instant used to refill the bucket. Tests pass a fake clock here.</param>
    public bool TryAcquire(string scopeKey, int requestsPerSecond, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (requestsPerSecond <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(requestsPerSecond),
                requestsPerSecond,
                "requestsPerSecond must be > 0.");

        var bucket = _buckets.GetOrAdd(
            scopeKey,
            static (_, state) => new TokenBucket(state.rps, state.now),
            (rps: requestsPerSecond, now));

        // If the operator changes the bucket's rate at runtime via a config
        // edit, accept the new rate the next call lands on.
        bucket.UpdateRate(requestsPerSecond);
        return bucket.TryAcquire(now);
    }

    /// <summary>
    ///     Peek the current token count for diagnostics. Returns <c>null</c>
    ///     when no bucket has been minted for <paramref name="scopeKey"/>.
    ///     Test-only surface.
    /// </summary>
    internal double? PeekTokens(string scopeKey, DateTimeOffset now)
    {
        if (!_buckets.TryGetValue(scopeKey, out var bucket)) return null;
        return bucket.PeekTokens(now);
    }
}

/// <summary>
///     Standard fixed-rate token bucket. Holds at most
///     <see cref="_ratePerSecond"/> tokens; refills linearly at
///     <see cref="_ratePerSecond"/> tokens/sec since the previous
///     <see cref="TryAcquire"/> call.
/// </summary>
internal sealed class TokenBucket
{
    private readonly object _gate = new();
    private double _tokens;
    private DateTimeOffset _lastRefill;
    private int _ratePerSecond;

    public TokenBucket(int ratePerSecond, DateTimeOffset now)
    {
        _ratePerSecond = Math.Max(1, ratePerSecond);
        _tokens = _ratePerSecond; // start full so a burst at boot is admitted
        _lastRefill = now;
    }

    public void UpdateRate(int ratePerSecond)
    {
        if (ratePerSecond <= 0) return;
        lock (_gate)
        {
            if (_ratePerSecond == ratePerSecond) return;
            _ratePerSecond = ratePerSecond;
            // Cap any leftover tokens to the new capacity so a rate decrease
            // takes effect immediately.
            if (_tokens > _ratePerSecond) _tokens = _ratePerSecond;
        }
    }

    public bool TryAcquire(DateTimeOffset now)
    {
        lock (_gate)
        {
            // Linear refill since the last call. Clamp delta at zero so a
            // clock jump backwards doesn't drain the bucket.
            var delta = (now - _lastRefill).TotalSeconds;
            if (delta > 0)
            {
                _tokens = Math.Min(_ratePerSecond, _tokens + delta * _ratePerSecond);
                _lastRefill = now;
            }

            if (_tokens >= 1)
            {
                _tokens -= 1;
                return true;
            }
            return false;
        }
    }

    public double PeekTokens(DateTimeOffset now)
    {
        lock (_gate)
        {
            var delta = (now - _lastRefill).TotalSeconds;
            if (delta <= 0) return _tokens;
            return Math.Min(_ratePerSecond, _tokens + delta * _ratePerSecond);
        }
    }
}
