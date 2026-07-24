using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     Measured-cost-vs-budget adaptive controller for the Stage 2b tick-driven due-time
///     gate (supersedes an earlier abandoned draft that used "tick-overrun ratio" -- the
///     time between successive ticks -- as a load proxy; that conflates idle gaps with
///     actual work and was rejected).
///     <para>
///         Tracks a smoothed (EMA) measure of the ACTUAL wall-clock time spent warming
///         page keys each tick (<see cref="RecordTickCost"/> -- the sum of real compose
///         time, not tick-to-tick elapsed time). <see cref="CurrentScaleFactor"/> derives
///         a uniform stretch factor (never below 1.0) from how far that smoothed cost sits
///         above <see cref="DashboardMaterializerOptions.RefreshCostBudgetMs"/>:
///         <c>scaleFactor = max(1.0, smoothedCostMs / RefreshCostBudgetMs)</c>. As cost
///         rises above budget the factor rises proportionally (every page key's effective
///         interval stretches uniformly, per <see cref="DashboardRefreshCadence"/>); as
///         cost falls back under budget the smoothed value -- and therefore the factor --
///         relaxes back toward 1.0 on its own, with no separate ratchet/step logic needed.
///     </para>
///     <para>
///         Net effect: rising load -&gt; refresh less often -&gt; the coordinator converges
///         toward serving the same cached bundle repeatedly, which structurally cannot
///         collapse into an unbounded feedback loop the way naive per-request computation
///         would under the same load.
///     </para>
///     <para>
///         Thread-safety: <see cref="RecordTickCost"/> and <see cref="CurrentScaleFactor"/>
///         are both called from <c>DashboardMaterializerCoordinator.MaterializeTickAsync</c>,
///         which the schedule coordinator's single-flight guard ensures never overlaps with
///         itself -- but the lock here is cheap insurance against any future caller (e.g. a
///         diagnostics endpoint reading the current factor) racing a tick.
///     </para>
/// </summary>
public sealed class DashboardMaterializerAdaptiveController
{
    private readonly IOptions<DashboardMaterializerOptions> _options;
    private readonly object _gate = new();
    private double _smoothedCostMs;

    public DashboardMaterializerAdaptiveController(IOptions<DashboardMaterializerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    ///     The current uniform scale factor (always &gt;= 1.0) every page key's
    ///     class/hotness-derived interval is multiplied by. 1.0 means no throttling: either
    ///     no cost has been recorded yet, the smoothed cost is at or under budget, or the
    ///     configured budget is zero/negative (a misconfiguration that disables throttling
    ///     rather than dividing by zero -- fails toward "no slowdown", the safer direction
    ///     for a broken config, never toward "infinite slowdown").
    /// </summary>
    public double CurrentScaleFactor
    {
        get
        {
            lock (_gate)
            {
                var budget = _options.Value.RefreshCostBudgetMs;
                if (budget <= 0) return 1.0;
                return Math.Max(1.0, _smoothedCostMs / budget);
            }
        }
    }

    /// <summary>
    ///     Feeds one tick's measured total warm-work cost (milliseconds) into the smoothed
    ///     estimate. Uses a standard EMA blended against the running smoothed value
    ///     (starting from an implicit 0 baseline) -- so even the very FIRST recorded sample
    ///     is already dampened by <see cref="DashboardMaterializerOptions.AdaptiveCostSmoothingAlpha"/>,
    ///     not applied raw. This means a single unusually slow tick (a corpus-scale query
    ///     regression on one pass) cannot alone trip the throttle to the full raw ratio; a
    ///     sustained trend still reaches it within a handful of ticks.
    /// </summary>
    /// <param name="measuredMs">
    ///     Real wall-clock time spent actually composing/warming this tick (sum across every
    ///     page key warmed, not tick-to-tick elapsed time). Negative values (should never
    ///     happen) are treated as zero rather than corrupting the running estimate.
    /// </param>
    public void RecordTickCost(double measuredMs)
    {
        if (measuredMs < 0) measuredMs = 0;

        lock (_gate)
        {
            var alpha = Math.Clamp(_options.Value.AdaptiveCostSmoothingAlpha, 0.0, 1.0);
            _smoothedCostMs = alpha * measuredMs + (1 - alpha) * _smoothedCostMs;
        }
    }
}
