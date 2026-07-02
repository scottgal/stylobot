namespace Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;

public sealed class DashboardLayoutOptions
{
    /// <summary>How many rows each breakdown card shows on Traffic.</summary>
    public int TrafficCardTopN { get; set; } = 8;

    /// <summary>
    ///     Default chart window on Traffic in minutes; URL <c>?window=</c>
    ///     overrides. Defaults to 24h (1440min): a full day at 20-minute
    ///     buckets (72 bars) reads as a normal analytics view, where the
    ///     shorter 6h window on a quiet console collapsed to a single block.
    /// </summary>
    public int DefaultTimeWindowMinutes { get; set; } = 1440;

    /// <summary>Header search type-ahead result cap.</summary>
    public int SearchMaxResults { get; set; } = 10;

    /// <summary>
    ///     Minimum total request count for an endpoint to surface its p95 latency +
    ///     error % on the Top Endpoints widget. Endpoints below the threshold render
    ///     a dash placeholder so cold-start / brand-new paths do not show misleading
    ///     numbers. Default 3 mirrors the DegradationAtom min-sample convention.
    /// </summary>
    public int TopEndpointsMinSamplesForPerf { get; set; } = 3;

    /// <summary>
    ///     Kill-switch during migration. When true (default after M1), sidebar +
    ///     landing-page routing use the new IA (Traffic default, three aggregates
    ///     + packs + manage). When false, the legacy 10+ tab sidebar continues.
    ///     URL ?legacy=1 forces legacy even when this is true. Removed in M2 after
    ///     legacy surfaces are deleted.
    /// </summary>
    public bool V2Enabled { get; set; } = true;   // was false; M1 flips to default-on
}
