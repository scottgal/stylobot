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
    ///     Resolve a signature's display name from the canonical read-through path.
    ///     The LFU <see cref="SignatureAggregateCache"/> is the source of truth at
    ///     read time -- it gets the latest composed name from the matcher's
    ///     <c>EmitDisplayNameSignal</c> recompose, so the dashboard list, the
    ///     signature-detail page, and the "Your Detection" card all see the same
    ///     name on the same render.
    ///
    ///     Order:
    ///     <list type="number">
    ///         <item>Cache hit with a non-fallback name -> wins outright. The
    ///             matcher's recompose path keeps this fresh; a stored / lookup
    ///             value that hasn't caught up yet must NOT override.</item>
    ///         <item>Stored name (last persisted detection's <c>bot_name</c>) when
    ///             it's a real Priority-1/2 catalog name -- this is what carries
    ///             the row through a cold dashboard render before the cache has
    ///             been seeded for that signature.</item>
    ///         <item>Cache hit even if fallback-shaped -- a fresh "Unknown" from
    ///             the cache beats a missing lookup.</item>
    ///         <item>The persisted <c>dashboard_signatures</c> lookup as last
    ///             resort.</item>
    ///     </list>
    ///
    ///     Crucially the stored value NEVER wins over a fresh non-fallback cache
    ///     hit. That was the pre-2026-06-24 priority and it created the
    ///     list-vs-detail divergence the user called out ("list shows X, click
    ///     through to detail shows Y") because the detail page was raw-reading
    ///     the stale <c>latest.bot_name</c> from the detection row while the list
    ///     went through the cache.
    /// </summary>
    public static string? ResolveBotName(
        this Dictionary<string, string?> signatureLookup,
        SignatureAggregateCache? cache,
        string signature,
        string? storedName)
    {
        var cached = cache?.GetResolvedName(signature);

        // Cache wins over stored as long as the cache value isn't a fallback
        // ("Unknown ..." / "analysing" / UA-prefix). A fresh real name from the
        // matcher recompose beats a stale stored name on the detection row.
        if (!string.IsNullOrEmpty(cached)
            && !Mostlylucid.BotDetection.Services.FingerprintNameComposer.IsFallback(cached))
            return cached;

        // Stored name is the cold-render fallback (dashboard renders before the
        // cache is seeded for this signature). Still only when it's a real name --
        // a previously-persisted fallback ("Chrome Desktop" before the verdict-
        // honest rewrite, "Unknown 0..." pre-cleanup) must yield to the cache.
        if (!string.IsNullOrEmpty(storedName)
            && !Mostlylucid.BotDetection.Services.FingerprintNameComposer.IsFallback(storedName))
            return storedName;

        // Cache wins for fallbacks too -- fresh "Unknown" beats nothing.
        if (!string.IsNullOrEmpty(cached)) return cached;

        // Final resort: the persistent signatures-lookup dict (loaded from
        // dashboard_signatures.bot_name via LoadSignatureLookupAsync). Then null.
        if (signatureLookup.TryGetValue(signature, out var name) && !string.IsNullOrEmpty(name))
            return name;
        return !string.IsNullOrEmpty(storedName) ? storedName : null;
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
