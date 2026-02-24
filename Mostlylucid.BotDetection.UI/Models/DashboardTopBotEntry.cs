namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Top bot entry from the event store, used by the /api/topbots endpoint.
/// </summary>
public sealed record DashboardTopBotEntry
{
    public required string PrimarySignature { get; init; }
    public int HitCount { get; init; }
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public string? RiskBand { get; init; }
    public double BotProbability { get; init; }
    public double Confidence { get; init; }
    public string? Action { get; init; }
    public string? CountryCode { get; init; }
    public double ProcessingTimeMs { get; init; }
    public List<string>? TopReasons { get; init; }
    public DateTime LastSeen { get; init; }
    public string? Narrative { get; init; }
    public string? Description { get; init; }
    public bool IsKnownBot { get; init; }
    public double? ThreatScore { get; init; }
    public string? ThreatBand { get; init; }

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
