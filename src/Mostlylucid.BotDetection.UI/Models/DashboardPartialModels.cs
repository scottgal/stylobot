using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Models;

// PaginationModel + _Pagination.cshtml removed -- every list view now uses the
// single canonical primitive TablePaginationModel + _Primitives/_TablePagination.cshtml.
// Two pager shapes for one job was the bug behind the "12>" squish on the
// endpoints list and the dropdown-rows-per-page divergence between widgets.

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

    // Identity signals that say "this is you, here's what we see" -- replaces the
    // earlier text-heavy "Your Detection" panel.
    public string? CountryCode { get; init; }
    public string? UaFamily { get; init; }
    public string? UserAgent { get; init; }
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    /// <summary>12-axis ClockProjection vector, [0,1]-clamped, ready for direct radar rendering.</summary>
    public double[]? ClockAxes { get; init; }
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
public sealed record EndpointsListModel
{
    public required IReadOnlyList<DashboardEndpointStats> Endpoints { get; init; }
    public required string BasePath { get; init; }
    public string SortField { get; init; } = "total";
    public string SortDir { get; init; } = "desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public int TotalCount { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));

    /// <summary>
    ///     Status-code bucket filter. Valid values are <c>all</c> (no filter),
    ///     <c>2xx</c>, <c>3xx</c>, <c>4xx</c>, <c>5xx</c>. The server-side
    ///     handler filters endpoint rows whose dominant response bucket
    ///     matches; <c>all</c> renders the unfiltered set.
    /// </summary>
    public string StatusFilter { get; init; } = "all";

    /// <summary>
    ///     Free-text substring match against the endpoint <c>Path</c>. Empty
    ///     string renders the unfiltered set. Server-side handler treats it
    ///     as case-insensitive contains.
    /// </summary>
    public string PathFilter { get; init; } = "";

    /// <summary>
    ///     Audience filter. Forwarded to <c>IDashboardEventStore.GetEndpointStatsAsync</c>'s
    ///     <c>audienceFilter</c> parameter. Recognised values are <c>all</c>
    ///     (default, no filter), <c>humans</c> (rows where <c>is_bot=0</c> dominated),
    ///     <c>bots</c>, and <c>honeypot</c> (rows whose path classifies via
    ///     <c>HoneypotPathDefinitions.Classify</c> as Always / Probable). The
    ///     chip strip on the endpoints view drives this via the URL.
    /// </summary>
    public string AudienceFilter { get; init; } = "all";

    /// <summary>When true the partial renders a narrow column set that fits inside a
    /// half-width container without horizontal scroll: Method, Path, Req, Bot %. The
    /// wider columns (Sigs, Policy) are dropped entirely rather than gated by Tailwind
    /// breakpoints, which fail when the parent column is narrower than the breakpoint.</summary>
    public bool IsCompact { get; init; }

    /// <summary>True when the request resolves to commercial mode. Gates the
    /// "Pin Endpoint" / honeypot management UI -- pinning is a paid feature
    /// and must never render or accept input in FOSS mode.</summary>
    public bool IsCommercial { get; init; }

    /// <summary>Operator opted in to the "Pin Endpoint" admin UI via
    /// <see cref="Configuration.StyloBotDashboardOptions.EnableEndpointPinning"/>.
    /// Default false; only true when the dashboard host explicitly gates access
    /// behind admin auth. The button + form render only when this AND
    /// <see cref="IsCommercial"/> are true -- so the public marketing demo
    /// (commercial-tier chrome, anonymous visitors) never sees a write button.</summary>
    public bool AllowEndpointPinning { get; init; }
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
    public double MinProcessingTimeMs { get; init; }
    public double MaxProcessingTimeMs { get; init; }
    public double P95ProcessingTimeMs { get; init; }
    public double AvgThreatScore { get; init; }
    public Dictionary<string, int> TopActions { get; init; } = new();
    public Dictionary<string, int> TopCountries { get; init; } = new();
    public Dictionary<string, int> RiskBands { get; init; } = new();
    public List<DashboardTopBotEntry> TopBots { get; init; } = [];
    public List<SignatureDetectionRow> RecentDetections { get; init; } = [];
    public EndpointBehavioralProfile? BotProfile { get; init; }
    public EndpointBehavioralProfile? HumanProfile { get; init; }
    public EndpointBehavioralProfile? OverallProfile { get; init; }
    public string CspNonce { get; init; } = "";
    public string? PolicyName { get; init; }
    public IReadOnlyList<EndpointPackCoverage> PackCoverage { get; init; } = [];
    public bool IsPinned { get; init; }
    public bool IsHoneypot { get; init; }
    public long? PinId { get; init; }

    /// <summary>True when the request resolves to commercial mode. Gates the
    /// Unpin button -- FOSS may display the pinned/honeypot badge (seeded data
    /// shows the feature exists) but the unpin action itself is paid.</summary>
    public bool IsCommercial { get; init; }

    /// <summary>
    ///     True when the view is being rendered as a standalone page (host's
    ///     _Layout wraps it) rather than a hx-target swap fragment. The page
    ///     surface renders a breadcrumb / back-to-dashboard nav above the
    ///     detail body; the partial surface skips that chrome.
    /// </summary>
    public bool IsStandalonePage { get; init; }

    /// <summary>
    ///     Base path for user-facing navigation links (back to dashboard, all
    ///     endpoints). Defaults to <see cref="BasePath"/>; commercial hosts can
    ///     point it at the marketing-site shell so the in-page "back to
    ///     dashboard" link traverses the host's tab system.
    /// </summary>
    public string NavBasePath { get; init; } = "";
    public string ResolvedNavBasePath => string.IsNullOrEmpty(NavBasePath) ? BasePath : NavBasePath;

    /// <summary>Hub path for SignalR (forwarded to client-side widgets that need it).</summary>
    public string HubPath { get; init; } = "/dashboard/hub";
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
    /// <summary>Per-filter counts so the toolbar chips can render real numbers.</summary>
    public TopBotsCounts Counts { get; init; } = new(0, 0, 0);
    /// <summary>Active search query (substring on bot name / type / signature). Null = no filter.</summary>
    public string? Query { get; init; }
}

/// <summary>Counts per filter bucket for the SbTopBots toolbar chips.</summary>
public sealed record TopBotsCounts(int All, int Bots, int Humans);

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

    /// <summary>
    ///     Per-endpoint stats restricted to this signature (HitCount, Avg/P95/Max
    ///     latency, BotRate, threat, status mix, dominant action / risk). Powers
    ///     the Endpoints Visited table on the detail page. Empty list when the
    ///     store query failed, the remote adapter cannot supply the data, or this
    ///     signature has no persisted detections yet; the view falls back to
    ///     <see cref="Paths"/> in that case.
    /// </summary>
    public List<SignatureEndpointStats> EndpointStats { get; init; } = [];
    public string? UserAgent { get; init; }
    public string? Protocol { get; init; }
    public DateTime FirstSeen { get; init; }
    public List<double> BotProbabilityHistory { get; init; } = [];
    public List<double> ConfidenceHistory { get; init; } = [];
    public List<double> ProcessingTimeHistory { get; init; } = [];

    // ── Ground-truth UA surface ────────────────────────────────────────────
    // Carry the parsed UA family / version / OS alongside the (mutable, LLM-
    // renameable) BotName so the title can show "Name · Chrome 119 / macOS"
    // and the operator always has the raw claim next to the inference. The
    // verification triple (state + method + reason) drives the trust badge:
    //  - verified  : VerifiedBotConfirmed=true  (claim matched rDNS / IP / nodeinfo)
    //  - spoofed   : VerifiedBotSpoofed=true    (claim made, verification failed)
    //  - unverified: claim made, no verifier ran or result inconclusive
    //  - none      : no bot claim in the UA (human browser)
    public string? UaFamily { get; init; }
    public string? UaVersion { get; init; }
    public string? UaOs { get; init; }
    public string UaTrustState { get; init; } = "none";
    public string? UaTrustMethod { get; init; }

    // From DB detections (recent per-request records)
    public List<SignatureDetectionRow> RecentDetections { get; init; } = [];

    // 7-bucket fingerprint centroid projection used by the radar partial on
    // both the home card and the signature-detail page. Null when the matcher
    // hasn't produced a fingerprint for this signature yet (the partial then
    // renders its calibrating placeholder). Projected once at model
    // construction; same FingerprintRadarProjection the home card uses, so
    // the polygons read identically on both surfaces.
    public FingerprintRadarShape? FingerprintShape { get; init; }

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

    /// <summary>
    ///     7×24 day/hour hit-count grid for this signature, aggregated from the
    ///     requests table over a trailing window (default 30 days). Null when the
    ///     query failed or returned no data; the view renders "no temporal data
    ///     yet" in that case.
    /// </summary>
    public Mostlylucid.BotDetection.UI.Services.HeatmapResult? PeriodicityHeatmap { get; init; }

    /// <summary>
    ///     Nearest known fingerprints by centroid L2 distance, ordered closest
    ///     first, computed fresh per request via vec0 KNN. Empty when identity
    ///     is disabled, vec0 is unavailable, the signature is unbound, or the
    ///     dashboard runs against a remote gateway whose API does not yet
    ///     expose centroid KNN.
    /// </summary>
    public IReadOnlyList<Mostlylucid.BotDetection.Identity.NearestFingerprint> LooksLike { get; init; }
        = Array.Empty<Mostlylucid.BotDetection.Identity.NearestFingerprint>();

    /// <summary>
    ///     Browser modes the signature's fingerprint has played (composite-spec
    ///     step 7). Each row is one persisted entry from
    ///     <c>fingerprint_modes</c> — same browser, different modes. Empty when
    ///     identity is disabled, the signature is unbound, the mode store isn't
    ///     registered (remote-mode dashboard host), or the fingerprint hasn't
    ///     accumulated any post-step-2 observations yet.
    /// </summary>
    public IReadOnlyList<SignatureBrowserModeRow> BrowserModes { get; init; }
        = Array.Empty<SignatureBrowserModeRow>();

    /// <summary>
    ///     Gateway-projected drift badge for the signature header (plan task
    ///     19). Null when the signature has no bound fingerprint, the
    ///     fingerprint has no root centroid yet, the drift magnitude is below
    ///     <c>IdentityOptions.Drift.DriftBadgeThreshold</c>, or the dashboard
    ///     runs on a remote-mode host without IFingerprintReader. Per spec D5
    ///     the threshold cross + label resolution happen on the gateway; the
    ///     wire carries this projected payload only.
    /// </summary>
    public Mostlylucid.BotDetection.UI.Models.Primitives.DriftBadgeModel? DriftBadge { get; init; }
}

/// <summary>
///     One browser-mode row rendered on the signature detail page (composite
///     spec step 7). Replaces the legacy "Drifted Chrome Desktop → Chrome XHR"
///     misnomer with an honest per-mode list: this fingerprint plays these
///     modes, with this many observations each, last seen at these timestamps.
/// </summary>
public sealed record SignatureBrowserModeRow
{
    /// <summary>YAML mode id (navigation / xhr / sub-resource / signalr-negotiate / websocket-upgrade / prefetch / bot-raw / unknown).</summary>
    public required string ModeId { get; init; }

    /// <summary>EWMA centroid maturity for this mode — N observations folded in.</summary>
    public required int CentroidMaturity { get; init; }

    /// <summary>Total observation count (including unabsorbed, before the next drain tick).</summary>
    public required int ObservationCount { get; init; }

    public required DateTime FirstSeen { get; init; }
    public required DateTime LastSeen { get; init; }

    /// <summary>
    ///     Per-mode shift off the parent fingerprint's rolled-up centroid
    ///     (composite-spec step 7, plan task 15). Replaces the deleted
    ///     "Nearest archetype" column with the spec's honest framing: "this
    ///     mode shifts your baseline by X, top dims: …". Projected on the
    ///     gateway (per spec D5 / <c>project_gateway_data_locality</c>) so
    ///     the wire payload never carries raw centroid arrays. Null when the
    ///     parent fingerprint or the mode row has no centroid to project
    ///     against (legacy migration boundary; runtime steady state always
    ///     populates it).
    /// </summary>
    public Mostlylucid.BotDetection.UI.Services.ModeDelta? Shift { get; init; }
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

    /// <summary>
    ///     True when the request carried <c>Sec-Purpose: prefetch</c> or
    ///     <c>Purpose: prefetch</c> -- a browser preload, not a user-driven
    ///     click. The Raw Requests panel paints a "preload" badge so the
    ///     operator can tell apart two same-path rows that are really one
    ///     preload + one navigation, not a duplicate write.
    /// </summary>
    public bool IsPrefetch { get; init; }
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

/// <summary>Identifies a monitoring pack tab in the dashboard nav.</summary>
public sealed record PackTabInfo(string Id, string TabName);

/// <summary>
///     One (hour bucket, UA version) bin from the UA-version-history query. The dashboard
///     stacks these into a per-version area chart so an operator can see when a new Chrome
///     version started showing up, when an old one disappeared, or when an unusual version
///     spiked. No new storage required -- read from <c>dashboard_detections.important_signals</c>.
/// </summary>
public sealed record UserAgentVersionBucket
{
    public required DateTime Bucket { get; init; }
    public required string Version { get; init; }
    public required int Hits { get; init; }
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
    /// <summary>
    ///     Active row reference parsed off the request path
    ///     (<c>/stylobot/{area}[/{sub}]</c>). The view dispatches its content
    ///     section by inspecting <c>ActiveRow.Area</c> + <c>ActiveRow.Sub</c>.
    /// </summary>
    public required UI.Dashboard.DashboardRowRef ActiveRow { get; init; }

    /// <summary>
    ///     Back-compat shim. Returns <c>ActiveRow.Area</c> so any external
    ///     reflection / serialisation that depended on the old string-typed
    ///     "ActiveTab" still resolves. Internal call sites read
    ///     <see cref="ActiveRow" /> directly.
    /// </summary>
    public string ActiveTab => ActiveRow.Area;

    public string? Version { get; init; }

    /// <summary>
    ///     Mirrors <c>StyloBotDashboardOptions.RenderShell</c>. When false, the view skips
    ///     its own brand header so a host's _Layout navbar can sit above the dashboard body
    ///     without doubling up.
    /// </summary>
    public bool RenderShell { get; init; } = true;

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

    /// <summary>
    ///     Settled snapshot from <c>ITunnelEnvironmentInspector</c>. Null when the
    ///     inspector hasn't seen enough requests to settle yet, or when tunnel
    ///     detection is disabled. The overview tab renders a dismissable banner
    ///     when <c>IsTunnelWithoutEnrichment</c> is true so the operator knows
    ///     the TLS fingerprint surface is dark and can follow the docs link to
    ///     wire forwarding.
    /// </summary>
    public Mostlylucid.BotDetection.Proxy.TunnelEnvironmentSnapshot? TunnelEnvironment { get; init; }

    /// <summary>
    ///     Docs URL the tunnel banner links to. Cached on the shell model so the
    ///     view doesn't have to resolve options again at render time.
    /// </summary>
    public string TunnelDocsUrl { get; init; } = "https://stylobot.net/articles/tunnel-trade-off";

    /// <summary>Compliance tab model. Only set when active tab is "compliance".</summary>
    public ComplianceTabModel? Compliance { get; init; }

    /// <summary>
    ///     Dashboard-contributing packs registered in DI. Empty when no packs
    ///     are loaded. Drives the PACKS section of the left nav.
    /// </summary>
    public IReadOnlyList<UI.Dashboard.IDashboardPack> Packs { get; init; } = [];

    /// <summary>
    ///     Back-compat shim. Projects the new <see cref="Packs" /> list down
    ///     to the old <see cref="PackTabInfo" /> shape so the existing
    ///     PackUxTests keep passing during the migration window.
    /// </summary>
    public IReadOnlyList<PackTabInfo> MonitoringPacks =>
        Packs.Select(p => new PackTabInfo(p.Id, p.Label)).ToList();

    public bool HasPackTabs => Packs.Count > 0;
    public bool IsPackTab(string id) => Packs.Any(p => p.Id == id);
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
    ///     Effective-config sections discovered by reflecting on
    ///     <see cref="Mostlylucid.BotDetection.Models.BotDetectionOptions"/>. First entry
    ///     is the synthetic "root" bucket (scalar/list properties); subsequent entries
    ///     are the complex sub-options classes (AiDetection, Behavioral, ...).
    /// </summary>
    public required IReadOnlyList<Mostlylucid.BotDetection.UI.Services.ConfigSectionInfo> Sections { get; init; }

    /// <summary>
    ///     True when the active license is paid (Active or Trial). Hides the per-target
    ///     upsell rail. Read off the LicenseCardModel that's already on the shell.
    /// </summary>
    public required bool IsCommercialLicensed { get; init; }

    /// <summary>
    ///     Control plane URL for loading the commercial config editor via HTMX.
    ///     When set, the Configuration tab loads the editor from this URL.
    ///     When null, the FOSS view-only surface renders instead.
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

    /// <summary>
    ///     Raw User-Agent of the most-recent detection for this session's
    ///     signature, resolved at render time via
    ///     <see cref="Services.SessionEnrichmentExtensions.ResolveUserAgent"/>.
    ///     Feeds the dashboard's "Chrome 148 / macOS" rich identity label via
    ///     <see cref="Services.SignatureDisplayName.Resolve"/>. Null when no
    ///     UA was captured (the row then falls through to the legacy
    ///     country/family composite).
    /// </summary>
    public string? UserAgent { get; init; }

    public int ErrorCount { get; init; }
    public float TimingEntropy { get; init; }
    public float Maturity { get; init; }

    /// <summary>Markov transitions as "State->State": count</summary>
    public Dictionary<string, int>? TransitionCounts { get; init; }

    /// <summary>Top 3 transitions by count for compact display</summary>
    public IEnumerable<KeyValuePair<string, int>> TopTransitions =>
        TransitionCounts?.OrderByDescending(kv => kv.Value).Take(3)
        ?? Enumerable.Empty<KeyValuePair<string, int>>();

    /// <summary>Distinct templatized paths visited in this session (e.g. /wp-admin, /api/users/{id}).</summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>
    ///     Delta of AvgBotProbability vs the immediately prior session for the same signature,
    ///     in percentage points. Null when no prior session is available in the current window.
    ///     Positive = score worsened (more bot-like); negative = score improved.
    /// </summary>
    public double? ScoreDeltaPp { get; init; }
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
    public string NavBasePath { get; init; } = "";
    public string ResolvedNavBasePath => string.IsNullOrEmpty(NavBasePath) ? BasePath : NavBasePath;
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
///     Per-session data for the behavioral fingerprint filmstrip/overlay.
///     StateFreqs is Vector[100..109] — the Markov stationary distribution
///     (time spent in each of 10 states, values 0..1 summing to ~1.0).
/// </summary>
public sealed class SessionFingerprintEntry
{
    public long Id { get; init; }
    public DateTime StartedAt { get; init; }
    public bool IsBot { get; init; }
    public string RiskBand { get; init; } = "";
    public double Probability { get; init; }
    public int RequestCount { get; init; }
    /// <summary>10 values from Vector[100..109]. May be all-zero if vector blob is absent.</summary>
    public float[] StateFreqs { get; init; } = new float[10];
}

public sealed class SessionFingerprintsModel
{
    public required string Signature { get; init; }
    /// <summary>Id of the session currently being viewed. Null when invoked from signature page.</summary>
    public long? CurrentSessionId { get; init; }
    public List<SessionFingerprintEntry> Sessions { get; init; } = [];
    public required string BasePath { get; init; }
    public string CspNonce { get; init; } = "";
    /// <summary>True when embedded in session detail panel (filmstrip only, 56px thumbnails, max 10 sessions).</summary>
    public bool CompactMode { get; init; }
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

    /// <summary>
    ///     Raw User-Agent of the detection that triggered the threat. Feeds the
    ///     dashboard's "Chrome 119 / macOS" rich identity label via
    ///     <see cref="Services.SignatureDisplayName.Resolve"/>. Without it the
    ///     threats row falls through to "GB Bot N" synth labels even when the
    ///     event store has the UA parsed and available -- the same hide-what-
    ///     we-know pattern that motivated [[task #110]].
    /// </summary>
    public string? UserAgent { get; init; }
}

/// <summary>
///     Aggregated row for the dashboard Honeypot subtab -- one row per
///     distinct honeypot path that's been hit in the window, with the
///     totals an operator needs to triage.
/// </summary>
public sealed class HoneypotHitRow
{
    public required string Path { get; init; }

    /// <summary>1 = Tier 1 (always honeypot), 2 = Tier 2 (probable, exempt-able).</summary>
    public int Tier { get; init; }

    /// <summary>
    ///     Category from <see cref="Mostlylucid.BotDetection.Honeypot.HoneypotCategory"/>;
    ///     drives the intent chip and lets the dashboard filter/group by
    ///     what the scanner is going for.
    /// </summary>
    public Mostlylucid.BotDetection.Honeypot.HoneypotCategory Category { get; init; }

    public string? MatchedPattern { get; init; }

    public int HitCount { get; init; }

    public int DistinctSignatures { get; init; }

    public DateTime FirstSeen { get; init; }

    public DateTime LastSeen { get; init; }

    /// <summary>Sample bot-name for the row (most recent non-null).</summary>
    public string? SampleBotName { get; init; }

    /// <summary>True when this path is in the operator's exempt list.</summary>
    public bool IsExempt { get; init; }

    /// <summary>
    ///     Deterministic explanation chips. Short reason strings derived at
    ///     query time from tier + pattern + lifecycle hints. Surfaced as
    ///     chips so an operator can see WHY a path is treated as honeypot
    ///     without drilling into raw signals.
    /// </summary>
    public IReadOnlyList<string> Why { get; init; } = Array.Empty<string>();
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

/// <summary>
///     View model for the Identities tab — listing of metastable fingerprints with the
///     surface an operator needs to triage drift candidates. Only populated when
///     Identity:Enabled is true; the tab renders an empty-state explainer otherwise.
/// </summary>
public sealed class IdentitiesListModel
{
    public IReadOnlyList<IdentityListEntry> Identities { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string BasePath { get; init; } = "";
    public bool IdentityEnabled { get; init; }
}

/// <summary>One row on the Identities tab. Compact projection of the fingerprints row + computed counts.</summary>
public sealed record IdentityListEntry
{
    public required string FingerprintId { get; init; }
    public required string InferredClientType { get; init; }
    public double InferredTypeConfidence { get; init; }
    public required int CentroidMaturity { get; init; }
    public required int ObservationCount { get; init; }

    /// <summary>Unabsorbed observation rows for this fingerprint — the freshness budget the next absorption tick has to work with.</summary>
    public required int UnabsorbedObservations { get; init; }
    public required int CorrectionCount { get; init; }
    public double CachedBotProbability { get; init; }
    public string? CachedRiskBand { get; init; }
    public DateTime? CachedScoreUpdatedAt { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public string? ArchetypeOrigin { get; init; }

    /// <summary>EWMA-smoothed boundary-probing score 0..1 (task #42); high values flag adversarial probing.</summary>
    public double AmbiguityPersistence { get; init; }
}

/// <summary>
///     Model for the /dashboard?tab=policies view. Aggregates the two policy
///     surfaces (detection-policy rules with the four-axis matcher, rate-
///     limit rules with composite subjects + scope inheritance) into a
///     single read-only render so operators can see the entire rule set in
///     one place.
/// </summary>
public sealed class PoliciesTabModel
{
    public required string BasePath { get; init; }
    public required Mostlylucid.BotDetection.EndpointPolicies.DetectionPolicyOptions DetectionPolicies { get; init; }
    public required Mostlylucid.BotDetection.RateLimiting.RateLimitOptions RateLimitOptions { get; init; }
}
