using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

/// <summary>
///     Identities tab listing — one row per metastable fingerprint with the unabsorbed
///     observation count surfaced so an operator can spot fingerprints with fresh data
///     waiting to be folded. Re-verify and Run-AI buttons in the Razor view post to the
///     dashboard's identity action endpoints.
///
///     Returns an empty model with <c>IdentityEnabled = false</c> when the identity layer
///     is dormant; the Razor view renders an explainer instead of a table in that case.
/// </summary>
public class SbIdentitiesListViewComponent(
    IFingerprintReader store,
    IOptions<BotDetectionOptions> options,
    StyloBotDashboardOptions dashboardOptions)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(int page = 1, int pageSize = 25)
    {
        var enabled = options.Value.Identity.Enabled;
        if (!enabled)
        {
            return View(new IdentitiesListModel
            {
                IdentityEnabled = false,
                BasePath = dashboardOptions.BasePath.TrimEnd('/'),
                Page = page,
                PageSize = pageSize
            });
        }

        // Fetch a bounded window (most-recent first) — never the unbounded overload, which
        // materialises every row carrying a full centroid vector (conn- 2026-08-08).
        // The cap mirrors IdentityEngineOptions.MaxFingerprintsPerPage (default 200).
        var cap = options.Value.Identity?.Engine?.MaxFingerprintsPerPage ?? 200;
        var all = await store.ListFingerprintsAsync(0, cap);
        var unabsorbedByFp = await store.GetUnabsorbedObservationCountsAsync();

        // Sort: highest unabsorbed-observation count first so drift candidates float; tie-break
        // on most-recent activity. The operator's primary use case is "where do I look?".
        // NOTE: sort is within the bounded window; a follow-up should push the sort to SQL
        // so the unbounded TotalCount survives the bounded fetch.
        var ordered = all
            .OrderByDescending(fp => unabsorbedByFp.GetValueOrDefault(fp.FingerprintId, 0))
            .ThenByDescending(fp => fp.LastSeen)
            .ToList();

        var rows = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(fp => new IdentityListEntry
            {
                FingerprintId = fp.FingerprintId,
                InferredClientType = fp.InferredClientType,
                InferredTypeConfidence = fp.InferredTypeConfidence,
                CentroidMaturity = fp.CentroidMaturity,
                ObservationCount = fp.ObservationCount,
                UnabsorbedObservations = unabsorbedByFp.GetValueOrDefault(fp.FingerprintId, 0),
                CorrectionCount = fp.CorrectionCount,
                CachedBotProbability = fp.CachedBotProbability,
                // RiskBand derived at read (verified-aware), never a stored band.
                CachedRiskBand = Mostlylucid.BotDetection.Identity.FingerprintRiskProjection.Compose(fp).RiskBand.ToString(),
                CachedScoreUpdatedAt = fp.CachedScoreUpdatedAt,
                FirstSeen = fp.FirstSeen,
                LastSeen = fp.LastSeen,
                ArchetypeOrigin = fp.ArchetypeOrigin,
                AmbiguityPersistence = fp.AmbiguityPersistence
            })
            .ToList();

        return View(new IdentitiesListModel
        {
            Identities = rows,
            TotalCount = ordered.Count,
            Page = page,
            PageSize = pageSize,
            IdentityEnabled = true,
            BasePath = dashboardOptions.BasePath.TrimEnd('/')
        });
    }
}
