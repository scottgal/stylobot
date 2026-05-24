using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Lock-free in-memory token-bucket store. Default
///     <see cref="ITokenBucketStore"/> implementation registered by
///     <c>AddBotDetection()</c>.
/// </summary>
/// <remarks>
///     <para>
///         State lives in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
///         keyed on <c>(policyName, key)</c>. Reads + writes use the
///         compare-and-swap idiom (<c>TryAdd</c> + <c>TryUpdate</c> in a
///         tight loop) so concurrent requests on the same bucket can't
///         lose updates.
///     </para>
///     <para>
///         The store retains buckets indefinitely -- inactive buckets cost
///         a 24-byte record each. A hosted sweep that drops buckets older
///         than 1h is left for a later phase; expected hit on per-process
///         memory is well under 10MB for normal traffic patterns.
///     </para>
/// </remarks>
public sealed class InMemoryTokenBucketStore : ITokenBucketStore
{
    private readonly ConcurrentDictionary<(string Policy, string Key), Bucket> _buckets = new();
    private readonly Func<DateTime> _clock;

    public InMemoryTokenBucketStore() : this(static () => DateTime.UtcNow) { }

    /// <summary>
    ///     Test constructor: inject a deterministic clock. Production code
    ///     uses the parameterless ctor.
    /// </summary>
    public InMemoryTokenBucketStore(Func<DateTime> clock)
    {
        _clock = clock;
    }

    public bool TryConsume(string policyName, string key, int capacity, int refillRatePerMinute)
    {
        if (capacity < 1 || refillRatePerMinute < 1) return true; // misconfig -> open

        var bucketKey = (policyName, key);
        var refillPerSecond = refillRatePerMinute / 60.0;

        while (true)
        {
            var now = _clock();

            if (!_buckets.TryGetValue(bucketKey, out var current))
            {
                // First request for this key: full bucket minus one.
                var initial = new Bucket(capacity - 1.0, now);
                if (_buckets.TryAdd(bucketKey, initial)) return true;
                continue;   // raced with another adder; retry
            }

            // Refill against wall clock since last touch.
            var elapsedSec = (now - current.LastRefillUtc).TotalSeconds;
            var refilled = Math.Min(capacity, current.Tokens + (elapsedSec * refillPerSecond));

            if (refilled < 1.0)
            {
                // Update last-refill so we don't double-count this elapsed
                // time on the next try -- but only if the CAS succeeds, so
                // a concurrent successful consumer wins normally.
                var deniedUpdate = new Bucket(refilled, now);
                _buckets.TryUpdate(bucketKey, deniedUpdate, current);
                return false;
            }

            var consumed = new Bucket(refilled - 1.0, now);
            if (_buckets.TryUpdate(bucketKey, consumed, current)) return true;
            // CAS failed (concurrent consumer); retry against the new state.
        }
    }

    public BucketSnapshot? Peek(string policyName, string key)
    {
        if (!_buckets.TryGetValue((policyName, key), out var b)) return null;
        return new BucketSnapshot(b.Tokens, (int)Math.Ceiling(b.Tokens), b.LastRefillUtc);
    }

    private readonly record struct Bucket(double Tokens, DateTime LastRefillUtc);
}
