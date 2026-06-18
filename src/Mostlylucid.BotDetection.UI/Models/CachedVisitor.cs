using Mostlylucid.BotDetection.Grouping;

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