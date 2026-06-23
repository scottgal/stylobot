namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Backing store for token-bucket rate limiters. One bucket per
///     (policy name, identity key) pair. The interface is deliberately
///     minimal so the in-memory implementation (default for phase 2)
///     can later be swapped for a SQLite-backed write-through cache
///     without touching consumers.
/// </summary>
/// <remarks>
///     <para>
///         Token-bucket state is per-request transient -- worst case on
///         a process restart is a brief burst gets through, and the only
///         effect is a slightly higher request rate for the first second
///         or two. That fits the "ConcurrentDictionary for transient
///         state" carve-out from the no-in-memory-store rule.
///     </para>
///     <para>
///         All operations are thread-safe and lock-free on the hot path
///         (the default <see cref="InMemoryTokenBucketStore"/> uses
///         CAS via <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryUpdate"/>).
///     </para>
/// </remarks>
public interface ITokenBucketStore
{
    /// <summary>
    ///     Try to consume one token from the bucket identified by
    ///     <paramref name="policyName"/> + <paramref name="key"/>.
    /// </summary>
    /// <param name="policyName">
    ///     The action policy that owns this bucket (e.g. <c>rate-limit-search</c>).
    ///     Buckets are isolated per policy so two policies pointing at the
    ///     same key don't share capacity.
    /// </param>
    /// <param name="key">
    ///     Stable identity for the visitor (signature or IP, depending on
    ///     <see cref="Mostlylucid.BotDetection.Actions.RateLimitActionOptions.KeyBy"/>).
    /// </param>
    /// <param name="capacity">
    ///     Maximum bucket fill (the burst allowance).
    /// </param>
    /// <param name="refillRatePerMinute">
    ///     Steady-state allowance. A bucket at empty refills to capacity in
    ///     <c>60 * capacity / refillRatePerMinute</c> seconds.
    /// </param>
    /// <returns>
    ///     <c>true</c> if a token was consumed (request allowed); <c>false</c>
    ///     if the bucket was empty (request must fall through to the
    ///     over-limit action).
    /// </returns>
    bool TryConsume(string policyName, string key, int capacity, int refillRatePerMinute);

    /// <summary>
    ///     Inspect the current state of a bucket without consuming. Used by
    ///     the dashboard to render "tokens remaining" without affecting
    ///     rate-limit accounting.
    /// </summary>
    BucketSnapshot? Peek(string policyName, string key);
}

/// <summary>
///     Read-only snapshot of a bucket's state at a point in time. The
///     dashboard renders one of these per (policy, visitor) row.
/// </summary>
/// <param name="TokensRemaining">
///     How many tokens are left after the most recent refill calculation.
///     Fractional (a bucket halfway between two refills sits at e.g. 4.7).
/// </param>
/// <param name="Capacity">Burst size (the ceiling).</param>
/// <param name="LastRefillUtc">When the bucket was last touched.</param>
public sealed record BucketSnapshot(double TokensRemaining, int Capacity, DateTime LastRefillUtc);
