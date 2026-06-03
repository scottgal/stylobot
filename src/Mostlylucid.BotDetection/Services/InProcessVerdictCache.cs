using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Single-process verdict cache keyed by primary signature (IP + UA hash).
///     Populated synchronously by <c>BotDetectionMiddleware</c> right after the
///     orchestrator pipeline returns. Consulted by <see cref="SignatureVerdictGate"/>
///     BEFORE the SignatureCoordinator's in-memory atom store and the identity
///     SQLite store, so a fresh visitor's HTML detection result is immediately
///     visible to the subsequent CSS / JS / font asset fetches in the same
///     page-load burst -- closing the cold-start window that previously made
///     every parallel HTTP/2 sub-resource fetch pay the full pipeline cost.
/// </summary>
/// <remarks>
///     <para>
///         <b>Live trigger</b>: prod sig <c>lXkzR1RLyBbyq19hUIP9ZA</c>, "Firefox on
///         macOS (privacy headers)". Telemetry-driven Bug O trace
///         (commit <c>b3ca0f93</c>) confirmed the cold-start race: the visitor's
///         root request hit identity-cache, but the parallel asset fetches that
///         followed within the same second arrived BEFORE the identity row was
///         written, so each ran the full 14-detector pipeline at 13-15 ms,
///         compounding into hundreds of ms of added page latency and producing
///         multiple divergent verdicts for the same actor.
///     </para>
///     <para>
///         <b>Why IP+UA is the safe key</b>: the metastable fingerprint store
///         already uses primarySig = hash(IP, UA) as the join key in
///         <c>fingerprint_keys</c>. Within a single session burst the visitor
///         doesn't rotate either dimension; an identified bot might rotate
///         across sessions but the SkipMaxAge TTL bounds how long a wrong
///         classification can persist (default 60 s for the L0, 300 s for the
///         identity store).
///     </para>
///     <para>
///         <b>Bounded</b>: a soft cap of <see cref="MaxEntries"/> evicts the
///         oldest-by-insert when full, so a flood of fresh sigs can't push the
///         cache to consume unbounded memory. The cap is intentionally generous
///         (10 000) so realistic traffic mixes fit without churn.
///     </para>
///     <para>
///         <b>Thread-safe</b>: backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>;
///         eviction uses a snapshot enumeration plus single-writer pruning, which
///         is safe under the hot-path concurrent read pattern.
///     </para>
/// </remarks>
public sealed class InProcessVerdictCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;

    /// <summary>Default soft cap on cache size before LRU eviction kicks in.</summary>
    public const int DefaultMaxEntries = 10_000;

    /// <summary>Default TTL on cached verdicts. Matches the L0 SkipMaxAge default.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    /// <summary>Soft cap on cache size. Reads return existing entries past the cap until prune runs.</summary>
    public int MaxEntries => _maxEntries;

    public InProcessVerdictCache() : this(DefaultMaxEntries, DefaultTtl) { }

    public InProcessVerdictCache(int maxEntries, TimeSpan ttl)
    {
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
        _ttl = ttl > TimeSpan.Zero ? ttl : DefaultTtl;
    }

    /// <summary>Current cached entry count (for diagnostics / metrics).</summary>
    public int Count => _entries.Count;

    /// <summary>
    ///     Reads the cached verdict for a primary signature. Returns null when the
    ///     entry is missing or its TTL has expired. Expired entries are removed
    ///     lazily on read; no background sweep keeps the cache "fresh."
    /// </summary>
    public SignatureVerdict? TryGet(string primarySignature)
    {
        if (string.IsNullOrEmpty(primarySignature))
            return null;

        if (!_entries.TryGetValue(primarySignature, out var entry))
            return null;

        if (DateTime.UtcNow >= entry.ExpiresAtUtc)
        {
            // Expired: remove eagerly so we don't return stale data and so prune
            // doesn't have to scan past it next time.
            _entries.TryRemove(primarySignature, out _);
            return null;
        }

        return entry.Verdict;
    }

    /// <summary>
    ///     Inserts or replaces the cached verdict for a primary signature. Called
    ///     by <c>BotDetectionMiddleware</c> at the END of detection so the very
    ///     next request that lands on the same primarySig (typically a parallel
    ///     asset fetch in the same page burst) sees the verdict on read instead
    ///     of paying for a duplicate pipeline run.
    /// </summary>
    public void Put(string primarySignature, SignatureVerdict verdict)
    {
        if (string.IsNullOrEmpty(primarySignature) || verdict is null)
            return;

        var expiresAt = DateTime.UtcNow.Add(_ttl);
        _entries[primarySignature] = new Entry(verdict, expiresAt);

        if (_entries.Count > _maxEntries)
            PruneOldestSnapshot();
    }

    /// <summary>
    ///     Removes a single entry. Used by callers that want to force a
    ///     re-detection -- e.g. an operator-triggered "invalidate this visitor"
    ///     action or a watchdog that just saw a verdict diverge from live
    ///     evidence.
    /// </summary>
    public void Invalidate(string primarySignature)
    {
        if (string.IsNullOrEmpty(primarySignature))
            return;
        _entries.TryRemove(primarySignature, out _);
    }

    /// <summary>
    ///     Drops the soft-cap-overflow tail. Snapshots the entries, sorts by
    ///     ExpiresAtUtc ascending, removes the head until we're back below the
    ///     cap. Snapshot work is bounded by <see cref="MaxEntries"/>; the
    ///     prune fires at most once per Put that crosses the cap.
    /// </summary>
    private void PruneOldestSnapshot()
    {
        // Snapshot first so the active hot path keeps reading without lock contention.
        var snapshot = _entries.ToArray();
        if (snapshot.Length <= _maxEntries) return;

        var overflow = snapshot.Length - _maxEntries;
        Array.Sort(snapshot, (a, b) => a.Value.ExpiresAtUtc.CompareTo(b.Value.ExpiresAtUtc));
        for (var i = 0; i < overflow; i++)
            _entries.TryRemove(snapshot[i].Key, out _);
    }

    private readonly record struct Entry(SignatureVerdict Verdict, DateTime ExpiresAtUtc);
}