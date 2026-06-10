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
///         Memory bound: a lazy sweep runs every
///         <see cref="InMemoryTokenBucketStoreOptions.SweepEveryN"/> calls
///         and drops buckets idle longer than
///         <see cref="InMemoryTokenBucketStoreOptions.IdleTtl"/>. When the
///         dictionary still exceeds
///         <see cref="InMemoryTokenBucketStoreOptions.MaxBuckets"/>, the
///         oldest-touched entries are evicted (LRU by
///         <c>LastRefillUtc</c>). Keeps memory bounded even when the
///         policy is keyed on a high-cardinality identity (per-fingerprint,
///         per-session) where the IP-keyed assumption no longer holds.
///     </para>
/// </remarks>
public sealed class InMemoryTokenBucketStore : ITokenBucketStore
{
    private readonly ConcurrentDictionary<(string Policy, string Key), Bucket> _buckets = new();
    private readonly Func<DateTime> _clock;
    private readonly InMemoryTokenBucketStoreOptions _options;
    private long _opCount;

    public InMemoryTokenBucketStore()
        : this(static () => DateTime.UtcNow, new InMemoryTokenBucketStoreOptions()) { }

    public InMemoryTokenBucketStore(InMemoryTokenBucketStoreOptions options)
        : this(static () => DateTime.UtcNow, options) { }

    /// <summary>
    ///     Test constructor: inject a deterministic clock. Production code
    ///     uses the parameterless ctor or the options-only ctor.
    /// </summary>
    public InMemoryTokenBucketStore(Func<DateTime> clock)
        : this(clock, new InMemoryTokenBucketStoreOptions()) { }

    /// <summary>
    ///     Full test constructor: deterministic clock + sweep options.
    /// </summary>
    public InMemoryTokenBucketStore(Func<DateTime> clock, InMemoryTokenBucketStoreOptions options)
    {
        _clock = clock;
        _options = options;
    }

    public bool TryConsume(string policyName, string key, int capacity, int refillRatePerMinute)
    {
        if (capacity < 1 || refillRatePerMinute < 1) return true; // misconfig -> open

        var result = ConsumeImpl(policyName, key, capacity, refillRatePerMinute);
        // Sweep runs AFTER consume so the bucket just added is visible to the
        // cap check on the same call; otherwise steady-state size sits at
        // MaxBuckets + 1.
        MaybeSweep();
        return result;
    }

    private bool ConsumeImpl(string policyName, string key, int capacity, int refillRatePerMinute)
    {
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

    /// <summary>
    ///     Lazy sweep entry point. Runs on the calling thread when the
    ///     op-counter rolls through <see cref="InMemoryTokenBucketStoreOptions.SweepEveryN"/>.
    ///     A single-pass enumeration over the dictionary on the rare
    ///     unlucky request is cheaper than a HostedService for the
    ///     bucket counts this store sees in practice.
    /// </summary>
    private void MaybeSweep()
    {
        if (_options.SweepEveryN < 1) return;
        if (Interlocked.Increment(ref _opCount) % _options.SweepEveryN != 0) return;
        Sweep();
    }

    private void Sweep()
    {
        var now = _clock();

        // Phase 1: drop buckets idle longer than IdleTtl.
        if (_options.IdleTtl > TimeSpan.Zero)
        {
            var idleCutoff = now - _options.IdleTtl;
            foreach (var kvp in _buckets)
            {
                if (kvp.Value.LastRefillUtc < idleCutoff)
                {
                    // CAS-style remove: only drop if the bucket is still the
                    // one we observed (concurrent TryConsume may have just
                    // touched it).
                    ((ICollection<KeyValuePair<(string Policy, string Key), Bucket>>)_buckets)
                        .Remove(kvp);
                }
            }
        }

        // Phase 2: enforce MaxBuckets via LRU-by-LastRefillUtc.
        if (_options.MaxBuckets > 0 && _buckets.Count > _options.MaxBuckets)
        {
            var excess = _buckets.Count - _options.MaxBuckets;
            // Snapshot keys + timestamps; sort by LastRefillUtc asc; evict the
            // oldest `excess`. Acceptable for the cap branch -- it only runs
            // when the dictionary has already overshot, which we treat as a
            // memory-pressure event worth a one-off O(n log n).
            var snapshot = _buckets.ToArray();
            Array.Sort(snapshot, static (a, b) => a.Value.LastRefillUtc.CompareTo(b.Value.LastRefillUtc));
            for (var i = 0; i < excess && i < snapshot.Length; i++)
            {
                ((ICollection<KeyValuePair<(string Policy, string Key), Bucket>>)_buckets)
                    .Remove(snapshot[i]);
            }
        }
    }

    private readonly record struct Bucket(double Tokens, DateTime LastRefillUtc);
}

/// <summary>
///     Memory-bounding knobs for <see cref="InMemoryTokenBucketStore"/>.
///     Defaults are sized for a single-process deployment of stylobot's
///     gateway under steady-state load (low-cardinality IP or signature
///     key, p99 active key count well under <see cref="MaxBuckets"/>).
/// </summary>
public sealed class InMemoryTokenBucketStoreOptions
{
    /// <summary>
    ///     A bucket is evicted when it has not been touched (refilled or
    ///     consumed against) for at least this long. Default 10 minutes:
    ///     long enough that a real visitor pausing on a page keeps their
    ///     bucket, short enough that abandoned scraper fingerprints don't
    ///     linger. Set to <see cref="TimeSpan.Zero"/> to disable the TTL.
    /// </summary>
    public TimeSpan IdleTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Hard cap on the number of buckets the store keeps in memory.
    ///     When exceeded, the sweep evicts the oldest-touched (LRU by
    ///     <c>LastRefillUtc</c>) until the count is at the cap. Default
    ///     100,000 (~2.4 MB at 24 bytes per bucket record). Set to 0 to
    ///     disable the cap.
    /// </summary>
    public int MaxBuckets { get; set; } = 100_000;

    /// <summary>
    ///     Sweep runs lazily, once every N <see cref="InMemoryTokenBucketStore.TryConsume"/>
    ///     calls. Default 1024: at 100 RPS that's roughly a sweep every
    ///     10 seconds. Lower values increase sweep frequency at the cost
    ///     of more enumeration work; higher values let the dictionary
    ///     overshoot <see cref="MaxBuckets"/> further between sweeps.
    /// </summary>
    public int SweepEveryN { get; set; } = 1024;
}
