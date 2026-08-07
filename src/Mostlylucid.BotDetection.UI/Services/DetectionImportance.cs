using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Write-time importance score for a detection row — the compression fold's
///     ordering key (temporal-store design: resolution lost by importance, not by
///     clock buckets).
///     <para>
///     Computed ONCE when the row lands (<c>SqliteDashboardEventStore.AddDetectionAsync</c>)
///     and stored on the row; the fold reads it back, never recomputes it. The
///     blend weights are operator knobs (<see cref="TemporalStoreOptions"/>);
///     the per-action bonus map below is the fixed enforcement ranking because
///     action policy names are free-form (operator-configured), not a closed enum.
///     </para>
/// </summary>
public static class DetectionImportance
{
    /// <summary>
    ///     Importance in [0, 1]; higher = keep per-request detail longer.
    ///     <c>botProbability * BotScoreWeight + threatRatio * ThreatScoreWeight +
    ///     actionBonus * ActionWeight</c>, clamped to [0, 1]. A plain low-score
    ///     request lands near 0 and folds first; a blocked high-threat bot lands
    ///     near 1 and keeps its detail until full absorption.
    /// </summary>
    public static double ComputeWeight(
        double botProbability,
        double? threatScore,
        string? action,
        TemporalStoreOptions options)
    {
        var botTerm = options.BotScoreWeight * Math.Clamp(botProbability, 0.0, 1.0);
        var threatRatio = Math.Clamp(
            (threatScore ?? 0.0) / Math.Max(options.ThreatScoreNormalizer, double.Epsilon),
            0.0, 1.0);
        var threatTerm = options.ThreatScoreWeight * threatRatio;
        var actionTerm = options.ActionWeight * ActionBonus(action);
        return Math.Clamp(botTerm + threatTerm + actionTerm, 0.0, 1.0);
    }

    /// <summary>
    ///     Enforcement ranking for the row's action policy name. Action names are
    ///     free-form ("throttle-stealth", "block-hard", "simulation-pack", ...), so
    ///     this ranks by keyword — the first (strongest) match wins. Enforcement
    ///     actions are the audit trail; benign/observe rows get 0 and compress
    ///     first.
    /// </summary>
    private static double ActionBonus(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return 0.0;
        var a = action.ToLowerInvariant();
        if (a.Contains("block", StringComparison.Ordinal)) return 1.0;
        if (a.Contains("challenge", StringComparison.Ordinal)) return 0.8;
        // Honeypot engagements are enforcement + evidence (positive detections).
        if (a.Contains("honeypot", StringComparison.Ordinal) ||
            a.Contains("simulation", StringComparison.Ordinal)) return 0.7;
        if (a.Contains("throttle", StringComparison.Ordinal)) return 0.6;
        if (a.Contains("rate", StringComparison.Ordinal)) return 0.4;
        return 0.0;
    }
}
