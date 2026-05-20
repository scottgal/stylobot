namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Endpoint-level statistics aggregated by method + path.
/// </summary>
public sealed record DashboardEndpointStats
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public int TotalCount { get; init; }
    public int BotCount { get; init; }
    public int HumanCount => TotalCount - BotCount;
    public double BotRate { get; init; }
    public int UniqueSignatures { get; init; }
    public double AvgProcessingTimeMs { get; init; }
    public double MinProcessingTimeMs { get; init; }
    public double MaxProcessingTimeMs { get; init; }
    public double P95ProcessingTimeMs { get; init; }
    public double AvgThreatScore { get; init; }
    public string? TopAction { get; init; }
    public string? DominantRiskBand { get; init; }
    public DateTime LastSeen { get; init; }

    /// <summary>
    ///     The detection policy that resolves for this endpoint's path.
    ///     Populated from IPolicyRegistry.GetPolicyForPath() when available.
    /// </summary>
    public string? ActivePolicyName { get; init; }

    public bool IsPinned { get; init; }
    public bool IsHoneypot { get; init; }
    public long? PinId { get; init; }
}

/// <summary>
///     Behavioral profile for a group of requests (bots, humans, or all) on a single endpoint.
///     All values are percentages (0-100) ready for radar chart rendering.
/// </summary>
public sealed record EndpointBehavioralProfile
{
    public double ThreatScore { get; init; }      // avg threat_score normalized to 0-100
    public double Probability { get; init; }       // avg bot_probability * 100
    public double Confidence { get; init; }        // avg confidence * 100
    public double BlockRate { get; init; }         // fraction blocked * 100
    public double ErrorRate { get; init; }         // fraction 4xx/5xx * 100
    public double LatencyScore { get; init; }      // latency relative to endpoint max, 0-100
    public int SampleCount { get; init; }
}

/// <summary>
///     Detailed drill-down for a single endpoint.
/// </summary>
public sealed record DashboardEndpointDetail
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public int TotalCount { get; init; }
    public int BotCount { get; init; }
    public int HumanCount => TotalCount - BotCount;
    public double BotRate { get; init; }
    public int UniqueSignatures { get; init; }
    public double AvgProcessingTimeMs { get; init; }
    public double MinProcessingTimeMs { get; init; }
    public double MaxProcessingTimeMs { get; init; }
    public double P95ProcessingTimeMs { get; init; }
    public double AvgThreatScore { get; init; }
    public required Dictionary<string, int> TopActions { get; init; }
    public required Dictionary<string, int> TopCountries { get; init; }
    public required Dictionary<string, int> RiskBands { get; init; }
    public required List<DashboardTopBotEntry> TopBots { get; init; }
    public required List<SignatureDetectionRow> RecentDetections { get; init; }
    public EndpointBehavioralProfile? BotProfile { get; init; }
    public EndpointBehavioralProfile? HumanProfile { get; init; }
    public EndpointBehavioralProfile? OverallProfile { get; init; }
}

public sealed record EndpointPackCoverage(
    string PackName,
    string Scope,
    int CurrentLevel,
    string? CurrentPolicy);
