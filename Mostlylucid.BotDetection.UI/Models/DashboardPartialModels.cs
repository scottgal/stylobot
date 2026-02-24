using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for the visitor list partial.
///     Server-rendered, no client-side state management.
/// </summary>
public sealed class VisitorListModel
{
    public required IReadOnlyList<CachedVisitor> Visitors { get; init; }
    public required FilterCounts Counts { get; init; }
    public required string Filter { get; init; }
    public required string SortField { get; init; }
    public required string SortDir { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }
    public required string BasePath { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>
///     View model for the summary stats partial.
/// </summary>
public sealed class SummaryStatsModel
{
    public required DashboardSummary Summary { get; init; }
    public required string BasePath { get; init; }
}

/// <summary>
///     View model for the "Your Detection" partial.
/// </summary>
public sealed class YourDetectionModel
{
    public bool IsBot { get; init; }
    public double BotProbability { get; init; }
    public double Confidence { get; init; }
    public string? RiskBand { get; init; }
    public double ProcessingTimeMs { get; init; }
    public int DetectorCount { get; init; }
    public string? Narrative { get; init; }
    public List<string> TopReasons { get; init; } = [];
    public string? Signature { get; init; }
    public double? ThreatScore { get; init; }
    public string? ThreatBand { get; init; }
    public bool HasData { get; init; }
    public required string BasePath { get; init; }
}

/// <summary>
///     View model for the countries list partial.
/// </summary>
public sealed class CountriesListModel
{
    public required IReadOnlyList<DashboardCountryStats> Countries { get; init; }
    public required string BasePath { get; init; }
    public string SortField { get; init; } = "total";
    public string SortDir { get; init; } = "desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public int TotalCount { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>
///     View model for the clusters list partial.
/// </summary>
public sealed class ClustersListModel
{
    public required IReadOnlyList<ClusterViewModel> Clusters { get; init; }
    public required string BasePath { get; init; }
    public string SortField { get; init; } = "members";
    public string SortDir { get; init; } = "desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 12;
    public int TotalCount { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>
///     View model for a single cluster card.
/// </summary>
public sealed class ClusterViewModel
{
    public required string ClusterId { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public required string Type { get; init; }
    public int MemberCount { get; init; }
    public double AvgBotProb { get; init; }
    public string? Country { get; init; }
    public double AverageSimilarity { get; init; }
    public double TemporalDensity { get; init; }
    public string? DominantIntent { get; init; }
    public double AverageThreatScore { get; init; }
}

/// <summary>
///     View model for the user agents list partial.
/// </summary>
public sealed class UserAgentsListModel
{
    public required IReadOnlyList<DashboardUserAgentSummary> UserAgents { get; init; }
    public required string BasePath { get; init; }
    public string Filter { get; init; } = "all";
    public string SortField { get; init; } = "requests";
    public string SortDir { get; init; } = "desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public int TotalCount { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>
///     View model for the top bots list partial.
/// </summary>
public sealed class TopBotsListModel
{
    public required IReadOnlyList<DashboardTopBotEntry> Bots { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }
    public required string SortField { get; init; }
    public required string BasePath { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>
///     Shell view model for the full dashboard page.
///     Composes all partial models for initial server-side render.
/// </summary>
public sealed class DashboardShellModel
{
    public required string CspNonce { get; init; }
    public required string BasePath { get; init; }
    public required string HubPath { get; init; }
    public required string ActiveTab { get; init; }

    public string? Version { get; init; }

    // Partial models for initial render
    public required SummaryStatsModel Summary { get; init; }
    public required VisitorListModel Visitors { get; init; }
    public required YourDetectionModel YourDetection { get; init; }
    public required CountriesListModel Countries { get; init; }
    public required ClustersListModel Clusters { get; init; }
    public required UserAgentsListModel UserAgents { get; init; }
    public required TopBotsListModel TopBots { get; init; }
}
