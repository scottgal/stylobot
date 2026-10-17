namespace Mostlylucid.BotDetection.UI.Models;

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
    public string TunnelDocsUrl { get; init; } = "https://stylo.bot/articles/tunnel-trade-off";

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

    /// <summary>
    ///     HttpContext.Items key a commercial host can set to <c>true</c> before this middleware
    ///     builds the shell model, to flip <see cref="IsPrivilegedViewer" /> without another FOSS
    ///     code-path change. Read at the model-construction call site in
    ///     <c>StyloBotDashboardMiddleware.ServeDashboardPageAsync</c>.
    /// </summary>
    public const string PrivilegedViewerItemsKey = "sb.dashboard.privileged_viewer";

    /// <summary>
    ///     Whether the current viewer is privileged (sees every nav row regardless of
    ///     <see cref="Services.INavVisibilityPolicy" /> hidden-path config). Defaults to
    ///     <c>false</c> -- the safe FOSS default. Threaded from <see cref="PrivilegedViewerItemsKey" />
    ///     on <c>HttpContext.Items</c> rather than a new constructor parameter, so a future
    ///     commercial host (e.g. after resolving license/role) can flip it with an
    ///     <c>HttpContext.Items</c> write in its own middleware, upstream of this one, with zero
    ///     FOSS call-site changes.
    /// </summary>
    public bool IsPrivilegedViewer { get; init; } = false;

    /// <summary>
    ///     When set, <c>Index.cshtml</c> renders <c>_SignatureDetail.cshtml</c> with this model
    ///     instead of dispatching through <see cref="UI.Dashboard.IDashboardRowRegistry" />. Lets
    ///     a per-ID detail page (no static row registration) share the same drawer/sidebar shell
    ///     as the registry-dispatched tabs, instead of rendering standalone with a hand-rolled
    ///     (and pre-V2, since-deleted-elsewhere) fallback nav. Null for every normal tab render.
    /// </summary>
    public SignatureDetailModel? SignatureDetailContent { get; init; }

    /// <summary>
    ///     Si2 (endpoint-IA unification): when set, <c>Index.cshtml</c> renders
    ///     <c>_EndpointDetail.cshtml</c> with this model instead of dispatching
    ///     through <see cref="UI.Dashboard.IDashboardRowRegistry"/>. Mirrors
    ///     <see cref="SignatureDetailContent"/> -- the endpoint-detail page used to
    ///     render standalone (isMainPage: true, no dashboard sidebar/drawer,
    ///     falling back to whatever _ViewStart the host provides) the same way
    ///     signature-detail did before that was fixed; this closes the same gap
    ///     for endpoints. Null for every normal tab render.
    /// </summary>
    public EndpointDetailModel? EndpointDetailContent { get; init; }
}
