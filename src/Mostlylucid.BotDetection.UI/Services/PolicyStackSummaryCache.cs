namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Tiny in-memory cache for the global policy-stack summary tile. Holds
///     ONE composed <see cref="PolicyStackSummary"/> and the time it was
///     captured; the matching pack-health provider hits the cache first
///     and falls back to <see cref="PolicyStackSummaryBuilder"/> on a miss.
///
///     <para>
///         The cache is invalidated by the centralised
///         <see cref="DashboardFreshnessBeacon"/> path -- not by a private
///         TTL -- so the read surface always reflects the rule corpus the
///         operator just edited. The next read after invalidate rebuilds,
///         that's it; no per-cache warmup hosted service, no per-cache
///         expiry timer.
///     </para>
///
///     <para>
///         Acceptable as in-memory state per
///         <c>feedback_no_inmemory_stores</c>: this is a runtime cache
///         (rebuilt on miss) of a computation that itself reads from the
///         persistent rule store; never a substitute for storage.
///     </para>
/// </summary>
public sealed class PolicyStackSummaryCache
{
    private readonly object _gate = new();
    private PolicyStackSummary? _cached;

    /// <summary>
    ///     Returns the cached summary, or <c>null</c> if the cache has
    ///     never been populated or was invalidated. The caller rebuilds
    ///     on miss via <see cref="PolicyStackSummaryBuilder.BuildAsync"/>
    ///     and stores the result with <see cref="Set"/>.
    /// </summary>
    public PolicyStackSummary? TryGet()
    {
        lock (_gate) return _cached;
    }

    /// <summary>Store a freshly-built summary in the cache.</summary>
    public void Set(PolicyStackSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        lock (_gate) _cached = summary;
    }

    /// <summary>
    ///     Drop the cached summary. Producers invoke this when the
    ///     underlying rule corpus changes; the next read rebuilds via the
    ///     summary builder.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate) _cached = null;
    }
}
