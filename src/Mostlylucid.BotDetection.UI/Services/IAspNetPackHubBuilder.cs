using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Read surface for the <c>/dashboard/aspnet-hub</c> page (B2 of the Pack
///     Metrics + Cohesive Views plan). Two methods on purpose: the dashboard
///     partial calls <see cref="BuildAsync" /> for the rendered view model;
///     the <c>GET /api/v1/packs/aspnet/summary</c> JSON endpoint calls
///     <see cref="BuildSummaryAsync" /> for the rollup-only payload.
///     <para>
///         Lives in the UI project so the dashboard partial + middleware can
///         depend on a stable contract without forming a circular reference on
///         AspNetPack (which already references UI for its dashboard
///         contributions). The implementation ships from AspNetPack so it can
///         take a direct optional dependency on <c>IAspNetEndpointInventory</c>.
///     </para>
///     <para>
///         Per <c>feedback_remote_mode_optional_di</c>: viewer hosts that pull
///         data via REST (no in-process inventory) are a valid mode -- the
///         implementation degrades gracefully when the inventory is null
///         rather than throwing.
///     </para>
/// </summary>
public interface IAspNetPackHubBuilder
{
    /// <summary>
    ///     Composes the full hub-page view model: header + top tiles + ingest
    ///     trend card + meter table. Pure read; never mutates state.
    /// </summary>
    Task<AspNetPackHubViewModel> BuildAsync(CancellationToken ct);

    /// <summary>
    ///     Computes the rollup summary served by
    ///     <c>GET /api/v1/packs/aspnet/summary</c>. Numbers match the values
    ///     surfaced in <see cref="BuildAsync" />'s tiles so a fleet-rollup
    ///     consumer sees what an operator looking at the page sees.
    /// </summary>
    Task<AspNetPackSummary> BuildSummaryAsync(CancellationToken ct);
}
