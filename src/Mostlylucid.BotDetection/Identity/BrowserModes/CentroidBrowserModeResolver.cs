using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     <see cref="IBrowserModeResolver"/> backed by the DB-persisted
///     <see cref="ModeCentroidClassifier"/> (T12 + T11b). Replaces the
///     YAML-predicate-walking <see cref="CachingBrowserModeResolver"/> on the
///     production DI path so mode classification runs against centroids loaded
///     from the <c>identity_archetypes</c> table (<c>catalogue_kind = 'browser_mode'</c>)
///     instead of static YAML predicates. Calibration drift on a centroid
///     survives gateway restart per <c>feedback_centroids_not_rules</c>.
///
///     <para>
///     Caches the resolved id + similarity in <see cref="HttpContext.Items"/>
///     under the same <see cref="CachingBrowserModeResolver.ItemsKey"/> the
///     prior resolver used, so the early endpoint-policy resolver and the
///     later <c>BrowserModeClassifierContributor</c> still observe the same
///     classification for a given request without recomputation. Dashboard /
///     telemetry consumers that read the items entry by string key are
///     unaffected by the swap.
///     </para>
///
///     <para>
///     <b>Path-distinguishing caveat:</b> the 3-input classifier
///     (method + sec-fetch-mode + upgrade) physically cannot separate
///     <c>signalr-negotiate</c> from <c>xhr</c>; both observations land in the
///     <c>xhr</c> bucket. Path-based mode resolution is a future enhancement
///     (T15+) and is intentionally out of scope here.
///     </para>
/// </summary>
public sealed class CentroidBrowserModeResolver : IBrowserModeResolver
{
    /// <summary>
    ///     HttpContext.Items key for the cached similarity score. Stored
    ///     alongside the mode id (which lives under
    ///     <see cref="CachingBrowserModeResolver.ItemsKey"/>) so downstream
    ///     consumers can surface the classifier's confidence without
    ///     re-running cosine. Stable string constant, no typed marker, so the
    ///     dashboard can read it without a hard reference on this assembly.
    /// </summary>
    public const string SimilarityItemsKey = "Mostlylucid.BotDetection.BrowserMode.Similarity";

    private readonly ModeCentroidClassifier _classifier;

    public CentroidBrowserModeResolver(ModeCentroidClassifier classifier)
    {
        _classifier = classifier;
    }

    public string Resolve(HttpContext context)
    {
        if (context.Items.TryGetValue(CachingBrowserModeResolver.ItemsKey, out var cached)
            && cached is string s)
        {
            return s;
        }

        // Three inputs the layout's mode-relevant slots take. Empty header
        // values feed through to the encoder which treats them as the
        // canonical "no Sec-Fetch headers observed" / "no Upgrade" signal --
        // distinguishable from missing slots and from navigation, per the
        // sparse-encoder contract in ModeVectorEncoder.
        var headers = context.Request.Headers;
        var match = _classifier.Classify(
            method:           context.Request.Method,
            secFetchMode:     headers["Sec-Fetch-Mode"].ToString(),
            upgradeWebsocket: string.Equals(
                headers["Upgrade"].ToString(),
                "websocket",
                StringComparison.OrdinalIgnoreCase));

        context.Items[CachingBrowserModeResolver.ItemsKey] = match.ModeId;
        context.Items[SimilarityItemsKey] = match.Similarity;
        return match.ModeId;
    }

    /// <summary>
    ///     Lookup-only accessor for the cached similarity score. Returns
    ///     <c>null</c> when this resolver hasn't classified the request yet
    ///     (e.g. an upstream call site asked for the id from a different
    ///     resolver impl, or this resolver was swapped out in DI). Callers
    ///     should treat null as "no similarity surfaced" rather than zero so
    ///     the dashboard doesn't show a false "0% confidence" hit.
    /// </summary>
    public static double? TryGetSimilarity(HttpContext context)
    {
        if (context.Items.TryGetValue(SimilarityItemsKey, out var v) && v is double d)
            return d;
        return null;
    }
}
