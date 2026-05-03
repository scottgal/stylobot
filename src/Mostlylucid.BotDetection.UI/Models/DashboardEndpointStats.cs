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
    public double AvgThreatScore { get; init; }
    public string? TopAction { get; init; }
    public string? DominantRiskBand { get; init; }
    public DateTime LastSeen { get; init; }

    /// <summary>
    ///     The detection policy that resolves for this endpoint's path.
    ///     Populated from IPolicyRegistry.GetPolicyForPath() when available.
    /// </summary>
    public string? ActivePolicyName { get; init; }
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
    public double AvgThreatScore { get; init; }
    public required Dictionary<string, int> TopActions { get; init; }
    public required Dictionary<string, int> TopCountries { get; init; }
    public required Dictionary<string, int> RiskBands { get; init; }
    public required List<DashboardTopBotEntry> TopBots { get; init; }
    public required List<SignatureDetectionRow> RecentDetections { get; init; }
}
