using Mostlylucid.BotDetection.Grouping;
using Mostlylucid.BotDetection.UI.Models.Primitives;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Projection of a single dashboard visitor row, returned by
///     <see cref="Services.SignatureAggregateCache.GetFiltered"/> and consumed by
///     the <c>_VisitorCard.cshtml</c> partial and the visitor-list view component.
///     Mutable so the cache projection can lazily refine fields without
///     allocating; nothing outside the cache should hold a reference long enough
///     for that to matter.
/// </summary>
public class CachedVisitor
{
    public required string PrimarySignature { get; set; }
    public int Hits { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsBot { get; set; }
    public double BotProbability { get; set; }
    public double Confidence { get; set; }
    public string RiskBand { get; set; } = "Medium";
    public string? LastPath { get; set; }
    public List<string> Paths { get; set; } = new();
    public string Action { get; set; } = "Allow";
    public string? BotName { get; set; }
    public string? BotType { get; set; }
    public string? CountryCode { get; set; }
    public string? UserAgent { get; set; }
    public string? Narrative { get; set; }
    public string? Description { get; set; }
    public List<string> TopReasons { get; set; } = new();
    public double ProcessingTimeMs { get; set; }
    public double MaxProcessingTimeMs { get; set; }
    public double MinProcessingTimeMs { get; set; }

    public Queue<double> ProcessingTimeHistory { get; set; } = new();
    public Queue<double> BotProbabilityHistory { get; set; } = new();
    public Queue<double> ConfidenceHistory { get; set; } = new();

    public string? LastRequestId { get; set; }
    public double? ThreatScore { get; set; }
    public string? ThreatBand { get; set; }
    public string? Protocol { get; set; }

    public string? IpSubnetSignature { get; set; }
    public string? UaFamily { get; set; }
    public string? FingerprintId { get; set; }
    public string? ClusterId { get; set; }
    public float[]? RadarShape { get; set; }

    public GroupKey? GroupKey { get; set; }
    public int GroupMemberCount { get; set; } = 1;

    /// <summary>
    ///     Gateway-projected drift badge for the visitor row (plan task 19).
    ///     Null when the visitor has no bound fingerprint, the fingerprint has
    ///     no root centroid yet (cold start), the drift magnitude is below
    ///     <c>IdentityOptions.Drift.DriftBadgeThreshold</c>, or the dashboard
    ///     is running in a remote-mode host without IFingerprintReader. The
    ///     view treats null as "no badge"; a non-null with
    ///     <see cref="DriftBadgeModel.IsDrifted"/> == false also renders
    ///     nothing — the partial fast-skips on that flag. Per spec D5 the
    ///     threshold cross + label resolution happen on the gateway; this
    ///     field carries the projected payload only.
    /// </summary>
    public DriftBadgeModel? DriftBadge { get; set; }

    /// <summary>
    ///     Previous display name from <c>fingerprint_name_history</c>,
    ///     populated when the current BotName changed within the recent-window
    ///     (default 7d). The visitor card renders a small "was: X" pill next
    ///     to <see cref="BotName"/> so an operator can see at a glance that
    ///     the fingerprint has drifted into a new presentation. Null when no
    ///     prior history exists or the change is older than the window.
    ///     Spec docs/superpowers/specs/2026-06-26-fingerprint-name-projection-restore.md.
    /// </summary>
    public string? PriorBotName { get; set; }

    /// <summary>
    ///     UTC timestamp when <see cref="BotName"/> was last updated.
    ///     Drives the recent-window gate for <see cref="PriorBotName"/>;
    ///     null when no name has been written yet.
    /// </summary>
    public DateTime? BotNameUpdatedAt { get; set; }

    public string TimeAgo
    {
        get
        {
            var span = DateTime.UtcNow - LastSeen;
            if (span.TotalSeconds < 5) return "now";
            if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{(int)span.TotalDays}d";
        }
    }
}

/// <summary>Filter button badge counts on the Visitors tab.</summary>
public class FilterCounts
{
    public int All { get; set; }
    public int Humans { get; set; }
    public int Bots { get; set; }
    public int Ai { get; set; }
    public int Search { get; set; }
    public int Tools { get; set; }
}