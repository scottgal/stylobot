namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Bot-name resolution for session list rows. PersistedSession.BotName is written at
///     session-end and is usually null because the signature description pipeline names the
///     bot (e.g. "GPTBot", "wget") AFTER the session row was persisted. Two fallbacks:
///     <list type="number">
///         <item>SignatureAggregateCache.TryGet — in-memory, fast, but empty across container restart.</item>
///         <item>IDashboardEventStore.GetSignaturesAsync — persistent, survives restart.</item>
///     </list>
///     Sessions widget renders SbSessionsList from three code paths
///     (SbSessionsListViewComponent, SbWidgetBatchMiddleware, StyloBotDashboardMiddleware);
///     all three use the same helpers here.
/// </summary>
public static class SessionEnrichmentExtensions
{
    /// <summary>
    ///     Fetch the most recent signatures and build a primarySignature -> botName lookup.
    ///     One DB call per render — callers cache the result for the duration of the request.
    /// </summary>
    public static async Task<Dictionary<string, string?>> LoadSignatureLookupAsync(
        this IDashboardEventStore store,
        int limit = 500)
    {
        var lookup = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            var recent = await store.GetSignaturesAsync(limit);
            foreach (var s in recent)
                lookup[s.PrimarySignature] = s.BotName;
        }
        catch
        {
            // store unavailable -- caller falls through to cache-only resolution
        }
        return lookup;
    }

    /// <summary>
    ///     Resolve a session's display name in order of preference: the stored value (if the
    ///     session row already had one), then the in-memory cache, then the persistent
    ///     dashboard_signatures lookup. Returns null when no name is known anywhere; callers
    ///     fall back to the raw signature substring.
    /// </summary>
    public static string? ResolveBotName(
        this Dictionary<string, string?> signatureLookup,
        SignatureAggregateCache? cache,
        string signature,
        string? storedName)
    {
        if (!string.IsNullOrEmpty(storedName)) return storedName;
        // The in-memory cache no longer carries a BotName field; the resolved
        // dict (populated by ApplyResolvedNames from IFingerprintStore) is the
        // only on-the-gateway path. Falls through to the dashboard_signatures
        // lookup when the cache hasn't been seeded yet.
        if (cache is not null)
        {
            var cached = cache.GetResolvedName(signature);
            if (!string.IsNullOrEmpty(cached)) return cached;
        }
        return signatureLookup.TryGetValue(signature, out var name) ? name : null;
    }

    /// <summary>
    ///     Fetch the most-recent top-N signatures (24h window) and build a
    ///     primarySignature -> raw User-Agent lookup. Sources from
    ///     <c>GetTopBotsAsync</c> because <c>GetSignaturesAsync</c>'s
    ///     <see cref="DashboardSignatureEvent"/> projection doesn't carry UA --
    ///     the signatures table schema has no <c>user_agent_raw</c> column;
    ///     detections does, and TopBotsAsync's per-signature aggregation
    ///     already surfaces the latest UA per row after the identity-display
    ///     plumbing. Used by the sessions list to render "Chrome 148 / macOS"
    ///     instead of "GB User N" -- same identity-display fix the visitor
    ///     list got in <see cref="WidgetRenderHelpers.ProjectAsVisitors"/>.
    /// </summary>
    public static async Task<Dictionary<string, string?>> LoadUserAgentLookupAsync(
        this IDashboardEventStore store,
        int limit = 500)
    {
        var lookup = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            var top = await store.GetTopBotsAsync(
                count: limit,
                startTime: DateTime.UtcNow.AddHours(-24),
                endTime: DateTime.UtcNow,
                audienceFilter: "all");
            foreach (var entry in top)
                lookup[entry.PrimarySignature] = entry.UserAgent;
        }
        catch
        {
            // store unavailable -- caller falls through to cache-only resolution
        }
        return lookup;
    }

    /// <summary>
    ///     Resolve a session's raw UA in order of preference: the in-memory
    ///     aggregate cache (write-through, freshest), then the precomputed
    ///     <see cref="LoadUserAgentLookupAsync"/> dictionary. Returns null
    ///     when no UA is known anywhere -- SignatureDisplayName.Resolve then
    ///     falls back to the legacy country/family composite.
    /// </summary>
    public static string? ResolveUserAgent(
        this Dictionary<string, string?> userAgentLookup,
        SignatureAggregateCache? cache,
        string signature)
    {
        if (cache is not null && cache.TryGet(signature, out var agg) && !string.IsNullOrEmpty(agg?.UserAgent))
            return agg.UserAgent;
        return userAgentLookup.TryGetValue(signature, out var ua) ? ua : null;
    }
}
