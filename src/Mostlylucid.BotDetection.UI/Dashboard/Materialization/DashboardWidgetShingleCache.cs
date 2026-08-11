using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     Bounded LFU cache of pre-rendered dashboard widget <b>shingles</b> -- each entry
///     is one widget's OOB-tagged HTML element, keyed by its per-widget fingerprint
///     (widget + filter/params + data-change version; see
///     <see cref="Middleware.WidgetRenderHelpers.ComputeWidgetShingleFingerprint"/>).
///     <para>
///         This is the render-layer sibling of <see cref="DashboardContentCache"/> (the
///         data layer): the content cache holds the composed datasets; the shingle cache
///         holds the rendered elements built from them. Treating every rendered element
///         as an LFU entity means the widgets operators actually watch stay resident and
///         serve warm, while cold fingerprints (a filter no one is on, a stale data
///         version) evict on their own. The version in the key makes freshness granular:
///         a data change re-keys only the affected widget, so the OOB update response
///         carries just the shingles that actually changed -- HTMX morphs each into place
///         by id.
///     </para>
///     <para>
///         Kept deliberately small (<see cref="StyloBotDashboardOptions.WidgetShingleCacheMaxEntries"/>):
///         the resident set is the hot working set, not every filter permutation ever seen.
///         Wraps the canonical <see cref="BoundedCache{TKey,TValue}"/> LFU primitive, whose
///         <c>Set</c> preserves the hit count on refresh so re-rendering a hot shingle keeps
///         its popularity score.
///     </para>
/// </summary>
public sealed class DashboardWidgetShingleCache
{
    private readonly BoundedCache<string, string> _shingles;

    public DashboardWidgetShingleCache(IOptions<StyloBotDashboardOptions> options)
    {
        var o = options.Value;
        _shingles = new BoundedCache<string, string>(
            maxSize: Math.Max(1, o.WidgetShingleCacheMaxEntries),
            defaultTtl: o.WidgetShingleSafetyTtl);
    }

    /// <summary>Serves the resident shingle for <paramref name="fingerprint"/>, if warm.</summary>
    public bool TryGet(string fingerprint, out string html) => _shingles.TryGet(fingerprint, out html!);

    /// <summary>Stores a freshly-rendered shingle. Bounded eviction reclaims cold entries.</summary>
    public void Set(string fingerprint, string html) => _shingles.Set(fingerprint, html);

    /// <summary>Resident shingle count (for diagnostics / the system-info surface).</summary>
    public int Count => _shingles.Count;

    /// <summary>
    ///     Live (non-expired) shingles as a fingerprint → HTML map — the disk-persistence
    ///     snapshot (operator directive 2026-08-11: persist rendered content so a warm
    ///     boot serves the last-known-good shingles from disk before the materializer
    ///     refreshes).
    /// </summary>
    public IReadOnlyDictionary<string, string> Snapshot() =>
        _shingles.Snapshot().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    /// <summary>Re-populates the cache from a persisted snapshot (warm-boot load).</summary>
    public void Restore(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var (fingerprint, html) in entries)
            if (!string.IsNullOrEmpty(fingerprint) && !string.IsNullOrEmpty(html))
                _shingles.Set(fingerprint, html);
    }
}
