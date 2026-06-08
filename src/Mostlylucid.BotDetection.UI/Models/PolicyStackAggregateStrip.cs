using System.Globalization;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     One-line summary of the currently visible rule set, rendered above the
///     Effective tab. Always computed against the POST-FILTER /
///     POST-SORT row list so the operator's filter narrows the strip too.
/// </summary>
/// <param name="TotalRequestsInWindow">Sum of <see cref="PolicyStackRowViewModel.TotalEvaluations"/> across visible rows.</param>
/// <param name="RulesVisible">Count of visible rows -- mirrors <see cref="PolicyStackViewModel.Rows"/>.</param>
/// <param name="RulesTriggeredInWindow">Rows whose <see cref="PolicyStackRowViewModel.TriggerCount"/> &gt; 0.</param>
/// <param name="RulesWithZeroHits">Rows with zero triggers (only relevant for windows &gt;= 7d).</param>
/// <param name="Window">The aggregate window the strip describes.</param>
public sealed record PolicyStackAggregateStrip(
    int TotalRequestsInWindow,
    int RulesVisible,
    int RulesTriggeredInWindow,
    int RulesWithZeroHits,
    TimeSpan Window);

/// <summary>Stable label for a <see cref="TimeSpan"/> window used by the strip.</summary>
public static class PolicyStackWindowFormat
{
    /// <summary>Returns <c>24h</c>, <c>7d</c>, <c>30d</c>, or a numeric fallback.</summary>
    public static string ForLabel(TimeSpan window)
    {
        if (window <= TimeSpan.FromHours(24)) return "24h";
        if (window <= TimeSpan.FromDays(7)) return "7d";
        if (window <= TimeSpan.FromDays(30)) return "30d";
        // Defensive fallback: round-tripped days. Operators rarely pick custom
        // windows beyond 30d, but ToCanonicalString must not crash on them.
        return $"{(int)window.TotalDays}d".ToString(CultureInfo.InvariantCulture);
    }
}
