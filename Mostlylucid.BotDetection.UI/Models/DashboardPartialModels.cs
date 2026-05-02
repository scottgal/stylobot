using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for the shared pagination partial.
///     Renders an ellipsis-aware page button strip via HTMX.
/// </summary>
public sealed record PaginationModel
{
    public required Func<int, string> PageUrl { get; init; }
    public required int Page { get; init; }
    public required int TotalPages { get; init; }
    public required string TargetId { get; init; }
    public string CssClass { get; init; } = "";
}

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
    public string NavBasePath { get; init; } = "";
    public string ResolvedNavBasePath => string.IsNullOrEmpty(NavBasePath) ? BasePath : NavBasePath;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>
///     View model for the summary stats partial.
/// </summary>
public sealed class SummaryStatsModel
{
    public required DashboardSummary Summary { get; init; }
    public required string BasePath { get; init; }

    // Session-first analytics (sessions = unique fingerprints, not hits)
    public int ActiveSessions { get; set; }
    public int UniqueVisitors { get; set; }
    public int BotSessions { get; set; }
    public int HumanSessions { get; set; }
    public double BounceRate { get; set; }
    public double HumanBounceRate { get; set; }
    public double BotBounceRate { get; set; }
    public double AvgSessionDurationSecs { get; set; }
    public double HumanAvgSessionDurationSecs { get; set; }
    public double BotAvgSessionDurationSecs { get; set; }
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
    public ClusterDiagnosticsViewModel? Diagnostics { get; init; }
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
///     View model for cluster engine diagnostics.
/// </summary>
public sealed class ClusterDiagnosticsViewModel
{
    public string? Algorithm { get; init; }
    public string? Status { get; init; }
    public DateTime? LastRunAt { get; init; }
    public int InputBehaviorCount { get; init; }
    public int EdgeCount { get; init; }
    public double GraphDensity { get; init; }
    public int RawCommunityCount { get; init; }
    public int ClusterCount { get; init; }
    public int HumanClusterCount { get; init; }
    public int MachineClusterCount { get; init; }
    public int MixedClusterCount { get; init; }
    public double SimilarityThreshold { get; init; }
    public int MinClusterSize { get; init; }
    public IReadOnlyList<KeyValuePair<string, double>> TopWeights { get; init; } = [];
}

/// <summary>
///     View model for the endpoints list partial.
/// </summary>
public sealed class EndpointsListModel
{
    public required IReadOnlyList<DashboardEndpointStats> Endpoints { get; init; }
    public required string BasePath { get; init; }
    public string SortField { get; init; } = "total";
    public string SortDir { get; init; } = "desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public int TotalCount { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>
///     View model for the endpoint detail panel.
/// </summary>
public sealed class EndpointDetailModel
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string BasePath { get; init; }
    public bool Found { get; init; }
    public int TotalCount { get; init; }
    public int BotCount { get; init; }
    public int HumanCount { get; init; }
    public double BotRate { get; init; }
    public int UniqueSignatures { get; init; }
    public double AvgProcessingTimeMs { get; init; }
    public double AvgThreatScore { get; init; }
    public Dictionary<string, int> TopActions { get; init; } = new();
    public Dictionary<string, int> TopCountries { get; init; } = new();
    public Dictionary<string, int> RiskBands { get; init; } = new();
    public List<DashboardTopBotEntry> TopBots { get; init; } = [];
    public List<SignatureDetectionRow> RecentDetections { get; init; } = [];
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
    public string SortDir { get; init; } = "desc";
    public required string BasePath { get; init; }
    /// <summary>Base path for user-facing navigation links (investigate, signature detail). Defaults to BasePath.</summary>
    public string NavBasePath { get; init; } = "";
    public string ResolvedNavBasePath => string.IsNullOrEmpty(NavBasePath) ? BasePath : NavBasePath;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
    /// <summary>Entry filter: "bots" (default), "humans", or "all".</summary>
    public string Filter { get; init; } = "bots";
    /// <summary>HTML id prefix; allows multiple widget instances on the same page.</summary>
    public string WidgetId { get; init; } = "topbots";
}

/// <summary>
///     View model for the signature detail page.
/// </summary>
public sealed class SignatureDetailModel
{
    public required string SignatureId { get; init; }
    public required string BasePath { get; init; }
    public string NavBasePath { get; init; } = "";
    public string ResolvedNavBasePath => string.IsNullOrEmpty(NavBasePath) ? BasePath : NavBasePath;
    public required string CspNonce { get; init; }
    public required string HubPath { get; init; }
    public bool Found { get; init; }

    // From SignatureAggregate
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public string? RiskBand { get; init; }
    public double BotProbability { get; init; }
    public double Confidence { get; init; }
    public int HitCount { get; init; }
    public string? Action { get; init; }
    public string? CountryCode { get; init; }
    public double ProcessingTimeMs { get; init; }
    public List<string>? TopReasons { get; init; }
    public DateTime LastSeen { get; init; }
    public string? Narrative { get; init; }
    public string? Description { get; init; }
    public bool IsBot { get; init; }
    public double? ThreatScore { get; init; }
    public string? ThreatBand { get; init; }
    public string? RiskJustification { get; init; }
    public List<double>? SparklineData { get; init; }

    // From CachedVisitor
    public List<string> Paths { get; init; } = [];
    public string? UserAgent { get; init; }
    public string? Protocol { get; init; }
    public DateTime FirstSeen { get; init; }
    public List<double> BotProbabilityHistory { get; init; } = [];
    public List<double> ConfidenceHistory { get; init; } = [];
    public List<double> ProcessingTimeHistory { get; init; } = [];

    // From DB detections (recent per-request records)
    public List<SignatureDetectionRow> RecentDetections { get; init; } = [];

    // Latest detection's detector contributions
    public List<SignatureDetectorEntry> DetectorContributions { get; init; } = [];

    // Signal intelligence (grouped by prefix)
    public Dictionary<string, Dictionary<string, string>> SignalCategories { get; init; } = new();

    /// <summary>
    ///     When true, the Tuner action surface is shown in the detector contributions table.
    ///     Set from <see cref="Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions.EnableTuner"/>
    ///     by the dashboard middleware. Only paid tiers with the <c>stylobot.tuner</c> feature enable this.
    /// </summary>
    public bool TunerEnabled { get; init; }
}

/// <summary>
///     A single detection record for the signature detail page.
/// </summary>
public sealed record SignatureDetectionRow
{
    public required DateTime Timestamp { get; init; }
    public required bool IsBot { get; init; }
    public required double BotProbability { get; init; }
    public required double Confidence { get; init; }
    public required string RiskBand { get; init; }
    public int StatusCode { get; init; }
    public string? Path { get; init; }
    public string? Method { get; init; }
    public double ProcessingTimeMs { get; init; }
    public string? Action { get; init; }
}

/// <summary>
///     A single detector's contribution for the signature detail page.
/// </summary>
public sealed record SignatureDetectorEntry
{
    public required string Name { get; init; }
    public required double ConfidenceDelta { get; init; }
    public required double Contribution { get; init; }
    public string? Reason { get; init; }
    public double ExecutionTimeMs { get; init; }
}

/// <summary>
///     View model for the user agent detail panel.
/// </summary>
public sealed record UserAgentDetailModel
{
    public required string Family { get; init; }
    public required string Category { get; init; }
    public required int TotalCount { get; init; }
    public required int BotCount { get; init; }
    public required int HumanCount { get; init; }
    public required double BotRate { get; init; }
    public required double AvgConfidence { get; init; }
    public required double AvgProcessingTimeMs { get; init; }
    public required Dictionary<string, int> Versions { get; init; }
    public required Dictionary<string, int> Countries { get; init; }
    public string CspNonce { get; init; } = "";
    public string BasePath { get; init; } = "/_stylobot";
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
    public required EndpointsListModel Endpoints { get; init; }
    public required ClustersListModel Clusters { get; init; }
    public required UserAgentsListModel UserAgents { get; init; }
    public required TopBotsListModel TopBots { get; init; }
    public required SessionsListModel Sessions { get; init; }
    public required ThreatsListModel Threats { get; init; }

    /// <summary>License entitlement card model. Always present (renders the muted OSS line in the unconfigured case).</summary>
    public required LicenseCardModel License { get; init; }

    /// <summary>
    ///     Configuration editor model. Optional -- only set when the active tab is "configuration"
    ///     so we don't pay the embedded-manifest enumeration cost on every dashboard render.
    /// </summary>
    public ConfigurationEditorModel? Configuration { get; init; }

    /// <summary>
    ///     Investigation view model. Optional -- only set when the active tab is "investigate"
    ///     to avoid running investigation queries on every dashboard render.
    /// </summary>
    public ShapeInvestigationViewModel? Investigation { get; init; }

    /// <summary>Whether commercial features (config editor, IP search, fingerprints) are enabled for this request.</summary>
    public bool IsCommercial { get; init; }

    /// <summary>System status strip model (overview tab header).</summary>
    public StatusStripModel? StatusStrip { get; init; }

    /// <summary>Compliance tab model. Only set when active tab is "compliance".</summary>
    public ComplianceTabModel? Compliance { get; init; }
}

/// <summary>
///     View model for the FOSS Monaco YAML config editor on the Configuration tab.
///     The detector list is server-rendered for the no-JS fallback; Monaco itself is
///     lazy-loaded from a CDN only after the tab opens.
/// </summary>
public sealed class ConfigurationEditorModel
{
    public required string BasePath { get; init; }

    /// <summary>All editable detector manifests (slug + display metadata + override flag).</summary>
    public required IReadOnlyList<Mostlylucid.BotDetection.Orchestration.Manifests.DetectorManifestSummary> Detectors { get; init; }

    /// <summary>
    ///     True when the active license is paid (Active or Trial). Hides the per-target
    ///     upsell rail. Read off the LicenseCardModel that's already on the shell.
    /// </summary>
    public required bool IsCommercialLicensed { get; init; }

    /// <summary>
    ///     When true, config editing (save/delete) is disabled in the UI.
    ///     Requires EnableConfigEditing + WriteAuthorizationFilter/Policy to be false.
    ///     Default: true (read-only).
    /// </summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>
    ///     Control plane URL for loading the commercial config editor via HTMX.
    ///     When set, the Configuration tab loads the editor from this URL.
    ///     When null, falls back to the FOSS Monaco YAML editor.
    /// </summary>
    public string? ControlPlaneUrl { get; init; }
}

/// <summary>
///     View model for the sessions list partial.
///     Sessions are the primary activity unit - each represents a compressed
///     behavioral snapshot with Markov chain transitions.
/// </summary>
public sealed class SessionsListModel
{
    public required IReadOnlyList<SessionListEntry> Sessions { get; init; }
    public required string BasePath { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public int TotalCount { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
    public string? Filter { get; init; }
}

/// <summary>
///     A single session entry for the sessions list.
/// </summary>
public sealed record SessionListEntry
{
    public long Id { get; init; }
    public required string Signature { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime EndedAt { get; init; }
    public double DurationMinutes => Math.Round((EndedAt - StartedAt).TotalMinutes, 1);
    public required int RequestCount { get; init; }
    public required string DominantState { get; init; }
    public required bool IsBot { get; init; }
    public required double AvgBotProbability { get; init; }
    public required string RiskBand { get; init; }
    public string? Action { get; init; }
    public string? BotName { get; init; }
    public string? CountryCode { get; init; }
    public int ErrorCount { get; init; }
    public float TimingEntropy { get; init; }
    public float Maturity { get; init; }

    /// <summary>Markov transitions as "State->State": count</summary>
    public Dictionary<string, int>? TransitionCounts { get; init; }

    /// <summary>Top 3 transitions by count for compact display</summary>
    public IEnumerable<KeyValuePair<string, int>> TopTransitions =>
        TransitionCounts?.OrderByDescending(kv => kv.Value).Take(3)
        ?? Enumerable.Empty<KeyValuePair<string, int>>();
}

/// <summary>
///     View model for the session detail panel (loaded via HTMX).
///     Shows behavioral radar chart, Markov chain transitions, paths, timing.
/// </summary>
public sealed class SessionDetailModel
{
    public long Id { get; init; }
    public required string Signature { get; init; }
    public required string BasePath { get; init; }
    public required string CspNonce { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime EndedAt { get; init; }
    public double DurationMinutes => Math.Round((EndedAt - StartedAt).TotalMinutes, 1);
    public required int RequestCount { get; init; }
    public required string DominantState { get; init; }
    public required bool IsBot { get; init; }
    public required double AvgBotProbability { get; init; }
    public required string RiskBand { get; init; }
    public int ErrorCount { get; init; }
    public float TimingEntropy { get; init; }
    public float Maturity { get; init; }

    /// <summary>Markov transition counts: "State->State" => count</summary>
    public Dictionary<string, int>? TransitionCounts { get; init; }

    /// <summary>Templatized paths visited</summary>
    public List<string>? Paths { get; init; }
}
/// <summary>
///     View model for the fingerprint approval form.
/// </summary>
public sealed class ApprovalFormModel
{
    public required string BasePath { get; init; }
    public required string CspNonce { get; init; }

    /// <summary>Current signal values that can be locked as approval dimensions.</summary>
    public Dictionary<string, string>? CurrentSignals { get; init; }
}

/// <summary>
///     View model for the threats list partial.
///     Shows CVE probe activity, active honeypot sessions, and threat intelligence.
/// </summary>
public sealed class ThreatsListModel
{
    public IReadOnlyList<ThreatEntry> Threats { get; init; } = [];
    public int TotalCount { get; init; }
    public int ActiveHoneypotSessions { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public required string BasePath { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

/// <summary>
///     Search result for the UA search API.
/// </summary>
public sealed class UserAgentSearchResult
{
    public required string UserAgent { get; init; }
    public required string Signature { get; init; }
    public double BotProbability { get; init; }
    public DateTime Timestamp { get; init; }
    public string? BotName { get; init; }
}

/// <summary>
///     A single threat entry for the threats list.
/// </summary>
public sealed class ThreatEntry
{
    public required string Signature { get; init; }
    public required string Path { get; init; }
    public string? CveId { get; init; }
    public string? CveSeverity { get; init; }
    public string? PackId { get; init; }
    public double ThreatScore { get; init; }
    public string? ThreatBand { get; init; }
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public double BotProbability { get; init; }
    public string? CountryCode { get; init; }
    public DateTime Timestamp { get; init; }
    public bool InHoneypot { get; init; }
}

/// <summary>View model for the unified investigation view.</summary>
public sealed class InvestigationViewModel
{
    public required InvestigationFilter Filter { get; init; }
    public required InvestigationResult Result { get; init; }
    public required string BasePath { get; init; }

    /// <summary>Available entity types for the filter dropdown (permission-gated).</summary>
    public IReadOnlyList<FilterOption> AvailableFilters { get; init; } = [];

    /// <summary>Available tabs (permission-gated).</summary>
    public IReadOnlyList<string> AvailableTabs { get; init; } = [];

    public string ActiveTab => Filter.Tab ?? "detections";
    public int TotalPages => Result.TotalCount > 0 ? (int)Math.Ceiling(Result.TotalCount / (double)Filter.Limit) : 0;
    public int CurrentPage => (Filter.Offset / Filter.Limit) + 1;
}

public sealed record FilterOption
{
    public required string Value { get; init; }   // "signature", "country", etc.
    public required string Label { get; init; }   // "Signature", "Country", etc.
}
