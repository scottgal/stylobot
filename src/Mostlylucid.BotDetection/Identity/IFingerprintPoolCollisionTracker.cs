namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Tracks how many distinct (IP-hash, session-id) contexts have produced
///     the same fingerprint "shape" hash within a sliding window. Drives the
///     <c>PoolCollisionContributor</c> that catches anti-detect browsers whose
///     curated real-fingerprint databases have finite cardinality (Multilogin
///     Mimic, Kameleo Chroma -- both distribute the same profiles to multiple
///     customers, so collisions are inevitable above scrape volume).
///     <para>
///     Implementation is a <see cref="Storage.WriteBehindLfuStore{TKey, TValue, TWriteOp}"/>
///     subclass: hot ConcurrentDictionary tier for the per-request hot path,
///     SQLite-backed durable tier for cross-restart persistence and dashboard
///     surfacing. The hot path is sync and lock-free; reads come from the
///     hot tier (cold misses fall through to SQLite).
///     </para>
/// </summary>
public interface IFingerprintPoolCollisionTracker
{
    /// <summary>
    ///     Record that <paramref name="shapeHash"/> was observed under
    ///     <paramref name="ipHash"/> + <paramref name="sessionId"/> at
    ///     <paramref name="utcNow"/>. Synchronous; the hot dict is updated
    ///     immediately and the durable write is enqueued for batched persist.
    /// </summary>
    void Observe(string shapeHash, string ipHash, string sessionId, DateTimeOffset utcNow);

    /// <summary>
    ///     Number of distinct (IP-hash, session-id) contexts observed for
    ///     <paramref name="shapeHash"/> since <paramref name="since"/>.
    ///     Hot-path only -- returns 0 for shapes that have been LFU-evicted
    ///     from the hot tier even if SQLite still has them. Acceptable because
    ///     LFU eviction targets the coldest entries; recent activity stays hot.
    /// </summary>
    int DistinctContextsInWindow(string shapeHash, DateTimeOffset since);

    /// <summary>Diagnostic: shape count currently in the hot tier.</summary>
    int TrackedShapeCount { get; }

    /// <summary>
    ///     Create the SQLite schema on first use. Called by the store's host
    ///     service at startup; subsequent calls are no-ops.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);
}
