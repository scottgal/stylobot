using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Implemented by anything that contributes a single-glance tile to the
///     dashboard overview pack-health row (C1 of the Pack Metrics + Cohesive
///     Views plan). Multiple providers are registered against
///     <c>IEnumerable&lt;IPackHealthSummaryProvider&gt;</c>; the row renders
///     one tile per provider that returns non-null.
///     <para>
///         Providers are in-process by design -- each one wraps an existing
///         summary builder (e.g. <see cref="IAspNetPackHubBuilder"/>,
///         <see cref="PolicyStackSummaryBuilder"/>, <see cref="PrometheusPack.Telemetry.IMeterStream"/>).
///         The dashboard never HTTP-loopbacks into its own gateway's JSON
///         endpoints; the JSON endpoints exist so cross-host viewer dashboards
///         (commercial overlay) can rollup the fleet.
///     </para>
///     <para>
///         Per <c>feedback_remote_mode_optional_di</c>: a provider's backing
///         dependency MUST be injected as nullable; null means the surface is
///         absent on this host and <see cref="BuildTileAsync"/> returns null.
///         A provider that throws does NOT take down the row -- the row
///         builder catches and logs.
///     </para>
/// </summary>
public interface IPackHealthSummaryProvider
{
    /// <summary>
    ///     Sort key (low = leftmost). Convention: 100s buckets per pack family
    ///     so future packs slot in without jostling every existing provider.
    /// </summary>
    int Order { get; }

    /// <summary>
    ///     Returns null if this provider's pack/surface is not enabled in the
    ///     current host (the most common reason: a nullable dependency
    ///     resolved to null on a viewer-mode host).
    /// </summary>
    Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct);
}
