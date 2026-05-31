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
        if (cache is not null && cache.TryGet(signature, out var agg) && !string.IsNullOrEmpty(agg?.BotName))
            return agg.BotName;
        return signatureLookup.TryGetValue(signature, out var name) ? name : null;
    }
}
