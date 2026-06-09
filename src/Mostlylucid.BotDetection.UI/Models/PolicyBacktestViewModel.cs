namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for the C8 backtest panel. Built from a
///     <see cref="Policies.Decisions.PolicyBacktestResult"/> by the
///     middleware route that the C6 editor's debounced JS calls into.
///     The panel renders below the predicate fieldset and above the mode
///     fieldset inside <c>_EditRow.cshtml</c>, populated only after the
///     JS has a successful parse and posted the candidate.
/// </summary>
/// <param name="TotalRequests">Total decision rows scanned in the window.</param>
/// <param name="WouldMatch">Rows where the candidate predicate evaluated true.</param>
/// <param name="WouldEnforce">Rows that matched AND would have won.</param>
/// <param name="OverriddenByHigherPriority">Rows that matched but a higher-priority Allow won.</param>
/// <param name="EstimatedAddedP50Micros">Median predicate-eval latency proxy, microseconds.</param>
/// <param name="EstimatedAddedP99Micros">99th-percentile predicate-eval latency proxy, microseconds.</param>
/// <param name="SampleMatchingFingerprints">Up to 25 fingerprints surfaced for "show matching".</param>
/// <param name="Window">The window the projection covered.</param>
/// <param name="WindowLabel">Human label for the window: "24h" / "7d" / "30d".</param>
/// <param name="Caveat">Inconclusive caveat (e.g. snapshot-missing rows) or <c>null</c>.</param>
public sealed record PolicyBacktestViewModel(
    int TotalRequests,
    int WouldMatch,
    int WouldEnforce,
    int OverriddenByHigherPriority,
    long EstimatedAddedP50Micros,
    long EstimatedAddedP99Micros,
    IReadOnlyList<string> SampleMatchingFingerprints,
    TimeSpan Window,
    string WindowLabel,
    string? Caveat)
{
    /// <summary>
    ///     Enforced percentage of WouldMatch, rounded to one decimal place.
    ///     Returns 0 when WouldMatch is 0 to avoid divide-by-zero in the
    ///     view template.
    /// </summary>
    public string WouldEnforcePercent =>
        WouldMatch == 0
            ? "0.0"
            : ((double)WouldEnforce / WouldMatch * 100.0).ToString("F1");

    /// <summary>
    ///     Overridden percentage of WouldMatch, rounded to one decimal place.
    /// </summary>
    public string OverriddenPercent =>
        WouldMatch == 0
            ? "0.0"
            : ((double)OverriddenByHigherPriority / WouldMatch * 100.0).ToString("F1");
}
