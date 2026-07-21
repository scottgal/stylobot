using System.Collections.Concurrent;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Middleware;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Auth;
using Mostlylucid.BotDetection.UI.Policies;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.Auth;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Middleware for handling Stylobot Dashboard routes.
///     Serves the dashboard UI and API endpoints.
/// </summary>
public class StyloBotDashboardMiddleware
{
    private readonly IDashboardEventStore _eventStore;
    private readonly DashboardAggregateCache _aggregateCache;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly ILogger<StyloBotDashboardMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly StyloBotDashboardOptions _options;
    private readonly RazorViewRenderer _razorViewRenderer;
    private readonly IMemoryCache _widgetCache;
    private readonly IDashboardContentCache? _contentCache;
    private readonly IDashboardPageManifestSource? _manifests;

    private static readonly TimeSpan WidgetCacheTtl = TimeSpan.FromSeconds(2);

    private static readonly string? DashboardVersion = GetDashboardVersion();

    private static string? GetDashboardVersion()
    {
        // Try BotDetection core assembly first (has MinVer with git tags)
        var coreAsm = typeof(BotDetectionOptions).Assembly;
        var coreVersion = coreAsm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(coreVersion) && coreVersion != "0.0.0")
            return coreVersion;
        // Fallback to UI assembly
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    }

    // Rate limiter: per IP, per minute (used only for diagnostics endpoint)
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _rateLimits = new();
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _exportRateLimits = new();
    private const int ExportRateLimit = 6; // bulk exports per IP per window
    private static volatile bool _authWarningLogged;

    // Caches whether any dashboard users exist. Set to true on first confirmed user; never reverts.
    // Avoids a DB round-trip on every unauthenticated request once deployment is past first-run.
    private static volatile bool _usersExist;

    // Serialises first-run admin creation: without it two concurrent POST /setup
    // requests can both pass the user-count check and both create an account.
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    internal const string SetupCsrfCookieName = "sb.setup.csrf";
    internal const string SetupCsrfFormField = "__csrf";

    private const string AuthPageCss = """
        *{box-sizing:border-box;margin:0;padding:0}
        body{font-family:system-ui,sans-serif;background:#0f172a;color:#e2e8f0;display:flex;align-items:center;justify-content:center;min-height:100vh}
        .card{background:#1e293b;border:1px solid #334155;border-radius:12px;padding:2rem;width:100%;max-width:420px}
        h1{font-size:1.25rem;font-weight:600;margin-bottom:.25rem}
        p{font-size:.875rem;color:#94a3b8;margin-bottom:1.5rem}
        label{display:block;font-size:.875rem;font-weight:500;margin-bottom:.375rem;margin-top:1rem}
        input{width:100%;padding:.5rem .75rem;background:#0f172a;border:1px solid #334155;border-radius:6px;color:#e2e8f0;font-size:.875rem}
        input:focus{outline:none;border-color:#6366f1}
        button{margin-top:1.5rem;width:100%;padding:.625rem;background:#6366f1;color:#fff;border:none;border-radius:6px;font-size:.875rem;font-weight:500;cursor:pointer}
        button:hover{background:#4f46e5}
        .error{color:#f87171;font-size:.875rem;margin-top:1rem}
        """;
    private const int DiagnosticsRateLimit = 10;
    private const int MaxRateLimitEntries = 10_000; // hard cap to prevent memory exhaustion
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);
    private static readonly HashSet<string> DataApiPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "api/detections",
        "api/signatures",
        "api/summary",
        "api/timeseries",
        "api/export",
        "api/countries",
        "api/endpoints",
        "api/clusters",
        "api/useragents",
        "api/topbots",
        "api/sessions",
        "api/sessions/recent",
        "api/threats",
        "api/ua/search",
        "api/license",
        "api/config/manifests",
        "api/config/sections",
        "api/config/effective",
        "api/endpoint-pins",
    };

    private const string CountryDetailPrefix = "api/countries/";
    private const string EndpointDetailPrefix = "api/endpoints/";

    /// <summary>
    ///     Per-slot baseline magnitude written into the synthetic centroid used
    ///     by <see cref="ResolveFingerprintShapeAsync"/>'s defensive fallback
    ///     (no fingerprint_keys binding). The identity vector is L2-normalised, so
    ///     an archetype's asserted slot values cluster around ~0.1; the baseline
    ///     must sit BELOW that band so archetype-defined buckets rise above the
    ///     floor (a higher baseline inverts the shape -- unasserted buckets end up
    ///     larger than asserted ones). FingerprintRadarProjection.ScaleToRim then
    ///     stretches the strongest bucket to the rim, so the floor only needs to
    ///     keep otherwise-empty buckets faintly visible.
    /// </summary>
    private const float InferredBaselineMagnitude = 0.06f;
    private const string BdfExportPrefix = "api/bdf/";

    private static int _cleanupRunning;

    /// <summary>Shared JSON options: camelCase to match SSR initial page load and JS frontend expectations.</summary>
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    ///     Per-request commercial feature check. Returns false if the request has
    ///     <c>?mode=foss</c> (used by the marketing site demo toggle), even when
    ///     <see cref="StyloBotDashboardOptions.EnableConfigEditing"/> is true.
    /// </summary>
    private bool IsCommercialMode(HttpContext context) =>
        _options.EnableConfigEditing &&
        !string.Equals(context.Request.Query["mode"].FirstOrDefault(), "foss", StringComparison.OrdinalIgnoreCase);

    private readonly IWebHostEnvironment _env;

    public StyloBotDashboardMiddleware(
        RequestDelegate next,
        StyloBotDashboardOptions options,
        IDashboardEventStore eventStore,
        DashboardAggregateCache aggregateCache,
        SignatureAggregateCache signatureCache,
        RazorViewRenderer razorViewRenderer,
        IMemoryCache widgetCache,
        IWebHostEnvironment env,
        ILogger<StyloBotDashboardMiddleware> logger,
        IDashboardContentCache? contentCache = null,
        IDashboardPageManifestSource? manifests = null)
    {
        _next = next;
        _options = options;
        _eventStore = eventStore;
        _aggregateCache = aggregateCache;
        _signatureCache = signatureCache;
        _razorViewRenderer = razorViewRenderer;
        _widgetCache = widgetCache;
        _env = env;
        _logger = logger;
        _contentCache = contentCache;
        _manifests = manifests;
    }

    private string GetOrCreateCspNonce(HttpContext context)
    {
        if (context.Items.TryGetValue("CspNonce", out var obj) && obj is string s && s.Length > 0)
            return s;
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        context.Items["CspNonce"] = nonce;
        return nonce;
    }

    private void WarnAuthOnce(LogLevel level, string message)
    {
        if (_authWarningLogged) return;
        _authWarningLogged = true;
        _logger.Log(level, "{Message}", message);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Check if this is a dashboard request
        if (!path.StartsWith(_options.BasePath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var relativePath = path.Substring(_options.BasePath.Length).TrimStart('/');
        var relLower = relativePath.ToLowerInvariant();

        // Every dashboard-handled response (HTML, JSON, CSV export) carries nosniff
        // so a browser can never reinterpret an export or API payload as HTML.
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        if (_options.RequireAuthentication)
        {
            // Identity API endpoints pass through to endpoint routing (MapIdentityApi).
            if (relLower.StartsWith("auth/")) { await _next(context); return; }

            // One-time setup route (disappears after first user exists).
            if (relLower is "setup" or "setup/") { await ServeSetupAsync(context); return; }

            // Login UI: form that POSTs to /_stylobot/auth/login?useCookies=true.
            if (relLower is "auth-ui" or "login") { await ServeLoginUiAsync(context); return; }
        }

        // Check authorization
        if (!await IsAuthorizedAsync(context))
        {
            if (_options.RequireAuthentication)
            {
                await HandleUnauthenticatedAsync(context, relLower);
                return;
            }

            _logger.LogWarning("Dashboard access denied for {IP} on {Path}",
                context.Connection.RemoteIpAddress, context.Request.Path);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Forbidden: Dashboard access denied");
            return;
        }

        // High-value dashboard data APIs are hard-blocked for detected bots,
        // UNLESS the request carries a valid API key (trusted tooling / monitoring).
        // Block detected bots from data APIs. If a human is being blocked here,
        // that's a detection bug - fix the detection, not the block.
        var isDataApi = DataApiPaths.Contains(relativePath)
                        || relativePath.StartsWith(CountryDetailPrefix, StringComparison.OrdinalIgnoreCase);
        isDataApi = isDataApi || relativePath.StartsWith(EndpointDetailPrefix, StringComparison.OrdinalIgnoreCase);
        if (isDataApi && context.IsBot())
        {
            var hasValidApiKey = context.Items.TryGetValue("BotDetection.ApiKeyContext", out var keyCtxObj)
                                 && keyCtxObj is ApiKeyContext;
            if (!hasValidApiKey)
                hasValidApiKey = !string.IsNullOrEmpty(context.Request.Headers["X-SB-Api-Key"].FirstOrDefault());
            if (!hasValidApiKey)
            {
                var policyName = context.Items.TryGetValue("BotDetection.PolicyName", out var pn) ? pn?.ToString() : null;
                if (policyName != null && policyName.Contains("+apikey:", StringComparison.OrdinalIgnoreCase))
                    hasValidApiKey = true;
            }

            if (!hasValidApiKey)
            {
                _logger.LogInformation("Blocked bot from dashboard data API: {Path} (probability={Probability:F2})",
                    context.Request.Path, context.GetBotProbability());
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"Access denied\"}");
                return;
            }
        }

        switch (relLower)
        {
            case "":
            case "index":
            case "index.html":
            {
                // M2 back-compat: bare /{base}/?tab=X 301-redirects to /{base}/X so
                // legacy bookmarks and Slack pastes survive the IA collapse. Other
                // query params ride along (e.g. ?tab=traffic&fp=abc becomes
                // /traffic?fp=abc). This MUST run before the V2 landing redirect
                // below: that redirect 302s and would lose the `tab=` parameter
                // intent (it just blindly forwards the querystring to /traffic,
                // landing on /traffic?tab=traffic — meaningless).
                var legacyTabAtRoot = context.Request.Query["tab"].FirstOrDefault();
                if (!string.IsNullOrEmpty(legacyTabAtRoot))
                {
                    var strippedQs = UI.Dashboard.DashboardRoutingHelpers.StripTabParam(
                        context.Request.QueryString.Value ?? "");
                    var tabTarget = _options.BasePath.TrimEnd('/') + "/" + legacyTabAtRoot.ToLowerInvariant() + strippedQs;
                    context.Response.Redirect(tabTarget, permanent: true);
                    break;
                }

                // V2 IA landing redirect: when the layout kill-switch is on AND the
                // request didn't explicitly opt back into legacy with ?legacy=1, send
                // the operator to /dashboard/traffic (the new default page). Legacy
                // behaviour is otherwise preserved verbatim.
                var v2Layout = context.RequestServices
                    .GetService<Microsoft.Extensions.Options.IOptions<UI.Models.Dashboard.Layout.DashboardLayoutOptions>>()
                    ?.Value;
                if (v2Layout is { V2Enabled: true } &&
                    !string.Equals(context.Request.Query["legacy"].FirstOrDefault(), "1", StringComparison.Ordinal))
                {
                    var trafficTarget = _options.BasePath.TrimEnd('/') + "/traffic";
                    var qs = context.Request.QueryString.Value;
                    context.Response.Redirect(string.IsNullOrEmpty(qs) ? trafficTarget : trafficTarget + qs);
                    break;
                }
                if (_options.RenderPage)
                {
                    await ServeDashboardPageAsync(context);
                }
                else
                {
                    // Host MVC owns the dashboard root page. Middleware continues to serve the API,
                    // SignalR hub, partials, and static assets under BasePath.
                    await _next(context);
                }
                break;
            }

            // V2 IA Traffic / Visitors / Site landing pages — owned by the
            // dashboard row registry via ServeDashboardPageAsync, NOT by their
            // separate MVC controllers. The controllers' hardcoded
            // [Route("dashboard/...")] attributes only fire when BasePath
            // happens to equal "/dashboard"; under any other mount (Demo uses
            // "/stylobot", marketing site uses "/_stylobot") the routes would
            // miss, and even when they hit, the controllers' Views render a
            // bare partial without the dashboard chrome (left nav, header,
            // SignalR script). ServeDashboardPageAsync renders the full page
            // and dispatches the area body via the row registry so chrome +
            // content stay in sync regardless of BasePath. Listed explicitly
            // here so the cases survive any future tightening of
            // IsDashboardRowPath.
            case "traffic":
            case var tp when tp.StartsWith("traffic/", StringComparison.OrdinalIgnoreCase):
            case "visitors":
            case var vp when vp.StartsWith("visitors/", StringComparison.OrdinalIgnoreCase):
            case "site":
            case var sp when sp.StartsWith("site/", StringComparison.OrdinalIgnoreCase):
                if (_options.RenderPage)
                {
                    await ServeDashboardPageAsync(context);
                }
                else
                {
                    await _next(context);
                }
                break;

            // M2 legacy IA -> V2 IA permanent redirects. Plan M1 already
            // flipped V2Enabled on by default + redirects /{base} to /traffic;
            // M2 now also redirects each individual legacy tab URL so deep
            // links from external bookmarks, telemetry traces, and Slack
            // pastes survive the rip-and-replace. ?legacy=1 deliberately does
            // NOT bypass these redirects -- the legacy controllers + partials
            // are physically deleted by this commit, so there is no behind to
            // resurrect. The escape hatch only affects the sidebar dispatch
            // in _LeftNav.cshtml and the bare /{base} landing redirect.
            case "overview":
                context.Response.Redirect(_options.BasePath.TrimEnd('/') + "/traffic", permanent: true);
                break;
            case "activity":
                context.Response.Redirect(_options.BasePath.TrimEnd('/') + "/traffic", permanent: true);
                break;
            case "sessions":
                context.Response.Redirect(_options.BasePath.TrimEnd('/') + "/visitors", permanent: true);
                break;
            case "threats":
                context.Response.Redirect(_options.BasePath.TrimEnd('/') + "/visitors?threat=Medium%2B", permanent: true);
                break;
            case "insights":
                context.Response.Redirect(_options.BasePath.TrimEnd('/') + "/traffic", permanent: true);
                break;
            case "investigate":
                context.Response.Redirect(_options.BasePath.TrimEnd('/') + "/visitors", permanent: true);
                break;
            case "endpoints":
                context.Response.Redirect(_options.BasePath.TrimEnd('/') + "/site", permanent: true);
                break;

            case "api/detections":
                await ServeDetectionsApiAsync(context);
                break;

            case "api/signatures":
                await ServeSignaturesApiAsync(context);
                break;

            case "api/summary":
                await ServeSummaryApiAsync(context);
                break;

            case "api/timeseries":
                await ServeTimeSeriesApiAsync(context);
                break;

            case "api/export":
                await ServeExportApiAsync(context);
                break;

            case "api/diagnostics":
                await ServeDiagnosticsApiAsync(context);
                break;

            case "api/countries":
                await ServeCountriesApiAsync(context);
                break;

            case "api/endpoints":
                await ServeEndpointsApiAsync(context);
                break;

            case "api/clusters":
                await ServeClustersApiAsync(context);
                break;

            case "api/useragents":
                await ServeUserAgentsApiAsync(context);
                break;

            case "api/topbots":
                await ServeTopBotsApiAsync(context);
                break;

            case "api/threats":
                await ServeThreatsApiAsync(context);
                break;

            case "api/ua/search":
                await ServeUaSearchApiAsync(context);
                break;

            // Se2 -- header type-ahead visitor search. Walks the IFingerprintStore
            // LFU (in-memory only, never opens a DB connection) and returns up to
            // DashboardLayoutOptions.SearchMaxResults hits matched on resolved
            // display name. Empty / missing q -> empty array (not 400) so the
            // type-ahead can cheaply reset to "no matches" without distinguishing
            // request shape from result shape on the client.
            case "search/visitors":
                await ServeVisitorSearchApiAsync(context);
                break;

            case "api/me":
                await ServeMeApiAsync(context);
                break;

            case "api/license":
                await ServeLicenseApiAsync(context);
                break;

            case "api/config/manifests":
                await ServeConfigManifestsListAsync(context);
                break;

            case var p when p.StartsWith("api/config/manifests/", StringComparison.OrdinalIgnoreCase):
                await ServeConfigManifestApiAsync(context, relativePath["api/config/manifests/".Length..]);
                break;

            case "api/config/sections":
                await ServeConfigSectionsListAsync(context);
                break;

            case var p when p.StartsWith("api/config/section/", StringComparison.OrdinalIgnoreCase):
                await ServeConfigSectionAsync(context, relativePath["api/config/section/".Length..]);
                break;

            case "api/config/effective":
                await ServeConfigEffectiveAsync(context);
                break;

            case "api/labels":
                await ServeLabelsListApiAsync(context);
                break;

            case "api/labels/counts":
                await ServeLabelsCountsApiAsync(context);
                break;

            case var p when p.StartsWith("api/labels/", StringComparison.OrdinalIgnoreCase):
                await ServeSignatureLabelApiAsync(context, relativePath["api/labels/".Length..]);
                break;

            case "api/approvals":
                await ServeApprovalsListApiAsync(context);
                break;

            case var p when p.StartsWith("api/approvals/", StringComparison.OrdinalIgnoreCase):
                await ServeFingerprintApprovalApiAsync(context, relativePath["api/approvals/".Length..]);
                break;

            case "api/sessions":
            case "api/sessions/recent":
                await ServeSessionsApiAsync(context);
                break;

            case var p when p.StartsWith(CountryDetailPrefix, StringComparison.OrdinalIgnoreCase):
                await ServeCountryDetailApiAsync(context, p.Substring(CountryDetailPrefix.Length));
                break;

            case var p when p.StartsWith(EndpointDetailPrefix, StringComparison.OrdinalIgnoreCase):
                await ServeEndpointDetailApiAsync(context, p.Substring(EndpointDetailPrefix.Length));
                break;

            case var p when p.StartsWith("api/sessions/signature/", StringComparison.OrdinalIgnoreCase):
                await ServeSignatureSessionsApiAsync(context, relativePath["api/sessions/signature/".Length..]);
                break;

            case var p when p.StartsWith("api/sparkline/", StringComparison.OrdinalIgnoreCase):
                // Use original relativePath to preserve signature case
                await ServeSparklineApiAsync(context, relativePath.Substring("api/sparkline/".Length));
                break;

            case var p when p.StartsWith(BdfExportPrefix, StringComparison.OrdinalIgnoreCase):
                // Use original relativePath to preserve signature case
                await ServeBdfExportApiAsync(context, relativePath.Substring(BdfExportPrefix.Length));
                break;

            // Pack Metrics B8 -- rollup summary for the policy-stack pack.
            // Mirrors the AspNet summary contract: per-mode rule counts +
            // 15-minute decision count + a HealthBand the C1 dashboard
            // overview row consumes to render a "Policy stack" SbStatTile.
            // Anonymous-readable: counts only, no PII. 404 when no rule store
            // is registered (viewer hosts that pull data via REST).
            case "api/v1/packs/policystack/summary":
                await ServePolicyStackSummaryAsync(context);
                break;

            // --- HTMX partial endpoints (server-rendered HTML islands) ---
            case "partials/visitors":
                await ServeVisitorListPartialAsync(context);
                break;

            case "partials/summary":
                await ServeSummaryPartialAsync(context);
                break;

            case "partials/your-detection":
                await ServeYourDetectionPartialAsync(context);
                break;

            case "partials/license":
                await ServeLicensePartialAsync(context);
                break;

            case "partials/configuration":
                await ServeConfigurationPartialAsync(context);
                break;

            case "partials/countries":
                await ServeCountriesPartialAsync(context);
                break;

            case "partials/endpoints":
                await ServeEndpointsPartialAsync(context);
                break;

            case "partials/endpoints-compact":
                await ServeEndpointsCompactPartialAsync(context);
                break;

            case "partials/clusters":
                await ServeClustersPartialAsync(context);
                break;

            case "partials/useragents":
                await ServeUserAgentsPartialAsync(context);
                break;

            case "partials/useragent-detail":
                await ServeUserAgentDetailPartialAsync(context);
                break;

            case "partials/endpoint-detail":
                await ServeEndpointDetailPartialAsync(context);
                break;

            case "partials/topbots":
                await ServeTopBotsPartialAsync(context);
                break;

            case "partials/bot-breakdown":
                await ServeBotBreakdownPartialAsync(context);
                break;

            case "partials/sessions":
                await ServeSessionsPartialAsync(context);
                break;

            case "partials/threats":
                await ServeThreatsPartialAsync(context);
                break;

            case "partials/identities":
                await ServeIdentitiesPartialAsync(context);
                break;

            case var p when p.StartsWith("api/identities/", StringComparison.OrdinalIgnoreCase) && p.EndsWith("/reverify", StringComparison.OrdinalIgnoreCase):
                await ServeIdentityReverifyAsync(context, p);
                break;

            case var p when p.StartsWith("api/identities/", StringComparison.OrdinalIgnoreCase) && p.EndsWith("/run-ai", StringComparison.OrdinalIgnoreCase):
                await ServeIdentityRunAiAsync(context, p);
                break;

            case "partials/session-detail":
                await ServeSessionDetailPartialAsync(context);
                break;

            case "partials/signature-sessions":
                await ServeSignatureSessionsPartialAsync(context);
                break;

            case "partials/session-fingerprints":
                await ServeSessionFingerprintsPartialAsync(context);
                break;

            case "partials/approval-form":
                await ServeApprovalFormPartialAsync(context);
                break;

            case "partials/update":
                await ServeOobUpdateAsync(context);
                break;

            // M2: the investigate tab + its HTMX sub-routes (tab swap,
            // preset reload) are gone -- the surface itself was deleted and
            // its functions absorbed into Visitors. /investigate at the
            // top-level redirects to /visitors above; the HTMX sub-routes
            // simply 404 (no external callers, only the deleted partials
            // hit them).

            case var p when p.StartsWith("help/", StringComparison.OrdinalIgnoreCase):
                await ServeHelpAsync(context, relativePath["help/".Length..]);
                break;

            // Policy Stack signal vocabulary (autocomplete + tooltip surface).
            // Anonymous-readable: the catalogue is compile-time public; gating it
            // would only frustrate the editor without protecting anything secret.
            case "signals/namespaces":
                await ServeSignalNamespacesAsync(context);
                break;

            case "signals/search":
                await ServeSignalSearchAsync(context);
                break;

            case var p when p.StartsWith("signals/", StringComparison.OrdinalIgnoreCase):
                // Use original relativePath (not lowercased) so a future mixed-case
                // signal key still resolves; today's catalogue is all lowercase.
                await ServeSignalDetailAsync(context, relativePath["signals/".Length..]);
                break;

            // Policy stack explainer panel partial. Returns the rendered
            // _ExplainerPanel HTML so HTMX can swap the panel without a
            // full page reload. Anonymous-readable for the same reason
            // /dashboard/signals/* is: the rules + decisions are not secrets,
            // and gating the partial would block the editor without
            // protecting anything.
            case "policystack/explain":
                await ServePolicyStackExplainAsync(context);
                break;

            // B6 -- Policy Stack row swap. Targets ONLY the rule list (the
            // _EffectiveTab or _StackTab body, wrapped in the envelope id the
            // browser's hx-swap=outerHTML aims at). No header chrome. Drives
            // both the SignalR-beacon invalidation refresh and the sort-header
            // click pattern B4 laid the data attributes for. Anonymous-readable
            // for the same reason policystack/explain is.
            case "policystack/rows":
                await ServePolicyStackRowsAsync(context);
                break;

            // C6 -- expression editor. The two GET routes return rendered
            // _EditRow.cshtml partials so HTMX can outerHTML-swap the
            // compact row in for the edit form. The POST /parse route
            // serves the bidirectional editor: it parses the text the
            // user typed and returns a canonical lowercase AST the JS
            // renders as chips. Anonymous-readable for the same reason
            // /dashboard/signals/* is -- the parse surface doesn't
            // disclose anything the SignalCatalog hasn't already exposed.
            case "policystack/edit":
                await ServePolicyStackEditExistingAsync(context);
                break;

            case "policystack/edit/new":
                await ServePolicyStackEditNewAsync(context);
                break;

            case "policystack/parse":
                await ServePolicyStackParseAsync(context);
                break;

            // Per-action-kind metadata editor swap endpoint. Returns the
            // rendered _EditAction_{kind}.cshtml partial; the 7-kind matrix
            // (allow / observe / block / tag / challenge / ratelimit /
            // throttle) is dispatched via PolicyActionEditorViewPaths.
            // The JS / HTMX hook-up that actually issues GET ?kind=<x> on
            // the kind <select> change lands in Task 8 when
            // _EditAction.cshtml becomes the slot host; the
            // policy-stack-edit.js chip-pane refactor that consumes the
            // data-edit-action-kind contract lands in Task 12.
            case "policystack/action-editor":
                await ServePolicyStackActionEditorAsync(context);
                break;

            // Traffic-shaping plan Tasks 9 + 10 -- scope-picker fragment.
            // Renders the existing SbScopePicker view component so _EditRow
            // + Apply Template flows can swap a fresh picker into the form
            // without re-rendering the whole row. The plan originally
            // called for a new SbPolicyScopePicker component but
            // SbScopePicker already covers the composite-scope axes
            // (Host / Method / Geo / Identity); creating a parallel
            // component would violate the no-duplication rule. Three
            // modes are supported: inline (single picker, Task 9),
            // multi (N pickers + "+ Add scope" wrapper, Task 10) and
            // inline-row (single row wrapper appended by the multi
            // wrapper's add button, Task 10). See
            // ServePolicyStackScopePickerAsync for the full contract.
            case "policystack/scope-picker":
                await ServePolicyStackScopePickerAsync(context);
                break;

            // C8 -- backtest projection. POST body is JSON
            // { predicate: "text", actionKind: "block", window: "24h" }.
            // Returns the rendered _BacktestPanel.cshtml partial as
            // text/html so the editor JS can swap it into the slot. 400 on
            // an invalid predicate. Anonymous-readable for the same
            // reason the parse route is.
            case "policystack/backtest":
                await ServePolicyStackBacktestAsync(context);
                break;

            case var p when p.StartsWith("signature/", StringComparison.OrdinalIgnoreCase):
                if (_options.RenderPage)
                {
                    var rawSigId = relativePath.Substring("signature/".Length);
                    await ServeSignatureDetailAsync(context, rawSigId);
                }
                else
                {
                    // Host MVC owns the signature detail page.
                    await _next(context);
                }
                break;

            case var p when p.StartsWith("entity/", StringComparison.OrdinalIgnoreCase):
                if (_options.RenderPage)
                {
                    // Use original relativePath (not lowercased) to preserve entity-id case
                    await ServeEntityDetailAsync(context, relativePath.Substring("entity/".Length));
                }
                else
                {
                    // Host MVC owns entity-detail rendering.
                    await _next(context);
                }
                break;

            case "endpoint":
                if (_options.RenderPage)
                {
                    await ServeEndpointDetailPageAsync(context);
                }
                else
                {
                    await _next(context);
                }
                break;

            case "session":
                if (_options.RenderPage)
                {
                    await ServeSessionDetailPageAsync(context);
                }
                else
                {
                    await _next(context);
                }
                break;

            case "api/endpoint-pins":
                if (!IsCommercialMode(context))
                {
                    context.Response.StatusCode = 402;
                    await context.Response.WriteAsync("Endpoint pinning is a commercial feature.");
                    break;
                }
                if (context.Request.Method == "GET")
                    await ServeEndpointPinsApiAsync(context);
                else if (context.Request.Method == "POST")
                    await HandlePinEndpointAsync(context);
                else
                    context.Response.StatusCode = 405;
                break;

            case var p when p.StartsWith("api/endpoint-pins/", StringComparison.OrdinalIgnoreCase):
                if (!IsCommercialMode(context))
                {
                    context.Response.StatusCode = 402;
                    await context.Response.WriteAsync("Endpoint pinning is a commercial feature.");
                    break;
                }
                if (context.Request.Method == "DELETE")
                    await HandleUnpinEndpointAsync(context, relLower["api/endpoint-pins/".Length..]);
                else
                    context.Response.StatusCode = 405;
                break;

            // Left-nav routes: /{BasePath}/{area} or /{BasePath}/{area}/{sub}.
            // Resolved against IDashboardRowRegistry inside ServeDashboardPageAsync;
            // unknown areas/subs yield 404 from inside the page handler.
            // Placed last so all specific named cases above take precedence.
            case var p when UI.Dashboard.DashboardRoutingHelpers.IsDashboardRowPath(p):
                if (_options.RenderPage)
                {
                    await ServeDashboardPageAsync(context);
                }
                else
                {
                    await _next(context);
                }
                break;

            default:
                // Static assets are served by static files middleware
                await _next(context);
                break;
        }
    }

    private async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        // Built-in Identity bearer/cookie auth (FOSS tier).
        // Authenticates the request inline without depending on UseAuthentication() placement.
        if (_options.RequireAuthentication)
        {
            var result = await context.AuthenticateAsync();
            if (result?.Principal != null)
                context.User = result.Principal;
            return context.User.Identity?.IsAuthenticated == true;
        }

        // Custom filter takes precedence
        if (_options.AuthorizationFilter != null) return await _options.AuthorizationFilter(context);

        // Policy-based auth
        if (!string.IsNullOrEmpty(_options.RequireAuthorizationPolicy))
        {
            var authService = context.RequestServices
                    .GetService(typeof(IAuthorizationService))
                as IAuthorizationService;

            if (authService != null)
            {
                var result = await authService.AuthorizeAsync(
                    context.User,
                    null,
                    _options.RequireAuthorizationPolicy);

                return result.Succeeded;
            }
        }

        // No auth configured - check if unauthenticated access is explicitly allowed
        if (_options.AllowUnauthenticatedAccess)
        {
            WarnAuthOnce(
                _env.IsDevelopment() ? LogLevel.Information : LogLevel.Warning,
                _env.IsDevelopment()
                    ? "Dashboard accessible without authentication (Development environment). Set AllowUnauthenticatedAccess=false and configure auth before deploying to production."
                    : "Dashboard running with AllowUnauthenticatedAccess=true in a non-Development environment. Configure AuthorizationFilter or RequireAuthorizationPolicy for production.");
            return true;
        }

        // Development: auto-allow so first-time users can reach the dashboard without config.
        if (_env.IsDevelopment())
        {
            WarnAuthOnce(LogLevel.Information,
                "Dashboard accessible without authentication (Development environment, no auth configured). " +
                "Set AllowUnauthenticatedAccess=false and configure AuthorizationFilter or RequireAuthorizationPolicy before deploying to production.");
            return true;
        }

        // Production default deny
        WarnAuthOnce(LogLevel.Error,
            "Dashboard access DENIED: no authorization configured. " +
            "Configure via AddStyloBotDashboard(options => options.AuthorizationFilter = ...) or " +
            "options.RequireAuthorizationPolicy = \"PolicyName\". " +
            "For local development, set the ASPNETCORE_ENVIRONMENT=Development environment variable.");
        return false;
    }

    private async Task HandleUnauthenticatedAsync(HttpContext context, string relLower)
    {
        if (!_usersExist)
        {
            var userStore = context.RequestServices.GetService<IUserStore<StyloBotUser>>() as StyloBotUserStore;
            if (userStore != null)
            {
                var count = await userStore.GetUserCountAsync(context.RequestAborted);
                if (count > 0) _usersExist = true;
                else { context.Response.Redirect(_options.BasePath.TrimEnd('/') + "/setup"); return; }
            }
        }

        // API paths return 401 JSON; browser paths redirect to login UI
        if (relLower.StartsWith("api/") || relLower.StartsWith("partials/"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Unauthorized\"}");
        }
        else
        {
            context.Response.Redirect(_options.BasePath.TrimEnd('/') + "/auth-ui");
        }
    }

    private async Task ServeSetupAsync(HttpContext context)
    {
        if (_usersExist) { context.Response.StatusCode = 404; return; }

        var userStore = context.RequestServices.GetService<IUserStore<StyloBotUser>>() as StyloBotUserStore;
        if (userStore == null) { context.Response.StatusCode = 404; return; }

        var count = await userStore.GetUserCountAsync(context.RequestAborted);
        if (count > 0)
        {
            _usersExist = true;
            context.Response.StatusCode = 404;
            return;
        }

        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSetupPostAsync(context);
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        var basePath = _options.BasePath.TrimEnd('/');
        var csrfToken = IssueSetupCsrfToken(context);
        await context.Response.WriteAsync($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>StyloBot Dashboard Setup</title>
            <style>{{AuthPageCss}}</style>
            </head>
            <body>
            <div class="card">
              <h1>StyloBot Dashboard</h1>
              <p>Create the first admin account to secure your dashboard.</p>
              <form method="post" action="{{basePath}}/setup">
                <input type="hidden" name="{{SetupCsrfFormField}}" value="{{csrfToken}}">
                <label for="email">Email</label>
                <input type="email" id="email" name="email" required autocomplete="email">
                <label for="password">Password</label>
                <input type="password" id="password" name="password" required minlength="8" autocomplete="new-password">
                <label for="confirmPassword">Confirm Password</label>
                <input type="password" id="confirmPassword" name="confirmPassword" required minlength="8" autocomplete="new-password">
                <button type="submit">Create Admin Account</button>
              </form>
            </div>
            </body>
            </html>
            """);
    }

    private async Task HandleSetupPostAsync(HttpContext context)
    {
        await context.Request.ReadFormAsync(context.RequestAborted);
        var email = context.Request.Form["email"].FirstOrDefault()?.Trim();
        var password = context.Request.Form["password"].FirstOrDefault();
        var confirm = context.Request.Form["confirmPassword"].FirstOrDefault();

        var basePath = _options.BasePath.TrimEnd('/');

        // CSRF: double-submit token. A cross-site form post can neither read nor
        // set our SameSite=Strict cookie, so it can't supply a matching pair.
        if (!ValidateSetupCsrfToken(context))
        {
            _logger.LogWarning("StyloBot Dashboard: setup POST rejected - missing/mismatched CSRF token from {IP}",
                context.Connection.RemoteIpAddress);
            context.Response.Redirect($"{basePath}/setup?error=invalid");
            return;
        }

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password) || password != confirm)
        {
            context.Response.Redirect($"{basePath}/setup?error=invalid");
            return;
        }

        var userManager = context.RequestServices.GetService<UserManager<StyloBotUser>>();
        if (userManager == null)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Identity services not registered.");
            return;
        }

        // Serialise the count-recheck + create so two concurrent first-run POSTs
        // can't both observe zero users and both create an admin account.
        await SetupLock.WaitAsync(context.RequestAborted);
        try
        {
            var userStore = context.RequestServices.GetService<IUserStore<StyloBotUser>>() as StyloBotUserStore;
            if (userStore != null && await userStore.GetUserCountAsync(context.RequestAborted) > 0)
            {
                _usersExist = true;
                context.Response.StatusCode = 404;
                return;
            }

            var user = new StyloBotUser { UserName = email, Email = email, EmailConfirmed = true };
            var result = await userManager.CreateAsync(user, password);

            if (result.Succeeded)
            {
                _usersExist = true;
                _logger.LogInformation("StyloBot Dashboard: first admin account created.");
                context.Response.Redirect($"{basePath}/auth-ui");
            }
            else
            {
                var errors = RedactEmailFromIdentityErrors(result.Errors.Select(e => e.Description), email);
                context.Response.Redirect($"{basePath}/setup?error={Uri.EscapeDataString(errors)}");
            }
        }
        finally
        {
            SetupLock.Release();
        }
    }

    /// <summary>
    ///     Issues a fresh random token, stores it in a SameSite=Strict cookie and
    ///     returns it for embedding as a hidden form field. Hex-only output, safe
    ///     to interpolate into the HTML attribute.
    /// </summary>
    private string IssueSetupCsrfToken(HttpContext context)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        context.Response.Cookies.Append(SetupCsrfCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = _options.BasePath.TrimEnd('/') + "/setup",
            MaxAge = TimeSpan.FromMinutes(30)
        });
        return token;
    }

    /// <summary>
    ///     Joins identity error descriptions for round-tripping back through
    ///     the redirect query string, with the submitted email scrubbed out.
    ///     ASP.NET Core Identity descriptions can echo the email verbatim
    ///     ("Username 'a@b.com' is already taken."); without this guard the
    ///     address lands in the redirect URL and from there in access logs
    ///     and the browser history.
    /// </summary>
    internal static string RedactEmailFromIdentityErrors(IEnumerable<string> descriptions, string email)
    {
        if (string.IsNullOrEmpty(email))
            return string.Join(", ", descriptions);
        return string.Join(", ", descriptions.Select(d =>
            d.Replace(email, "[redacted]", StringComparison.OrdinalIgnoreCase)));
    }

    internal static bool ValidateSetupCsrfToken(HttpContext context)
    {
        var cookie = context.Request.Cookies[SetupCsrfCookieName];
        var form = context.Request.Form[SetupCsrfFormField].FirstOrDefault();
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(form)) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(cookie),
            Encoding.UTF8.GetBytes(form));
    }

    private async Task ServeLoginUiAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        var basePath = _options.BasePath.TrimEnd('/');
        var error = context.Request.Query["error"].FirstOrDefault();
        var errorHtml = string.IsNullOrEmpty(error) ? "" :
            $"<p class='error'>{System.Net.WebUtility.HtmlEncode(error)}</p>";

        await context.Response.WriteAsync($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>StyloBot Dashboard Login</title>
            <style>{{AuthPageCss}}</style>
            </head>
            <body>
            <div class="card">
              <h1>StyloBot Dashboard</h1>
              <p>Sign in to access the dashboard.</p>
              <div id="form-area">
                <label for="email">Email</label>
                <input type="email" id="email" autocomplete="email">
                <label for="password">Password</label>
                <input type="password" id="password" autocomplete="current-password">
                <button onclick="doLogin()">Sign In</button>
                {{errorHtml}}
                <p id="msg" class="error"></p>
              </div>
            </div>
            <script>
            async function doLogin() {
              const email = document.getElementById('email').value;
              const password = document.getElementById('password').value;
              const msg = document.getElementById('msg');
              msg.textContent = '';
              const res = await fetch('{{basePath}}/auth/login?useCookies=true', {
                method: 'POST',
                headers: {'Content-Type': 'application/json'},
                body: JSON.stringify({email, password})
              });
              if (res.ok) {
                window.location.href = '{{basePath}}';
              } else {
                const data = await res.json().catch(() => ({}));
                msg.textContent = data.detail || data.title || 'Invalid email or password.';
              }
            }
            document.addEventListener('keydown', e => { if (e.key === 'Enter') doLogin(); });
            </script>
            </body>
            </html>
            """);
    }
    private async Task ServeDashboardPageAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html";

        // Stamp the dashboard-viewed signal so the broadcaster keeps the aggregate
        // cache warm while someone is looking and parks when they stop (see the
        // idle-skip in DashboardSummaryBroadcaster). The HTML dashboard never hits
        // the /api/v1 surface, so without this the broadcaster's idle-skip can't tell
        // the dashboard is being viewed.
        _aggregateCache.MarkHit();

        // Allow same-origin iframing (e.g., LiveDemo page embedding the dashboard)
        context.Response.Headers.Remove("X-Frame-Options");
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        // Replace restrictive CSP with dashboard-appropriate one
        context.Response.Headers.Remove("Content-Security-Policy");
        var cspNonce = GetOrCreateCspNonce(context);
        var dashboardCsp = string.Join("; ",
            "default-src 'self'",
            "base-uri 'self'",
            "frame-ancestors 'self'",
            "object-src 'none'",
            // Flag thumbnails are vendored locally (wwwroot/flags/<cc>.svg, MIT-licensed
            // flag-icons set). No third-party image origins permitted -- this is a security
            // product, and our own copy says "zero CDN dependencies". blob: is required for
            // ApexCharts toolbar exports (URL.createObjectURL on a same-origin canvas blob).
            "img-src 'self' data: blob:",
            "font-src 'self' data:",
            $"style-src 'self' 'unsafe-inline'",
            // 'unsafe-eval' is required for Alpine.js's reactive-expression
            // evaluator (new AsyncFunction("...") under the hood). Removing
            // it silently broke every x-bind / x-show / x-if on the dashboard:
            // bindings throw EvalError on compile, Alpine swallows them, and
            // the operator just sees a sluggish/dead dashboard. Alpine offers
            // a CSP-safe build that uses a pre-compiled expression evaluator,
            // but switching to it is a separate piece of work because the
            // dashboard's x-bind syntax has to be hand-converted. Until then,
            // 'unsafe-eval' is the conscious trade. Nonce + 'self' still
            // prevents arbitrary external script loads.
            $"script-src 'self' 'nonce-{cspNonce}' 'unsafe-eval'",
            // worker-src for Monaco's language-service web workers (loaded from CDN as
            // blob: URLs after the AMD loader rewrites them). Safe to allow on the
            // dashboard origin since Monaco is the only consumer.
            "worker-src 'self' blob:",
            "connect-src 'self' ws: wss:");
        context.Response.Headers["Content-Security-Policy"] = dashboardCsp;

        var basePath = _options.BasePath.TrimEnd('/');

        // Back-compat: old ?tab=X URLs 301 to /{BasePath}/{X}. Only applies on
        // a bare /{BasePath} request (segmented path requests already carry
        // their own area in the URL).
        var rawRelative = (context.Request.Path.Value ?? "")
            .Substring(_options.BasePath.Length).TrimStart('/');
        var rawRelLower = rawRelative.ToLowerInvariant();
        var registry = context.RequestServices
            .GetRequiredService<UI.Dashboard.IDashboardRowRegistry>();

        if (string.IsNullOrEmpty(rawRelLower) || rawRelLower is "index" or "index.html")
        {
            var legacyTab = context.Request.Query["tab"].FirstOrDefault();
            if (!string.IsNullOrEmpty(legacyTab))
            {
                // "metrics" was the hardcoded pack tab before id-driven packs.
                // Post-M2 fallback is the V2 IA landing page (Traffic), not the
                // deleted Overview row.
                if (legacyTab.Equals("metrics", StringComparison.OrdinalIgnoreCase))
                {
                    legacyTab = registry.Packs.Count > 0 ? registry.Packs[0].Id : "traffic";
                }
                var qs = UI.Dashboard.DashboardRoutingHelpers.StripTabParam(
                    context.Request.QueryString.Value ?? "");
                context.Response.Redirect($"{basePath}/{legacyTab}{qs}", permanent: true);
                return;
            }
        }

        var rowRef = UI.Dashboard.DashboardRoutingHelpers.ParseRowRef(rawRelative);

        // Pack bare-id: 301 to the pack's first sub-row.
        if (rowRef.Sub is null)
        {
            var pack = registry.Packs.FirstOrDefault(p =>
                string.Equals(p.Id, rowRef.Area, StringComparison.OrdinalIgnoreCase));
            if (pack is not null && pack.SubRows.Count > 0)
            {
                context.Response.Redirect(
                    $"{basePath}/{pack.Id}/{pack.SubRows[0].Id}{context.Request.QueryString.Value}",
                    permanent: true);
                return;
            }
        }

        // tab is the legacy local variable used by many string compares further down.
        // Keep it as an alias of rowRef.Area to minimize touched lines below.
        var tab = rowRef.Area;

        // Build all partial models server-side - fully rendered, no JSON serialization needed.
        // Compose the same Traffic bundle used by the TrafficController first. The
        // middleware owns /dashboard/visitors, so the MVC VisitorsController cannot
        // supply this data on hosts where the dashboard middleware is enabled.
        DashboardPageResult? composedPage = null;
        if (_contentCache is not null && _manifests is not null)
        {
            try
            {
                var trafficManifest = _manifests.For("dashboard.traffic");
                if (trafficManifest is not null)
                {
                    var candidatePage = await _contentCache.GetCurrentAsync(
                        trafficManifest,
                        new DashboardPageWindow(
                            StartTime: DateTime.UtcNow.AddHours(-24),
                            EndTime: DateTime.UtcNow,
                            AudienceFilter: "all",
                            ProbMin: null,
                            Domains: null,
                        TopN: 500,
                        BucketMinutes: 60),
                        context.RequestAborted);

                    // Remote compose can degrade to a non-null bundle with empty slices
                    // when the gateway endpoint is unavailable or returns an incomplete
                    // response. Do not stash that as authoritative: view components treat
                    // non-null empty lists as a successful read and skip their populated
                    // IDashboardEventStore fallback. The direct store path is the same
                    // source used by the working Traffic render on remote hosts.
                    if (candidatePage.Summary is not null
                        && candidatePage.BotAggregate is { Count: > 0 }
                        && candidatePage.Geo is { Count: > 0 })
                    {
                        composedPage = candidatePage;
                        context.Items["sb.dashboard.pageresult"] = candidatePage;
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Visitors shell page bundle was incomplete; using direct event-store reads");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Visitors shell page-bundle compose failed; using event-store fallback");
            }
        }

        // Visitor list reads through the composed bundle when available. The
        // event-store path remains a safe fallback for lightweight hosts.
        var sigCacheForSummary = context.RequestServices.GetService<SignatureAggregateCache>();
        var visitorTask = _eventStore.GetTopBotsAsync(
            count: _visitorListMaxEntries,
            startTime: DateTime.UtcNow.AddHours(-24),
            endTime: DateTime.UtcNow,
            audienceFilter: "all");
        Task<DashboardSummary> summaryTask = composedPage?.Summary is { } composedSummary
            ? Task.FromResult(composedSummary)
            : _aggregateCache.Current.Summary is { } cachedSummary
            ? Task.FromResult(cachedSummary)
            : SafeGetSummaryAsync();
        var countriesTask = SafeGetCountriesDataAsync();
        var endpointsTask = SafeGetEndpointsDataAsync(context);
        var userAgentsTask = _aggregateCache.Current.UserAgents.Count > 0
            ? Task.FromResult(_aggregateCache.Current.UserAgents)
            : ComputeUserAgentsFallbackAsync();

        await Task.WhenAll(visitorTask, summaryTask, countriesTask, endpointsTask, userAgentsTask);
        var visitorRaw = composedPage?.BotAggregate ?? visitorTask.Result;
        var (visitors, visitorTotal, visitorCounts) = WidgetRenderHelpers.ProjectAsVisitors(
            visitorRaw, filter: "all", sortField: "lastSeen", sortDir: "desc", page: 1, pageSize: 24);

        // Plan task 19: enrich the first-paint visitor slice with the
        // gateway-projected drift badge. Lives next to the projection so
        // the SSR'd dashboard shell stays in agreement with the HTMX-swap
        // re-renders below (which all share Services.FingerprintDriftProjector
        // for one consistent enrichment contract).
        await Services.FingerprintDriftProjector.EnrichVisitorsAsync(
            visitors, context.RequestServices, context.RequestAborted, _logger);

        var summary = summaryTask.Result;
        var countriesData = composedPage?.Geo?.ToList() ?? countriesTask.Result;
        var endpointsData = endpointsTask.Result;
        var allUserAgents = userAgentsTask.Result;

        // M2: investigate surface deleted. The Investigation view model on the
        // shell model now always renders as null; readers (Index.cshtml's
        // partial dispatch + DashboardShellModel consumers) treat null as
        // "skip the block", which is the legacy behaviour for every non-
        // investigate tab.
        ShapeInvestigationViewModel? investigationVm = null;

        var yourDetectionTask = BuildYourDetectionPartialModel(context);
        var clustersTask = BuildClustersModelAsync(context);
        var topBotsTask = BuildTopBotsModel(page: 1, pageSize: 10, sortBy: "default", sortDir: "desc");
        var sessionsTask = BuildSessionsModel(context);
        var threatsTask = BuildThreatsModelAsync();
        await Task.WhenAll(yourDetectionTask, clustersTask, topBotsTask, sessionsTask, threatsTask);

        var model = new DashboardShellModel
        {
            CspNonce = cspNonce,
            BasePath = basePath,
            HubPath = _options.HubPath,
            ActiveRow = rowRef,
            Version = DashboardVersion,
            RenderShell = _options.RenderShell,
            Summary = BuildSummaryStatsModelFromVisitorCache(summary, basePath, sigCacheForSummary),
            Visitors = new VisitorListModel
            {
                Visitors = visitors, Counts = visitorCounts,
                Filter = "all", SortField = "lastSeen", SortDir = "desc",
                Page = 1, PageSize = 24, TotalCount = visitorTotal, BasePath = basePath
            },
            YourDetection = yourDetectionTask.Result,
            Countries = BuildCountriesModel("total", "desc", 1, 20, countriesData),
            Endpoints = BuildEndpointsModel(context, "total", "desc", 1, 20, endpointsData),
            Clusters = clustersTask.Result,
            UserAgents = BuildUserAgentsModel("all", "requests", "desc", 1, 25, allUserAgents),
            TopBots = topBotsTask.Result,
            Sessions = sessionsTask.Result,
            Threats = threatsTask.Result,
            License = BuildLicenseCardModel(context),
            // Only build the editor model when the operator is on the Configuration tab --
            // listing all 30+ embedded manifests on every dashboard render is wasteful.
            Configuration = tab.Equals("configuration", StringComparison.OrdinalIgnoreCase)
                ? await BuildConfigurationModelAsync(context)
                : null,
            // Only run investigation queries when the operator is on the Investigate tab.
            Investigation = investigationVm,
            IsCommercial = IsCommercialMode(context),
            StatusStrip = BuildStatusStripModel(context),
            Compliance = tab.Equals("compliance", StringComparison.OrdinalIgnoreCase)
                ? BuildComplianceTabModel(context)
                : null,
            Packs = registry.Packs,
            TunnelEnvironment = context.RequestServices
                .GetService<BotDetection.Proxy.ITunnelEnvironmentInspector>()?.GetSnapshot(),
            TunnelDocsUrl = context.RequestServices
                .GetService<Microsoft.Extensions.Options.IOptions<BotDetection.Models.BotDetectionOptions>>()
                ?.Value.TunnelEnvironment.DocsUrl
                ?? "https://stylo.bot/articles/tunnel-trade-off"
        };

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/Index.cshtml", model, context, isMainPage: true);
        await context.Response.WriteAsync(html);

        async Task<DashboardSummary> SafeGetSummaryAsync()
        {
            try { return await _eventStore.GetSummaryAsync(); }
            catch { return new DashboardSummary { Timestamp = DateTime.UtcNow, TotalRequests = 0, BotRequests = 0, HumanRequests = 0, UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(), UniqueSignatures = 0 }; }
        }

        async Task<List<DashboardCountryStats>> SafeGetCountriesDataAsync()
        {
            try { return await GetCountriesDataAsync(); }
            catch { return []; }
        }

        async Task<List<DashboardEndpointStats>> SafeGetEndpointsDataAsync(HttpContext requestContext)
        {
            try { return await GetEndpointsDataAsync(requestContext); }
            catch { return []; }
        }
    }

    /// <summary>
    ///     Builds the "You" panel JSON by looking up the current visitor in the cache.
    ///     Since /_stylobot is excluded from detection, we compute the visitor's signature
    ///     using MultiFactorSignatureService and look them up in SignatureAggregateCache.
    /// </summary>
    private async Task<string> BuildYourDetectionJson(HttpContext context)
    {
        try
        {
            var sigService = context.RequestServices.GetService(typeof(MultiFactorSignatureService))
                as MultiFactorSignatureService;
            var signatureCache = context.RequestServices.GetService(typeof(SignatureAggregateCache))
                as SignatureAggregateCache;

            // sigService and signatureCache are OPTIONAL on remote-mode dashboard
            // viewer hosts. Hydrator-populated Items + the event-store fallback
            // below are sufficient; only short-circuit when we have NO source of
            // signatures at all (resolved below after the lookup chain).

            // Read the signature set the orchestrator's foundation wave wrote; fall back to
            // a fresh compute when no detection ran on this request. Same dual-path lookup
            // as BuildYourDetectionPartialModel -- the forwarded-headers hydrator path is
            // what remote-mode hosts use, the AggregatedEvidence path is the in-process
            // detection path.
            MultiFactorSignatures? sigs = null;
            if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
                && evObj is AggregatedEvidence ev
                && ev.Signals.TryGetValue(SignalKeys.SignatureMultifactor, out var multiObj)
                && multiObj is MultiFactorSignatures m)
            {
                sigs = m;
            }
            if (sigs is null
                && context.Items.TryGetValue(SignalKeys.SignatureMultifactor, out var hydMulti)
                && hydMulti is MultiFactorSignatures hm)
            {
                sigs = hm;
            }
            // Local fallback only when the detection-pipeline service is present
            // (single-binary gateway hosts). Pure dashboard-viewer hosts have no
            // sigService; if neither AggregatedEvidence nor the hydrator populated
            // sigs by this point, the gateway never tagged the request and there
            // is nothing useful to show.
            if (sigs is null && sigService is not null)
                sigs = sigService.GenerateSignatures(context);
            if (sigs is null)
                return "null";

            // SINGLE SOURCE OF TRUTH: the headline score is the fingerprint's
            // persisted verdict (CachedBotProbability) via IFingerprintReader —
            // the SAME value the Your-Detection card + signature-detail page read.
            // The SignatureAggregateCache is used only for display enrichment
            // (narrative / name / reasons), NEVER the headline number — it's a
            // cross-mode average that disagreed with the fingerprint.
            var fpVerdict = await ResolveFingerprintVerdictAsync(context);
            var visitor = signatureCache?.GetVisitor(sigs.PrimarySignature);

            if (fpVerdict is not null || visitor != null)
            {
                var headlineProb = fpVerdict?.Probability ?? visitor!.BotProbability;
                var headlineRisk = fpVerdict?.RiskBand ?? visitor?.RiskBand;
                var headlineConf = visitor?.Confidence ?? fpVerdict?.Confidence ?? 0.0;

                var narrativeEvent = new DashboardDetectionEvent
                {
                    RequestId = "self",
                    Timestamp = visitor?.LastSeen ?? fpVerdict?.UpdatedAt ?? DateTime.UtcNow,
                    IsBot = headlineProb >= 0.5,
                    BotProbability = headlineProb,
                    Confidence = headlineConf,
                    RiskBand = headlineRisk ?? "Unknown",
                    BotType = visitor?.BotType,
                    BotName = visitor?.BotName ?? fpVerdict?.Name,
                    Action = visitor?.Action,
                    Method = "GET",
                    Path = visitor?.LastPath ?? "/",
                    TopReasons = visitor?.TopReasons ?? new List<string>()
                };
                var narrative = DetectionNarrativeBuilder.Build(narrativeEvent);

                var yourDetection = new
                {
                    isBot = headlineProb >= 0.5,
                    botProbability = Math.Round(headlineProb, 4),
                    confidence = Math.Round(headlineConf, 4),
                    riskBand = headlineRisk,
                    processingTimeMs = visitor?.ProcessingTimeMs ?? 0.0,
                    detectorCount = visitor?.TopReasons.Count ?? 0,
                    narrative = visitor?.Narrative ?? narrative,
                    topReasons = visitor?.TopReasons ?? new List<string>(),
                    signature = sigs.PrimarySignature,
                    scoreUpdatedAt = fpVerdict?.UpdatedAt,
                    threatScore = visitor?.ThreatScore.HasValue == true ? Math.Round(visitor.ThreatScore.Value, 3) : (double?)null,
                    threatBand = visitor?.ThreatBand
                };

                return JsonSerializer.Serialize(yourDetection, CamelCaseJson);
            }

            // Visitor cache miss -- fall through to the event store so remote-mode hosts
            // (no in-process DetectionBroadcastMiddleware to write the cache) get back a
            // populated /api/me JSON instead of a signature-only stub. Same hydration
            // pattern BuildYourDetectionPartialModel uses for the dashboard pill.
            try
            {
                var detections = await _eventStore.GetDetectionsAsync(
                    new DashboardFilter { SignatureId = sigs.PrimarySignature, Limit = 1 },
                    context.RequestAborted);
                if (detections.Count > 0)
                {
                    var d = detections[0];
                    var narrative = DetectionNarrativeBuilder.Build(d);
                    var hydrated = new
                    {
                        isBot = d.IsBot,
                        botProbability = Math.Round(d.BotProbability, 4),
                        confidence = Math.Round(d.Confidence, 4),
                        riskBand = d.RiskBand ?? "Unknown",
                        processingTimeMs = d.ProcessingTimeMs,
                        detectorCount = d.TopReasons?.Count ?? 0,
                        narrative,
                        topReasons = d.TopReasons ?? new List<string>(),
                        signature = sigs.PrimarySignature,
                        threatScore = (double?)null,
                        threatBand = (string?)null
                    };
                    return JsonSerializer.Serialize(hydrated, CamelCaseJson);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dashboard: /api/me event-store hydration failed");
            }

            return JsonSerializer.Serialize(new { signature = sigs.PrimarySignature }, CamelCaseJson);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dashboard: failed to build your detection from cache");
            return "null";
        }
    }

    /// <summary>
    ///     Sentinel endpoint: returns the current visitor's cached detection as JSON.
    ///     Called by the dashboard when the initial page load has no yourData (first visit).
    /// </summary>
    private async Task ServeMeApiAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json";
        var json = await BuildYourDetectionJson(context);
        await context.Response.WriteAsync(json);
    }

    /// <summary>
    ///     <c>POST /api/labels/{signature}</c> upserts a label. Body JSON:
    ///     <c>{ "kind": "bot|human|benign-bot|uncertain", "confidence"?: 0..1, "note"?: string }</c>.
    ///     <c>GET /api/labels/{signature}</c> returns the most recent label for that signature, or 404.
    ///     <c>DELETE /api/labels/{signature}</c> removes the caller's label.
    /// </summary>
    private async Task ServeSignatureLabelApiAsync(HttpContext context, string signature)
    {
        var labelStore = context.RequestServices.GetService(typeof(ISignatureLabelStore)) as ISignatureLabelStore;
        if (labelStore is null)
        {
            context.Response.StatusCode = 501;
            await context.Response.WriteAsync("ISignatureLabelStore not registered");
            return;
        }

        signature = Uri.UnescapeDataString(signature).Trim();
        if (string.IsNullOrWhiteSpace(signature))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("signature is required");
            return;
        }

        context.Response.ContentType = "application/json";

        if (HttpMethods.IsGet(context.Request.Method))
        {
            var existing = await labelStore.GetLatestAsync(signature, context.RequestAborted);
            if (existing is null) { context.Response.StatusCode = 404; return; }
            await JsonSerializer.SerializeAsync(context.Response.Body, existing, CamelCaseJson);
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            var labeler = ResolveLabeler(context);
            await labelStore.RemoveAsync(signature, labeler, context.RequestAborted);
            context.Response.StatusCode = 204;
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = 405;
            return;
        }

        LabelPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<LabelPayload>(
                context.Request.Body, CamelCaseJson, context.RequestAborted);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("malformed JSON body");
            return;
        }

        if (payload is null || !TryParseLabelKind(payload.Kind, out var kind))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("kind must be one of: bot, human, benign-bot, uncertain");
            return;
        }

        var label = new SignatureLabel
        {
            Signature = signature,
            Kind = kind,
            Confidence = Math.Clamp(payload.Confidence ?? 1.0, 0.0, 1.0),
            LabeledBy = ResolveLabeler(context),
            LabeledAt = DateTime.UtcNow,
            Note = string.IsNullOrWhiteSpace(payload.Note) ? null : payload.Note.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(payload.DisplayName) ? null : payload.DisplayName.Trim()
        };

        var saved = await labelStore.UpsertAsync(label, context.RequestAborted);
        _logger.LogInformation(
            "Signature labeled: {Sig} → {Kind} by {Labeler}",
            signature[..Math.Min(12, signature.Length)], kind, label.LabeledBy);

        await JsonSerializer.SerializeAsync(context.Response.Body, saved, CamelCaseJson);
    }

    /// <summary>
    ///     <c>GET /api/labels?since=…&amp;limit=…</c> - bulk export for offline weight-tuning.
    ///     Output is the raw <see cref="SignatureLabel"/> list; pair with <c>/api/signatures</c>
    ///     to get each signature's detection context.
    /// </summary>
    private async Task ServeLabelsListApiAsync(HttpContext context)
    {
        var labelStore = context.RequestServices.GetService(typeof(ISignatureLabelStore)) as ISignatureLabelStore;
        if (labelStore is null) { context.Response.StatusCode = 501; return; }

        DateTime? since = null;
        var sinceStr = context.Request.Query["since"].FirstOrDefault();
        if (!string.IsNullOrEmpty(sinceStr) && DateTime.TryParse(sinceStr, out var s))
            since = s.ToUniversalTime();

        var limit = int.TryParse(context.Request.Query["limit"].FirstOrDefault(), out var l)
            ? Math.Clamp(l, 1, 10_000)
            : 1000;

        var labels = await labelStore.ListSinceAsync(since, limit, context.RequestAborted);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, labels, CamelCaseJson);
    }

    /// <summary>
    ///     <c>GET /api/labels/counts</c> - count per label kind for the dashboard badge.
    /// </summary>
    private async Task ServeLabelsCountsApiAsync(HttpContext context)
    {
        var labelStore = context.RequestServices.GetService(typeof(ISignatureLabelStore)) as ISignatureLabelStore;
        if (labelStore is null) { context.Response.StatusCode = 501; return; }

        var counts = await labelStore.GetCountsAsync(context.RequestAborted);
        context.Response.ContentType = "application/json";
        // Serialise with enum names rather than integers for readability.
        var payload = counts.ToDictionary(k => k.Key.ToString(), v => v.Value);
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, CamelCaseJson);
    }

    // ==========================================
    // Fingerprint Approval API
    // ==========================================

    /// <summary>
    ///     <c>GET /api/approvals?limit=…</c> - list recent fingerprint approvals.
    /// </summary>
    private async Task ServeApprovalsListApiAsync(HttpContext context)
    {
        var store = context.RequestServices.GetService(typeof(IFingerprintApprovalStore)) as IFingerprintApprovalStore;
        if (store is null) { context.Response.StatusCode = 501; await context.Response.WriteAsync("IFingerprintApprovalStore not registered"); return; }

        var limit = int.TryParse(context.Request.Query["limit"].FirstOrDefault(), out var l)
            ? Math.Clamp(l, 1, 500) : 50;

        var approvals = await store.ListRecentAsync(limit, context.RequestAborted);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, approvals, CamelCaseJson);
    }

    /// <summary>
    ///     <c>GET/POST/DELETE /api/approvals/{signature}</c> - CRUD for fingerprint approvals.
    ///     POST can use an approval-id token (from X-SB-Approval-Id header) or direct signature.
    /// </summary>
    private async Task ServeFingerprintApprovalApiAsync(HttpContext context, string signatureOrAction)
    {
        var store = context.RequestServices.GetService(typeof(IFingerprintApprovalStore)) as IFingerprintApprovalStore;
        if (store is null) { context.Response.StatusCode = 501; await context.Response.WriteAsync("IFingerprintApprovalStore not registered"); return; }

        context.Response.ContentType = "application/json";

        // POST /api/approvals/by-token - approve via one-time token
        if (signatureOrAction.Equals("by-token", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(context.Request.Method))
        {
            await HandleApproveByTokenAsync(context, store);
            return;
        }

        var signature = Uri.UnescapeDataString(signatureOrAction).Trim();
        if (string.IsNullOrWhiteSpace(signature))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"signature is required\"}");
            return;
        }

        // GET - fetch approval
        if (HttpMethods.IsGet(context.Request.Method))
        {
            var existing = await store.GetAsync(signature, context.RequestAborted);
            if (existing is null) { context.Response.StatusCode = 404; return; }
            await JsonSerializer.SerializeAsync(context.Response.Body, existing, CamelCaseJson);
            return;
        }

        // DELETE - revoke approval
        if (HttpMethods.IsDelete(context.Request.Method))
        {
            var revoker = ResolveLabeler(context);
            await store.RevokeAsync(signature, revoker, context.RequestAborted);
            context.Response.StatusCode = 204;
            return;
        }

        // POST - create/update approval directly by signature
        if (HttpMethods.IsPost(context.Request.Method))
        {
            await HandleApproveDirectAsync(context, store, signature);
            return;
        }

        context.Response.StatusCode = 405;
    }

    private async Task HandleApproveByTokenAsync(HttpContext context, IFingerprintApprovalStore store)
    {
        ApprovalByTokenPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<ApprovalByTokenPayload>(
                context.Request.Body, CamelCaseJson, context.RequestAborted);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"malformed JSON body\"}");
            return;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.ApprovalId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"approvalId is required\"}");
            return;
        }

        // Consume the one-time token to get the associated signature
        var signature = await store.ConsumeApprovalTokenAsync(payload.ApprovalId, context.RequestAborted);
        if (signature is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Token not found, expired, or already used\"}");
            return;
        }

        var record = new ApprovalRecord
        {
            Signature = signature,
            Justification = payload.Justification?.Trim() ?? "Approved via token",
            ApprovedBy = ResolveLabeler(context),
            ApprovedAt = DateTimeOffset.UtcNow,
            ExpiresAt = payload.ExpiresInDays.HasValue
                ? DateTimeOffset.UtcNow.AddDays(payload.ExpiresInDays.Value)
                : null,
            LockedDimensions = payload.LockedDimensions ?? new Dictionary<string, string>()
        };

        await store.UpsertAsync(record, context.RequestAborted);
        _logger.LogInformation("Fingerprint approved via token: {Sig} by {By}", signature[..Math.Min(12, signature.Length)], record.ApprovedBy);
        await JsonSerializer.SerializeAsync(context.Response.Body, record, CamelCaseJson);
    }

    private async Task HandleApproveDirectAsync(HttpContext context, IFingerprintApprovalStore store, string signature)
    {
        ApprovalDirectPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<ApprovalDirectPayload>(
                context.Request.Body, CamelCaseJson, context.RequestAborted);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"malformed JSON body\"}");
            return;
        }

        var record = new ApprovalRecord
        {
            Signature = signature,
            Justification = payload?.Justification?.Trim() ?? "Manually approved",
            ApprovedBy = ResolveLabeler(context),
            ApprovedAt = DateTimeOffset.UtcNow,
            ExpiresAt = payload?.ExpiresInDays.HasValue == true
                ? DateTimeOffset.UtcNow.AddDays(payload.ExpiresInDays!.Value)
                : null,
            LockedDimensions = payload?.LockedDimensions ?? new Dictionary<string, string>()
        };

        await store.UpsertAsync(record, context.RequestAborted);
        _logger.LogInformation("Fingerprint approved directly: {Sig} by {By}", signature[..Math.Min(12, signature.Length)], record.ApprovedBy);
        await JsonSerializer.SerializeAsync(context.Response.Body, record, CamelCaseJson);
    }

    // API payload DTOs for fingerprint approvals
    private sealed record ApprovalByTokenPayload
    {
        public string? ApprovalId { get; init; }
        public string? Justification { get; init; }
        public Dictionary<string, string>? LockedDimensions { get; init; }
        public int? ExpiresInDays { get; init; }
    }

    private sealed record ApprovalDirectPayload
    {
        public string? Justification { get; init; }
        public Dictionary<string, string>? LockedDimensions { get; init; }
        public int? ExpiresInDays { get; init; }
    }

    /// <summary>
    ///     Resolve the labeler identity from request context. Falls back to "anonymous" when
    ///     no auth is configured - operators running on a private dashboard with no OIDC
    ///     still get useful single-operator corpora.
    /// </summary>
    private static string ResolveLabeler(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var name = context.User.FindFirst("email")?.Value
                       ?? context.User.FindFirst("preferred_username")?.Value
                       ?? context.User.Identity.Name;
            if (!string.IsNullOrEmpty(name)) return name;
        }
        // X-SB-Labeler header only honored when user is authenticated (prevents spoofing audit trail)
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var hdr = context.Request.Headers["X-SB-Labeler"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(hdr)) return hdr.Trim();
        }

        return "anonymous";
    }

    private static bool TryParseLabelKind(string? raw, out SignatureLabelKind kind)
    {
        kind = SignatureLabelKind.Uncertain;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "bot":         kind = SignatureLabelKind.Bot; return true;
            case "human":       kind = SignatureLabelKind.Human; return true;
            case "benign-bot":
            case "benignbot":
            case "good-bot":    kind = SignatureLabelKind.BenignBot; return true;
            case "uncertain":   kind = SignatureLabelKind.Uncertain; return true;
            default: return false;
        }
    }

    private sealed record LabelPayload(string? Kind, double? Confidence, string? Note, string? DisplayName);

    private async Task ServeDetectionsApiAsync(HttpContext context)
    {
        var filter = ParseFilter(context.Request.Query);
        var detections = await _eventStore.GetDetectionsAsync(filter);

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, detections, CamelCaseJson);
    }

    private async Task ServeSignaturesApiAsync(HttpContext context)
    {
        var limitStr = context.Request.Query["limit"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var l) ? Math.Clamp(l, 1, 1000) : 100;

        var offsetStr = context.Request.Query["offset"].FirstOrDefault();
        var offset = int.TryParse(offsetStr, out var o) ? Math.Max(0, o) : 0;

        var isBotStr = context.Request.Query["isBot"].FirstOrDefault();
        bool? isBot = bool.TryParse(isBotStr, out var b) ? b : null;

        var signatures = await _eventStore.GetSignaturesAsync(limit, offset, isBot);

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, signatures, CamelCaseJson);
    }

    private async Task ServeSummaryApiAsync(HttpContext context)
    {
        _aggregateCache.MarkHit();
        // Audience axis: cache snapshot is unfiltered. humans/bots require a
        // store hit so the SQL is_bot predicate gates the counts; otherwise
        // a humans-only API consumer sees full traffic.
        var audienceFilter = (context.Request.Query["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();
        var summary = audienceFilter is "humans" or "bots"
            ? await _eventStore.GetSummaryAsync(audienceFilter: audienceFilter)
            : _aggregateCache.Current.Summary ?? await _eventStore.GetSummaryAsync();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, summary, CamelCaseJson);
    }

    private async Task ServeTimeSeriesApiAsync(HttpContext context)
    {
        _aggregateCache.MarkHit();
        try
        {
            var startTimeStr = context.Request.Query["start"].FirstOrDefault();
            var endTimeStr = context.Request.Query["end"].FirstOrDefault();
            var bucketSizeStr = context.Request.Query["bucket"].FirstOrDefault() ?? "60";
            var audienceFilter = (context.Request.Query["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();

            var startTime = DateTime.TryParse(startTimeStr, out var start)
                ? start
                : DateTime.UtcNow.AddHours(-1);

            var endTime = DateTime.TryParse(endTimeStr, out var end)
                ? end
                : DateTime.UtcNow;

            var bucketSize = TimeSpan.FromSeconds(
                int.TryParse(bucketSizeStr, out var b) ? b : 60);

            // audienceFilter is plumbed through so the SQL predicate gates each bucket.
            var audienceArg = audienceFilter is "humans" or "bots" ? audienceFilter : null;
            var timeSeries = await _eventStore.GetTimeSeriesAsync(startTime, endTime, bucketSize, audienceArg);

            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, timeSeries, CamelCaseJson);
        }
        catch (Exception ex)
        {
            _ = ex; // Preserve no exception detail leakage to clients.
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body,
                new
                {
                    error = "Failed to retrieve time series data",
                    requestId = context.TraceIdentifier
                });
        }
    }

    private async Task ServeExportApiAsync(HttpContext context)
    {
        // Bulk export is the most expensive read surface; throttle per IP like
        // diagnostics so an authenticated client can't hammer the event store.
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTime.UtcNow;
        var (allowed, remaining) = CheckRateLimit(clientIp, now, _exportRateLimits, ExportRateLimit);
        context.Response.Headers["X-RateLimit-Limit"] = ExportRateLimit.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();
        if (!allowed)
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = ((int)RateLimitWindow.TotalSeconds).ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Rate limit exceeded\"}");
            return;
        }

        var format = context.Request.Query["format"].FirstOrDefault() ?? "json";
        var filter = ParseFilter(context.Request.Query);
        var detections = await _eventStore.GetDetectionsAsync(filter);

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "text/csv";
            context.Response.Headers["Content-Disposition"] = "attachment; filename=detections.csv";
            await WriteCsvAsync(context.Response.Body, detections);
        }
        else
        {
            context.Response.ContentType = "application/json";
            context.Response.Headers["Content-Disposition"] = "attachment; filename=detections.json";
            await JsonSerializer.SerializeAsync(context.Response.Body, detections, CamelCaseJson);
        }
    }

    /// <summary>
    ///     Diagnostics API endpoint with rate limiting (10 requests/minute per IP).
    ///     Returns comprehensive detection data for optimization and debugging.
    /// </summary>
    private async Task ServeDiagnosticsApiAsync(HttpContext context)
    {
        // Rate limit: 10 requests per minute per IP
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTime.UtcNow;

        var (allowed, remaining) = CheckRateLimit(clientIp, now);
        context.Response.Headers["X-RateLimit-Limit"] = DiagnosticsRateLimit.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();

        if (!allowed)
        {
            context.Response.StatusCode = 429;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, new
            {
                error = "Rate limit exceeded",
                limit = DiagnosticsRateLimit,
                windowSeconds = (int)RateLimitWindow.TotalSeconds,
                retryAfterSeconds = (int)RateLimitWindow.TotalSeconds
            });
            return;
        }

        // Build comprehensive diagnostics response
        var filter = ParseFilter(context.Request.Query);
        // Default to higher limit for diagnostics
        if (!filter.Limit.HasValue || filter.Limit > 500)
            filter = filter with { Limit = 500 };

        var summary = await _eventStore.GetSummaryAsync();
        var detections = await _eventStore.GetDetectionsAsync(filter);
        var signatures = await _eventStore.GetSignaturesAsync(200);

        // Get signature cache if available
        var signatureCache = context.RequestServices
            .GetService(typeof(SignatureAggregateCache)) as SignatureAggregateCache;

        var topBots = signatureCache?.GetTopBotVisitors(10);
        var filterCounts = signatureCache?.GetVisitorCounts();

        var diagnostics = new
        {
            generatedAt = now,
            summary,
            filterCounts,
            topBots = topBots?.Select(b => new
            {
                b.PrimarySignature,
                b.Hits,
                b.BotName,
                b.BotType,
                b.RiskBand,
                b.BotProbability,
                b.Confidence,
                b.Action,
                b.CountryCode,
                b.ProcessingTimeMs,
                b.MaxProcessingTimeMs,
                b.MinProcessingTimeMs,
                processingTimeHistory = b.ProcessingTimeHistory,
                botProbabilityHistory = b.BotProbabilityHistory,
                confidenceHistory = b.ConfidenceHistory,
                b.Narrative,
                topReasons = b.TopReasons,
                b.ThreatScore,
                b.ThreatBand
            }),
            detections = detections.Select(d => new
            {
                d.RequestId,
                d.Timestamp,
                d.IsBot,
                d.BotProbability,
                d.Confidence,
                d.RiskBand,
                d.BotType,
                d.BotName,
                d.Action,
                d.PolicyName,
                d.Method,
                d.Path,
                d.StatusCode,
                d.ProcessingTimeMs,
                d.PrimarySignature,
                d.CountryCode,
                d.Narrative,
                d.TopReasons,
                d.DetectorContributions,
                d.ImportantSignals,
                d.ThreatScore,
                d.ThreatBand
            }),
            signatures = signatures.Select(s => new
            {
                s.SignatureId,
                s.Timestamp,
                s.PrimarySignature,
                s.FactorCount,
                s.RiskBand,
                s.HitCount,
                s.IsKnownBot,
                s.BotName,
                s.ThreatScore,
                s.ThreatBand
            })
        };

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, diagnostics,
            CamelCaseJson);
    }

    private async Task ServeCountriesApiAsync(HttpContext context)
    {
        var countStr = context.Request.Query["count"].FirstOrDefault();
        var count = int.TryParse(countStr, out var c) ? Math.Clamp(c, 1, 50) : 20;

        var startTimeStr = context.Request.Query["start"].FirstOrDefault();
        var endTimeStr = context.Request.Query["end"].FirstOrDefault();
        DateTime? startTime = DateTime.TryParse(startTimeStr, out var st) ? st : null;
        DateTime? endTime = DateTime.TryParse(endTimeStr, out var et) ? et : null;
        var audienceFilter = (context.Request.Query["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();
        var humansBots = audienceFilter is "humans" or "bots";

        List<DashboardCountryStats> dbCountries;
        if (startTime.HasValue || endTime.HasValue || humansBots)
        {
            // Any axis beyond the default flat snapshot requires a store query so
            // the SQL is_bot / time predicates apply. The cache only covers the
            // no-filter, no-time case.
            dbCountries = await _eventStore.GetCountryStatsAsync(count, startTime, endTime,
                humansBots ? audienceFilter : null);
        }
        else
        {
            // Default request: serve from periodic cache.
            var cached = _aggregateCache.Current.Countries;
            dbCountries = cached.Count > 0 ? cached : await _eventStore.GetCountryStatsAsync(count);
        }

        var countries = dbCountries
            .Take(count)
            .Select(db => new
            {
                countryCode = db.CountryCode,
                countryName = db.CountryName,
                botRate = db.BotRate,
                botCount = db.BotCount,
                humanCount = db.HumanCount,
                totalCount = db.TotalCount,
                flag = GetCountryFlag(db.CountryCode)
            })
            .ToList();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, countries, CamelCaseJson);
    }

    private async Task ServeCountryDetailApiAsync(HttpContext context, string countryCode)
    {
        if (string.IsNullOrEmpty(countryCode) || countryCode.Length != 2)
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Invalid country code\"}");
            return;
        }

        var startTimeStr = context.Request.Query["start"].FirstOrDefault();
        var endTimeStr = context.Request.Query["end"].FirstOrDefault();
        DateTime? startTime = DateTime.TryParse(startTimeStr, out var st) ? st : null;
        DateTime? endTime = DateTime.TryParse(endTimeStr, out var et) ? et : null;

        var detail = await _eventStore.GetCountryDetailAsync(countryCode, startTime, endTime);
        if (detail == null)
        {
            // Return 200 with empty object instead of 404.
            // Dashboard calls this for countries in the list; 404s feed responseBehavior detector.
            context.Response.ContentType = "application/json";
            var emptyDetail = new
            {
                countryCode,
                detections = Array.Empty<object>(),
                totalHits = 0
            };
            await JsonSerializer.SerializeAsync(context.Response.Body, emptyDetail, CamelCaseJson);
            return;
        }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, detail, CamelCaseJson);
    }

    private async Task ServeEndpointsApiAsync(HttpContext context)
    {
        var countStr = context.Request.Query["count"].FirstOrDefault();
        var count = int.TryParse(countStr, out var c) ? Math.Clamp(c, 1, 100) : 25;

        var startTimeStr = context.Request.Query["start"].FirstOrDefault();
        var endTimeStr = context.Request.Query["end"].FirstOrDefault();
        DateTime? startTime = DateTime.TryParse(startTimeStr, out var st) ? st : null;
        DateTime? endTime = DateTime.TryParse(endTimeStr, out var et) ? et : null;
        var audienceFilter = (context.Request.Query["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();
        var storeFilters = audienceFilter is "humans" or "bots" or "honeypot";

        List<DashboardEndpointStats> endpoints;
        if (startTime.HasValue || endTime.HasValue || storeFilters)
        {
            // honeypot is path-shape but still routes through the store so
            // IsHoneypot is populated per row by the path classifier and the
            // store applies the honeypot filter in-process post-query.
            endpoints = await _eventStore.GetEndpointStatsAsync(count, startTime, endTime,
                storeFilters ? audienceFilter : null);
        }
        else
        {
            var cached = _aggregateCache.Current.Endpoints;
            endpoints = cached.Count > 0 ? cached.Take(count).ToList() : await _eventStore.GetEndpointStatsAsync(count);
        }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, endpoints, CamelCaseJson);
    }

    private async Task ServeEndpointDetailApiAsync(HttpContext context, string encodedKey)
    {
        var decoded = Uri.UnescapeDataString(encodedKey);
        var split = decoded.Split('|', 2, StringSplitOptions.TrimEntries);
        if (split.Length != 2 || string.IsNullOrWhiteSpace(split[0]) || string.IsNullOrWhiteSpace(split[1]))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Invalid endpoint key\"}");
            return;
        }

        var detail = await _eventStore.GetEndpointDetailAsync(split[0], split[1]);
        if (detail == null)
        {
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, new { method = split[0], path = split[1], totalCount = 0 }, CamelCaseJson);
            return;
        }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, detail, CamelCaseJson);
    }

    private async Task ServeClustersApiAsync(HttpContext context)
    {
        var clusterService = context.RequestServices.GetService(typeof(IBotClusterReader))
            as IBotClusterReader;

        if (clusterService == null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]");
            return;
        }

        var clusters = (await clusterService.GetClustersAsync(context.RequestAborted))
            .Select(cl => new
            {
                clusterId = cl.ClusterId,
                label = cl.Label ?? "Unknown",
                description = cl.Description,
                type = cl.Type.ToString(),
                memberCount = cl.MemberCount,
                avgBotProb = Math.Round(cl.AverageBotProbability, 3),
                country = cl.DominantCountry,
                averageSimilarity = Math.Round(cl.AverageSimilarity, 3),
                temporalDensity = Math.Round(cl.TemporalDensity, 3),
                dominantIntent = cl.DominantIntent,
                averageThreatScore = Math.Round(cl.AverageThreatScore, 3)
            })
            .ToList();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, clusters,
            CamelCaseJson);
    }

    /// <summary>
    ///     Serves recent sessions from the session store.
    ///     Sessions are the unit of storage - each contains a compressed behavioral vector,
    ///     Markov transition counts, and summary stats.
    /// </summary>
    private async Task ServeSessionsApiAsync(HttpContext context)
    {
        var sessionStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.IDetectionArchive>();
        if (sessionStore == null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]");
            return;
        }

        var limit = int.TryParse(context.Request.Query["limit"], out var l) ? Math.Min(l, 100) : 50;
        var botFilter = context.Request.Query["isBot"].FirstOrDefault();
        bool? isBot = botFilter switch { "true" => true, "false" => false, _ => null };

        var since = DateTime.UtcNow - _options.DetectionRetention;
        var sessions = await sessionStore.GetRecentSessionsAsync(limit, isBot, since);

        var result = sessions.Select(s => new
        {
            s.Id,
            s.Signature,
            s.StartedAt,
            s.EndedAt,
            durationMinutes = (s.EndedAt - s.StartedAt).TotalMinutes,
            s.RequestCount,
            s.DominantState,
            s.IsBot,
            avgBotProbability = Math.Round(s.AvgBotProbability, 3),
            avgConfidence = Math.Round(s.AvgConfidence, 3),
            s.RiskBand,
            s.Action,
            s.BotName,
            s.BotType,
            s.CountryCode,
            s.ErrorCount,
            timingEntropy = Math.Round(s.TimingEntropy, 3),
            topReasons = s.TopReasonsJson != null
                ? JsonSerializer.Deserialize<List<string>>(s.TopReasonsJson)
                : null,
            transitionCounts = s.TransitionCountsJson != null
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson)
                : null,
            paths = s.PathsJson != null
                ? JsonSerializer.Deserialize<List<string>>(s.PathsJson)
                : null,
            s.Maturity,
            s.Narrative
        }).ToList();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, result, CamelCaseJson);
    }

    /// <summary>
    ///     Serves sessions for a specific signature, enabling drill-in from signature detail.
    /// </summary>
    private async Task ServeSignatureSessionsApiAsync(HttpContext context, string signature)
    {
        // If an ISessionDataSource is registered (e.g. GatewayApiSessionDataSource for API-only
        // deployments), delegate entirely to it and skip local session store logic.
        var dataSource = context.RequestServices.GetService<Mostlylucid.BotDetection.UI.Adapters.ISessionDataSource>();
        if (dataSource != null)
        {
            await dataSource.ServeAsync(context, signature, context.RequestAborted);
            return;
        }

        var sessionStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.IDetectionArchive>();
        if (sessionStore == null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]");
            return;
        }

        var decodedSignature = Uri.UnescapeDataString(signature);
        var limit = int.TryParse(context.Request.Query["limit"], out var l) ? Math.Min(l, 50) : 20;

        // Unified signature: PrimarySignature is used by both dashboard and session store
        var sessions = await sessionStore.GetSessionsAsync(decodedSignature, limit);

        // Compute inter-session velocity (behavioral drift between consecutive sessions)
        var sessionVectors = sessions
            .Select(s => s.Vector is { Length: > 0 }
                ? BotDetection.Data.SqliteDetectionArchive.DeserializeVector(s.Vector)
                : null)
            .ToList();

        var result = sessions.Select((s, idx) =>
        {
            var vec = sessionVectors[idx];
            var radarAxes = vec is { Length: > 0 }
                ? BotDetection.Analysis.VectorRadarProjection.Project(vec)
                : null;
            // 12-axis clock via the shared resolver -- same helper the Your Detection
            // card on the marketing site calls, so one signature's polygon renders
            // identically across both surfaces.
            var clockAxes = Mostlylucid.BotDetection.UI.Services.ClockAxesResolver.FromSessionVector(vec);

            return new
            {
                s.Id,
                s.StartedAt,
                s.EndedAt,
                durationMinutes = Math.Round((s.EndedAt - s.StartedAt).TotalMinutes, 1),
                s.RequestCount,
                s.DominantState,
                s.IsBot,
                avgBotProbability = Math.Round(s.AvgBotProbability, 3),
                s.RiskBand,
                s.ErrorCount,
                timingEntropy = Math.Round(s.TimingEntropy, 3),
                s.Maturity,
                live = false,
                // Inter-session velocity: L2 magnitude of the delta vector from previous session.
                // High velocity = sudden behavioral shift (bot rotation, account takeover, LLM-driven drift)
                velocity = (idx < sessions.Count - 1 && sessionVectors[idx] != null && sessionVectors[idx + 1] != null)
                    ? (double?)Math.Round(BotDetection.Analysis.SessionVectorizer.VelocityMagnitude(
                        BotDetection.Analysis.SessionVectorizer.ComputeVelocity(sessionVectors[idx]!, sessionVectors[idx + 1]!)), 3)
                    : null,
                // Markov chain for drill-in visualization
                transitionCounts = s.TransitionCountsJson != null
                    ? JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson)
                    : null,
                paths = s.PathsJson != null
                    ? JsonSerializer.Deserialize<List<string>>(s.PathsJson)
                    : null,
                // Radar projection for behavioral shape visualization
                radarAxes,
                // 12-axis clock, ready for direct ApexCharts series consumption
                clockAxes
            };
        }).ToList<object>();

        // Live in-progress session: prefer the in-memory accumulator (has a real session
        // vector for radar) when warm. Falls back to detection-event grouping below if the
        // accumulator is cold (e.g. post-restart) so the radar never blanks on the in-flight
        // entry just because the process restarted -- per "we never lose more than a couple
        // of seconds of data" the persisted detection stream is the source of truth.
        var liveAdded = false;
        var liveSessionStore = context.RequestServices.GetService<BotDetection.Analysis.SessionStore>();
        if (liveSessionStore?.GetCurrentSession(decodedSignature) is { Count: >= 1 } liveSession)
        {
            var liveVector = BotDetection.Analysis.SessionVectorizer.Encode(liveSession);
            var liveRadar = BotDetection.Analysis.VectorRadarProjection.Project(liveVector);
            var dominantState = liveSession
                .GroupBy(r => r.State)
                .OrderByDescending(g => g.Count())
                .First().Key;

            result.Insert(0, new
            {
                Id = "live",
                StartedAt = liveSession[0].Timestamp,
                EndedAt = liveSession[^1].Timestamp,
                durationMinutes = Math.Round((liveSession[^1].Timestamp - liveSession[0].Timestamp).TotalMinutes, 1),
                RequestCount = liveSession.Count,
                DominantState = dominantState.ToString(),
                IsBot = false,
                avgBotProbability = 0.0,
                RiskBand = "Unknown",
                ErrorCount = 0,
                timingEntropy = 0.0,
                Maturity = BotDetection.Analysis.SessionVectorizer.ComputeMaturity(liveSession),
                live = true,
                transitionCounts = (Dictionary<string, int>?)null,
                paths = liveSession.Select(r => r.PathTemplate).Distinct().ToList(),
                radarAxes = liveRadar,
                clockAxes = Mostlylucid.BotDetection.UI.Services.ClockAxesResolver.FromSessionVector(liveVector)
            });
            liveAdded = true;
        }

        // Detection-fallback: if no live session was added from the in-memory accumulator,
        // synthesise from persisted detections. Runs whenever the accumulator is cold,
        // regardless of whether finalised sessions exist -- restart must not blank the
        // in-flight portion of the radar. When no finalised sessions exist either, the
        // fallback also fills in past activity periods so a low-traffic signature still
        // has a behavioral shape to render.
        if (!liveAdded)
        {
            var eventStore = context.RequestServices.GetService<IDashboardEventStore>();
            if (eventStore != null)
            {
                var detections = await eventStore.GetDetectionsAsync(new DashboardFilter
                {
                    SignatureId = decodedSignature,
                    Limit = limit
                });

                if (detections.Count > 0)
                {
                    var syntheticGroups = GroupDetectionsBySessionGap(detections);

                    // When finalised sessions exist, only synthesise the most-recent group
                    // (the in-flight one) and only when its start is after the newest
                    // finalised session's end -- older groups are already covered by the
                    // finalised rows in `result`.
                    var newestFinalisedEnd = sessions.Count > 0
                        ? sessions.Max(s => s.EndedAt)
                        : DateTime.MinValue;

                    var groupsToEmit = sessions.Count == 0
                        ? syntheticGroups.OrderByDescending(g => g[^1].Timestamp).ToList()
                        : syntheticGroups
                            .Where(g => g[0].Timestamp > newestFinalisedEnd)
                            .OrderByDescending(g => g[^1].Timestamp)
                            .Take(1)
                            .ToList();

                    for (var gi = 0; gi < groupsToEmit.Count; gi++)
                    {
                        var group = groupsToEmit[gi];
                        var avgProb = group.Average(d => d.BotProbability);
                        var dominantRisk = group
                            .GroupBy(d => d.RiskBand)
                            .OrderByDescending(g => g.Count())
                            .First().Key;
                        var paths = group.Select(d => d.Path).Distinct().ToList();

                        // Build 8-axis radar: prefer 16-dim RadarShape; fall back to
                        // aggregating detector contributions (available on all detections).
                        double[]? radarAxes = null;
                        var withShape = group.Where(d => d.RadarShape is { Length: 16 }).ToList();
                        if (withShape.Count > 0)
                        {
                            var avgShape = new float[16];
                            foreach (var d in withShape)
                                for (var i = 0; i < 16; i++) avgShape[i] += d.RadarShape![i];
                            for (var i = 0; i < 16; i++) avgShape[i] /= withShape.Count;
                            radarAxes = ProjectDetectionRadarTo8Axes(avgShape);
                        }
                        else
                        {
                            // Fallback: build radar from detector_contributions using the
                            // SearchProjection axis map (16-dim indices) then project to 8 axes.
                            var accumulated = new float[16];
                            var accumCount = 0;
                            foreach (var d in group.Where(d => d.DetectorContributions is { Count: > 0 }))
                            {
                                foreach (var (name, contrib) in d.DetectorContributions!)
                                {
                                    var axisIdx = MapDetectorNameToRadarDim(name);
                                    if (axisIdx >= 0) accumulated[axisIdx] += (float)Math.Abs(contrib.Contribution);
                                }
                                accumCount++;
                            }
                            if (accumCount > 0)
                            {
                                for (var i = 0; i < 16; i++) accumulated[i] /= accumCount;
                                radarAxes = ProjectDetectionRadarTo8Axes(accumulated);
                            }
                        }

                        // gi==0 = most-recent group, after newestFinalisedEnd ⇒ this is the
                        // in-flight session; mark live=true and prepend so the radar chart
                        // and session selector treat it as the active row.
                        var isLive = gi == 0;
                        var entry = new
                        {
                            Id = isLive ? "live" : $"det-{group[0].Timestamp:yyyyMMddHHmm}",
                            StartedAt = group[0].Timestamp,
                            EndedAt = group[^1].Timestamp,
                            durationMinutes = Math.Round((group[^1].Timestamp - group[0].Timestamp).TotalMinutes, 1),
                            RequestCount = group.Count,
                            DominantState = dominantRisk,
                            IsBot = avgProb >= 0.5,
                            avgBotProbability = Math.Round(avgProb, 3),
                            RiskBand = dominantRisk,
                            ErrorCount = 0,
                            timingEntropy = 0.0,
                            Maturity = 0.0,
                            live = isLive,
                            velocity = (object?)null,
                            transitionCounts = (Dictionary<string, int>?)null,
                            paths,
                            radarAxes,
                            // Synthetic entry has no session vector yet → markov hours = 0.
                            clockAxes = Mostlylucid.BotDetection.UI.Services.ClockProjection.Compose12Axes(
                                radarAxes ?? new double[8],
                                new double[] { 0, 0, 0, 0 })
                        };
                        if (isLive) result.Insert(0, entry);
                        else result.Add(entry);
                    }
                }
            }
        }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, result, CamelCaseJson);
    }

    // Detector name → 16-dim RadarDimensions index (matches SearchProjection.DetectorAxisMap)
    private static readonly Dictionary<string, int> DetectorToRadarDimMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UserAgent"] = 0,           // ua_anomaly
        ["Header"] = 1,              // header_anomaly
        ["HeaderCorrelation"] = 1,   // header_anomaly
        ["Ip"] = 2,                  // ip_reputation
        ["FastPathReputation"] = 2,  // ip_reputation
        ["ProjectHoneypot"] = 2,     // ip_reputation
        ["Behavioral"] = 3,          // behavioral
        ["Heuristic"] = 3,           // behavioral
        ["HeuristicLate"] = 3,       // behavioral
        ["BehavioralWaveform"] = 3,  // behavioral
        ["SessionVector"] = 4,       // advanced_behavioral
        ["CacheBehavior"] = 5,       // cache_behavior
        ["ResourceWaterfall"] = 5,   // cache_behavior
        ["CookieBehavior"] = 5,      // cache_behavior
        ["SecurityTool"] = 6,        // security_tool
        ["Haxxor"] = 6,              // security_tool
        ["CveProbe"] = 6,            // security_tool
        ["CveFingerprint"] = 6,      // security_tool
        ["TlsFingerprint"] = 7,      // client_fingerprint
        ["Http2Fingerprint"] = 7,    // client_fingerprint
        ["Http3Fingerprint"] = 7,    // client_fingerprint
        ["TcpIpFingerprint"] = 7,    // client_fingerprint
        ["FingerprintApproval"] = 7, // client_fingerprint
        ["ClientSide"] = 7,          // client_fingerprint
        ["VersionAge"] = 8,          // version_age
        ["Inconsistency"] = 9,       // inconsistency
        ["TransportProtocol"] = 9,   // inconsistency
        ["ReputationBias"] = 10,     // reputation_match
        ["Similarity"] = 10,         // reputation_match
        ["VerifiedBot"] = 10,        // reputation_match
        ["Llm"] = 11,                // ai_classification
        ["AI"] = 11,                 // ai_classification
        ["AiScraper"] = 11,          // ai_classification
        ["ClusterContributor"] = 12, // cluster_signal
        ["MultiLayerCorrelation"] = 12, // cluster_signal
        ["GeoChange"] = 13,          // country_reputation
        ["StreamAbuse"] = 14,        // rate_pattern
        ["Intent"] = 14,             // rate_pattern
        ["ResponseBehavior"] = 15,   // payload_signature
        ["PiiQueryString"] = 15,     // payload_signature
    };

    private static int MapDetectorNameToRadarDim(string name)
    {
        // Try exact match first, then prefix match on the first capitalized word
        if (DetectorToRadarDimMap.TryGetValue(name, out var idx)) return idx;
        foreach (var (key, val) in DetectorToRadarDimMap)
            if (name.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return val;
        return -1;
    }

    /// <summary>
    ///     Projects a 16-dimensional RadarDimensions shape (from dashboard_detections) to the
    ///     8-axis radar used by the behavioral shape chart. Used when synthesizing session data
    ///     from detection records rather than finalized session vectors.
    /// </summary>
    private static double[] ProjectDetectionRadarTo8Axes(float[] shape16)
    {
        // shape16 index → RadarDimensions: 0=ua_anomaly, 1=header_anomaly, 2=ip_reputation,
        // 3=behavioral, 4=advanced_behavioral, 5=cache_behavior, 6=security_tool,
        // 7=client_fingerprint, 8=version_age, 9=inconsistency, 10=reputation_match,
        // 11=ai_classification, 12=cluster_signal, 13=country_reputation,
        // 14=rate_pattern, 15=payload_signature
        float R(int i) => i < shape16.Length ? shape16[i] : 0f;
        double Clamp(double v) => Math.Max(0.05, Math.Min(1.0, v));

        return
        [
            Clamp(R(3)),                          // 0: Browsing      ← behavioral
            Clamp(R(14)),                          // 1: API Activity  ← rate_pattern
            Clamp(Math.Max(R(6), R(15))),          // 2: Scan/Probe    ← security_tool, payload_signature
            Clamp(Math.Max(R(0), R(9))),           // 3: Auth Pressure ← ua_anomaly, inconsistency
            Clamp(R(4)),                           // 4: Timing Pattern← advanced_behavioral
            Clamp(Math.Max(R(14) * 0.5f, R(12))), // 5: Burst Speed   ← rate_pattern, cluster_signal
            Clamp(R(7)),                           // 6: Fingerprint   ← client_fingerprint
            Clamp(Math.Max(R(3), R(5))),           // 7: Path Diversity← behavioral, cache_behavior
        ];
    }

    /// <summary>Allowed values for sort parameter on top bots API (input validation).</summary>
    private static readonly HashSet<string> AllowedSortValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "hits", "name", "lastseen", "country", "probability"
    };

    private async Task ServeTopBotsApiAsync(HttpContext context)
    {
        var startTimeStr = context.Request.Query["start"].FirstOrDefault();
        var endTimeStr = context.Request.Query["end"].FirstOrDefault();
        DateTime? startTime = DateTime.TryParse(startTimeStr, out var st) ? st : null;
        DateTime? endTime = DateTime.TryParse(endTimeStr, out var et) ? et : null;

        List<DashboardTopBotEntry> topBots;
        if (startTime.HasValue || endTime.HasValue)
        {
            // Time-filtered: fall back to event store (historical data not in cache)
            var countParam = context.Request.Query["count"].FirstOrDefault();
            var count = int.TryParse(countParam, out var c) && c is > 0 and <= 100 ? c : 25;
            topBots = await _eventStore.GetTopBotsAsync(count, startTime, endTime);
        }
        else
        {
            // No time filter: read from write-through cache (single source of truth)
            var pageParam = context.Request.Query["page"].FirstOrDefault();
            var page = int.TryParse(pageParam, out var p) && p > 0 ? p : 1;

            var pageSizeParam = context.Request.Query["pageSize"].FirstOrDefault();
            var pageSize = int.TryParse(pageSizeParam, out var ps) && ps is > 0 and <= 100 ? ps : 25;

            var sortBy = context.Request.Query["sort"].FirstOrDefault();
            // Whitelist sort values to prevent parameter probing
            if (sortBy != null && !AllowedSortValues.Contains(sortBy))
                sortBy = null;

            var country = context.Request.Query["country"].FirstOrDefault();
            // Validate country code format: exactly 2 uppercase letters
            if (country != null && (country.Length != 2 || !country.All(char.IsLetter)))
                country = null;

            topBots = _signatureCache.GetTopBots(page, pageSize, sortBy, filterCountry: country);
        }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, topBots, CamelCaseJson);
    }

    private async Task ServeUserAgentsApiAsync(HttpContext context)
    {
        // Serve from periodic cache (computed by DashboardSummaryBroadcaster)
        var cached = _aggregateCache.Current.UserAgents;
        var result = cached.Count > 0
            ? cached
            : await ComputeUserAgentsFallbackAsync();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, result, CamelCaseJson);
    }

    /// <summary>Fallback for first request before beacon has run.</summary>
    private async Task<List<DashboardUserAgentSummary>> ComputeUserAgentsFallbackAsync()
    {
        var detections = await _eventStore.GetDetectionsAsync(new DashboardFilter { Limit = 500 });
        var uaGroups = new Dictionary<string, (int total, int bot, int human, double confSum, double procSum,
            DateTime lastSeen, Dictionary<string, int> versions, Dictionary<string, int> countries)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var d in detections)
        {
            string? family = null;
            string? version = null;
            if (d.ImportantSignals != null)
            {
                if (d.ImportantSignals.TryGetValue("ua.family", out var ff)) family = ff?.ToString();
                if (d.ImportantSignals.TryGetValue("ua.family_version", out var fv)) version = fv?.ToString();
                if (string.IsNullOrEmpty(family) && d.ImportantSignals.TryGetValue("ua.bot_name", out var bn))
                    family = bn?.ToString();
            }
            if (string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(d.UserAgent))
                family = DashboardUserAgentAggregator.ExtractBrowserFamily(d.UserAgent);
            if (string.IsNullOrEmpty(family)) family = "Unknown";

            if (!uaGroups.TryGetValue(family, out var g))
                g = (0, 0, 0, 0, 0, DateTime.MinValue,
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            g.total++;
            if (d.IsBot) g.bot++; else g.human++;
            g.confSum += d.Confidence;
            g.procSum += d.ProcessingTimeMs;
            if (d.Timestamp > g.lastSeen) g.lastSeen = d.Timestamp;
            if (!string.IsNullOrEmpty(version)) { g.versions.TryGetValue(version, out var vc); g.versions[version] = vc + 1; }
            if (!string.IsNullOrEmpty(d.CountryCode)) { g.countries.TryGetValue(d.CountryCode, out var cc); g.countries[d.CountryCode] = cc + 1; }
            uaGroups[family] = g;
        }

        return uaGroups.Select(kv => new DashboardUserAgentSummary
        {
            Family = kv.Key,
            Category = DashboardUserAgentAggregator.InferUaCategory(kv.Key),
            TotalCount = kv.Value.total, BotCount = kv.Value.bot, HumanCount = kv.Value.human,
            BotRate = kv.Value.total > 0 ? Math.Round((double)kv.Value.bot / kv.Value.total, 4) : 0,
            Versions = kv.Value.versions, Countries = kv.Value.countries,
            AvgConfidence = kv.Value.total > 0 ? Math.Round(kv.Value.confSum / kv.Value.total, 4) : 0,
            AvgProcessingTimeMs = kv.Value.total > 0 ? Math.Round(kv.Value.procSum / kv.Value.total, 2) : 0,
            LastSeen = kv.Value.lastSeen,
        }).OrderByDescending(u => u.TotalCount).ToList();
    }

    private static string GetCountryFlag(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 2 || code.Equals("XX", StringComparison.OrdinalIgnoreCase))
            return "\uD83C\uDF10"; // globe emoji
        var upper = code.ToUpperInvariant();
        return string.Concat(
            char.ConvertFromUtf32(0x1F1E6 + upper[0] - 'A'),
            char.ConvertFromUtf32(0x1F1E6 + upper[1] - 'A'));
    }

    /// <summary>
    ///     Serves sparkline history data for a specific signature.
    ///     Called on-demand by clients instead of broadcasting via SignalR.
    /// </summary>
    private async Task ServeSparklineApiAsync(HttpContext context, string signatureId)
    {
        if (string.IsNullOrEmpty(signatureId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing signature ID");
            return;
        }

        var decodedSignature = Uri.UnescapeDataString(signatureId);

        // Validate signature format: alphanumeric + base64url chars (-_+/=), max 64 chars
        if (decodedSignature.Length > 64 || !decodedSignature.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '+' or '/' or '='))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Invalid signature format\"}");
            return;
        }

        // Try SignatureAggregateCache first (write-through, always up to date)
        var sparklineFromCache = _signatureCache.GetSparkline(decodedSignature);
        if (sparklineFromCache != null)
        {
            var cachedSparkline = new
            {
                signatureId,
                botProbabilityHistory = sparklineFromCache
            };
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, cachedSparkline, CamelCaseJson);
            return;
        }

        // Fall back to SignatureAggregateCache (has richer history with processing times)
        var signatureCache = context.RequestServices
            .GetService(typeof(SignatureAggregateCache)) as SignatureAggregateCache;
        var visitor = signatureCache?.GetVisitor(decodedSignature);

        List<double> processingTimes, botProbabilities, confidences;

        // ProjectedVisitor is a per-call projection (SignatureAggregateCache.Project copies
        // the ring buffers under the aggregate's SyncRoot before returning), so the
        // history queues are already stable snapshots -- no lock needed here.
        if (visitor != null)
        {
            processingTimes = visitor.ProcessingTimeHistory.ToList();
            botProbabilities = visitor.BotProbabilityHistory.ToList();
            confidences = visitor.ConfidenceHistory.ToList();
        }
        else
        {
            // Fallback: build sparkline from DB detections
            var detections = await _eventStore.GetDetectionsAsync(new DashboardFilter
            {
                SignatureId = decodedSignature,
                Limit = 50
            });

            if (detections.Count == 0)
            {
                // Return 200 with empty arrays instead of 404.
                // 404s feed the responseBehavior detector - the dashboard calls sparkline
                // for every signature in the top-bots list, so unknown signatures would
                // generate hundreds of 404s that poison the user's own detection score.
                context.Response.ContentType = "application/json";
                var emptySparkline = new
                {
                    signatureId = decodedSignature,
                    botProbabilityHistory = Array.Empty<double>(),
                    processingTimeHistory = Array.Empty<double>(),
                    confidenceHistory = Array.Empty<double>()
                };
                await JsonSerializer.SerializeAsync(context.Response.Body, emptySparkline, CamelCaseJson);
                return;
            }

            // Detections come newest-first; reverse for chronological sparkline
            detections.Reverse();
            processingTimes = detections.Select(d => d.ProcessingTimeMs).ToList();
            botProbabilities = detections.Select(d => d.BotProbability).ToList();
            confidences = detections.Select(d => d.Confidence).ToList();
        }

        var sparkline = new
        {
            signatureId,
            processingTimeHistory = processingTimes,
            botProbabilityHistory = botProbabilities,
            confidenceHistory = confidences
        };

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, sparkline,
            CamelCaseJson);
    }

    /// <summary>
    ///     BDF export endpoint: generates a BDF v2 document for a specific signature.
    ///     Route: api/bdf/{signature}
    /// </summary>
    private async Task ServeBdfExportApiAsync(HttpContext context, string signatureId)
    {
        if (string.IsNullOrEmpty(signatureId))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Missing signature ID\"}");
            return;
        }

        var decodedSignature = Uri.UnescapeDataString(signatureId);

        // Validate signature format: alphanumeric + base64url chars, max 64 chars
        if (decodedSignature.Length > 64 || !decodedSignature.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '+' or '/' or '='))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Invalid signature format\"}");
            return;
        }

        var bdfService = context.RequestServices.GetService(typeof(BdfExportService)) as BdfExportService;
        if (bdfService == null)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"BDF export service not available\"}");
            return;
        }

        var document = await bdfService.ExportAsync(decodedSignature);
        if (document == null)
        {
            // Return 200 with empty object instead of 404 (same pattern as sparkline/country detail)
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"No detections found for this signature\"}");
            return;
        }

        context.Response.ContentType = "application/json";
        context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"bdf-{decodedSignature[..Math.Min(8, decodedSignature.Length)]}.json\"";
        context.Response.Headers["X-PII-Level"] = "none";
        await JsonSerializer.SerializeAsync(context.Response.Body, document, CamelCaseJson);
    }

    /// <summary>
    ///     Pack Metrics B8 -- <c>GET /api/v1/packs/policystack/summary</c>.
    ///     Returns per-mode rule counts + a 15-minute decision count + the
    ///     overall HealthBand for the policy stack. The C1 dashboard overview
    ///     row consumes this as the source of truth for the "Policy stack"
    ///     SbStatTile next to the AspNet one.
    ///     <para>
    ///         The builder degrades per <c>feedback_remote_mode_optional_di</c>:
    ///         a null <see cref="Mostlylucid.BotDetection.Policies.Rules.IPolicyRuleStore"/>
    ///         returns 404 ("not enabled on this host") so the dashboard can
    ///         hard-hide the row instead of fabricating zeros; a null
    ///         <see cref="Mostlylucid.BotDetection.Policies.Decisions.IPolicyDecisionLog"/>
    ///         keeps the row visible but emits <c>decisionsLast15m: null</c>.
    ///     </para>
    /// </summary>
    private async Task ServePolicyStackSummaryAsync(HttpContext context)
    {
        var builder = context.RequestServices
            .GetService(typeof(UI.Services.PolicyStackSummaryBuilder)) as UI.Services.PolicyStackSummaryBuilder;
        if (builder is null)
        {
            // Builder itself is missing: viewer host that never registered the
            // dashboard service extensions at all. Treat the same as a missing
            // rule store -- not enabled here.
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"Policy stack not enabled in this host\"}");
            return;
        }

        var summary = await builder.BuildAsync(context.RequestAborted);
        if (summary is null)
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"Policy stack not enabled in this host\"}");
            return;
        }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, summary, CamelCaseJson);
    }

    private static (bool Allowed, int Remaining) CheckRateLimit(string clientIp, DateTime now)
        => CheckRateLimit(clientIp, now, _rateLimits, DiagnosticsRateLimit);

    internal static (bool Allowed, int Remaining) CheckRateLimit(
        string clientIp, DateTime now,
        ConcurrentDictionary<string, (int Count, DateTime WindowStart)> store, int limit)
    {
        // Hard cap: reject new IPs when store is at capacity (prevents memory exhaustion)
        if (store.Count >= MaxRateLimitEntries && !store.ContainsKey(clientIp))
            return (false, 0);

        var entry = store.AddOrUpdate(clientIp,
            _ => (1, now),
            (_, existing) =>
            {
                if (now - existing.WindowStart > RateLimitWindow)
                    return (1, now); // New window
                return (existing.Count + 1, existing.WindowStart);
            });

        // Periodic cleanup: evict stale entries (single-thread guard to avoid contention)
        if (store.Count > 500 && Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) == 0)
        {
            try
            {
                foreach (var kv in store)
                {
                    if (now - kv.Value.WindowStart > RateLimitWindow)
                        store.TryRemove(kv.Key, out _);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _cleanupRunning, 0);
            }
        }

        var remaining = Math.Max(0, limit - entry.Count);
        return (entry.Count <= limit, remaining);
    }

    private DashboardFilter ParseFilter(IQueryCollection query)
    {
        var filter = new DashboardFilter();

        if (DateTime.TryParse(query["start"].FirstOrDefault(), out var start))
            filter = filter with { StartTime = start };

        if (DateTime.TryParse(query["end"].FirstOrDefault(), out var end))
            filter = filter with { EndTime = end };

        var riskBands = query["riskBands"].ToString();
        if (!string.IsNullOrEmpty(riskBands))
            filter = filter with { RiskBands = riskBands.Split(',').ToList() };

        if (bool.TryParse(query["isBot"].FirstOrDefault(), out var isBot))
            filter = filter with { IsBot = isBot };

        var pathContains = query["path"].FirstOrDefault();
        if (!string.IsNullOrEmpty(pathContains))
            filter = filter with { PathContains = pathContains };

        if (bool.TryParse(query["highRiskOnly"].FirstOrDefault(), out var highRisk))
            filter = filter with { HighRiskOnly = highRisk };

        var signatureId = query["signatureId"].FirstOrDefault();
        if (!string.IsNullOrEmpty(signatureId))
            filter = filter with { SignatureId = signatureId };

        if (int.TryParse(query["limit"].FirstOrDefault(), out var limit))
            filter = filter with { Limit = Math.Clamp(limit, 1, 100) };

        if (int.TryParse(query["offset"].FirstOrDefault(), out var offset))
            filter = filter with { Offset = Math.Max(0, offset) };

        return filter;
    }

    private async Task WriteCsvAsync(Stream stream, List<DashboardDetectionEvent> detections)
    {
        using var writer = new StreamWriter(stream, leaveOpen: true);

        // Header
        await writer.WriteLineAsync(
            "RequestId,Timestamp,IsBot,BotProbability,Confidence,RiskBand,BotType,BotName,Action,Method,Path,StatusCode,ProcessingTimeMs,ThreatScore,ThreatBand");

        // Rows
        foreach (var d in detections)
            await writer.WriteLineAsync(
                $"{EscapeCsv(d.RequestId)},{d.Timestamp:O},{d.IsBot},{d.BotProbability},{d.Confidence}," +
                $"{EscapeCsv(d.RiskBand)},{EscapeCsv(d.BotType)},{EscapeCsv(d.BotName)},{EscapeCsv(d.Action)},{EscapeCsv(d.Method)},{EscapeCsv(d.Path)}," +
                $"{d.StatusCode},{d.ProcessingTimeMs},{d.ThreatScore},{EscapeCsv(d.ThreatBand)}");
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // Prevent CSV injection: strip leading formula-trigger characters (=, +, -, @, \t, \r)
        // that could cause spreadsheet applications to execute formulas.
        var sanitized = value;
        while (sanitized.Length > 0 && sanitized[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            sanitized = sanitized[1..];
        if (sanitized.Contains(',') || sanitized.Contains('"') || sanitized.Contains('\n') || sanitized.Contains('\r'))
            return $"\"{sanitized.Replace("\"", "\"\"")}\"";
        return sanitized;
    }

    // ─── HTMX Partial Rendering ──────────────────────────────────────────

    /// <summary>Render the visitor list partial. Supports filter, sort, pagination via query params.</summary>
    private async Task ServeVisitorListPartialAsync(HttpContext context)
    {
        var q = context.Request.Query;
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sortField = q["sort"].FirstOrDefault() ?? "lastSeen";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(q["pageSize"].FirstOrDefault(), out var ps) && ps is > 0 and <= 100 ? ps : 24;

        // V1 (dashboard IA collapse plan): URL filter binding. Empty strings
        // collapse to null so the projector skips the corresponding clause.
        var country = NullIfEmpty(q["country"].FirstOrDefault());
        var botType = NullIfEmpty(q["bot_type"].FirstOrDefault());
        var threat = NullIfEmpty(q["threat"].FirstOrDefault());
        var fingerprint = NullIfEmpty(q["fingerprint"].FirstOrDefault());
        var internalOnly = string.Equals(q["internal"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

        // Read through the event store so remote-mode hosts (whose SignatureAggregateCache
        // may be empty -- no DetectionBroadcastMiddleware runs in-process) still get a
        // fresh cross-cutting top-N every partial render. ProjectAsVisitors mirrors
        // SignatureAggregateCache.GetFiltered's filter/sort/page semantics so the view
        // consumes the same shape.
        var raw = await _eventStore.GetTopBotsAsync(
            count: _visitorListMaxEntries,
            startTime: DateTime.UtcNow.AddHours(-24),
            endTime: DateTime.UtcNow,
            audienceFilter: "all");
        var (items, totalCount, counts) = WidgetRenderHelpers.ProjectAsVisitors(
            raw, filter, sortField, sortDir, page, pageSize,
            country: country, botType: botType, threat: threat,
            fingerprintId: fingerprint, internalOnly: internalOnly);

        // Plan task 19: enrich the paged visitor slice with the gateway-
        // projected drift badge. Only the visible page pays the lookup
        // cost — pageSize is bounded above to 100 by the query parser.
        await Services.FingerprintDriftProjector.EnrichVisitorsAsync(
            items, context.RequestServices, context.RequestAborted, _logger);

        var model = new VisitorListModel
        {
            Visitors = items,
            Counts = counts,
            Filter = filter,
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            BasePath = _options.BasePath.TrimEnd('/'),
            Country = country,
            BotType = botType,
            Threat = threat,
            FingerprintId = fingerprint,
            Internal = internalOnly,
        };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbVisitorList/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    // Keeps the event-store fetch bounded; matches the visitor-list page sizes.
    private const int _visitorListMaxEntries = 500;

    /// <summary>Render the summary stats partial.</summary>
    private async Task ServeSummaryPartialAsync(HttpContext context)
    {
        var model = await BuildSummaryStatsModelAsync(context);
        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbSummaryStats/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>Build a SummaryStatsModel with session analytics from the signature cache.</summary>
    private static SummaryStatsModel BuildSummaryStatsModelFromVisitorCache(
        DashboardSummary summary, string basePath, SignatureAggregateCache? signatureCache)
    {
        var model = new SummaryStatsModel { Summary = summary, BasePath = basePath };
        if (signatureCache is not null)
            PopulateSessionAnalytics(model, signatureCache);
        return model;
    }

    /// <summary>Populate session analytics fields on an existing SummaryStatsModel from the signature cache.</summary>
    private static void PopulateSessionAnalytics(SummaryStatsModel model, SignatureAggregateCache signatureCache)
    {
        var (allVisitors, totalCount, _, _) = signatureCache.GetFiltered("all", "lastSeen", "desc", 1, int.MaxValue);
        var humanVisitors = allVisitors.Where(v => !v.IsBot).ToList();
        var botVisitors = allVisitors.Where(v => v.IsBot).ToList();

        model.UniqueVisitors = totalCount;
        model.ActiveSessions = allVisitors.Count(v => v.LastSeen > DateTime.UtcNow.AddMinutes(-5));
        model.BotSessions = botVisitors.Count;
        model.HumanSessions = humanVisitors.Count;

        var totalWithHits = allVisitors.Count(v => v.Hits > 0);
        model.BounceRate = totalWithHits > 0
            ? Math.Round((double)allVisitors.Count(v => v.Hits == 1) / totalWithHits * 100, 1) : 0;
        model.HumanBounceRate = humanVisitors.Count > 0
            ? Math.Round((double)humanVisitors.Count(v => v.Hits == 1) / humanVisitors.Count * 100, 1) : 0;
        model.BotBounceRate = botVisitors.Count > 0
            ? Math.Round((double)botVisitors.Count(v => v.Hits == 1) / botVisitors.Count * 100, 1) : 0;

        static double AvgDuration(IReadOnlyList<ProjectedVisitor> visitors)
        {
            var withDuration = visitors.Where(v => v.Hits > 1).ToList();
            if (withDuration.Count == 0) return 0;
            return Math.Round(withDuration.Average(v => (v.LastSeen - v.FirstSeen).TotalSeconds), 1);
        }

        model.AvgSessionDurationSecs = AvgDuration(allVisitors);
        model.HumanAvgSessionDurationSecs = AvgDuration(humanVisitors);
        model.BotAvgSessionDurationSecs = AvgDuration(botVisitors);
    }

    /// <summary>Build the SummaryStatsModel including session analytics from the visitor cache.</summary>
    private async Task<SummaryStatsModel> BuildSummaryStatsModelAsync(HttpContext context)
    {
        // Cache is audience-agnostic; humans/bots filter has to go through the store
        // so the SQL is_bot predicate applies. Otherwise the summary KPI strip would
        // show full-traffic totals regardless of the audience toggle.
        var audienceFilter = (context.Request.Query["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();
        var summary = audienceFilter is "humans" or "bots"
            ? await _eventStore.GetSummaryAsync(audienceFilter: audienceFilter)
            : _aggregateCache.Current.Summary ?? await _eventStore.GetSummaryAsync();
        var basePath = _options.BasePath.TrimEnd('/');
        var model = new SummaryStatsModel { Summary = summary, BasePath = basePath };

        var signatureCache = context.RequestServices.GetService<SignatureAggregateCache>();
        if (signatureCache != null)
            PopulateSessionAnalytics(model, signatureCache);

        return model;
    }

    /// <summary>Render the "Your Detection" partial.</summary>
    private async Task ServeYourDetectionPartialAsync(HttpContext context)
    {
        var model = await BuildYourDetectionPartialModel(context);
        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_YourDetection.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     <c>GET /api/license</c> - FOSS builds carry no license concept, so
    ///     this endpoint just returns the upsell target. Commercial replaces
    ///     the middleware and serves parsed claims + entitlement stats.
    /// </summary>
    private async Task ServeLicenseApiAsync(HttpContext context)
    {
        var model = BuildLicenseCardModel(context);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            status = model.Status.ToString(),
            upsellUrl = model.UpsellUrl,
            upsellLabel = model.UpsellLabel
        }, CamelCaseJson);
    }

    /// <summary>Render the license card partial (HTMX target - refreshes itself every 60s).</summary>
    private async Task ServeLicensePartialAsync(HttpContext context)
    {
        var model = BuildLicenseCardModel(context);
        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_LicenseCard.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     Build the license card view model by combining the runtime
    ///     <see cref="DomainEntitlementValidator"/> stats with the parsed JWT (when present).
    ///     Logic lives in <see cref="LicenseCardModelBuilder"/> so the controller-hosted
    ///     dashboard renders the same status calculation.
    /// </summary>
    private LicenseCardModel BuildLicenseCardModel(HttpContext context) =>
        LicenseCardModelBuilder.Build(context, _options.BasePath.TrimEnd('/'));

    // ----- Policy Stack signal catalogue API -----
    //
    // Three JSON routes the expression editor binds to for autocomplete + signal
    // description tooltips. The catalogue is immutable post-boot, so responses
    // are public-cacheable for 5 minutes. Unknown keys 404 with a Levenshtein
    // "did you mean" list pulled from ISignalCatalog.SuggestForUnknown.
    private static readonly System.Text.Json.JsonSerializerOptions SignalCatalogJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private const string SignalCatalogCacheControl = "public, max-age=300";

    private static Mostlylucid.BotDetection.Policies.Signals.ISignalCatalog? TryGetSignalCatalog(HttpContext context)
        => context.RequestServices.GetService<Mostlylucid.BotDetection.Policies.Signals.ISignalCatalog>();

    private async Task ServeSignalNamespacesAsync(HttpContext context)
    {
        var catalog = TryGetSignalCatalog(context);
        if (catalog is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Catalog.Namespaces is already sorted ordinal-ascending. Counts come
        // from a single pass over All -- O(n) and called once per editor session.
        var counts = new Dictionary<string, int>(catalog.Namespaces.Count, StringComparer.Ordinal);
        foreach (var ns in catalog.Namespaces) counts[ns] = 0;
        foreach (var d in catalog.All)
        {
            var dot = d.Key.IndexOf('.');
            var ns = dot > 0 ? d.Key[..dot] : d.Key;
            if (counts.TryGetValue(ns, out var c)) counts[ns] = c + 1;
        }

        var items = new List<UI.Models.SignalNamespaceDto>(catalog.Namespaces.Count);
        foreach (var ns in catalog.Namespaces)
            items.Add(new UI.Models.SignalNamespaceDto(ns, counts[ns]));

        var payload = new UI.Models.SignalNamespacesResponseDto(items);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = SignalCatalogCacheControl;
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, SignalCatalogJsonOptions);
    }

    private async Task ServeSignalSearchAsync(HttpContext context)
    {
        var catalog = TryGetSignalCatalog(context);
        if (catalog is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var q = context.Request.Query["q"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(q))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"error\":\"query parameter 'q' is required\"}");
            return;
        }

        var n = 20;
        var nRaw = context.Request.Query["n"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(nRaw) && int.TryParse(nRaw, out var parsed))
            n = parsed;
        // Clamp [1, 100] -- silent (per spec) so an over-eager editor can pass
        // n=999 without seeing a 400. 0 / negative falls through to 1.
        if (n < 1) n = 1;
        if (n > 100) n = 100;

        var hits = new List<UI.Models.SignalSearchHitDto>(n);
        foreach (var d in catalog.Search(q, n))
        {
            hits.Add(new UI.Models.SignalSearchHitDto(
                Key: d.Key,
                Kind: d.Kind.ToString(),
                Short: d.Short,
                Operators: d.Operators));
        }
        var payload = new UI.Models.SignalSearchResponseDto(hits);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = SignalCatalogCacheControl;
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, SignalCatalogJsonOptions);
    }

    private async Task ServeSignalDetailAsync(HttpContext context, string rawKey)
    {
        var catalog = TryGetSignalCatalog(context);
        if (catalog is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Strip any trailing slash + URL-decode so callers can use either
        // /signals/risk.justification or /signals/risk.justification/.
        var key = rawKey.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(key))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        key = Uri.UnescapeDataString(key);

        var sig = catalog.TryGet(key);
        if (sig is null)
        {
            var suggestions = catalog.SuggestForUnknown(key, 3);
            var err = new UI.Models.SignalUnknownErrorDto("unknown signal", suggestions);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.Body, err, SignalCatalogJsonOptions);
            return;
        }

        var examples = new List<UI.Models.SignalExampleDto>(sig.Examples.Count);
        foreach (var e in sig.Examples)
            examples.Add(new UI.Models.SignalExampleDto(e.Expr, e.Reads));

        var payload = new UI.Models.SignalFullDto(
            Key: sig.Key,
            Kind: sig.Kind.ToString(),
            Short: sig.Short,
            Long: sig.Long,
            Operators: sig.Operators,
            Examples: examples,
            Related: sig.Related,
            DeclaringType: sig.DeclaringType);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = SignalCatalogCacheControl;
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, SignalCatalogJsonOptions);
    }

    /// <summary>
    ///     B5 explainer-panel route. Renders the SbPolicyStack
    ///     <c>_ExplainerPanel</c> partial for the requested scope + fingerprint
    ///     and writes the HTML back so HTMX can outerHTML-swap the panel. The
    ///     route is anonymous-readable; the same risk model that lets
    ///     <c>/dashboard/signals/*</c> serve catalog data also lets the panel
    ///     return decision-log replays.
    /// </summary>
    private async Task ServePolicyStackExplainAsync(HttpContext context)
    {
        var presenter = context.RequestServices
            .GetService<Mostlylucid.BotDetection.UI.Services.PolicyStackPresenter>();
        if (presenter is null)
        {
            // Caller registered Dashboard middleware without AddStyloBotDashboard
            // -- nothing to render. Surface a 404 rather than a 500 so HTMX
            // operators can distinguish "wrong host" from "bad fingerprint".
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var scopeParam = context.Request.Query["scope"].ToString();
        var fingerprint = context.Request.Query["fingerprint"].ToString();
        var locked = string.Equals(
            context.Request.Query["locked"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

        // The ⓘ row buttons emit ?focusRule=<guid>. Today the explainer
        // surfaces the most recent decision cycle regardless of which rule was
        // clicked -- the cycle includes every rule the evaluator touched, so
        // the focused rule is already in the rendered trace. The focusRule
        // hint stays in the URL so B6 can scroll-into-view client-side
        // without needing a second round-trip.
        var scope = Mostlylucid.BotDetection.UI.Services.PolicyScopeUrl.Decode(scopeParam);

        var vm = await presenter.BuildAsync(
            scope: scope,
            embed: Mostlylucid.BotDetection.UI.Models.PolicyStackEmbed.Full,
            activeTab: "effective",
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false,
            filter: null,
            sort: null,
            explainerFingerprint: fingerprint ?? string.Empty,
            explainerLocked: locked,
            ct: context.RequestAborted);

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbPolicyStack/_ExplainerPanel.cshtml", vm, context);

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    // B6 -- Policy Stack row partial. Returns ONLY the tab body wrapped in the
    // envelope id the browser's hx-swap=outerHTML targets. Both the SignalR
    // beacon refresh AND the B4 sort-header / filter-bar clicks land here so
    // the rule list mutates without disturbing the header / breadcrumb /
    // explainer panel above it.
    private async Task ServePolicyStackRowsAsync(HttpContext context)
    {
        var presenter = context.RequestServices
            .GetService<Mostlylucid.BotDetection.UI.Services.PolicyStackPresenter>();
        if (presenter is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var scopeParam = context.Request.Query["scope"].ToString();
        var tabParam = context.Request.Query["tab"].ToString();
        var filterExpression = context.Request.Query["policystack-filter"].ToString();
        var sortKey = context.Request.Query["policystack-sort"].ToString();
        var sortDir = context.Request.Query["policystack-dir"].ToString();

        var scope = Mostlylucid.BotDetection.UI.Services.PolicyScopeUrl.Decode(scopeParam);

        // tab=badge refreshes the StatusBadge envelope; tab=stack renders the
        // Stack tab; everything else (including the absent param) falls through
        // to the Effective tab -- matches the data-policy-stack-rows-url
        // emitted by each embed shape.
        var tabKind = string.IsNullOrWhiteSpace(tabParam)
            ? "effective"
            : tabParam.Trim().ToLowerInvariant();

        var filter = Mostlylucid.BotDetection.UI.Models.PolicyStackFilter.Parse(filterExpression);
        var sort = Mostlylucid.BotDetection.UI.Models.PolicyStackSort.Parse(sortKey, sortDir);

        var embed = tabKind == "badge"
            ? Mostlylucid.BotDetection.UI.Models.PolicyStackEmbed.StatusBadge
            : Mostlylucid.BotDetection.UI.Models.PolicyStackEmbed.Full;
        var activeTab = tabKind == "stack" ? "stack" : "effective";

        var vm = await presenter.BuildAsync(
            scope: scope,
            embed: embed,
            activeTab: activeTab,
            aggregateWindow: TimeSpan.FromHours(24),
            canEdit: false,
            filter: filter,
            sort: sort,
            ct: context.RequestAborted);

        // Pick the partial that lives inside the envelope. The envelope id is
        // rebuilt here so the HTMX outerHTML swap target matches the section
        // partial's id-of-the-same-name.
        string partialPath = tabKind switch
        {
            "stack" => "/Views/Shared/Components/SbPolicyStack/_StackTab.cshtml",
            "badge" => "/Views/Shared/Components/SbPolicyStack/_StatusBadge.cshtml",
            _ => "/Views/Shared/Components/SbPolicyStack/_EffectiveTab.cshtml"
        };

        var inner = await _razorViewRenderer.RenderViewToStringAsync(partialPath, vm, context);

        // Wrap in the envelope envelope so the outerHTML swap replaces the
        // div with the same id. The StatusBadge embed renders its own outer
        // wrapper inside _StatusBadge.cshtml; the other embeds want the
        // envelope explicitly. Either way the id matches.
        string html = tabKind == "badge"
            ? inner
            : $"<div id=\"sb-policy-stack-rows-{vm.ScopeHash}\" class=\"sb-policy-stack-rows-envelope\">{inner}</div>";

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    // B8 -- GET /dashboard/policystack/edit?ruleId=<guid>
    // Returns the rendered _RuleCardExpand.cshtml partial for an existing
    // rule. HTMX swaps the compact card out for the card-shaped expand
    // surface (header + curated facet picker + the existing _EditRow form
    // mechanics + hysteresis disclosure). 404 when the rule is unknown or
    // the editor presenter is not registered.
    private async Task ServePolicyStackEditExistingAsync(HttpContext context)
    {
        var presenter = context.RequestServices
            .GetService<Mostlylucid.BotDetection.UI.Services.PolicyEditPresenter>();
        if (presenter is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ruleIdRaw = context.Request.Query["ruleId"].ToString();
        if (!Guid.TryParse(ruleIdRaw, out var ruleId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var vm = await presenter.BuildExpandForExistingRuleAsync(ruleId, context.RequestAborted);
        if (vm is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbPolicyStack/_RuleCardExpand.cshtml", vm, context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    // B8 -- GET /dashboard/policystack/edit/new?scope=<encoded>
    // Returns the rendered _RuleCardExpand.cshtml partial with empty defaults
    // so the operator can fill in a new rule at the requested scope.
    private async Task ServePolicyStackEditNewAsync(HttpContext context)
    {
        var presenter = context.RequestServices
            .GetService<Mostlylucid.BotDetection.UI.Services.PolicyEditPresenter>();
        if (presenter is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var scopeParam = context.Request.Query["scope"].ToString();
        var scope = Mostlylucid.BotDetection.UI.Services.PolicyScopeUrl.Decode(scopeParam);
        var vm = presenter.BuildExpandForNewRule(scope);

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbPolicyStack/_RuleCardExpand.cshtml", vm, context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     <c>GET /dashboard/policystack/action-editor?kind=&lt;x&gt;</c>.
    ///     Renders the per-kind action-editor partial; the 7-kind matrix
    ///     (allow / observe / block / tag / challenge / ratelimit /
    ///     throttle) is dispatched through
    ///     <see cref="PolicyActionEditorViewPaths.ForKind"/>. Task 8 wires
    ///     the kind <c>&lt;select&gt;</c> in <c>_EditAction.cshtml</c> up to
    ///     issue the swap into the <c>[data-edit-action-slot]</c> element.
    ///     404 when the kind isn't recognised so the editor JS (Task 12)
    ///     can leave the slot untouched and surface the failure in the
    ///     existing error channel.
    /// </summary>
    private async Task ServePolicyStackActionEditorAsync(HttpContext context)
    {
        var kind = (context.Request.Query["kind"].ToString() ?? string.Empty).ToLowerInvariant();
        var viewPath = PolicyActionEditorViewPaths.ForKind(kind);

        if (viewPath is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"unknown action kind: {kind}");
            return;
        }

        // Build the per-kind @model from the query string via the same
        // helper the test controller uses, so the lockstep dispatch stays
        // single-source. Zero-field partials return null; RazorViewRenderer
        // declares its model parameter non-nullable, so we hand it a
        // sentinel object in that case.
        var model = PolicyActionEditorViewPaths.BuildActionEditorModel(kind, context.Request.Query)
                    ?? new object();
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            viewPath, model, context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     Traffic-shaping plan Tasks 9 + 10 --
    ///     <c>GET /dashboard/policystack/scope-picker</c>. Renders the
    ///     existing <c>SbScopePicker</c> view component for use inside the
    ///     policy edit row and the Apply Template dialog. The picker is
    ///     already wired for the four-axis composite
    ///     <see cref="Mostlylucid.BotDetection.Policies.Rules.PolicyScope"/>
    ///     shape (Host / Method / Geo / Identity); this endpoint just hands
    ///     it the seed model from the query string.
    ///
    ///     <para>
    ///         Three modes are supported:
    ///         <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <c>mode=inline</c> (default) -- single picker.
    ///                 <c>?scope=</c> accepts the URL-encoded form produced by
    ///                 <see cref="PolicyScopeUrl.Encode"/> (unknown values
    ///                 decode to the wildcard scope, which renders all axes
    ///                 as <c>none</c>). <c>?fieldName=</c> overrides the
    ///                 hidden-input name (defaults to <c>scope</c>).
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>mode=multi</c> -- N pickers wrapped in a fieldset
    ///                 with a "+ Add scope" button (Apply Template flow).
    ///                 Repeat <c>?multi=&lt;encoded&gt;</c> once per existing
    ///                 row; empty / unknown rows render as wildcards.
    ///                 <c>?fieldNamePrefix=</c> drives the indexed hidden
    ///                 names (<c>{prefix}[0]</c>, ...; defaults to
    ///                 <c>applied-to</c>). No rows -&gt; a single empty row.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>mode=inline-row</c> -- single row wrapper (picker
    ///                 + remove button) for the multi wrapper's
    ///                 "+ Add scope" button to HTMX-append into the row
    ///                 container. Returns <c>_Row.cshtml</c>, not the full
    ///                 multi fieldset. <c>?fieldNamePrefix=</c> and
    ///                 <c>?rowIndex=</c> control the picker's
    ///                 <c>FieldName</c> (the client-side wiring lands later
    ///                 and can rewrite indexes after server-side append).
    ///             </description>
    ///         </item>
    ///         </list>
    ///     </para>
    /// </summary>
    private async Task ServePolicyStackScopePickerAsync(HttpContext context)
    {
        var mode = (context.Request.Query["mode"].ToString() ?? "inline").ToLowerInvariant();
        if (mode.Length == 0) mode = "inline";

        switch (mode)
        {
            case "inline":
                await RenderScopePickerInlineAsync(context);
                return;

            case "multi":
                await RenderScopePickerMultiAsync(context);
                return;

            case "inline-row":
                await RenderScopePickerInlineRowAsync(context);
                return;

            default:
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync($"unsupported scope-picker mode: {mode}");
                return;
        }
    }

    /// <summary>
    ///     <c>mode=inline</c> branch -- single <c>SbScopePicker</c>
    ///     fieldset. The wrapping form posts the picker's hidden JSON
    ///     projection under <c>?fieldName=</c> (default <c>scope</c>).
    /// </summary>
    private async Task RenderScopePickerInlineAsync(HttpContext context)
    {
        var encoded = context.Request.Query["scope"].ToString();
        var initial = string.IsNullOrEmpty(encoded)
            ? null
            : PolicyScopeUrl.Decode(encoded);

        var fieldName = context.Request.Query["fieldName"].ToString();
        if (string.IsNullOrEmpty(fieldName)) fieldName = "scope";

        var model = new ScopePickerViewModel(FieldName: fieldName, Initial: initial);

        // Render the SbScopePicker Default.cshtml directly via the existing
        // RazorViewRenderer; view-components rendered through the view
        // engine pick up the same view-locator paths the production
        // pipeline does, so we don't need a separate view-component
        // rendering helper.
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbScopePicker/Default.cshtml", model, context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     <c>mode=multi</c> branch -- N pickers in a single fieldset plus
    ///     the "+ Add scope" button. Decodes each <c>?multi=</c> entry into
    ///     a <see cref="PolicyScope"/>; entries that fail to decode are
    ///     swallowed (the resulting list still binds well rather than
    ///     erroring on operator-supplied junk). An empty list renders a
    ///     single wildcard row so the operator always has one to fill in.
    /// </summary>
    private async Task RenderScopePickerMultiAsync(HttpContext context)
    {
        var scopes = new List<Mostlylucid.BotDetection.Policies.Rules.PolicyScope?>();
        foreach (var encoded in context.Request.Query["multi"])
        {
            if (string.IsNullOrEmpty(encoded)) continue;
            try
            {
                scopes.Add(PolicyScopeUrl.Decode(encoded));
            }
            catch
            {
                // Operator-supplied opaque scope string; skip the bad
                // entry rather than 400. The Apply Template UX surfaces
                // a wildcard slot in its place which the operator can
                // re-populate.
            }
        }
        if (scopes.Count == 0) scopes.Add(null);

        var prefix = context.Request.Query["fieldNamePrefix"].ToString();
        if (string.IsNullOrEmpty(prefix)) prefix = "applied-to";

        var model = new ScopePickerMultiViewModel(scopes, prefix);
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbScopePicker/_Multi.cshtml", model, context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     <c>mode=inline-row</c> branch -- single row wrapper
    ///     (data-scope-row + remove button) for the multi wrapper's
    ///     "+ Add scope" button to HTMX-append. The picker inside the row
    ///     gets a <see cref="ScopePickerViewModel.FieldName"/> built from
    ///     <c>?fieldNamePrefix=</c> + <c>?rowIndex=</c> so the appended row
    ///     binds under the correct collection slot. When <c>?rowIndex=</c>
    ///     is missing or non-numeric the row uses <c>new</c> as a
    ///     placeholder index -- the client-side wiring that lands later
    ///     can rewrite it before submit if model-binding needs strict
    ///     contiguous indexes.
    /// </summary>
    private async Task RenderScopePickerInlineRowAsync(HttpContext context)
    {
        var prefix = context.Request.Query["fieldNamePrefix"].ToString();
        if (string.IsNullOrEmpty(prefix)) prefix = "applied-to";

        var rowIndexRaw = context.Request.Query["rowIndex"].ToString();
        var rowIndexLabel = string.IsNullOrEmpty(rowIndexRaw) ? "new" : rowIndexRaw;

        var fieldName = $"{prefix}[{rowIndexLabel}]";
        var model = new ScopePickerViewModel(FieldName: fieldName, Initial: null);

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbScopePicker/_Row.cshtml", model, context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    // C6 -- POST /dashboard/policystack/parse  (text/plain body)
    // Parses the supplied predicate text via PredicateParser, then returns
    // a canonical lowercase-discriminator AST JSON the policy-stack-edit.js
    // chip-renderer consumes. Lowercase keys + no $ prefix keeps the wire
    // format identical to the mutation API DTO. Bad input returns 400
    // with { message, position } so the editor can anchor the inline
    // error indicator.
    private async Task ServePolicyStackParseAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        string text;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            text = await reader.ReadToEndAsync(context.RequestAborted);
        }

        Mostlylucid.BotDetection.Policies.Predicate.Predicate ast;
        try
        {
            ast = Mostlylucid.BotDetection.Policies.Predicate.PredicateParser.Parse(text);
        }
        catch (Mostlylucid.BotDetection.Policies.Predicate.PredicateParseException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                $"{{\"message\":{JsonSerializer.Serialize(ex.Message)},\"position\":{ex.Position.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
            return;
        }

        var canonical = PolicyStackParseSerialiser.SerialiseAst(ast);

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync($"{{\"ast\":{canonical}}}");
    }

    // C8 -- POST /dashboard/policystack/backtest (application/json body)
    // Body: { predicate: "text", actionKind: "block", window: "24h" }.
    // Parses the predicate, runs PolicyBacktestRunner over the chosen
    // window, then returns the rendered _BacktestPanel.cshtml partial.
    // The editor JS swaps the rendered HTML into the data-edit-backtest-slot
    // wrapper inside the edit row. Bad predicate -> 400. Decision-log not
    // registered (FOSS standalone with no SQLite store) -> 503 with a
    // text/plain message; the JS leaves the placeholder in place.
    private async Task ServePolicyStackBacktestAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var log = context.RequestServices
            .GetService<Mostlylucid.BotDetection.Policies.Decisions.IPolicyDecisionLog>();
        if (log is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("decision log not registered");
            return;
        }

        // Body envelope. The DTO is local to this handler since it's not
        // shared with anything else; lowercase camelCase to match the
        // editor JS's outbound shape.
        BacktestRequestDto? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<BacktestRequestDto>(
                context.Request.Body,
                BacktestJsonOptions,
                context.RequestAborted);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"message\":\"invalid JSON body\"}");
            return;
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Predicate))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"message\":\"predicate is required\"}");
            return;
        }

        Mostlylucid.BotDetection.Policies.Predicate.Predicate ast;
        try
        {
            ast = Mostlylucid.BotDetection.Policies.Predicate.PredicateParser.Parse(body.Predicate);
        }
        catch (Mostlylucid.BotDetection.Policies.Predicate.PredicateParseException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                $"{{\"message\":{JsonSerializer.Serialize(ex.Message)},\"position\":{ex.Position.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
            return;
        }

        var (window, label) = ParseBacktestWindow(body.Window);

        var runner = new Mostlylucid.BotDetection.Policies.Decisions.PolicyBacktestRunner(log);
        var result = await runner.RunAsync(ast, window, ct: context.RequestAborted);

        var vm = new Mostlylucid.BotDetection.UI.Models.PolicyBacktestViewModel(
            TotalRequests: result.TotalRequests,
            WouldMatch: result.WouldMatch,
            WouldEnforce: result.WouldEnforce,
            OverriddenByHigherPriority: result.OverriddenByHigherPriority,
            EstimatedAddedP50Micros: result.EstimatedAddedP50Micros,
            EstimatedAddedP99Micros: result.EstimatedAddedP99Micros,
            SampleMatchingFingerprints: result.SampleMatchingFingerprints,
            Window: result.Window,
            WindowLabel: label,
            Caveat: result.Caveat);

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbPolicyStack/_BacktestPanel.cshtml", vm, context);

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     Map "24h" / "7d" / "30d" to a <see cref="TimeSpan"/> + canonical
    ///     label echoed back into the rendered panel's data-window attr.
    ///     Unknown inputs default to 24h so an empty/malformed window in
    ///     the JS request doesn't fail the response.
    /// </summary>
    private static (TimeSpan window, string label) ParseBacktestWindow(string? raw)
    {
        var normalised = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return normalised switch
        {
            "7d" => (TimeSpan.FromDays(7), "7d"),
            "30d" => (TimeSpan.FromDays(30), "30d"),
            _ => (TimeSpan.FromHours(24), "24h"),
        };
    }

    /// <summary>
    ///     Reused JSON options for the C8 backtest request body. Sets
    ///     <see cref="System.Text.Json.JsonSerializerOptions.PropertyNameCaseInsensitive"/>
    ///     so the editor JS can post either camelCase or PascalCase without
    ///     a server-side coupling.
    /// </summary>
    private static readonly JsonSerializerOptions BacktestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    ///     Inbound DTO for <c>POST /dashboard/policystack/backtest</c>.
    /// </summary>
    private sealed class BacktestRequestDto
    {
        public string Predicate { get; set; } = string.Empty;
        public string? ActionKind { get; set; }
        public string? Window { get; set; }
    }

    private async Task ServeHelpAsync(HttpContext context, string sectionId)
    {
        var helpService = context.RequestServices.GetService<DashboardHelpService>();
        if (helpService is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var entry = helpService.GetHelp(sectionId, IsCommercialMode(context));
        if (entry is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_HelpPanel.cshtml", entry, context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    private ComplianceTabModel? BuildComplianceTabModel(HttpContext context)
    {
        try
        {
            var packProvider = context.RequestServices.GetService<Mostlylucid.BotDetection.Compliance.ICompliancePackProvider>();
            if (packProvider is null)
                return new ComplianceTabModel
                {
                    ActivePackId = "default", ActivePackName = "Default (no compliance module)",
                    BasePath = _options.BasePath.TrimEnd('/'), IsCommercial = IsCommercialMode(context)
                };

            var pack = packProvider.ActivePack;
            return new ComplianceTabModel
            {
                ActivePackId = pack.Id,
                ActivePackName = pack.Name,
                ActivePackExplain = pack.Explain,
                LegalReferences = pack.LegalReferences,
                Jurisdiction = pack.Jurisdiction,
                Position = pack.Position,
                IsCommercial = IsCommercialMode(context),
                BasePath = _options.BasePath.TrimEnd('/'),
                // Guardians run on the enforcement component; on a remote host the
                // count comes from the gateway's compliance status endpoint via
                // IComplianceStatusReader (absent -> 0 on hosts with no compliance).
                GuardianCount = context.RequestServices
                    .GetService<Mostlylucid.BotDetection.Compliance.IComplianceStatusReader>()?.GuardianCount ?? 0,
                AvailablePacks = packProvider.AvailablePacks
                    .Select(p => new CompliancePackSummary(p.Id, p.Name, p.Jurisdiction))
                    .ToList()
            };
        }
        catch
        {
            return new ComplianceTabModel
            {
                ActivePackId = "default", ActivePackName = "Default",
                BasePath = _options.BasePath.TrimEnd('/'), IsCommercial = false
            };
        }
    }

    private StatusStripModel BuildStatusStripModel(HttpContext context)
    {
        try { return BuildStatusStripModelInternal(context); }
        catch { return new StatusStripModel { ActivePackName = "Default", Services = [], DetectionActive = true }; }
    }

    private StatusStripModel BuildStatusStripModelInternal(HttpContext context)
    {
        var isCommercial = IsCommercialMode(context);

        // Detect available services
        var services = new List<ServiceStatus>();

        // Check if PostgreSQL is available (via IDashboardEventStore type)
        var storeType = _eventStore.GetType().Name;
        if (storeType.Contains("Postgre", StringComparison.OrdinalIgnoreCase))
            services.Add(new ServiceStatus("PostgreSQL", true));
        else if (storeType.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            services.Add(new ServiceStatus("SQLite", true));
        else
            services.Add(new ServiceStatus("In-Memory", true));

        // Check if Redis is available
        var redisType = Type.GetType("Stylobot.Commercial.Cache.Redis.IRedisCacheProvider, Stylobot.Commercial.Cache.Redis");
        var redis = redisType is not null ? context.RequestServices.GetService(redisType) : null;
        if (redis is not null)
            services.Add(new ServiceStatus("Redis", true));

        // Check compliance pack
        var packProvider = context.RequestServices.GetService<Mostlylucid.BotDetection.Compliance.ICompliancePackProvider>();
        var packName = packProvider?.ActivePack.Name ?? "Default";

        // Check LLM
        var llmType = Type.GetType("Mostlylucid.BotDetection.Services.ILlmClassificationService, Mostlylucid.BotDetection");
        var llm = llmType is not null ? context.RequestServices.GetService(llmType) : null;
        var llmConnected = llm is not null && llm.GetType().Name != "NullLlmClassificationService";
        var llmProvider = llmConnected ? llm!.GetType().Name.Replace("LlmClassificationService", "").Replace("Classification", "") : null;

        // Check LLM tunnel nodes
        var tunnelNodes = new List<TunnelNodeStatus>();
        var registryType = Type.GetType(
            "Mostlylucid.BotDetection.Llm.Tunnel.ILlmNodeRegistry, Mostlylucid.BotDetection.Llm.Tunnel");
        if (registryType is not null)
        {
            var registry = context.RequestServices.GetService(registryType);
            if (registry is not null)
            {
                var getAllMethod = registryType.GetMethod("GetAll");
                if (getAllMethod?.Invoke(registry, null) is System.Collections.IEnumerable nodes)
                {
                    foreach (var node in nodes)
                    {
                        var t = node.GetType();
                        var nodeId   = t.GetProperty("NodeId")?.GetValue(node) as string ?? "";
                        var name     = t.GetProperty("Name")?.GetValue(node) as string ?? nodeId;
                        var enabled  = t.GetProperty("Enabled")?.GetValue(node) is bool b && b;
                        var models   = (t.GetProperty("Models")?.GetValue(node) as System.Collections.IEnumerable)?
                                           .Cast<object>().Select(m => m.ToString() ?? "").ToList()
                                       ?? [];
                        var queue    = t.GetProperty("QueueDepth")?.GetValue(node) is int q ? q : 0;
                        var kind     = t.GetProperty("TunnelKind")?.GetValue(node) as string ?? "unknown";
                        tunnelNodes.Add(new TunnelNodeStatus(nodeId, name, enabled, models, queue, kind));
                    }
                }
            }
        }

        // Check guardians (commercial only)
        var guardianCount = 0;
        var guardianAlerts = 0;
        if (isCommercial)
        {
            var guardians = context.RequestServices.GetServices(
                Type.GetType("Stylobot.Commercial.Compliance.Guardians.IComplianceGuardian, Stylobot.Commercial.Compliance") ?? typeof(object));
            guardianCount = guardians.Count();
        }

        return new StatusStripModel
        {
            ActivePackName = packName,
            DetectionActive = true,
            Services = services,
            GuardiansEnabled = guardianCount > 0,
            GuardianCount = guardianCount,
            GuardianAlerts = guardianAlerts,
            LlmConnected = llmConnected,
            LlmProvider = llmProvider,
            IsCommercial = isCommercial,
            TunnelNodes = tunnelNodes
        };
    }

    /// <summary>Render the Configuration tab partial. Lazy-loads Monaco from CDN once it boots.</summary>
    private async Task ServeConfigurationPartialAsync(HttpContext context)
    {
        var model = await BuildConfigurationModelAsync(context);
        if (model is null)
        {
            // No editor service registered - show a friendly empty state instead of a 500.
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<div class=\"text-sm text-base-content/60 p-4\">Config editor unavailable in this build.</div>");
            return;
        }

        // Re-emit a CSP nonce that the inline Monaco bootstrap script can use. The shell
        // already set one, but partials served standalone (HTMX) may not have inherited it.
        if (!context.Items.ContainsKey("CspNonce"))
            GetOrCreateCspNonce(context);

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_ConfigurationEditor.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     Build the Configuration tab model. Returns null when the editor service isn't
    ///     registered (defensive - should always be present once <c>AddBotDetection()</c>
    ///     has run, but the dashboard may be hosted in trimmed configurations).
    /// </summary>
    private async Task<ConfigurationEditorModel?> BuildConfigurationModelAsync(HttpContext context)
    {
        var editor = context.RequestServices.GetService<IConfigEditorService>();
        if (editor is null) return null;

        // Reuse the shared license-status logic so the upsell rail's gating matches what
        // the License card on Overview is showing - no risk of inconsistency.
        var license = LicenseCardModelBuilder.Build(context, _options.BasePath.TrimEnd('/'));
        var commercial = license.Status is LicenseStatusKind.Active or LicenseStatusKind.Trial;

        return new ConfigurationEditorModel
        {
            BasePath = _options.BasePath.TrimEnd('/'),
            Detectors = await editor.ListManifestsAsync(context.RequestAborted),
            Sections = EffectiveConfigSerializer.DiscoverSections(),
            IsCommercialLicensed = commercial
        };
    }

    // ====================================================================================
    // Configuration viewer endpoints (FOSS, read-only).
    // List sections, list detectors, return a single section's effective JSON, or return
    // a single detector manifest's YAML + effective JSON. Writes live in the commercial
    // control plane on its own routes; nothing in this file mutates configuration state.
    // ====================================================================================

    /// <summary><c>GET /api/config/manifests</c> - list every editable detector + override status.</summary>
    private async Task ServeConfigManifestsListAsync(HttpContext context)
    {
        var editor = context.RequestServices.GetService<IConfigEditorService>();
        if (editor is null) { context.Response.StatusCode = 503; return; }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body,
            new { detectors = await editor.ListManifestsAsync(context.RequestAborted) }, CamelCaseJson);
    }

    /// <summary>
    ///     GET-only route for <c>/api/config/manifests/{slug}</c>. PUT and DELETE were
    ///     removed - the FOSS dashboard cannot modify detector overrides. Editing lives
    ///     in the commercial control plane on its own routes.
    /// </summary>
    private async Task ServeConfigManifestApiAsync(HttpContext context, string slug)
    {
        var reader = context.RequestServices.GetService<IConfigEditorService>();
        if (reader is null) { context.Response.StatusCode = 503; return; }

        if (context.Request.Method != "GET")
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers["Allow"] = "GET";
            return;
        }

        await ServeConfigManifestGetAsync(context, reader, slug);
    }

    private async Task ServeConfigManifestGetAsync(HttpContext context, IConfigEditorService editor, string slug)
    {
        var doc = await editor.GetManifestAsync(slug, context.RequestAborted);
        if (doc is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // ?format=html drives the dashboard's Configuration tab: server-rendered,
        // syntax-highlighted YAML or JSON returned as an HTMX fragment. Any other
        // format (or absent) keeps the legacy JSON-of-the-document response so
        // external API consumers don't break.
        var format = context.Request.Query["format"].FirstOrDefault();
        if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
        {
            var view = context.Request.Query["view"].FirstOrDefault() ?? "yaml";
            string highlighted;
            string label;
            if (string.Equals(view, "json", StringComparison.OrdinalIgnoreCase))
            {
                // "JSON view" of a detector = the effective DetectorDefaults (weights,
                // confidence, timing, features, parameters) AFTER appsettings overrides.
                var provider = context.RequestServices.GetService<IDetectorConfigProvider>();
                var manifest = provider?.GetManifest(doc.Name);
                var defaults = manifest is not null ? provider!.GetDefaults(manifest.Name) : null;
                if (defaults is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                var json = EffectiveConfigSerializer.SerializeDetectorDefaults(defaults);
                highlighted = ConfigSyntaxHighlighter.HighlightJson(json);
                label = $"detectors.{slug}.effective.json";
            }
            else
            {
                highlighted = ConfigSyntaxHighlighter.HighlightYaml(doc.EffectiveYaml);
                label = $"detectors/{slug}.detector.yaml{(doc.HasOverride ? " (override)" : " (embedded)")}";
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(BuildConfigFragment(label, highlighted, view));
            return;
        }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, doc, CamelCaseJson);
    }

    /// <summary><c>GET /api/config/sections</c> — list every effective-config section the dashboard rail can render.</summary>
    private async Task ServeConfigSectionsListAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body,
            new { sections = EffectiveConfigSerializer.DiscoverSections() }, CamelCaseJson);
    }

    /// <summary>
    ///     <c>GET /api/config/section/{name}?format=html</c> — pre-highlighted JSON
    ///     fragment for one section of <see cref="BotDetectionOptions"/>. Returns
    ///     raw JSON when <c>format</c> is absent. Secrets are masked.
    /// </summary>
    private async Task ServeConfigSectionAsync(HttpContext context, string sectionId)
    {
        var opts = ResolveBotDetectionOptions(context);
        if (opts is null) { context.Response.StatusCode = 503; return; }

        var json = EffectiveConfigSerializer.SerializeSection(opts, sectionId);
        if (json is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var format = context.Request.Query["format"].FirstOrDefault();
        if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
        {
            var label = string.Equals(sectionId, EffectiveConfigSerializer.RootSectionId, StringComparison.OrdinalIgnoreCase)
                ? "BotDetection (root settings)"
                : $"BotDetection.{sectionId}";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(BuildConfigFragment(label, ConfigSyntaxHighlighter.HighlightJson(json), "json"));
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }

    /// <summary>
    ///     <c>GET /api/config/effective</c>. <c>?download=1</c> returns the full
    ///     effective options as a JSON attachment; otherwise returns a highlighted
    ///     HTML fragment (intended for embedding) or raw JSON.
    /// </summary>
    private async Task ServeConfigEffectiveAsync(HttpContext context)
    {
        var opts = ResolveBotDetectionOptions(context);
        if (opts is null) { context.Response.StatusCode = 503; return; }

        var json = EffectiveConfigSerializer.SerializeFull(opts);

        if (string.Equals(context.Request.Query["download"].FirstOrDefault(), "1", StringComparison.Ordinal))
        {
            context.Response.ContentType = "application/json";
            context.Response.Headers["Content-Disposition"] =
                "attachment; filename=\"stylobot-effective-config.json\"";
            await context.Response.WriteAsync(json);
            return;
        }

        var format = context.Request.Query["format"].FirstOrDefault();
        if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                BuildConfigFragment("BotDetection (effective)", ConfigSyntaxHighlighter.HighlightJson(json), "json"));
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }

    /// <summary>
    ///     Resolve <see cref="BotDetectionOptions"/> from the request services. The
    ///     three /api/config endpoints all need this; centralised so the null-handling
    ///     story is the same in each one (503 when options aren't registered, which
    ///     should only happen in a trimmed dashboard-only host).
    /// </summary>
    private static BotDetectionOptions? ResolveBotDetectionOptions(HttpContext context) =>
        context.RequestServices
            .GetService<Microsoft.Extensions.Options.IOptions<BotDetectionOptions>>()?.Value;

    /// <summary>
    ///     Wraps highlighted content in the markup the Configuration tab expects:
    ///     a small header strip with the path label and a <c>&lt;pre&gt;&lt;code&gt;</c>
    ///     scroller. Kept here (rather than a partial) because the fragment is
    ///     trivial and embedding the markup avoids a per-request Razor render.
    /// </summary>
    private static string BuildConfigFragment(string pathLabel, string highlighted, string viewKind)
    {
        var encodedLabel = System.Net.WebUtility.HtmlEncode(pathLabel);
        var lang = string.Equals(viewKind, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";
        return
            $"<div class=\"sb-config-fragment\" data-view=\"{lang}\">" +
            $"<div class=\"sb-config-path\">{encodedLabel}</div>" +
            $"<pre class=\"sb-config-pre\"><code class=\"sb-config-code lang-{lang}\">{highlighted}</code></pre>" +
            "</div>";
    }


    /// <summary>Render the countries list partial with server-side sort and pagination.</summary>
    private async Task ServeCountriesPartialAsync(HttpContext context)
    {
        var sortField = context.Request.Query["sort"].FirstOrDefault() ?? "total";
        var sortDir = context.Request.Query["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(context.Request.Query["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(context.Request.Query["pageSize"].FirstOrDefault(), out var ps) && ps is > 0 and <= 50 ? ps : 20;
        var audienceFilter = (context.Request.Query["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();

        // Cache is audience-agnostic; humans/bots route through the store so the
        // is_bot predicate applies (mirrors the endpoints partial fix).
        var data = audienceFilter is "humans" or "bots"
            ? await _eventStore.GetCountryStatsAsync(100, audienceFilter: audienceFilter)
            : await GetCountriesDataAsync();

        var model = BuildCountriesModel(sortField, sortDir, page, pageSize, data);

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbCountriesList/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>Render the endpoints list partial with server-side sort and pagination.</summary>
    // Static resource extensions to exclude from live endpoint views (like Chrome DevTools filter)
    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".avif",
        ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".mp4", ".webm", ".mp3", ".ogg",
        ".pdf", ".zip", ".gz", ".br",
        ".json", ".xml", ".txt", ".webmanifest"
    };

    private static bool IsStaticResource(string path)
    {
        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && StaticExtensions.Contains(ext)) return true;
        // Common static prefixes
        return path.StartsWith("/dist/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/img/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ServeEndpointsPartialAsync(HttpContext context)
    {
        var sortField = context.Request.Query["sort"].FirstOrDefault() ?? "total";
        var sortDir = context.Request.Query["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(context.Request.Query["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(context.Request.Query["pageSize"].FirstOrDefault(), out var ps) && ps is > 0 and <= 100 ? ps : 20;
        var excludeStatic = context.Request.Query["excludeStatic"].FirstOrDefault() == "true";
        var statusFilter = (context.Request.Query["status"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();
        var pathFilter = (context.Request.Query["path"].FirstOrDefault() ?? string.Empty).Trim();
        var audienceFilter = (context.Request.Query["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();

        // Cache is audience-agnostic: the broadcaster precomputes one snapshot
        // with no filter. humans / bots gating must come from the store so the
        // is_bot SQL predicate applies. honeypot is path-shape and works on any
        // snapshot, so we keep the cached path for it.
        List<DashboardEndpointStats> endpoints = audienceFilter is "humans" or "bots"
            ? await _eventStore.GetEndpointStatsAsync(500, audienceFilter: audienceFilter)
            : await GetEndpointsDataAsync(context);

        if (excludeStatic)
            endpoints = endpoints.Where(e => !IsStaticResource(e.Path)).ToList();

        if (!string.IsNullOrEmpty(pathFilter))
            endpoints = endpoints
                .Where(e => e.Path.Contains(pathFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (statusFilter is "2xx" or "3xx" or "4xx" or "5xx")
            endpoints = endpoints
                .Where(e => string.Equals(e.DominantStatusBucket, statusFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        // Honeypot audience filter is applied in-memory against IsHoneypot
        // (populated per-row by the store via HoneypotPathDefinitions.Classify).
        if (audienceFilter == "honeypot")
            endpoints = endpoints.Where(e => e.IsHoneypot).ToList();

        var model = BuildEndpointsModel(context, sortField, sortDir, page, pageSize, endpoints)
            with { StatusFilter = statusFilter, PathFilter = pathFilter, AudienceFilter = audienceFilter };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbEndpointsList/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>Compact endpoint table for overview/activity sidebar slots. No widget management attributes — not an OOB target.</summary>
    private async Task ServeEndpointsCompactPartialAsync(HttpContext context)
    {
        var sortField = context.Request.Query["sort"].FirstOrDefault() ?? "botrate";
        var sortDir = context.Request.Query["dir"].FirstOrDefault() ?? "desc";
        var pageSize = int.TryParse(context.Request.Query["pageSize"].FirstOrDefault(), out var ps) && ps is > 0 and <= 100 ? ps : 10;
        var excludeStatic = context.Request.Query["excludeStatic"].FirstOrDefault() == "true";
        var compact = context.Request.Query["compact"].FirstOrDefault() == "true";

        var endpoints = await GetEndpointsDataAsync(context);
        if (excludeStatic)
            endpoints = endpoints.Where(e => !IsStaticResource(e.Path)).ToList();

        var model = BuildEndpointsModel(context, sortField, sortDir, 1, pageSize, endpoints) with { IsCompact = compact };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_EndpointsCompact.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>Render the clusters list partial.</summary>
    private async Task ServeClustersPartialAsync(HttpContext context)
    {
        var model = await BuildClustersModelAsync(context);
        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_ClustersList.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>Render the top bots list partial.</summary>
    private async Task ServeTopBotsPartialAsync(HttpContext context)
    {
        var sortBy = context.Request.Query["sort"].FirstOrDefault() ?? "default";
        var sortDir = context.Request.Query["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(context.Request.Query["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(context.Request.Query["pageSize"].FirstOrDefault(), out var ps) && ps is > 0 and <= 50 ? ps : 10;
        var filter = context.Request.Query["filter"].FirstOrDefault() ?? "bots";
        var widgetId = context.Request.Query["widgetId"].FirstOrDefault() ?? "topbots";
        var searchQuery = context.Request.Query["q"].FirstOrDefault();

        var model = await BuildTopBotsModel(page, pageSize, sortBy, sortDir, filter, widgetId, searchQuery);

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbTopBots/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    private async Task ServeThreatsApiAsync(HttpContext context)
    {
        var countStr = context.Request.Query["count"].FirstOrDefault();
        var count = int.TryParse(countStr, out var c) ? Math.Clamp(c, 1, 50) : 20;

        var threats = await _eventStore.GetThreatsAsync(count);

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, threats, CamelCaseJson);
    }

    private async Task ServeUaSearchApiAsync(HttpContext context)
    {
        var query = context.Request.Query["q"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(query))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Query parameter 'q' is required\"}");
            return;
        }

        var limitStr = context.Request.Query["limit"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var l) ? Math.Clamp(l, 1, 100) : 20;

        var results = await _eventStore.SearchUserAgentsAsync(query, limit);

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, results, CamelCaseJson);
    }

    /// <summary>
    ///     Se2 -- type-ahead visitor search backing the header search box. Resolves
    ///     <see cref="IFingerprintStore"/> from request services (avoids forcing
    ///     hosts without a fingerprint store registered to take a hard dep at
    ///     middleware-construction time) and forwards to
    ///     <see cref="IFingerprintStore.SearchByResolvedNameAsync"/>. Empty / blank
    ///     <c>q</c> returns <c>[]</c> with 200 so the type-ahead client can keep a
    ///     single happy-path branch.
    /// </summary>
    private async Task ServeVisitorSearchApiAsync(HttpContext context)
    {
        var query = context.Request.Query["q"].FirstOrDefault();
        context.Response.ContentType = "application/json";

        if (string.IsNullOrWhiteSpace(query))
        {
            await context.Response.WriteAsync("[]");
            return;
        }

        var store = context.RequestServices.GetService<IFingerprintStore>();
        if (store is null)
        {
            await context.Response.WriteAsync("[]");
            return;
        }

        var layout = context.RequestServices
            .GetService<Microsoft.Extensions.Options.IOptions<UI.Models.Dashboard.Layout.DashboardLayoutOptions>>()
            ?.Value;
        var max = layout?.SearchMaxResults ?? 10;

        var hits = await store.SearchByResolvedNameAsync(query, max, context.RequestAborted);
        await JsonSerializer.SerializeAsync(context.Response.Body, hits, CamelCaseJson, context.RequestAborted);
    }

    private async Task ServeThreatsPartialAsync(HttpContext context)
    {
        var pageStr = context.Request.Query["page"].FirstOrDefault();
        var pageSizeStr = context.Request.Query["pageSize"].FirstOrDefault();
        var page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(pageSizeStr, out var ps) ? Math.Clamp(ps, 1, 100) : 20;

        var threats = await _eventStore.GetThreatsAsync(pageSize * 10);
        var totalCount = threats.Count;
        var pagedThreats = threats.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var model = new ThreatsListModel
        {
            Threats = pagedThreats,
            TotalCount = totalCount,
            ActiveHoneypotSessions = pagedThreats.Count(t => t.InHoneypot),
            Page = page,
            PageSize = pageSize,
            BasePath = _options.BasePath.TrimEnd('/')
        };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbThreatsList/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>Renders the Identities tab partial. Empty-state when Identity is dormant.</summary>
    private async Task ServeIdentitiesPartialAsync(HttpContext context)
    {
        var page = int.TryParse(context.Request.Query["page"], out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(context.Request.Query["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 25;

        var model = await BuildIdentitiesModelAsync(context, page, pageSize);
        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbIdentitiesList/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    private async Task<Models.IdentitiesListModel> BuildIdentitiesModelAsync(HttpContext context, int page, int pageSize)
    {
        var store = context.RequestServices.GetService<BotDetection.Identity.IFingerprintReader>();
        var optionsAccessor = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<BotDetection.Models.BotDetectionOptions>>();
        var enabled = optionsAccessor?.Value.Identity.Enabled ?? false;
        if (store is null || !enabled)
        {
            return new Models.IdentitiesListModel
            {
                IdentityEnabled = false,
                BasePath = _options.BasePath.TrimEnd('/'),
                Page = page,
                PageSize = pageSize
            };
        }

        var all = await store.ListFingerprintsAsync();
        var unabsorbed = await store.GetUnabsorbedObservationCountsAsync();
        var ordered = all
            .OrderByDescending(fp => unabsorbed.GetValueOrDefault(fp.FingerprintId, 0))
            .ThenByDescending(fp => fp.LastSeen)
            .ToList();
        var rows = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(fp => new Models.IdentityListEntry
            {
                FingerprintId = fp.FingerprintId,
                InferredClientType = fp.InferredClientType,
                InferredTypeConfidence = fp.InferredTypeConfidence,
                CentroidMaturity = fp.CentroidMaturity,
                ObservationCount = fp.ObservationCount,
                UnabsorbedObservations = unabsorbed.GetValueOrDefault(fp.FingerprintId, 0),
                CorrectionCount = fp.CorrectionCount,
                CachedBotProbability = fp.CachedBotProbability,
                // RiskBand derived at read (verified-aware), never a stored band.
                CachedRiskBand = Mostlylucid.BotDetection.Identity.FingerprintRiskProjection.Compose(fp).RiskBand.ToString(),
                CachedScoreUpdatedAt = fp.CachedScoreUpdatedAt,
                FirstSeen = fp.FirstSeen,
                LastSeen = fp.LastSeen,
                ArchetypeOrigin = fp.ArchetypeOrigin,
                AmbiguityPersistence = fp.AmbiguityPersistence
            })
            .ToList();

        return new Models.IdentitiesListModel
        {
            Identities = rows,
            TotalCount = ordered.Count,
            Page = page,
            PageSize = pageSize,
            IdentityEnabled = true,
            BasePath = _options.BasePath.TrimEnd('/')
        };
    }

    /// <summary>
    ///     POST /api/identities/{id}/reverify — runs an on-demand L2 verification check
    ///     against the fingerprint's most recent observation. Returns the row HTML so HTMX
    ///     can swap it in place.
    /// </summary>
    private async Task ServeIdentityReverifyAsync(HttpContext context, string relativePath)
    {
        // Path shape: api/identities/{id}/reverify
        var fingerprintId = ExtractFingerprintId(relativePath, "/reverify");
        if (string.IsNullOrEmpty(fingerprintId))
        {
            context.Response.StatusCode = 400;
            return;
        }

        var driftService = context.RequestServices.GetService<BotDetection.Identity.FingerprintDriftService>();
        if (driftService is null)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync("Identity drift service not registered");
            return;
        }

        var result = await driftService.VerifyOneAsync(fingerprintId, context.RequestAborted);
        if (result is null)
        {
            context.Response.StatusCode = 404;
            return;
        }
        await RenderSingleIdentityRowAsync(context, fingerprintId);
    }

    /// <summary>
    ///     POST /api/identities/{id}/run-ai — invokes the slow-path classifier (LLM escalation
    ///     included) against the fingerprint's metadata, writes the verdict back to the
    ///     fingerprint's cached score, and returns the updated row HTML for HTMX swap. When no
    ///     LLM provider is registered, returns the row unchanged with a header explaining the
    ///     no-provider state so the operator sees the click registered but nothing changed.
    /// </summary>
    private async Task ServeIdentityRunAiAsync(HttpContext context, string relativePath)
    {
        var fingerprintId = ExtractFingerprintId(relativePath, "/run-ai");
        if (string.IsNullOrEmpty(fingerprintId))
        {
            context.Response.StatusCode = 400;
            return;
        }

        var aiService = context.RequestServices.GetService<BotDetection.Identity.IdentityAiOpinionService>();
        if (aiService is null)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync("Identity AI opinion service not registered");
            return;
        }

        var result = await aiService.RunAsync(fingerprintId, context.RequestAborted);
        // Surface status to the operator via response header so the dashboard can show why
        // a click was a no-op (e.g. "no-llm-provider", "parse-error") without changing the
        // visible row layout.
        context.Response.Headers["X-StyloBot-AiOpinion-Status"] = result.Status.ToHeaderValue();
        if (result.Status == BotDetection.Identity.IdentityAiOpinionStatus.Ok && result.BotProbability is { } prob)
            context.Response.Headers["X-StyloBot-AiOpinion-Probability"] = prob.ToString("F2");
        if (!string.IsNullOrEmpty(result.ErrorDetail))
        {
            // HTTP header values must be ASCII without CR/LF; clean before forwarding.
            var detail = result.ErrorDetail.Replace('\n', ' ').Replace('\r', ' ');
            if (detail.Length > 200) detail = detail[..200] + "…";
            context.Response.Headers["X-StyloBot-AiOpinion-Detail"] = detail;
        }

        await RenderSingleIdentityRowAsync(context, fingerprintId);
    }

    private static string? ExtractFingerprintId(string relativePath, string suffix)
    {
        const string prefix = "api/identities/";
        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = relativePath[prefix.Length..];
        if (!rest.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = rest[..^suffix.Length];
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private async Task RenderSingleIdentityRowAsync(HttpContext context, string fingerprintId)
    {
        // Targeted O(1) lookup — no full-table scan or page-N enumeration. Two queries:
        // the fingerprint row + a focused unabsorbed-obs count for that one id.
        var store = context.RequestServices.GetService<BotDetection.Identity.IFingerprintReader>();
        if (store is null)
        {
            context.Response.StatusCode = 503;
            return;
        }
        var fp = await store.GetFingerprintAsync(fingerprintId, context.RequestAborted);
        if (fp is null)
        {
            context.Response.StatusCode = 404;
            return;
        }
        var unabsorbed = await store.GetUnabsorbedObservationCountAsync(fingerprintId, context.RequestAborted);
        var entry = new Models.IdentityListEntry
        {
            FingerprintId = fp.FingerprintId,
            InferredClientType = fp.InferredClientType,
            InferredTypeConfidence = fp.InferredTypeConfidence,
            CentroidMaturity = fp.CentroidMaturity,
            ObservationCount = fp.ObservationCount,
            UnabsorbedObservations = unabsorbed,
            CorrectionCount = fp.CorrectionCount,
            CachedBotProbability = fp.CachedBotProbability,
            // RiskBand derived at read (verified-aware), never a stored band.
            CachedRiskBand = Mostlylucid.BotDetection.Identity.FingerprintRiskProjection.Compose(fp).RiskBand.ToString(),
            CachedScoreUpdatedAt = fp.CachedScoreUpdatedAt,
            FirstSeen = fp.FirstSeen,
            LastSeen = fp.LastSeen,
            ArchetypeOrigin = fp.ArchetypeOrigin,
            AmbiguityPersistence = fp.AmbiguityPersistence
        };
        var rowModel = new Models.IdentitiesListModel
        {
            Identities = new[] { entry },
            TotalCount = 1,
            Page = 1,
            PageSize = 1,
            IdentityEnabled = true,
            BasePath = _options.BasePath.TrimEnd('/')
        };
        context.Response.ContentType = "text/html";
        // Render the full partial then extract the single row by id. Cheap and avoids a
        // separate template; the table fragment HTMX swaps with outerHTML matches the
        // <tr id="identity-row-{id}"> the list view emits.
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbIdentitiesList/Default.cshtml", rowModel, context);
        var marker = $"<tr id=\"identity-row-{fingerprintId}\"";
        var startIdx = html.IndexOf(marker, StringComparison.Ordinal);
        if (startIdx < 0)
        {
            await context.Response.WriteAsync(html); // fallback: full partial
            return;
        }
        var endIdx = html.IndexOf("</tr>", startIdx, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            await context.Response.WriteAsync(html);
            return;
        }
        await context.Response.WriteAsync(html[startIdx..(endIdx + 5)]);
    }

    private async Task<ThreatsListModel> BuildThreatsModelAsync()
    {
        List<ThreatEntry> threats;
        // Serve the broadcaster-precomputed threats; fall through only when cold.
        var cachedThreats = _aggregateCache.Current.Threats;
        try { threats = cachedThreats.Count > 0 ? cachedThreats : await _eventStore.GetThreatsAsync(20); }
        catch { threats = []; }

        return new ThreatsListModel
        {
            Threats = threats,
            TotalCount = threats.Count,
            ActiveHoneypotSessions = threats.Count(t => t.InHoneypot),
            BasePath = _options.BasePath.TrimEnd('/')
        };
    }

    /// <summary>Render the recent activity partial with server-side sort and pagination.</summary>
    private async Task ServeSessionsPartialAsync(HttpContext context)
    {
        var filter = context.Request.Query["filter"].FirstOrDefault();
        var page = int.TryParse(context.Request.Query["page"], out var p) ? p : 1;
        var pageSize = int.TryParse(context.Request.Query["pageSize"], out var ps) ? ps : 25;
        var model = await BuildSessionsModel(context, page, pageSize, filter);

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbSessionsList/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    private async Task ServeSessionDetailPartialAsync(HttpContext context)
    {
        var sig = context.Request.Query["sig"].FirstOrDefault();
        var idStr = context.Request.Query["id"].FirstOrDefault();
        var groupStr = context.Request.Query["group"].FirstOrDefault();

        var sessionStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.IDetectionArchive>();
        if (string.IsNullOrEmpty(sig))
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>Missing signature parameter</div>");
            return;
        }

        var decodedSig = Uri.UnescapeDataString(sig);
        BotDetection.Data.PersistedSession? s = null;

        if (sessionStore != null)
        {
            if (long.TryParse(idStr, out var sessionId) && sessionId > 0)
            {
                var candidates = await sessionStore.GetSessionsAsync(decodedSig, 500);
                s = candidates.FirstOrDefault(x => x.Id == sessionId) ?? (candidates.Count > 0 ? candidates[0] : null);
            }
            else
            {
                var fallback = await sessionStore.GetSessionsAsync(decodedSig, 1);
                s = fallback.Count > 0 ? fallback[0] : null;
            }
        }

        if (s == null)
        {
            // No persisted session yet -- sessions finalise after 30 min idle. Fall back
            // to rendering the synthetic detection group (the same data the signature
            // detail page's "in-flight session" row was built from). The ?group=<unixMs>
            // parameter narrows to the group around that start time; without it we show
            // the latest activity period.
            await RenderSyntheticSessionDetailAsync(context, decodedSig, groupStr);
            return;
        }
        var cspNonce = GetOrCreateCspNonce(context);

        var model = new SessionDetailModel
        {
            Id = s.Id,
            Signature = s.Signature,
            BasePath = _options.BasePath.TrimEnd('/'),
            CspNonce = cspNonce,
            StartedAt = s.StartedAt,
            EndedAt = s.EndedAt,
            RequestCount = s.RequestCount,
            DominantState = s.DominantState,
            IsBot = s.IsBot,
            AvgBotProbability = s.AvgBotProbability,
            RiskBand = s.RiskBand,
            ErrorCount = s.ErrorCount,
            TimingEntropy = s.TimingEntropy,
            Maturity = s.Maturity,
            TransitionCounts = s.TransitionCountsJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson)
                : null,
            Paths = s.PathsJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(s.PathsJson)
                : null
        };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_SessionDetail.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     Serves inline HTML for signature sessions (loaded via HTMX in signature detail page).
    ///     Shows session timeline with Markov chain previews and path sequences.
    /// </summary>
    // Single pathway: every dashboard read that needs to reconstruct an "in-flight session"
    // groups persisted detection events by a 30-minute idle gap. Used by the filmstrip,
    // the Behavioral Sessions table, and the synthetic session-detail page. There is no
    // in-memory fallback -- the Postgres event store is the source of truth.
    private static List<List<DashboardDetectionEvent>> GroupDetectionsBySessionGap(
        IReadOnlyList<DashboardDetectionEvent> detections)
    {
        var groups = new List<List<DashboardDetectionEvent>>();
        if (detections.Count == 0) return groups;
        var ordered = detections.OrderBy(d => d.Timestamp).ToList();
        var current = new List<DashboardDetectionEvent> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            if ((ordered[i].Timestamp - ordered[i - 1].Timestamp).TotalMinutes > 30)
            {
                groups.Add(current);
                current = new List<DashboardDetectionEvent>();
            }
            current.Add(ordered[i]);
        }
        groups.Add(current);
        return groups;
    }

    private async Task RenderSyntheticSessionDetailAsync(HttpContext context, string signature, string? groupStr)
    {
        context.Response.ContentType = "text/html";
        var eventStore = context.RequestServices.GetService<IDashboardEventStore>();
        if (eventStore == null)
        {
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>Session not found and no event store available</div>");
            return;
        }

        var detections = await eventStore.GetDetectionsAsync(new DashboardFilter
        {
            SignatureId = signature,
            Limit = 200
        });

        if (detections.Count == 0)
        {
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>No activity recorded for this signature yet.</div>");
            return;
        }

        var groups = GroupDetectionsBySessionGap(detections);

        // Pick the group whose start timestamp matches the requested group key, else
        // most recent (groups were ordered ascending so OrderByDescending picks newest).
        List<DashboardDetectionEvent> selected;
        if (long.TryParse(groupStr, out var groupKey))
        {
            var match = groups.FirstOrDefault(g =>
                new DateTimeOffset(g[0].Timestamp).ToUnixTimeMilliseconds() == groupKey);
            selected = match ?? groups.OrderByDescending(g => g[0].Timestamp).First();
        }
        else
        {
            selected = groups.OrderByDescending(g => g[0].Timestamp).First();
        }

        var startedAt = selected[0].Timestamp;
        var endedAt = selected[^1].Timestamp;
        var duration = (endedAt - startedAt).TotalMinutes;
        var avgProb = selected.Average(d => d.BotProbability);
        var maxThreat = selected.Max(d => d.BotProbability);
        var dominantRisk = selected.GroupBy(d => d.RiskBand).OrderByDescending(g => g.Count()).First().Key;
        var pathCounts = selected.GroupBy(d => d.Path ?? "(unknown)")
            .Select(g => new { Path = g.Key, Count = g.Count() })
            .OrderByDescending(p => p.Count)
            .ToList();
        var statusCounts = selected.GroupBy(d => d.StatusCode)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .OrderByDescending(p => p.Count)
            .ToList();

        var probClass = avgProb >= 0.7 ? "text-error" : avgProb >= 0.4 ? "text-warning" : "text-success";
        var riskClass = dominantRisk is "VeryHigh" or "High" ? "text-error" : dominantRisk is "Elevated" or "Medium" ? "text-warning" : "text-success";

        var sb = new System.Text.StringBuilder();
        sb.Append("<div class=\"rounded-xl border p-4 mb-4\" style=\"border-color: var(--sb-card-border); background: var(--sb-card-bg);\">");
        sb.Append("<div class=\"flex items-center justify-between mb-2\">");
        sb.Append("<div class=\"flex items-center gap-2\">");
        sb.Append("<h3 class=\"text-xs font-semibold text-base-content/70 uppercase\"><i class=\"bx bx-time text-sm\" style=\"color: var(--sb-accent);\"></i> Activity Period</h3>");
        sb.Append("<span class=\"text-[9px] px-1 py-0.5 rounded bg-warning/20 text-warning font-medium\">IN-FLIGHT</span>");
        sb.Append("</div>");
        sb.Append($"<span class=\"text-[10px] text-base-content/40\">{System.Net.WebUtility.HtmlEncode(signature[..Math.Min(16, signature.Length)])}...</span>");
        sb.Append("</div>");
        sb.Append("<div class=\"text-[10px] text-base-content/40 mb-3\">Sessions are finalised after 30 min of inactivity. This view shows the activity grouped from raw detection events.</div>");

        sb.Append("<div class=\"grid grid-cols-2 md:grid-cols-4 gap-3 text-xs\">");
        sb.Append($"<div><div class=\"text-[10px] uppercase text-base-content/40\">Started</div><div class=\"font-mono\">{startedAt:MMM dd HH:mm:ss}</div></div>");
        sb.Append($"<div><div class=\"text-[10px] uppercase text-base-content/40\">Duration</div><div class=\"font-mono\">{duration:F1}m</div></div>");
        sb.Append($"<div><div class=\"text-[10px] uppercase text-base-content/40\">Requests</div><div class=\"font-mono\">{selected.Count}</div></div>");
        sb.Append($"<div><div class=\"text-[10px] uppercase text-base-content/40\">Avg Bot Prob</div><div class=\"font-mono font-bold {probClass}\">{avgProb:P0}</div></div>");
        sb.Append($"<div><div class=\"text-[10px] uppercase text-base-content/40\">Peak Threat</div><div class=\"font-mono {probClass}\">{maxThreat:P0}</div></div>");
        sb.Append($"<div><div class=\"text-[10px] uppercase text-base-content/40\">Dominant Risk</div><div class=\"font-mono font-bold {riskClass}\">{dominantRisk}</div></div>");
        sb.Append($"<div><div class=\"text-[10px] uppercase text-base-content/40\">Unique Paths</div><div class=\"font-mono\">{pathCounts.Count}</div></div>");
        sb.Append($"<div><div class=\"text-[10px] uppercase text-base-content/40\">Status Codes</div><div class=\"font-mono\">{statusCounts.Count}</div></div>");
        sb.Append("</div>");
        sb.Append("</div>");

        // Per-request table
        sb.Append("<div class=\"rounded-xl border p-4 mb-4\" style=\"border-color: var(--sb-card-border); background: var(--sb-card-bg);\">");
        sb.Append("<h3 class=\"text-xs font-semibold text-base-content/70 uppercase mb-2\"><i class=\"bx bx-list-ul text-sm\" style=\"color: var(--sb-accent);\"></i> Requests</h3>");
        sb.Append("<div class=\"overflow-x-auto\"><table class=\"table table-xs w-full\"><thead>");
        sb.Append("<tr class=\"text-[10px] uppercase tracking-wider text-base-content/40\">");
        sb.Append("<th class=\"py-1\">Time</th><th class=\"py-1\">Method</th><th class=\"py-1\">Path</th><th class=\"py-1 text-right\">Status</th><th class=\"py-1 text-right\">Bot Prob</th><th class=\"py-1\">Risk</th><th class=\"py-1\">Action</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var d in selected.OrderByDescending(d => d.Timestamp).Take(50))
        {
            var rowProb = d.BotProbability >= 0.7 ? "text-error" : d.BotProbability >= 0.4 ? "text-warning" : "text-success";
            var rowRisk = d.RiskBand is "VeryHigh" or "High" ? "text-error" : d.RiskBand is "Elevated" or "Medium" ? "text-warning" : "text-base-content/60";
            sb.Append("<tr>");
            sb.Append($"<td class=\"py-1 text-[10px] text-base-content/50 whitespace-nowrap font-mono\">{d.Timestamp:HH:mm:ss}</td>");
            sb.Append($"<td class=\"py-1 text-[10px] text-base-content/60\">{System.Net.WebUtility.HtmlEncode(d.Method ?? "-")}</td>");
            sb.Append($"<td class=\"py-1 text-[10px] text-base-content/60 max-w-[400px] truncate\" title=\"{System.Net.WebUtility.HtmlEncode(d.Path ?? "")}\">{System.Net.WebUtility.HtmlEncode(d.Path ?? "-")}</td>");
            sb.Append($"<td class=\"py-1 text-[10px] text-right font-mono text-base-content/60\">{d.StatusCode}</td>");
            sb.Append($"<td class=\"py-1 text-[10px] text-right font-mono font-bold {rowProb}\">{d.BotProbability:P0}</td>");
            sb.Append($"<td class=\"py-1 text-[10px] {rowRisk}\">{System.Net.WebUtility.HtmlEncode(d.RiskBand ?? "-")}</td>");
            sb.Append($"<td class=\"py-1 text-[10px] text-base-content/60\">{System.Net.WebUtility.HtmlEncode(d.Action ?? "-")}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
        if (selected.Count > 50)
            sb.Append($"<div class=\"text-[10px] text-base-content/40 mt-2 text-center\">Showing 50 of {selected.Count} requests</div>");
        sb.Append("</div>");

        await context.Response.WriteAsync(sb.ToString());
    }

    private async Task ServeSignatureSessionsPartialAsync(HttpContext context)
    {
        var signature = context.Request.Query["signature"].FirstOrDefault() ?? "";

        if (string.IsNullOrEmpty(signature))
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                "<div class=\"text-xs text-base-content/40 py-4 text-center\">No session data available</div>");
            return;
        }

        var decodedSig = Uri.UnescapeDataString(signature);
        var sessionStore = context.RequestServices.GetService<BotDetection.Data.IDetectionArchive>();

        // Bridge signature key spaces (multi-factor → waveform)
        var sessions = sessionStore != null
            ? await sessionStore.GetSessionsAsync(decodedSig, 20)
            : new List<BotDetection.Data.PersistedSession>();

        // Detection fallback: if no finalized sessions, synthesize from dashboard_detections
        if (sessions.Count == 0)
        {
            var eventStore = context.RequestServices.GetService<IDashboardEventStore>();
            if (eventStore != null)
            {
                var detections = await eventStore.GetDetectionsAsync(new DashboardFilter
                {
                    SignatureId = decodedSig,
                    Limit = 100
                });

                if (detections.Count > 0)
                {
                    var syntheticGroups = GroupDetectionsBySessionGap(detections);

                    var syntheticHtml = new System.Text.StringBuilder();
                    syntheticHtml.Append("<div class=\"overflow-x-auto\"><table class=\"table table-xs w-full\"><thead>");
                    syntheticHtml.Append("<tr class=\"text-[10px] uppercase tracking-wider text-base-content/40\">");
                    syntheticHtml.Append("<th class=\"py-1\">Started</th>");
                    syntheticHtml.Append("<th class=\"py-1\">Duration</th>");
                    syntheticHtml.Append("<th class=\"py-1 text-right\">Requests</th>");
                    syntheticHtml.Append("<th class=\"py-1\">Dominant Risk</th>");
                    syntheticHtml.Append("<th class=\"py-1 text-right\">Bot %</th>");
                    syntheticHtml.Append("<th class=\"py-1\">Risk Band</th>");
                    syntheticHtml.Append("<th class=\"py-1\">Paths</th>");
                    syntheticHtml.Append("</tr></thead><tbody>");

                    foreach (var group in syntheticGroups.OrderByDescending(g => g[0].Timestamp))
                    {
                        var avgProb = group.Average(d => d.BotProbability);
                        var dominantRisk = group.GroupBy(d => d.RiskBand).OrderByDescending(g => g.Count()).First().Key;
                        var paths = group.Select(d => d.Path).Where(p => p != null).Distinct().Take(4).ToList();
                        var pathPreview = string.Join(" > ", paths) + (group.Select(d => d.Path).Distinct().Count() > 4 ? $" (+{group.Select(d => d.Path).Distinct().Count() - 4})" : "");
                        var duration = (group[^1].Timestamp - group[0].Timestamp).TotalMinutes;
                        var probClass = avgProb >= 0.7 ? "text-error" : avgProb >= 0.4 ? "text-warning" : "text-success";
                        var riskClass = dominantRisk is "VeryHigh" or "High" ? "text-error" : dominantRisk is "Elevated" or "Medium" ? "text-warning" : "text-success";

                        // Synthetic group click-through: no real session id yet, so we pass
                        // sig + a synthetic 'group' parameter (the group's first timestamp as
                        // unix ms). The session detail page falls back to rendering the
                        // detections-derived view when no persisted session matches.
                        var groupKey = new DateTimeOffset(group[0].Timestamp).ToUnixTimeMilliseconds();
                        var syntheticHref = $"{_options.BasePath.TrimEnd('/')}/session?sig={Uri.EscapeDataString(decodedSig)}&group={groupKey}";
                        syntheticHtml.Append($"<tr class=\"hover:bg-base-200/50 cursor-pointer\" data-href=\"{syntheticHref}\">");
                        syntheticHtml.Append($"<td class=\"py-1 text-[10px] text-base-content/50 whitespace-nowrap\">{group[0].Timestamp:MMM dd HH:mm}</td>");
                        syntheticHtml.Append($"<td class=\"py-1 text-xs text-base-content/60\">{duration:F1}m</td>");
                        syntheticHtml.Append($"<td class=\"py-1 text-right text-xs font-mono\">{group.Count}</td>");
                        syntheticHtml.Append($"<td class=\"py-1 text-[10px] text-base-content/60\">{dominantRisk}</td>");
                        syntheticHtml.Append($"<td class=\"py-1 text-right text-xs font-bold {probClass}\">{avgProb:P0}</td>");
                        syntheticHtml.Append($"<td class=\"py-1 text-[10px] {riskClass}\">{dominantRisk}</td>");
                        syntheticHtml.Append($"<td class=\"py-1 text-[10px] text-base-content/40 max-w-[250px] truncate\" title=\"{System.Net.WebUtility.HtmlEncode(pathPreview)}\">{System.Net.WebUtility.HtmlEncode(pathPreview)}</td>");
                        syntheticHtml.Append("</tr>");
                    }

                    syntheticHtml.Append("</tbody></table></div>");
                    syntheticHtml.Append($"<div class=\"text-[10px] text-base-content/30 mt-2\">{syntheticGroups.Count} activity period(s) from {detections.Count} detections <span class=\"italic\">(in-flight sessions)</span></div>");

                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync(syntheticHtml.ToString());
                    return;
                }
            }

            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                "<div class=\"text-xs text-base-content/40 py-4 text-center\">No sessions recorded yet. Sessions are created when a visitor's activity gap exceeds 30 minutes.</div>");
            return;
        }

        // Pre-compute inter-session velocity vectors
        var vectors = sessions.Select(s => s.Vector is { Length: > 0 }
            ? BotDetection.Data.SqliteDetectionArchive.DeserializeVector(s.Vector) : null).ToList();

        var html = new System.Text.StringBuilder();
        html.Append("<div class=\"overflow-x-auto\"><table class=\"table table-xs w-full\"><thead>");
        html.Append("<tr class=\"text-[10px] uppercase tracking-wider text-base-content/40\">");
        html.Append("<th class=\"py-1\">Started</th>");
        html.Append("<th class=\"py-1\">Duration</th>");
        html.Append("<th class=\"py-1 text-right\">Requests</th>");
        html.Append("<th class=\"py-1\">Dominant</th>");
        html.Append("<th class=\"py-1 text-right\">Bot %</th>");
        html.Append("<th class=\"py-1\">Risk</th>");
        html.Append("<th class=\"py-1 text-right\">Drift</th>");
        html.Append("<th class=\"py-1 text-right\">Errors</th>");
        html.Append("<th class=\"py-1\">Paths</th>");
        html.Append("</tr></thead><tbody>");

        for (var si = 0; si < sessions.Count; si++)
        {
            var s = sessions[si];
            var duration = (s.EndedAt - s.StartedAt).TotalMinutes;
            var probClass = s.AvgBotProbability >= 0.7 ? "text-error" : s.AvgBotProbability >= 0.4 ? "text-warning" : "text-success";
            var riskClass = s.RiskBand is "VeryHigh" or "High" ? "text-error" : s.RiskBand is "Elevated" or "Medium" ? "text-warning" : "text-success";

            // Compute inter-session velocity (drift from previous session)
            double? velocity = null;
            if (si < sessions.Count - 1 && vectors[si] != null && vectors[si + 1] != null)
                velocity = BotDetection.Analysis.SessionVectorizer.VelocityMagnitude(
                    BotDetection.Analysis.SessionVectorizer.ComputeVelocity(vectors[si]!, vectors[si + 1]!));
            var driftClass = velocity switch
            {
                >= 0.6 => "text-error font-bold",
                >= 0.3 => "text-warning",
                _ => "text-base-content/40"
            };

            // Parse paths JSON for preview
            var pathPreview = "";
            if (!string.IsNullOrEmpty(s.PathsJson))
            {
                try
                {
                    var paths = JsonSerializer.Deserialize<List<string>>(s.PathsJson);
                    if (paths is { Count: > 0 })
                        pathPreview = string.Join(" > ", paths.Take(4)) + (paths.Count > 4 ? $" (+{paths.Count - 4})" : "");
                }
                catch { }
            }

            // Row is a click-through to /dashboard/session?sig=...&id=... -- the new
            // session detail page wraps _SessionDetail.cshtml with the shared navbar.
            // Click delegation lives in the signature detail page's script block so
            // the row can have data-href without an inline onclick handler (CSP-safe).
            var sessionHref = $"{_options.BasePath.TrimEnd('/')}/session?sig={Uri.EscapeDataString(decodedSig)}&id={s.Id}";
            html.Append($"<tr class=\"hover:bg-base-200/50 cursor-pointer\" data-href=\"{sessionHref}\">");
            html.Append($"<td class=\"py-1 text-[10px] text-base-content/50 whitespace-nowrap\">{s.StartedAt:MMM dd HH:mm}</td>");
            html.Append($"<td class=\"py-1 text-xs text-base-content/60\">{duration:F1}m</td>");
            html.Append($"<td class=\"py-1 text-right text-xs font-mono\">{s.RequestCount}</td>");
            html.Append($"<td class=\"py-1 text-[10px] text-base-content/60\">{s.DominantState}</td>");
            html.Append($"<td class=\"py-1 text-right text-xs font-bold {probClass}\">{s.AvgBotProbability:P0}</td>");
            html.Append($"<td class=\"py-1 text-[10px] {riskClass}\">{s.RiskBand}</td>");
            html.Append(velocity.HasValue
                ? $"<td class=\"py-1 text-right text-[10px] font-mono {driftClass}\" title=\"Inter-session velocity: behavioral shift magnitude\">{velocity.Value:F2}</td>"
                : "<td class=\"py-1 text-right text-[10px] text-base-content/20\">-</td>");
            html.Append($"<td class=\"py-1 text-right text-xs {(s.ErrorCount > 0 ? "text-error font-bold" : "text-base-content/40")}\">{s.ErrorCount}</td>");
            html.Append($"<td class=\"py-1 text-[10px] text-base-content/40 max-w-[250px] truncate\" title=\"{System.Net.WebUtility.HtmlEncode(pathPreview)}\">{System.Net.WebUtility.HtmlEncode(pathPreview)}</td>");
            html.Append("</tr>");

            // Transition preview row (collapsible)
            if (!string.IsNullOrEmpty(s.TransitionCountsJson))
            {
                try
                {
                    var transitions = JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson);
                    if (transitions is { Count: > 0 })
                    {
                        var top5 = transitions.OrderByDescending(t => t.Value).Take(5);
                        var bars = string.Join("", top5.Select(t =>
                            $"<span class=\"inline-flex items-center gap-0.5 mr-2\"><span class=\"text-base-content/50\">{System.Net.WebUtility.HtmlEncode(t.Key)}</span><span class=\"font-bold\">{t.Value}</span></span>"));
                        html.Append($"<tr><td colspan=\"9\" class=\"py-0.5 px-4 text-[9px] text-base-content/30 border-b\" style=\"border-color: var(--sb-card-divider);\">{bars}</td></tr>");
                    }
                }
                catch { }
            }
        }

        html.Append("</tbody></table></div>");
        html.Append($"<div class=\"text-[10px] text-base-content/30 mt-2\">{sessions.Count} session(s) recorded</div>");

        await context.Response.WriteAsync(html.ToString());
    }

    private async Task ServeSessionFingerprintsPartialAsync(HttpContext context)
    {
        var signature = Uri.UnescapeDataString(context.Request.Query["signature"].FirstOrDefault() ?? "");
        long? currentId = long.TryParse(context.Request.Query["currentId"].FirstOrDefault(), out var parsedId) ? parsedId : null;
        _ = bool.TryParse(context.Request.Query["compact"].FirstOrDefault(), out var compact);

        context.Response.ContentType = "text/html";

        if (string.IsNullOrEmpty(signature))
        {
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>No signature specified</div>");
            return;
        }

        var sessionStore = context.RequestServices.GetService<BotDetection.Data.IDetectionArchive>();
        if (sessionStore == null)
        {
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>Session store unavailable</div>");
            return;
        }

        var limit = compact ? 10 : 20;
        var raw = await sessionStore.GetSessionsAsync(signature, limit);

        var entries = raw.Select(s =>
        {
            var vector = BotDetection.Data.SqliteDetectionArchive.DeserializeVector(s.Vector);
            var stateFreqs = new float[10];
            if (vector != null && vector.Length >= 110)
                Array.Copy(vector, 100, stateFreqs, 0, 10);
            return new Models.SessionFingerprintEntry
            {
                Id           = s.Id,
                StartedAt    = s.StartedAt,
                IsBot        = s.IsBot,
                RiskBand     = s.RiskBand,
                Probability  = s.AvgBotProbability,
                RequestCount = s.RequestCount,
                StateFreqs   = stateFreqs
            };
        }).ToList();

        // Behavioural History was first wired to read in-flight session state from
        // BotDetection.Analysis.SessionStore -- a ConcurrentDictionary that doesn't
        // survive a gateway restart and routinely disagreed with the Behavioural Sessions
        // table on the same page (table source: persisted detections; filmstrip source:
        // volatile cache). Now both partials reconstruct in-flight activity from the
        // same Postgres event stream via GroupDetectionsBySessionGap, so they always
        // agree. Synthesised entries carry a negative Id (-unixMs of group start) so
        // the filmstrip Razor template can route the click to ?group=<key> instead of
        // ?id=<sessionId>. State freqs are zero (no finalised vector yet) -- the radar
        // renders as a centre dot, which is the intended "session in progress" glyph.
        if (entries.Count == 0)
        {
            var eventStore = context.RequestServices.GetService<IDashboardEventStore>();
            if (eventStore != null)
            {
                var detections = await eventStore.GetDetectionsAsync(new DashboardFilter
                {
                    SignatureId = signature,
                    Limit = 200
                });
                foreach (var group in GroupDetectionsBySessionGap(detections)
                    .OrderByDescending(g => g[0].Timestamp)
                    .Take(limit))
                {
                    var groupKey = new DateTimeOffset(group[0].Timestamp).ToUnixTimeMilliseconds();
                    var avgProb = group.Average(d => d.BotProbability);
                    var dominantRisk = group.GroupBy(d => d.RiskBand)
                        .OrderByDescending(g2 => g2.Count())
                        .First().Key ?? "InFlight";
                    entries.Add(new Models.SessionFingerprintEntry
                    {
                        Id = -groupKey,
                        StartedAt = group[0].Timestamp,
                        IsBot = avgProb >= 0.5,
                        RiskBand = dominantRisk,
                        Probability = avgProb,
                        RequestCount = group.Count,
                        StateFreqs = new float[10]
                    });
                }
            }
        }

        var cspNonce = GetOrCreateCspNonce(context);

        var model = new Models.SessionFingerprintsModel
        {
            Signature        = signature,
            CurrentSessionId = currentId,
            Sessions         = entries,
            BasePath         = _options.BasePath.TrimEnd('/'),
            CspNonce         = cspNonce,
            CompactMode      = compact
        };

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_SessionFingerprints.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    private async Task ServeApprovalFormPartialAsync(HttpContext context)
    {
        var cspNonce = context.Items["CspNonce"]?.ToString() ?? "";

        // Collect lockable signal values from a recent detection for this signature
        Dictionary<string, string>? currentSignals = null;
        var sig = context.Request.Query["signature"].FirstOrDefault();
        if (!string.IsNullOrEmpty(sig))
        {
            // Get the most recent detection for this signature to show lockable values
            var signatureCache = context.RequestServices.GetService<SignatureAggregateCache>();
            if (signatureCache != null)
            {
                var (visitors, _, _, _) = signatureCache.GetFiltered("all", "lastSeen", "desc", 1, 100);
                // Find matching visitor and extract signal-like properties
                currentSignals = new Dictionary<string, string>();
                // Common lockable dimensions
                currentSignals["ua.family"] = context.Request.Headers.UserAgent.ToString().Split('/')[0];
            }
        }

        var model = new ApprovalFormModel
        {
            BasePath = _options.BasePath,
            CspNonce = cspNonce,
            CurrentSignals = currentSignals
        };

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_ApprovalForm.cshtml", model, context);
        context.Response.ContentType = "text/html";
        await context.Response.WriteAsync(html);
    }

    /// <summary>Render the user agents list partial with server-side filter, sort, and pagination.</summary>
    private async Task ServeUserAgentsPartialAsync(HttpContext context)
    {
        var cached = _aggregateCache.Current.UserAgents;
        var allUas = cached.Count > 0 ? cached : await ComputeUserAgentsFallbackAsync();
        var filter = context.Request.Query["filter"].FirstOrDefault() ?? "all";
        var sortField = context.Request.Query["sort"].FirstOrDefault() ?? "requests";
        var sortDir = context.Request.Query["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(context.Request.Query["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(context.Request.Query["pageSize"].FirstOrDefault(), out var ps) && ps is > 0 and <= 100 ? ps : 25;

        var model = BuildUserAgentsModel(filter, sortField, sortDir, page, pageSize, allUas);

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbUserAgentsList/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     HTMX OOB update endpoint - renders multiple partials in a single response.
    ///     Called by the SignalR coordinator when widgets need refreshing.
    ///     Query param: widgets=summary,visitors,countries (comma-separated widget IDs)
    ///     Per-widget params: {widgetId}.{param}=value (e.g. visitors.page=2, visitors.filter=bots)
    /// </summary>
    private async Task ServeOobUpdateAsync(HttpContext context)
    {
        // Live OOB refresh = the dashboard is open and consuming; keep the aggregate
        // cache warm so the broadcaster idle-skip doesn't park while someone's watching.
        _aggregateCache.MarkHit();
        var widgetList = context.Request.Query["widgets"].FirstOrDefault() ?? "summary,visitors";
        var widgets = widgetList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        context.Response.ContentType = "text/html";

        var sb = new System.Text.StringBuilder();
        foreach (var widget in widgets)
        {
            var html = await RenderOobWidgetAsync(context, widget);
            if (!string.IsNullOrEmpty(html))
                sb.Append(html);
        }
        await context.Response.WriteAsync(sb.ToString());
    }

    /// <summary>
    ///     Render a single widget partial with per-widget params, 2s render cache, and
    ///     hx-swap-oob="true" injection on the root element.
    /// </summary>
    private async Task<string> RenderOobWidgetAsync(HttpContext context, string widgetId)
    {
        var q = ExtractWidgetParams(context, widgetId);
        var cacheKey = ComputeWidgetCacheKey(widgetId, q);

        if (_widgetCache.TryGetValue(cacheKey, out string? cached) && cached != null)
            return cached;

        try
        {
            var html = widgetId switch
            {
                "summary" => await RenderPartialAsync(context, "/Views/Shared/Components/SbSummaryStats/Default.cshtml",
                    await BuildSummaryStatsModelAsync(context)),
                "visitors" => await RenderVisitorPartialAsync(context, q),
                "countries" => await RenderCountryPartialAsync(context, q),
                "endpoints" => await RenderEndpointPartialAsync(context, q),
                "clusters" => await RenderPartialAsync(context, "/Views/StyloBot/Dashboard/_ClustersList.cshtml", await BuildClustersModelAsync(context)),
                "useragents" => await RenderUaPartialAsync(context, q),
                "topbots" or "top-visitors" or "live-visitors" or "live-activity" => await RenderPartialAsync(context, "/Views/Shared/Components/SbTopBots/Default.cshtml", await BuildTopBotsModelFromQuery(widgetId, q)),
                "sessions" => await RenderPartialAsync(context, "/Views/Shared/Components/SbSessionsList/Default.cshtml",
                    await BuildSessionsModel(context,
                        page: WidgetRenderHelpers.QueryPage(q),
                        pageSize: WidgetRenderHelpers.QueryPageSize(q, 25),
                        filter: q["filter"].FirstOrDefault())),
                "your-detection" => await RenderPartialAsync(context, "/Views/StyloBot/Dashboard/_YourDetection.cshtml", await BuildYourDetectionPartialModel(context)),
                _ => ""
            };

            // Inject hx-swap-oob="true" into the root element so HTMX swaps it in place.
            if (!string.IsNullOrEmpty(html))
                html = InjectOobAttribute(html);

            if (!string.IsNullOrEmpty(html))
                _widgetCache.Set(cacheKey, html, WidgetCacheTtl);

            return html ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to render OOB widget: {Widget}", widgetId);
            return "";
        }
    }

    /// <summary>
    ///     Injects hx-swap-oob="true" into the first opening tag of the HTML fragment.
    ///     Delegates to <see cref="WidgetRenderHelpers.InjectOobAttribute"/>.
    /// </summary>
    private static string InjectOobAttribute(string html) =>
        WidgetRenderHelpers.InjectOobAttribute(html);

    private static IQueryCollection ExtractWidgetParams(HttpContext context, string widgetId) =>
        WidgetRenderHelpers.ExtractWidgetParams(context, widgetId);

    private static string ComputeWidgetCacheKey(string widgetId, IQueryCollection q) =>
        WidgetRenderHelpers.ComputeWidgetCacheKey(widgetId, q);

    private async Task<string> RenderPartialAsync<T>(HttpContext context, string viewPath, T model)
        where T : notnull
        => await _razorViewRenderer.RenderViewToStringAsync(viewPath, model, context);

    private async Task<string> RenderVisitorPartialAsync(HttpContext context, IQueryCollection? q = null)
    {
        q ??= context.Request.Query;
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sortField = q["sort"].FirstOrDefault() ?? "lastSeen";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;

        // V1: same URL filter set the dedicated partial endpoint binds. Forwarding
        // here keeps the OOB widget swap path symmetric with the HTMX single-widget
        // path so a SignalR beacon-driven refresh preserves the active filter chips.
        var country = NullIfEmpty(q["country"].FirstOrDefault());
        var botType = NullIfEmpty(q["bot_type"].FirstOrDefault());
        var threat = NullIfEmpty(q["threat"].FirstOrDefault());
        var fingerprint = NullIfEmpty(q["fingerprint"].FirstOrDefault());
        var internalOnly = string.Equals(q["internal"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

        // Same event-store read-through pattern as ServeVisitorListPartialAsync. The
        // local visitor cache is no longer the source of truth for the widget render
        // path -- the event store is.
        var raw = await _eventStore.GetTopBotsAsync(
            count: _visitorListMaxEntries,
            startTime: DateTime.UtcNow.AddHours(-24),
            endTime: DateTime.UtcNow,
            audienceFilter: "all");
        var (items, totalCount, counts) = WidgetRenderHelpers.ProjectAsVisitors(
            raw, filter, sortField, sortDir, page, 24,
            country: country, botType: botType, threat: threat,
            fingerprintId: fingerprint, internalOnly: internalOnly);

        // Plan task 19: same gateway-projected drift badge enrichment the
        // dedicated visitor-list partial path runs. Keeps the multi-widget
        // bulk renderer in agreement with the single-widget HTMX endpoint.
        await Services.FingerprintDriftProjector.EnrichVisitorsAsync(
            items, context.RequestServices, context.RequestAborted, _logger);

        var model = new VisitorListModel
        {
            Visitors = items, Counts = counts,
            Filter = filter, SortField = sortField, SortDir = sortDir,
            Page = page, PageSize = 24, TotalCount = totalCount,
            BasePath = _options.BasePath.TrimEnd('/'),
            Country = country,
            BotType = botType,
            Threat = threat,
            FingerprintId = fingerprint,
            Internal = internalOnly,
        };
        return await _razorViewRenderer.RenderViewToStringAsync("/Views/Shared/Components/SbVisitorList/Default.cshtml", model, context);
    }

    private async Task<string> RenderCountryPartialAsync(HttpContext context, IQueryCollection? q = null)
    {
        q ??= context.Request.Query;
        var sortField = q["sort"].FirstOrDefault() ?? "total";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var data = await GetCountriesDataAsync();
        var model = BuildCountriesModel(sortField, sortDir, page, 20, data);
        return await _razorViewRenderer.RenderViewToStringAsync("/Views/Shared/Components/SbCountriesList/Default.cshtml", model, context);
    }

    private async Task<string> RenderEndpointPartialAsync(HttpContext context, IQueryCollection? q = null)
    {
        q ??= context.Request.Query;
        var sortField = q["sort"].FirstOrDefault() ?? "total";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var data = await GetEndpointsDataAsync(context);
        var model = BuildEndpointsModel(context, sortField, sortDir, page, 20, data);
        return await _razorViewRenderer.RenderViewToStringAsync("/Views/Shared/Components/SbEndpointsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderUaPartialAsync(HttpContext context, IQueryCollection? q = null)
    {
        q ??= context.Request.Query;
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sortField = q["sort"].FirstOrDefault() ?? "requests";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var cached = _aggregateCache.Current.UserAgents;
        var uas = cached.Count > 0 ? cached : await ComputeUserAgentsFallbackAsync();
        var model = BuildUserAgentsModel(filter, sortField, sortDir, page, 25, uas);
        return await _razorViewRenderer.RenderViewToStringAsync("/Views/Shared/Components/SbUserAgentsList/Default.cshtml", model, context);
    }

    /// <summary>Serve the signature detail page for a specific signature.</summary>
    private async Task ServeSignatureDetailAsync(HttpContext context, string signatureId)
    {
        if (string.IsNullOrEmpty(signatureId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing signature ID");
            return;
        }

        var decodedSignature = Uri.UnescapeDataString(signatureId);

        // Validate signature format
        if (decodedSignature.Length > 64 || !decodedSignature.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '+' or '/' or '='))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid signature format");
            return;
        }

        // Resolve through the merge-history chain BEFORE rendering. If the requested
        // signature was merged into another (convergence absorb, commercial labelling
        // flow), 301-redirect to the canonical URL. Without this, a featured signature
        // like Googlebot whose id ever shifted would 404 because GetSignatureAsync would
        // miss; with it, the URL stays linkable forever. No-op on the FOSS upstream
        // because no writer populates signature_merges yet, but the resolver MUST exist
        // upstream so future writers (commercial labelling, manual merges from the
        // dashboard) cause the right behaviour here without any other change.
        var resolverStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.IDetectionArchive>();
        if (resolverStore is not null)
        {
            var canonical = await resolverStore.ResolveSignatureAsync(decodedSignature, context.RequestAborted);
            if (!string.IsNullOrEmpty(canonical)
                && !string.Equals(canonical, decodedSignature, StringComparison.Ordinal))
            {
                var redirectBase = _options.BasePath.TrimEnd('/');
                context.Response.Redirect(
                    $"{redirectBase}/signature/{Uri.EscapeDataString(canonical)}",
                    permanent: true);
                return;
            }
        }

        var basePath = _options.BasePath.TrimEnd('/');
        var navBasePath = string.IsNullOrEmpty(_options.NavBasePath) ? _options.BasePath : _options.NavBasePath;
        navBasePath = navBasePath.TrimEnd('/');
        var cspNonce = GetOrCreateCspNonce(context);

        // Set same CSP as dashboard page
        context.Response.Headers.Remove("X-Frame-Options");
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        context.Response.Headers.Remove("Content-Security-Policy");
        var dashboardCsp = string.Join("; ",
            "default-src 'self'",
            "base-uri 'self'",
            "frame-ancestors 'self'",
            "object-src 'none'",
            "img-src 'self' data: https:",
            "font-src 'self' data: https://fonts.gstatic.com https://unpkg.com",
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://unpkg.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com",
            $"script-src 'self' 'nonce-{cspNonce}' 'unsafe-eval' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://unpkg.com https://cdn.tailwindcss.com",
            // worker-src for Monaco's language-service web workers (loaded from CDN as
            // blob: URLs after the AMD loader rewrites them). Safe to allow on the
            // dashboard origin since Monaco is the only consumer.
            "worker-src 'self' blob:",
            "connect-src 'self' ws: wss:");
        context.Response.Headers["Content-Security-Policy"] = dashboardCsp;

        SignatureDetailModel model;

        // Compute the 7×24 periodicity heatmap once for both model-build branches
        // (cache-hit + cache-miss). Aggregates from the existing requests table;
        // service returns null when unavailable so view-side null-handling renders
        // the "no temporal data yet" empty state.
        var heatmapService = context.RequestServices.GetService<Services.SignaturePeriodicityHeatmap>();
        var heatmap = heatmapService is null
            ? null
            : await heatmapService.ComputeAsync(decodedSignature, windowDays: 30, ct: context.RequestAborted);

        // "Looks like" -- vec0 KNN against the current centroid population, computed
        // per request because the answer drifts as new fingerprints are allocated and
        // existing ones absorb observations. Never cached on the row. Computed once
        // here so both model branches receive the same result.
        var looksLike = await ResolveLooksLikeAsync(context, decodedSignature);

        // Per-signature endpoint stats. Powers the Endpoints Visited table on the
        // detail page (replaces the old #/Path list with hit count + p95 + bot rate
        // + risk band per endpoint). Cap at top-25 most-hit endpoints so a noisy
        // crawler doesn't blow out the table; the view shows the count and an
        // overflow chip when truncated. Failures here are swallowed because the
        // view falls back to Model.Paths when this comes back empty.
        List<SignatureEndpointStats> endpointStats;
        try
        {
            endpointStats = await _eventStore.GetEndpointStatsForSignatureAsync(
                decodedSignature, topN: 25, ct: context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "GetEndpointStatsForSignatureAsync failed for {Signature}; falling back to Paths list",
                decodedSignature);
            endpointStats = new List<SignatureEndpointStats>();
        }

        // Source of truth is the event store. The SignatureAggregateCache is a write-through
        // hot path on the gateway (where DetectionBroadcastMiddleware updates it on every
        // detection), but on a remote-mode dashboard host it freezes at startup-warm values
        // and stale-reads here would disagree with the home card (which already hydrates
        // from the event store). Read the latest detection for headline values; treat the
        // cache as optional sparkline / hit-count enrichment only -- never headline.
        List<DashboardDetectionEvent> detections;
        try
        {
            detections = await _eventStore.GetDetectionsAsync(new DashboardFilter
            {
                SignatureId = decodedSignature,
                Limit = 50
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch detections for signature {Sig}", decodedSignature);
            detections = new List<DashboardDetectionEvent>();
        }

        if (detections.Count == 0)
        {
            await WriteSignatureNotFoundAsync(context, decodedSignature, basePath);
            return;
        }

        var latest = detections[0]; // GetDetectionsAsync returns DESC by timestamp.

        // 7-bucket fingerprint centroid radar shape -- same projection the home
        // card uses, so the visitor's polygon reads identically on both
        // surfaces. Resolved AFTER detections are fetched so the fallback chain
        // can use `latest`'s bot-name / bot-type to infer an archetype baseline
        // when no fingerprint binding exists (remote-mode hosts have no
        // SignatureAggregateCache, but always have a detection row). The
        // signature-detail page is reached because detections.Count > 0, so an
        // archetype-driven inferred polygon is always available -- the radar
        // never renders "Calibrating fingerprint" here.
        var fingerprintShape = await ResolveFingerprintShapeAsync(context, decodedSignature, latest);

        // HitCount: when the local SignatureAggregateCache has an entry, prefer its full
        // count (write-through on gateway, startup-warmed on remote-mode). Otherwise
        // fall back to the detections we fetched (capped at the query limit -- it's a
        // lower bound, not a full count). A dedicated GetSignatureAggregateAsync method
        // on IDashboardEventStore would remove this fallback; tracked separately.
        int hitCount = detections.Count;
        List<double>? sparkline = null;
        SignatureAggregate? agg = _signatureCache.TryGet(decodedSignature, out var a) ? a : null;
        if (agg != null)
        {
            if (agg.HitCount > hitCount) hitCount = agg.HitCount;
            lock (agg.SyncRoot)
            {
                sparkline = agg.ScoreHistory.Count > 0 ? agg.ScoreHistory.ToList() : null;
            }
        }

        // Optional enrichment from SignatureAggregateCache (paths / UA / protocol /
        // per-request ring buffers). On remote-mode hosts the cache may be empty so
        // these are best-effort; the event-store recent-detections list above is the
        // fallback for the history charts.
        var visitorCache = context.RequestServices.GetService<SignatureAggregateCache>();
        var visitor = visitorCache?.GetVisitor(decodedSignature);
        List<string> paths = [];
        // Seed UA from the latest detection event so remote-mode dashboard hosts
        // (whose cache may be empty) still surface the raw UA on the signature-detail
        // h1. Without this the h1 falls back to "GB User N" even though the persisted
        // row has the full UA -- the same hide-what-we-know pattern that motivated
        // task #110.
        string? userAgent = detections[0].UserAgentRaw ?? detections[0].UserAgent;
        string? protocol = null;
        DateTime firstSeen = detections[^1].Timestamp;
        // Reverse detections (DESC -> ASC) so the history chart renders oldest -> newest,
        // matching the visitor cache's ring-buffer ordering.
        List<double> botProbHistory = detections.AsEnumerable().Reverse().Select(d => d.BotProbability).ToList();
        List<double> confHistory = detections.AsEnumerable().Reverse().Select(d => d.Confidence).ToList();
        List<double> procTimeHistory = detections.AsEnumerable().Reverse().Select(d => (double)d.ProcessingTimeMs).ToList();
        // ProjectedVisitor is a per-call projection; the underlying lock happens inside
        // SignatureAggregateCache.Project so the fields read here are already stable.
        if (visitor != null)
        {
            if (visitor.Paths.Count > 0) paths = visitor.Paths.ToList();
            if (!string.IsNullOrEmpty(visitor.UserAgent)) userAgent = visitor.UserAgent;
            if (!string.IsNullOrEmpty(visitor.Protocol)) protocol = visitor.Protocol;
            if (visitor.FirstSeen != default && visitor.FirstSeen < firstSeen) firstSeen = visitor.FirstSeen;
            if (visitor.BotProbabilityHistory.Count > 0) botProbHistory = visitor.BotProbabilityHistory.ToList();
            if (visitor.ConfidenceHistory.Count > 0) confHistory = visitor.ConfidenceHistory.ToList();
            if (visitor.ProcessingTimeHistory.Count > 0) procTimeHistory = visitor.ProcessingTimeHistory.ToList();
        }
        if (paths.Count == 0)
        {
            paths = detections.Where(d => d.Path != null).Select(d => d.Path!).Distinct().ToList();
        }

        // Recent detections table + detector contributions + signal categories all source
        // from the same event-store fetch.
        var recentDetections = detections.Select(d => new SignatureDetectionRow
        {
            Timestamp = d.Timestamp,
            IsBot = d.IsBot,
            BotProbability = d.BotProbability,
            Confidence = d.Confidence,
            RiskBand = d.RiskBand,
            StatusCode = d.StatusCode,
            Path = d.Path,
            Method = d.Method,
            ProcessingTimeMs = d.ProcessingTimeMs,
            Action = d.Action
        }).ToList();

        var detectorContributions = latest.DetectorContributions?
            .Select(kv => new SignatureDetectorEntry
            {
                Name = kv.Key,
                ConfidenceDelta = kv.Value.ConfidenceDelta,
                Contribution = kv.Value.Contribution,
                Reason = kv.Value.Reason,
                ExecutionTimeMs = kv.Value.ExecutionTimeMs
            })
            .OrderByDescending(e => Math.Abs(e.Contribution))
            .ToList() ?? new List<SignatureDetectorEntry>();

        var signalCategories = new Dictionary<string, Dictionary<string, string>>();
        if (latest.ImportantSignals != null)
        {
            foreach (var kv in latest.ImportantSignals)
            {
                var dotIndex = kv.Key.IndexOf('.');
                var category = dotIndex > 0 ? kv.Key[..dotIndex] : "general";
                var key = dotIndex > 0 ? kv.Key[(dotIndex + 1)..] : kv.Key;
                if (!signalCategories.TryGetValue(category, out var cat))
                {
                    cat = new Dictionary<string, string>();
                    signalCategories[category] = cat;
                }
                cat[key] = kv.Value?.ToString() ?? "";
            }
        }

        // Parse the UA into (family, version, os) so the title can show the raw
        // claim next to the inferred name. Falls back to nulls when no UA was
        // recorded; the view treats null as "don't render the chip".
        string? uaFamily = null, uaVersion = null, uaOs = null;
        if (!string.IsNullOrEmpty(userAgent))
        {
            var (fam, ver) = Mostlylucid.BotDetection.Helpers.UserAgentParser.Parse(userAgent);
            uaFamily = string.IsNullOrEmpty(fam) || fam == "Other" ? null : fam;
            uaVersion = string.IsNullOrEmpty(ver) ? null : ver;
            uaOs = Mostlylucid.BotDetection.Helpers.UserAgentParser.ExtractOs(userAgent);
        }

        // Verification triple from the latest detection's signals. VerifiedBotContributor
        // writes verifiedbot.confirmed/spoofed/method on every check; we project them
        // into a single trust state for the title badge. "spoofed" wins over "confirmed"
        // on the rare case both are set (a re-check that flipped) -- showing the failure
        // is safer than hiding it.
        string uaTrustState = "none";
        string? uaTrustMethod = null;
        if (latest.ImportantSignals != null)
        {
            latest.ImportantSignals.TryGetValue("verifiedbot.method", out var methodObj);
            uaTrustMethod = methodObj?.ToString();
            var spoofed = latest.ImportantSignals.TryGetValue("verifiedbot.spoofed", out var sObj)
                          && sObj is bool sb && sb;
            var confirmed = latest.ImportantSignals.TryGetValue("verifiedbot.confirmed", out var cObj)
                            && cObj is bool cb && cb;
            var checkedFlag = latest.ImportantSignals.ContainsKey("verifiedbot.checked");
            if (spoofed) uaTrustState = "spoofed";
            else if (confirmed) uaTrustState = "verified";
            else if (checkedFlag || !string.IsNullOrEmpty(uaTrustMethod)) uaTrustState = "unverified";
        }

        // Name goes through the canonical read-through path -- SignatureAggregateCache
        // is the LFU source of truth at read time and the matcher's recompose keeps
        // it fresh, so the detail page agrees with the list, the visitor card, and the
        // Top Bots row by construction. latest.BotName becomes the cache-miss fallback,
        // not the primary; the pre-2026-06-24 raw read of latest.BotName was the
        // list-vs-detail divergence the user called out ("list says X, click through
        // says Y") and the path that was rendering stale "Chrome Desktop" / "Unknown
        // 000000" rows after the matcher had already recomposed to the right name.
        // Other headline fields (risk band, probability, action, threat) still come
        // from the latest detection row -- they are not subject to the matcher
        // recompose and the user's "one name at a time" rule is about identity, not
        // verdict shape.
        var detailSigLookup = await _eventStore.LoadSignatureLookupAsync();
        var canonicalDetailName = detailSigLookup.ResolveBotName(
            _signatureCache, decodedSignature, latest.BotName);

        // Resolve fingerprint id for the heading pencil/operator-edit affordance
        // (ED4 §7). The commercial editor endpoint addresses fingerprints, not
        // signatures, so the heading needs the id ahead of view render. Best-effort:
        // remote-mode hosts without an IFingerprintReader / sigs not yet bound
        // come back null and the heading skips the pencil silently.
        string? headingFingerprintId = null;
        // SINGLE SOURCE OF TRUTH: the headline probability + risk band come from
        // the fingerprint's persisted verdict (CachedBotProbability), the SAME
        // value the Your-Detection pill reads. The previous design sourced the
        // headline from the latest detection row, which disagreed with both the
        // YOU pill (signature-cache average) and the fingerprint — three numbers
        // for one identity, the exact "list says X, detail says Y" divergence the
        // user called out. Per-request rows stay in the RecentDetections table;
        // the HEADLINE is the fingerprint verdict. UpdatedAt lets the page show
        // when the score last changed.
        double headlineProb = latest.BotProbability;
        string? headlineRisk = latest.RiskBand;
        double headlineConf = latest.Confidence;
        DateTime? headlineScoreUpdatedAt = null;
        try
        {
            var fpReader = context.RequestServices.GetService<BotDetection.Identity.IFingerprintReader>();
            if (fpReader is not null)
            {
                headingFingerprintId = await fpReader.LookupFingerprintIdAsync(
                    decodedSignature, context.RequestAborted);
                if (!string.IsNullOrEmpty(headingFingerprintId))
                {
                    var fp = await fpReader.GetFingerprintAsync(headingFingerprintId, context.RequestAborted);
                    if (fp is not null)
                    {
                        headlineProb = fp.CachedBotProbability;
                        // Derived at read (verified-aware): a verified good bot reads Low here.
                        headlineRisk = Mostlylucid.BotDetection.Identity.FingerprintRiskProjection.Compose(fp).RiskBand.ToString();
                        headlineConf = fp.InferredTypeConfidence;
                        headlineScoreUpdatedAt = fp.CachedScoreUpdatedAt;
                    }
                }
            }
        }
        catch (Exception fpEx)
        {
            _logger.LogDebug(fpEx,
                "Fingerprint verdict lookup failed for {Signature}; headline falls back to latest detection",
                decodedSignature);
        }

        model = new SignatureDetailModel
        {
            SignatureId = decodedSignature,
            BasePath = basePath,
            NavBasePath = navBasePath,
            CspNonce = cspNonce,
            HubPath = _options.HubPath,
            Found = true,
            BotName = canonicalDetailName,
            BotType = latest.BotType,
            // Derive the headline band AT READ from the probability being shown, via the same
            // single source the store uses at write (SignatureRiskVerdictComposer.BucketRisk, which
            // DeriveConsistentBand wraps). The old `headlineRisk = fp.CachedRiskBand ?? latest.RiskBand`
            // fell back to latest.RiskBand when the fingerprint cache was cold/null -- a DIFFERENT
            // probability than headlineProb -- so the "Risk Profile" card contradicted the
            // "Probability" beside it (e.g. prob 0.89 next to a Low band). Deriving from headlineProb
            // guarantees the (probability, band) pair on the detail page is always internally
            // consistent, matching the fingerprint cache when present and correct when it is not.
            RiskBand = Mostlylucid.BotDetection.Risk.SignatureRiskVerdictComposer
                .BucketRisk(headlineProb, headlineConf).ToString(),
            BotProbability = headlineProb,
            Confidence = headlineConf,
            HitCount = hitCount,
            Action = latest.Action,
            CountryCode = latest.CountryCode,
            ProcessingTimeMs = latest.ProcessingTimeMs,
            TopReasons = latest.TopReasons?.ToList() ?? [],
            LastSeen = headlineScoreUpdatedAt ?? latest.Timestamp,
            Narrative = latest.Narrative,
            Description = latest.Description,
            IsBot = headlineProb >= 0.5,
            ThreatScore = latest.ThreatScore,
            ThreatBand = latest.ThreatBand,
            RiskJustification = latest.RiskJustification,
            SparklineData = sparkline,
            Paths = paths,
            EndpointStats = endpointStats,
            UserAgent = userAgent,
            UaFamily = uaFamily,
            UaVersion = uaVersion,
            UaOs = uaOs,
            UaTrustState = uaTrustState,
            UaTrustMethod = uaTrustMethod,
            Protocol = protocol,
            FirstSeen = firstSeen,
            BotProbabilityHistory = botProbHistory,
            ConfidenceHistory = confHistory,
            ProcessingTimeHistory = procTimeHistory,
            RecentDetections = recentDetections,
            DetectorContributions = detectorContributions,
            SignalCategories = signalCategories,
            TunerEnabled = _options.EnableTuner,
            PeriodicityHeatmap = heatmap,
            LooksLike = looksLike,
            FingerprintShape = fingerprintShape,
            BrowserModes = await ResolveBrowserModesAsync(context, decodedSignature),
            DriftBadge = await Services.FingerprintDriftProjector.ResolveForSignatureAsync(
                decodedSignature, context.RequestServices, context.RequestAborted, _logger),
            FingerprintId = headingFingerprintId,
        };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_SignatureDetail.cshtml", model, context, isMainPage: true);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     Runs the per-page-load "Looks like" KNN. Honours the IdentityOptions.LooksLike
    ///     gate, the configurable neighbour count, and the configurable distance ceiling.
    ///     Returns an empty list when identity is off, the reader is absent, the lookup
    ///     throws (vec0 unavailable, remote-mode adapter), or no centroid neighbour comes
    ///     back within the distance ceiling. The signature detail page tolerates the empty
    ///     case by hiding the panel; nothing else depends on the result.
    /// </summary>
    private async Task<IReadOnlyList<BotDetection.Identity.NearestFingerprint>> ResolveLooksLikeAsync(
        HttpContext context, string decodedSignature)
    {
        var idOpts = context.RequestServices
            .GetService<Microsoft.Extensions.Options.IOptions<BotDetection.Models.BotDetectionOptions>>()
            ?.Value.Identity;
        if (idOpts is null || !idOpts.Enabled) return Array.Empty<BotDetection.Identity.NearestFingerprint>();

        var cfg = idOpts.LooksLike;
        if (!cfg.Enabled || cfg.NeighbourCount <= 0)
            return Array.Empty<BotDetection.Identity.NearestFingerprint>();

        var reader = context.RequestServices.GetService<BotDetection.Identity.IFingerprintReader>();
        if (reader is null) return Array.Empty<BotDetection.Identity.NearestFingerprint>();

        try
        {
            var hits = await reader.GetNearestForSignatureAsync(
                decodedSignature, cfg.NeighbourCount, context.RequestAborted);
            return hits.Count == 0
                ? Array.Empty<BotDetection.Identity.NearestFingerprint>()
                : hits.Where(h => h.Distance <= cfg.MaxDistance).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Looks-like KNN failed for {Signature}; rendering signature detail without it",
                decodedSignature);
            return Array.Empty<BotDetection.Identity.NearestFingerprint>();
        }
    }

    /// <summary>
    ///     Resolves the per-mode rows for the signature's fingerprint
    ///     (composite-spec step 7). Powers the Browser Modes panel — replaces
    ///     the legacy "Drifted Chrome Desktop → Chrome XHR" misnomer that
    ///     conflated multi-mode evolution with identity drift. Returns an
    ///     empty list when identity is off, the reader is absent (remote-mode
    ///     dashboard host without a SignalR pull yet), the signature is
    ///     unbound to a fingerprint, the mode store isn't registered, or the
    ///     fingerprint has no mode rows persisted yet (cold post-deploy);
    ///     the view hides the panel header in that case so the page never
    ///     renders an empty box. Per <c>feedback_remote_mode_optional_di</c>:
    ///     every service is resolved as optional and missing-on-purpose paths
    ///     fall through quietly.
    /// </summary>
    private async Task<IReadOnlyList<SignatureBrowserModeRow>> ResolveBrowserModesAsync(
        HttpContext context, string decodedSignature)
    {
        // Service-presence-only gate. The previous IdentityOptions.Enabled +
        // BrowserMode.Enabled checks were correct for the GATEWAY host (where
        // identity is the in-process detection pipeline) but wrong for the
        // REMOTE-MODE DASHBOARD HOST: the website's own IdentityOptions
        // defaults to Enabled=false because it doesn't run a detection
        // pipeline at all, even when the upstream gateway has identity fully
        // enabled and is serving mode data over REST. With the options-gate
        // active the modes panel hid itself on every remote-mode signature-
        // detail render even though the gateway endpoint was returning rows.
        // The actual gate is whether IFingerprintBrowserModeStore got
        // registered: gateway registers a local store when identity is on;
        // remote-mode registers the Remote* adapter unconditionally. Either
        // way, presence means we can render.
        var reader = context.RequestServices.GetService<BotDetection.Identity.IFingerprintReader>();
        if (reader is null) return Array.Empty<SignatureBrowserModeRow>();

        var modeStore = context.RequestServices
            .GetService<BotDetection.Identity.BrowserModes.IFingerprintBrowserModeStore>();
        if (modeStore is null) return Array.Empty<SignatureBrowserModeRow>();

        // Optional dependencies for the per-mode shift projection (plan task
        // 15). Both are present on the gateway (layout is registered by
        // AddStyloBotIdentity; catalogue by AddStyloBotPolicyStack) and may
        // be absent on a thin remote-mode dashboard host. Missing-on-purpose
        // means we skip the projection per row but still render the modes
        // panel without it.
        var layout = context.RequestServices
            .GetService<BotDetection.Identity.IdentityVectorLayout>();
        var signalCatalog = context.RequestServices
            .GetService<BotDetection.Policies.Signals.ISignalCatalog>();

        try
        {
            var fpId = await reader.LookupFingerprintIdAsync(decodedSignature, context.RequestAborted);
            if (string.IsNullOrEmpty(fpId)) return Array.Empty<SignatureBrowserModeRow>();

            var modes = await modeStore.GetModesAsync(fpId, context.RequestAborted);
            if (modes.Count == 0) return Array.Empty<SignatureBrowserModeRow>();

            // Fetch the parent fingerprint once so per-mode rows can project
            // their shift off the same rolled-up centroid. Per spec D5 the
            // projection runs here on the gateway -- the view-model carries
            // only the small ModeDelta record.
            BotDetection.Identity.Fingerprint? parentFp = null;
            if (layout is not null && signalCatalog is not null)
                parentFp = await reader.GetFingerprintAsync(fpId, context.RequestAborted);

            // Project to the view-row record; sort by last_seen descending so
            // the most-recently-played mode renders first.
            return modes
                .OrderByDescending(m => m.LastSeen)
                .Select(m =>
                {
                    Mostlylucid.BotDetection.UI.Services.ModeDelta? shift = null;
                    if (parentFp is not null
                        && layout is not null
                        && signalCatalog is not null
                        && m.Centroid.Length == parentFp.Centroid.Length
                        && m.Centroid.Length > 0)
                    {
                        shift = Mostlylucid.BotDetection.UI.Services.ModeDeltaProjector.Project(
                            identityCentroid: parentFp.Centroid.AsSpan(),
                            modeCentroid:     m.Centroid.AsSpan(),
                            layout:           layout,
                            signalCatalog:    signalCatalog);
                    }

                    return new SignatureBrowserModeRow
                    {
                        ModeId             = m.ModeId,
                        CentroidMaturity   = m.CentroidMaturity,
                        ObservationCount   = m.ObservationCount,
                        FirstSeen          = m.FirstSeen,
                        LastSeen           = m.LastSeen,
                        Shift              = shift,
                    };
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Browser-mode lookup failed for {Signature}; rendering signature detail without modes panel",
                decodedSignature);
            return Array.Empty<SignatureBrowserModeRow>();
        }
    }

    /// <summary>
    ///     Resolves the 7-bucket fingerprint centroid radar shape for the
    ///     signature-detail page. Mirrors what the home card's
    ///     <see cref="BotDetection.UI.ViewComponents.BotDetectionDetailsViewComponent"/>
    ///     does at request time, so both surfaces render through the same
    ///     <see cref="FingerprintRadarProjection"/> and produce identical
    ///     polygons for the same signature. Returns <c>null</c> when identity
    ///     is off, the reader is absent (remote-mode dashboard), the
    ///     signature has no fingerprint mapped yet, the registry/layout DI is
    ///     missing, or the lookup throws (vec0 unavailable, layout-version
    ///     mismatch). The shared partial handles null by rendering its
    ///     calibrating placeholder, so the page never goes blank.
    /// </summary>
    private async Task<FingerprintRadarShape?> ResolveFingerprintShapeAsync(
        HttpContext context, string decodedSignature, DashboardDetectionEvent? latest = null)
    {
        var reader = context.RequestServices.GetService<IFingerprintReader>();
        if (reader is null) return null;

        var archetypes = context.RequestServices.GetService<IdentityArchetypeRegistry>();
        var layout = context.RequestServices.GetService<IdentityVectorLayout>();
        if (archetypes is null || layout is null) return null;

        // Optional in dashboard-viewer hosts that don't run detection. When
        // absent the per-fp weights stand in unchanged -- the global
        // multiplier is a refinement, not a prerequisite.
        var globalWeights = context.RequestServices.GetService<IdentityGlobalWeightsCache>();

        try
        {
            var fpId = await reader.LookupFingerprintIdAsync(decodedSignature, context.RequestAborted);
            if (!string.IsNullOrEmpty(fpId))
            {
                var fp = await reader.GetFingerprintAsync(fpId, context.RequestAborted);
                if (fp is not null)
                {
                    // ArchetypeOrigin can be a mode-shaped id on legacy fingerprints
                    // allocated before the client/mode role split landed. If it is,
                    // resolve to the current nearest CLIENT archetype so the radar
                    // and the drift banner present client identities, not modes.
                    // Per docs/architecture/composite-browser-mode-fingerprints.md
                    // -- one identity plays many modes; mode shifts are not drift.
                    var archetype = archetypes.TryGetById(fp.ArchetypeOrigin);
                    if (archetype is { IsMode: true })
                        archetype = archetypes.FindNearestClient(fp.Centroid)?.Archetype;
                    var effectiveWeights = globalWeights?.Compose(fp.Weights) ?? fp.Weights;
                    var shape = FingerprintRadarProjection.Project(fp, archetype, layout, effectiveWeights);

                    // Categorical drift label: which archetype is the centroid CLOSEST
                    // to right now? The view reads this for the "Drifted A → B" text
                    // (A = ArchetypeOrigin from above, B = NearestArchetypeName here).
                    // Use the client-only nearest so the banner stays apples-to-apples:
                    // "Chrome Desktop -> Chrome XHR" is a mode shift, not an identity
                    // drift, and surfacing it as one is the category error the
                    // composite-browser-mode-fingerprints spec was designed to fix.
                    var nearest = archetypes.FindNearestClient(fp.Centroid);
                    string? nearestName = nearest?.Archetype.Name;

                    // Adaptation-loop drift: cosine of live centroid vs the active
                    // root_centroid. Runtime contract is "root is never null" --
                    // the InsertFingerprintAsync seed and the migration backfill
                    // both enforce that. If we get null here, it's a bug, not a
                    // "calibrating" state. Fall back to the archetype centroid so
                    // the score is still meaningful while the bug is fixed.
                    var rootVec = fp.RootCentroid ?? archetype?.Centroid;
                    double? selfDrift = rootVec is not null
                        ? FingerprintDriftMath.Cosine(fp.Centroid, rootVec)
                        : null;

                    // Root timeline: fetch the recent fingerprint_root_history rows
                    // and project each against the live centroid so the dashboard
                    // can render "how far have I drifted from THIS past anchor" for
                    // each row in the chain.
                    IReadOnlyList<RootHistoryView>? rootHistory = null;
                    var rootHistoryLimit = context.RequestServices
                        .GetService<Microsoft.Extensions.Options.IOptions<BotDetectionOptions>>()
                        ?.Value.Identity.Match.RootHistoryDisplayLimit ?? 12;
                    var hist = await reader.GetRootHistoryAsync(fpId, rootHistoryLimit, context.RequestAborted);
                    if (hist.Count > 0)
                    {
                        var projected = new List<RootHistoryView>(hist.Count);
                        foreach (var h in hist)
                            projected.Add(new RootHistoryView(
                                Source: h.RootSource,
                                MemberCount: h.MemberCount,
                                SetAt: h.SetAt,
                                SupersededAt: h.SupersededAt,
                                RootDistance: FingerprintDriftMath.Distance(fp.Centroid, h.RootCentroid)));
                        rootHistory = projected;
                    }

                    shape = shape with
                    {
                        NearestArchetypeName = nearestName,
                        SelfDriftScore = selfDrift,
                        SelfDriftLabel = fp.RootSource,
                        RootCentroidAt = fp.RootCentroidAt,
                        RootHistory = rootHistory,
                    };
                    return shape;
                }
            }

            // Defensive fallback: the signature has dashboard aggregate data
            // (this code path runs because the URL resolved to a known row)
            // but no fingerprint_keys binding. Cases: signatures created
            // before the matcher's Priority=1 wiring, requests that
            // quorum-exited before FingerprintMatchContributor allocated,
            // local fingerprint store wiped while the dashboard kept its
            // aggregate cache. The signature detail page must show a
            // polygon -- "calibrating" is reserved for first-encounter
            // home-card requests where no aggregate exists yet, not for
            // known historical signatures with hit counts and detector
            // contributions on the same page.
            //
            // Pick the archetype that best matches the aggregate's known
            // bot-name / bot-type labelling and render its centroid as the
            // inferred shape. The view component picks up ArchetypeName !=
            // null to mark the polygon as a baseline rather than a
            // per-actor observation.
            var aggCache = context.RequestServices.GetService<SignatureAggregateCache>();
            string? inferBotName = null;
            string? inferBotType = null;
            DateTime inferFirstSeen = DateTime.UtcNow;
            DateTime inferLastSeen = DateTime.UtcNow;
            if (aggCache is not null && aggCache.TryGet(decodedSignature, out var agg) && agg is not null)
            {
                // BotName no longer lives on the aggregate -- pull from the
                // resolved-name dict (store-gated). BotType is likewise read through
                // the fingerprint LFU via the resolved-verdict dict (single source).
                inferBotName = aggCache.GetResolvedName(decodedSignature);
                inferBotType = aggCache.GetResolvedVerdict(decodedSignature)?.BotType;
                inferFirstSeen = agg.FirstSeen;
                inferLastSeen = agg.LastSeen;
            }
            else if (latest is not null)
            {
                // Remote-mode dashboard hosts have no SignatureAggregateCache --
                // it freezes at startup-warm values (write-through is gateway-local).
                // The detection row from the event store carries the same
                // bot-name / bot-type identity signal the matcher used, so the
                // inferred-archetype shape resolves from `latest` exactly as it
                // would from the aggregate. Without this branch, every signature
                // detail page on a viewer host renders the "Calibrating
                // fingerprint" placeholder -- a forbidden state per the identity
                // invariant: detections.Count > 0 here, so SOMETHING is always
                // shaped.
                inferBotName = latest.BotName;
                inferBotType = latest.BotType;
                inferFirstSeen = latest.Timestamp;
                inferLastSeen = latest.Timestamp;
            }

            if (inferBotName is not null || inferBotType is not null || latest is not null)
            {
                var inferredArchetype = FindInferredArchetypeFor(archetypes, inferBotName, inferBotType);
                if (inferredArchetype is not null
                    && inferredArchetype.Centroid.Length == layout.Dimension
                    && inferredArchetype.DimensionMask.Length == layout.Dimension)
                {
                    // Single archetypes paint only the buckets whose slots
                    // they explicitly assert (chrome-desktop only sets hdr.*
                    // slots, so naive mask-weighting collapses every bucket
                    // except Headers). Build a synthetic centroid that
                    // overlays the archetype's defined slot values on top of
                    // a uniform low-magnitude baseline so every bucket gets
                    // a visible polygon vertex. Pair it with uniform 1.0
                    // weights so the baseline survives the weighted average
                    // and the archetype's defined slots show as bumps above it.
                    var inferredCentroid = new float[layout.Dimension];
                    Array.Fill(inferredCentroid, InferredBaselineMagnitude);
                    for (var i = 0; i < layout.Dimension; i++)
                    {
                        if (inferredArchetype.DimensionMask[i] > 0)
                            inferredCentroid[i] = inferredArchetype.Centroid[i];
                    }
                    var inferredWeights = new float[layout.Dimension];
                    Array.Fill(inferredWeights, 1f);
                    var syntheticFp = new Fingerprint
                    {
                        FingerprintId = "inferred",
                        Centroid = inferredCentroid,
                        CentroidMaturity = 0,
                        Weights = inferredWeights,
                        MemberCount = 0,
                        ObservationCount = 0,
                        CorrectionCount = 0,
                        FirstSeen = inferFirstSeen,
                        LastSeen = inferLastSeen,
                        Quality = 0,
                        ArchetypeOrigin = inferredArchetype.ArchetypeId,
                        InferredClientType = inferredArchetype.ArchetypeKind,
                        InferredTypeConfidence = 0,
                        InferredTypeChangedAt = DateTime.UtcNow,
                    };
                    var composedWeights = globalWeights?.Compose(inferredWeights) ?? inferredWeights;
                    return FingerprintRadarProjection.Project(syntheticFp, inferredArchetype, layout, composedWeights);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Fingerprint shape resolution failed for {Signature}; signature detail will render calibrating placeholder",
                decodedSignature);
            return null;
        }
    }

    /// <summary>
    ///     Drives archetype inference directly from the bot-name / bot-type signal.
    ///     Prefer exact name match (e.g. "bingbot" → "bingbot" archetype), fall back to
    ///     bot-type kind match (Scraper/Crawler/Tool/User), fall back to the
    ///     human-baseline archetype. Used by <see cref="ResolveFingerprintShapeAsync"/>'s
    ///     defensive fallback so signatures without a fingerprint_keys row still render
    ///     an inferred polygon rather than the home-card-only "calibrating" placeholder.
    ///     Callers supply the bot type read through the fingerprint LFU
    ///     (<see cref="SignatureAggregateCache.GetResolvedVerdict"/>) or from the latest
    ///     <see cref="DashboardDetectionEvent"/> on remote-mode viewer hosts that have no
    ///     aggregate cache.
    /// </summary>
    private static IdentityArchetype? FindInferredArchetypeFor(
        IdentityArchetypeRegistry registry, string? botName, string? botType)
    {
        var all = registry.All;
        if (all.Count == 0) return null;

        if (!string.IsNullOrEmpty(botName))
        {
            foreach (var a in all)
                if (string.Equals(a.Name, botName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.ArchetypeId, botName, StringComparison.OrdinalIgnoreCase))
                    return a;
        }
        if (!string.IsNullOrEmpty(botType))
        {
            foreach (var a in all)
                if (string.Equals(a.ArchetypeKind, botType, StringComparison.OrdinalIgnoreCase))
                    return a;
        }
        foreach (var a in all)
            if (string.Equals(a.ArchetypeKind, "user", StringComparison.OrdinalIgnoreCase))
                return a;
        return all[0];
    }

    /// <summary>
    ///     Resolve an entity-id URL to its current canonical signature-detail
    ///     page via 302. Entity ids are the durable URL key -- they survive
    ///     primary-signature rotation, and merges retroactively update where
    ///     the id resolves to without breaking the URL. The redirect target
    ///     is whichever primary signature is currently the most-recent active
    ///     edge on this entity; subsequent merges that re-key the edge update
    ///     the redirect target on the next visit without invalidating any
    ///     shared link.
    /// </summary>
    private async Task ServeEntityDetailAsync(HttpContext context, string entityId)
    {
        var basePath = _options.BasePath.TrimEnd('/');
        var decodedEntityId = Uri.UnescapeDataString(entityId).TrimEnd('/');
        if (string.IsNullOrEmpty(decodedEntityId))
        {
            await WriteEntityNotFoundAsync(context, decodedEntityId, basePath);
            return;
        }

        var reader = context.RequestServices.GetService<BotDetection.Data.IEntityReader>();
        if (reader is null)
        {
            await WriteEntityNotFoundAsync(context, decodedEntityId, basePath);
            return;
        }

        try
        {
            var edges = await reader.GetEntityEdgesAsync(decodedEntityId, context.RequestAborted);
            // Convergence edges store the OTHER entity's id in the signature column with a
            // "converge:" prefix; they're operator-review metadata, not navigable signatures.
            // Skip them here so /dashboard/entity/<id> never redirects to a bogus
            // /dashboard/signature/converge:<id> target.
            var activeEdge = edges
                .Where(e => e.IsActive)
                .Where(e => !string.IsNullOrEmpty(e.Signature)
                         && !e.Signature.StartsWith("converge:", StringComparison.Ordinal))
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefault();

            if (activeEdge is null)
            {
                await WriteEntityNotFoundAsync(context, decodedEntityId, basePath);
                return;
            }

            var target = $"{basePath}/signature/{Uri.EscapeDataString(activeEdge.Signature)}";
            // Local-redirect guard: target is built from config + DB values, but a
            // signature starting with "/" or "\" could otherwise turn this into a
            // protocol-relative (//host) or scheme-confused redirect.
            if (!target.StartsWith('/') || target.StartsWith("//") || target.StartsWith("/\\"))
            {
                context.Response.StatusCode = 400;
                return;
            }
            context.Response.Redirect(target, permanent: false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Entity-detail redirect failed for entity {EntityId}", decodedEntityId);
            await WriteEntityNotFoundAsync(context, decodedEntityId, basePath);
        }
    }

    private static async Task WriteEntityNotFoundAsync(HttpContext context, string entityId, string basePath)
    {
        context.Response.StatusCode = 404;
        context.Response.ContentType = "text/html; charset=utf-8";
        var safeId = string.IsNullOrEmpty(entityId)
            ? string.Empty
            : System.Web.HttpUtility.HtmlEncode(entityId[..Math.Min(24, entityId.Length)]);
        await context.Response.WriteAsync($"""
            <!DOCTYPE html><html lang="en" data-theme="dark">
            <head><meta charset="utf-8"><title>Entity Not Found - StyloBot</title>
            <link rel="stylesheet" href="/_content/Mostlylucid.BotDetection.UI/vendor/css/tailwind.min.css" /></head>
            <body class="min-h-screen flex items-center justify-center bg-base-100">
            <div class="text-center">
                <p class="text-4xl font-black text-base-content/20 mb-2">404</p>
                <p class="text-sm text-base-content/60">Entity not found</p>
                <p class="text-xs text-base-content/40 mt-1 font-mono">{safeId}&hellip;</p>
                <a href="{basePath}" class="btn btn-sm btn-ghost mt-4">Back to dashboard</a>
            </div></body></html>
            """);
    }

    private static async Task WriteSignatureNotFoundAsync(HttpContext context, string signature, string basePath)
    {
        context.Response.StatusCode = 404;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($"""
            <!DOCTYPE html><html lang="en" data-theme="dark">
            <head><meta charset="utf-8"><title>Signature Not Found - StyloBot</title>
            <link rel="stylesheet" href="/_content/Mostlylucid.BotDetection.UI/vendor/css/tailwind.min.css" /></head>
            <body class="min-h-screen flex items-center justify-center bg-base-100">
            <div class="text-center">
                <p class="text-4xl font-black text-base-content/20 mb-2">404</p>
                <p class="text-sm text-base-content/60">Signature not found</p>
                <p class="text-xs text-base-content/40 mt-1 font-mono">{System.Web.HttpUtility.HtmlEncode(signature[..Math.Min(24, signature.Length)])}&hellip;</p>
                <a href="{basePath}" class="btn btn-sm btn-ghost mt-4">Back to dashboard</a>
            </div></body></html>
            """);
    }

    /// <summary>Render the user agent detail panel partial.</summary>
    private async Task ServeUserAgentDetailPartialAsync(HttpContext context)
    {
        var family = context.Request.Query["family"].FirstOrDefault();
        if (string.IsNullOrEmpty(family))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing family parameter");
            return;
        }

        var cached = _aggregateCache.Current.UserAgents;
        var allUas = cached.Count > 0 ? cached : await ComputeUserAgentsFallbackAsync();
        var ua = allUas.FirstOrDefault(u => string.Equals(u.Family, family, StringComparison.OrdinalIgnoreCase));

        if (ua == null)
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<div class=\"text-xs text-base-content/40 py-8 text-center\">User agent not found</div>");
            return;
        }

        var cspNonce = GetOrCreateCspNonce(context);

        var model = new UserAgentDetailModel
        {
            Family = ua.Family,
            Category = ua.Category,
            TotalCount = ua.TotalCount,
            BotCount = ua.BotCount,
            HumanCount = ua.HumanCount,
            BotRate = ua.BotRate,
            AvgConfidence = ua.AvgConfidence,
            AvgProcessingTimeMs = ua.AvgProcessingTimeMs,
            Versions = ua.Versions,
            Countries = ua.Countries,
            CspNonce = cspNonce,
            BasePath = _options.BasePath.TrimEnd('/')
        };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_UserAgentDetail.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>Render the endpoint detail panel partial.</summary>
    /// <summary>
    ///     Serve the full-page endpoint detail. Mirrors the /signature/{id} pattern -- minimal
    ///     HTML shell wrapping the rich detail panel from _EndpointDetail.cshtml so the
    ///     overview's endpoints list can drill into a real per-endpoint page (NOT the
    ///     endpoints overview tab). Uses the same model builder as the HTMX partial route.
    /// </summary>
    private async Task ServeEndpointDetailPageAsync(HttpContext context)
    {
        var method = context.Request.Query["method"].FirstOrDefault();
        var path = context.Request.Query["path"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing method or path parameter");
            return;
        }

        // Same chrome contract as the signature-detail page: render _EndpointDetail.cshtml
        // as a main page and let the host's _ViewStart pick the layout (marketing-site
        // _Layout on stylo.bot; whatever the FOSS host provides elsewhere). Previously
        // this handler hand-rolled <!DOCTYPE>... + vendor CSS links + an embedded navbar,
        // which guaranteed visual drift the moment the host layout evolved (new theme
        // tokens, footer rules, vendor bundle updates). The signature-detail page already
        // moved off that pattern; the endpoint-detail page was the last hand-rolled shell.
        SetDashboardCsp(context);

        var model = await BuildEndpointDetailModelAsync(context, method, path, standalonePage: true);

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_EndpointDetail.cshtml", model, context, isMainPage: true);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     Applies the dashboard CSP + frame-ancestors headers. Shared by the
    ///     signature-detail and endpoint-detail page handlers so both pages get
    ///     identical scriptlet/worker/font allow-lists. Lifted out of inline
    ///     duplication when the endpoint-detail page moved to the host-layout
    ///     pattern.
    /// </summary>
    private void SetDashboardCsp(HttpContext context)
    {
        var cspNonce = GetOrCreateCspNonce(context);
        context.Response.Headers.Remove("X-Frame-Options");
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        context.Response.Headers.Remove("Content-Security-Policy");
        var dashboardCsp = string.Join("; ",
            "default-src 'self'",
            "base-uri 'self'",
            "frame-ancestors 'self'",
            "object-src 'none'",
            "img-src 'self' data: https:",
            "font-src 'self' data: https://fonts.gstatic.com https://unpkg.com",
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://unpkg.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com",
            $"script-src 'self' 'nonce-{cspNonce}' 'unsafe-eval' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://unpkg.com https://cdn.tailwindcss.com",
            "worker-src 'self' blob:",
            "connect-src 'self' ws: wss:");
        context.Response.Headers["Content-Security-Policy"] = dashboardCsp;
    }

    /// <summary>
    ///     Full-page wrapper for the session detail partial. Mirrors the signature +
    ///     endpoint detail surfaces so Behavioral Sessions rows can be drilled into
    ///     a dedicated page with the same shared navbar chrome.
    /// </summary>
    private async Task ServeSessionDetailPageAsync(HttpContext context)
    {
        var sig = context.Request.Query["sig"].FirstOrDefault();
        var idStr = context.Request.Query["id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(sig))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing sig parameter");
            return;
        }

        var detailHtml = await RenderSessionDetailBodyAsync(context, sig, idStr);
        var navbarHtml = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/_StylobotNavbar.cshtml", model: null!, context);
        var themeBootHtml = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/_ThemeBoot.cshtml", model: null!, context);
        var cspNonce = context.Items.TryGetValue("CspNonce", out var n) && n is string ns ? ns : "";
        var navBp = (string.IsNullOrEmpty(_options.NavBasePath) ? _options.BasePath : _options.NavBasePath).TrimEnd('/');
        var sigEncoded = Uri.EscapeDataString(sig);
        var pageTitle = "Session Detail: StyloBot";

        // Same shell as signature + endpoint detail. Brand-header rule defined inline
        // so the page paints correctly off the FOSS vendor CSS bundle alone.
        var html = $@"<!DOCTYPE html>
<html lang=""en"" data-theme=""dark"">
<head>
<meta charset=""utf-8"" />
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
<title>{System.Net.WebUtility.HtmlEncode(pageTitle)}</title>
<link rel=""stylesheet"" href=""/_content/Mostlylucid.BotDetection.UI/vendor/css/tailwind.min.css"" />
<link rel=""stylesheet"" href=""/_content/Mostlylucid.BotDetection.UI/vendor/css/boxicons.min.css"" />
<link rel=""stylesheet"" href=""/_content/Mostlylucid.BotDetection.UI/vendor/css/fonts.css"" />
<link rel=""stylesheet"" href=""/_content/Mostlylucid.BotDetection.UI/vendor/css/jsvectormap.min.css"" />
<script src=""/_content/Mostlylucid.BotDetection.UI/vendor/js/htmx.min.js"" nonce=""{cspNonce}""></script>
<script src=""/_content/Mostlylucid.BotDetection.UI/vendor/js/apexcharts.min.js"" nonce=""{cspNonce}""></script>
<script src=""/_content/Mostlylucid.BotDetection.UI/vendor/js/jsvectormap.min.js"" nonce=""{cspNonce}""></script>
<script src=""/_content/Mostlylucid.BotDetection.UI/vendor/js/world-merc.js"" nonce=""{cspNonce}""></script>
{themeBootHtml}
<style nonce=""{cspNonce}"">
:root, [data-theme] {{
  --sb-surface: var(--color-base-200);
  --sb-card-bg: var(--color-base-100);
  --sb-card-border: color-mix(in oklch, var(--color-base-content) 15%, transparent);
  --sb-card-divider: color-mix(in oklch, var(--color-base-content) 8%, transparent);
  --sb-brand-muted: color-mix(in oklch, var(--color-base-content) 55%, transparent);
  --sb-brand-strong: var(--color-base-content);
  --sb-signal-pos: var(--color-success);
  --sb-signal-warn: var(--color-warning);
  --sb-signal-danger: var(--color-error);
}}
body {{ font-family: 'Inter', sans-serif; background: var(--sb-surface); min-height: 100vh; }}
</style>
</head>
<body hx-ext=""morph"">
{navbarHtml}
<div class=""max-w-7xl mx-auto px-4 py-1.5 text-[10px] text-base-content/40 flex items-center justify-between"">
  <div>
    <a href=""{navBp}"" data-action=""smart-back"" class=""hover:text-base-content/70"">&larr; Dashboard</a>
    <span class=""mx-1"">/</span>
    <a href=""{navBp}/signature/{sigEncoded}"" class=""hover:text-base-content/70"">Signature</a>
    <span class=""mx-1"">/</span>
    <span class=""text-base-content/60"">Session Detail</span>
  </div>
</div>
<main class=""max-w-5xl mx-auto px-2 py-4"">
{detailHtml}
</main>
</body>
</html>";
        context.Response.ContentType = "text/html";
        await context.Response.WriteAsync(html);
    }

    private async Task<string> RenderSessionDetailBodyAsync(HttpContext context, string sig, string? idStr)
    {
        // Same capture-the-partial-writer trick endpoint detail uses, so the partial
        // route's model build doesn't have to be duplicated.
        var origPath = context.Request.Path;
        var origQuery = context.Request.QueryString;
        context.Request.Path = "/__internal-session-detail-render";
        context.Request.QueryString = QueryString.Create(new[]
        {
            new KeyValuePair<string, string?>("sig", sig),
            new KeyValuePair<string, string?>("id", idStr)
        });
        var origBody = context.Response.Body;
        var sw = new System.IO.MemoryStream();
        context.Response.Body = sw;
        try
        {
            await ServeSessionDetailPartialAsync(context);
            sw.Position = 0;
            using var reader = new System.IO.StreamReader(sw);
            return await reader.ReadToEndAsync();
        }
        finally
        {
            context.Response.Body = origBody;
            context.Request.Path = origPath;
            context.Request.QueryString = origQuery;
        }
    }

    private async Task ServeEndpointDetailPartialAsync(HttpContext context)
    {
        var method = context.Request.Query["method"].FirstOrDefault();
        var path = context.Request.Query["path"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing method or path parameter");
            return;
        }

        var model = await BuildEndpointDetailModelAsync(context, method, path, standalonePage: false);
        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_EndpointDetail.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     Builds the endpoint-detail view model from the persisted event store
    ///     plus pin / policy / pack-coverage state. Shared between the HTMX
    ///     partial response (<see cref="ServeEndpointDetailPartialAsync"/>) and
    ///     the standalone page (<see cref="ServeEndpointDetailPageAsync"/>) so
    ///     the two surfaces cannot drift on what they consider "this endpoint".
    /// </summary>
    private async Task<EndpointDetailModel> BuildEndpointDetailModelAsync(
        HttpContext context, string method, string path, bool standalonePage)
    {
        var detail = await _eventStore.GetEndpointDetailAsync(method, path);
        var epNonce = context.Items.TryGetValue("CspNonce", out var epN) && epN is string epNs ? epNs : "";

        var policyRegistry = context.RequestServices.GetService(typeof(BotDetection.Policies.IPolicyRegistry))
            as BotDetection.Policies.IPolicyRegistry;
        var pinStore = context.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
        IReadOnlyList<PinnedEndpoint> allPins = pinStore != null ? await pinStore.GetAllAsync() : [];
        var pin = allPins.FirstOrDefault(p =>
            string.Equals(p.Method, method, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Path, path, StringComparison.Ordinal));
        var policyName = policyRegistry?.GetPolicyForPath(path).Name;
        var packCoverage = BuildEndpointDetailCoverage(context, path);

        var isCommercial = IsCommercialMode(context);
        var basePath = _options.BasePath.TrimEnd('/');
        var navBasePath = string.IsNullOrEmpty(_options.NavBasePath) ? _options.BasePath : _options.NavBasePath;
        navBasePath = navBasePath.TrimEnd('/');

        if (detail == null)
            return new EndpointDetailModel
            {
                Method = method,
                Path = path,
                BasePath = basePath,
                NavBasePath = navBasePath,
                HubPath = _options.HubPath,
                Found = false,
                CspNonce = epNonce,
                PolicyName = policyName,
                PackCoverage = packCoverage,
                IsPinned = pin != null,
                IsHoneypot = pin?.IsHoneypot ?? false,
                PinId = pin?.Id,
                IsCommercial = isCommercial,
                IsStandalonePage = standalonePage
            };

        return new EndpointDetailModel
        {
            Method = detail.Method,
            Path = detail.Path,
            BasePath = basePath,
            NavBasePath = navBasePath,
            HubPath = _options.HubPath,
            Found = true,
            TotalCount = detail.TotalCount,
            BotCount = detail.BotCount,
            HumanCount = detail.HumanCount,
            BotRate = detail.BotRate,
            UniqueSignatures = detail.UniqueSignatures,
            AvgProcessingTimeMs = detail.AvgProcessingTimeMs,
            MinProcessingTimeMs = detail.MinProcessingTimeMs,
            MaxProcessingTimeMs = detail.MaxProcessingTimeMs,
            P95ProcessingTimeMs = detail.P95ProcessingTimeMs,
            AvgThreatScore = detail.AvgThreatScore,
            TopActions = detail.TopActions,
            TopCountries = detail.TopCountries,
            RiskBands = detail.RiskBands,
            // Same predicate the visitor list + Top Bots widget use -- one
            // Amazonbot row, not N rows for N converged fingerprints.
            TopBots = WidgetRenderHelpers.CollapseGroupableIdentities(detail.TopBots),
            RecentDetections = detail.RecentDetections,
            BotProfile = detail.BotProfile,
            HumanProfile = detail.HumanProfile,
            OverallProfile = detail.OverallProfile,
            CspNonce = epNonce,
            PolicyName = policyName,
            PackCoverage = packCoverage,
            IsPinned = pin != null,
            IsHoneypot = pin?.IsHoneypot ?? false,
            PinId = pin?.Id,
            IsCommercial = isCommercial,
            IsStandalonePage = standalonePage
        };
    }

    // ─── Shared model builders ───────────────────────────────────────────

    /// <summary>
    ///     The headline verdict for "this fingerprint", read from the SINGLE
    ///     source of truth: the fingerprint's persisted verdict via
    ///     <see cref="BotDetection.Identity.IFingerprintReader"/> (network-local
    ///     to the gateway that owns the fingerprint LFU). Both the "Your
    ///     Detection" pill and the signature-detail headline resolve through
    ///     here, so they show EXACTLY the same number. <c>UpdatedAt</c> is the
    ///     cache timestamp so a surface can show when the score last changed.
    /// </summary>
    private sealed record FingerprintVerdict(
        double Probability, string? RiskBand, double Confidence, string? Name, DateTime? UpdatedAt);

    private async Task<FingerprintVerdict?> ResolveFingerprintVerdictAsync(HttpContext context)
    {
        var fpId = context.Items.TryGetValue(SignalKeys.IdentityFingerprintId, out var fpIdObj)
            ? fpIdObj as string
            : null;
        if (string.IsNullOrEmpty(fpId)) return null;

        var reader = context.RequestServices.GetService<BotDetection.Identity.IFingerprintReader>();
        if (reader is null) return null;

        try
        {
            var fp = await reader.GetFingerprintAsync(fpId, context.RequestAborted);
            if (fp is null) return null;
            // The probability is a STORED fact read as-is (single source: the fingerprint
            // LFU entry). The RiskBand is NOT stored -- it is a DERIVED value, computed here
            // from that same entry's raw facts (probability + claim_status + bot type) via
            // FingerprintRiskProjection, the SAME compute site every other reader uses. This
            // is not a second source: it is the one derivation, run at read. Deriving here
            // (rather than reading a stored band) is exactly what makes a verified good bot
            // read Low instead of BucketRisk(1.0)=VeryHigh.
            return new FingerprintVerdict(
                Probability: fp.CachedBotProbability,
                RiskBand: BotDetection.Identity.FingerprintRiskProjection.Compose(fp).RiskBand.ToString(),
                Confidence: fp.InferredTypeConfidence,
                Name: fp.InducedName ?? fp.LlmName,
                UpdatedAt: fp.CachedScoreUpdatedAt);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ResolveFingerprintVerdict failed for the current request");
            return null;
        }
    }

    private async Task<YourDetectionModel> BuildYourDetectionPartialModel(HttpContext context)
    {
        try
        {
            var sigService = context.RequestServices.GetService(typeof(MultiFactorSignatureService))
                as MultiFactorSignatureService;
            var signatureCache = context.RequestServices.GetService(typeof(SignatureAggregateCache))
                as SignatureAggregateCache;

            // sigService and signatureCache are OPTIONAL: pure dashboard-viewer hosts
            // (no AddBotDetection / detection pipeline) don't register them. The
            // hydrator middleware still populates the same Items keys from the
            // gateway's X-Bot-Detection-* headers, and the event-store fallback below
            // reads the verdict from the gateway's REST endpoint. Bailing out here
            // when sigService is null was the "Detection pending..." root cause on
            // remote-mode dashboards.

            MultiFactorSignatures? sigs = null;
            // In-process orchestrator path (gateway / single-binary host): MultiFactorSignatures
            // hangs off the AggregatedEvidence Signals dictionary the broadcast middleware reads.
            if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
                && evObj is AggregatedEvidence ev
                && ev.Signals.TryGetValue(SignalKeys.SignatureMultifactor, out var multiObj)
                && multiObj is MultiFactorSignatures m)
            {
                sigs = m;
            }
            // Forwarded-headers hydrator path (remote-mode website): the gateway computed the
            // signature set and attached X-Bot-Detection-* on the YARP-forwarded request;
            // StyloBotForwardedHeadersHydratorMiddleware copies it to context.Items directly
            // (NOT under AggregatedEvidenceKey -- there's no in-process orchestrator to wrap
            // it). Without this the local GenerateSignatures fallback runs and produces a
            // different primarySig than what the gateway recorded, so the event-store hydration
            // never finds the visitor and the pill stays at "Detection pending..." forever.
            if (sigs is null
                && context.Items.TryGetValue(SignalKeys.SignatureMultifactor, out var hydMulti)
                && hydMulti is MultiFactorSignatures hm)
            {
                sigs = hm;
            }
            // Local fallback only when the detection-pipeline service is present
            // (single-binary gateway hosts). Pure dashboard-viewer hosts have no
            // sigService; if neither AggregatedEvidence nor the hydrator populated
            // sigs by this point, the gateway never tagged the request and there
            // is nothing useful to show.
            if (sigs is null && sigService is not null)
                sigs = sigService.GenerateSignatures(context);
            if (sigs is null)
                return new YourDetectionModel
                {
                    HasData = false, BasePath = _options.BasePath.TrimEnd('/')
                };

            // The header "You:" pill links Signature to /dashboard/signature/{hash},
            // which resolves ONLY a lowercase-hex primary_signature. But
            // MultiFactorSignatures.PrimarySignature falls back to a base64url
            // entity/session id when no hex signature is resolved (see
            // IntentAtom `sink.ReadHint(PrimarySignature) ?? sessionId`), and the
            // remote-mode header hydrator forwards that fallback verbatim. Linking a
            // non-hex id 404s. Gate the field to a linkable hex signature; when it's
            // the id fallback, leave it null so the view's existing
            // `@if (!string.IsNullOrEmpty(yd.Signature))` guard suppresses the link.
            // Root-cause fix at the model (don't emit an unlinkable signature), not a
            // view-side format band-aid.
            var linkableSig = IsHexSignature(sigs.PrimarySignature) ? sigs.PrimarySignature : null;

            // ── SINGLE SOURCE OF TRUTH for the headline score ──
            // ONE fingerprint, ONE bot score. The headline BotProbability +
            // RiskBand come from the fingerprint's persisted verdict
            // (CachedBotProbability) via IFingerprintReader — which is
            // network-local to the gateway that owns the fingerprint LFU. This
            // is the SAME value the signature-detail page reads, so the two can
            // never disagree.
            //
            // We deliberately do NOT use SignatureAggregateCache.GetVisitor()
            // .BotProbability for the headline: that's a cross-request/cross-mode
            // AVERAGE (nav 0.16 + sub-resource 0.58 + websocket 0.90 -> ~0.6),
            // which disagreed with both the fingerprint verdict and the latest
            // detection. The cache is used ONLY for display enrichment (radar
            // shape, narrative, name) below — never the number.
            var fpVerdict = await ResolveFingerprintVerdictAsync(context);
            var visitor = signatureCache?.GetVisitor(sigs.PrimarySignature);

            if (fpVerdict is not null || visitor != null)
            {
                // Enrichment from the cache (radar / name / UA) when present; the
                // SCORE always comes from the fingerprint verdict when we have it.
                var clockAxes = visitor != null
                    ? Mostlylucid.BotDetection.UI.Services.ClockAxesResolver.FromRadarShape(visitor.RadarShape)
                    : null;

                var headlineProb = fpVerdict?.Probability ?? visitor!.BotProbability;
                var headlineRisk = fpVerdict?.RiskBand ?? visitor?.RiskBand ?? "Unknown";

                return new YourDetectionModel
                {
                    HasData = true,
                    IsBot = headlineProb >= 0.5,
                    BotProbability = headlineProb,
                    Confidence = visitor?.Confidence ?? fpVerdict?.Confidence ?? 0.0,
                    RiskBand = headlineRisk,
                    ProcessingTimeMs = visitor?.ProcessingTimeMs ?? 0.0,
                    DetectorCount = visitor?.TopReasons.Count ?? 0,
                    Narrative = visitor?.Narrative,
                    TopReasons = visitor?.TopReasons ?? new List<string>(),
                    Signature = linkableSig,
                    ThreatScore = visitor?.ThreatScore,
                    ThreatBand = visitor?.ThreatBand,
                    BasePath = _options.BasePath.TrimEnd('/'),
                    CountryCode = visitor?.CountryCode,
                    UaFamily = visitor?.UaFamily,
                    UserAgent = visitor?.UserAgent,
                    BotName = visitor?.BotName ?? fpVerdict?.Name,
                    BotType = visitor?.BotType,
                    ClockAxes = clockAxes
                };
            }

            // Visitor cache miss -- fall through to the event store so the "You:" pill
            // and Your-Detection card don't read "Detection pending..." forever on remote-mode
            // dashboard hosts where the cache is never written to. Hydrates the headline
            // fields from the latest persisted detection for this visitor's primary
            // signature; the richer per-visitor fields (RadarShape -> ClockAxes, Narrative,
            // TopReasons) stay default because the detection row alone doesn't carry them.
            // Same pattern as BotDetectionDetailsViewComponent's event-store hydration.
            try
            {
                var detections = await _eventStore.GetDetectionsAsync(
                    new DashboardFilter { SignatureId = sigs.PrimarySignature, Limit = 1 },
                    context.RequestAborted);
                if (detections.Count > 0)
                {
                    var d = detections[0];
                    // Name goes through the same canonical resolver the visitor list
                    // uses so the "Your Detection" card and the rest of the dashboard
                    // can't disagree on the same signature. d.BotName is the cache-miss
                    // fallback, not the primary.
                    var yourSigLookup = await _eventStore.LoadSignatureLookupAsync();
                    var canonicalYourName = yourSigLookup.ResolveBotName(
                        signatureCache, sigs.PrimarySignature, d.BotName);
                    return new YourDetectionModel
                    {
                        HasData = true,
                        IsBot = d.IsBot,
                        BotProbability = d.BotProbability,
                        Confidence = d.Confidence,
                        RiskBand = d.RiskBand ?? "Unknown",
                        ProcessingTimeMs = d.ProcessingTimeMs,
                        Signature = linkableSig,
                        BasePath = _options.BasePath.TrimEnd('/'),
                        CountryCode = d.CountryCode,
                        UserAgent = d.UserAgent,
                        BotName = canonicalYourName,
                        BotType = d.BotType,
                    };
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Your-detection event-store hydration failed -- falling back to HasData=false");
            }

            return new YourDetectionModel
            {
                HasData = false, Signature = linkableSig,
                BasePath = _options.BasePath.TrimEnd('/')
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to build your detection partial model");
            return new YourDetectionModel { HasData = false, BasePath = _options.BasePath.TrimEnd('/') };
        }
    }

    /// <summary>
    ///     True when <paramref name="s"/> is a lowercase-hex primary_signature -- the
    ///     format the <c>/dashboard/signature/{hash}</c> route resolves. A real
    ///     signature is <c>Convert.ToHexString(...).ToLowerInvariant()</c>, so any
    ///     uppercase / '-' / '_' / non-hex character means the base64url entity/session
    ///     id fallback (<c>PrimarySignature ?? sessionId</c>), which the route 404s on.
    ///     Used to gate the "You:" pill's linked signature.
    /// </summary>
    internal static bool IsHexSignature(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }

    private async Task<TopBotsListModel> BuildTopBotsModel(int page = 1, int pageSize = 10, string sortBy = "default", string sortDir = "desc", string filter = "bots", string widgetId = "topbots", string? searchQuery = null)
    {
        // Read through the event store so the data path is the same whether we're on
        // a gateway host (SqliteDashboardEventStore / PostgreSQLDashboardEventStore
        // hitting local Postgres) or a remote-mode dashboard host (RemoteDashboardEventStore
        // hitting the gateway's REST API). The previous local SignatureAggregateCache
        // read returned empty/stale data in remote mode because no in-process
        // DetectionBroadcastMiddleware fed it -- the cache is only a write-through
        // hot path on the gateway, never the source of truth. The cache remains live
        // on gateway hosts; it just isn't this widget's read source.
        //
        // Always pull the unfiltered top-N. Counts header must show the WHOLE distribution
        // (All / Bots / Humans) -- if we passed audienceFilter to the event store it would
        // pre-restrict the rows and the headline counts would collapse to "Bots == All,
        // Humans = 0" when the user has the Bots filter selected. Apply the bots/humans/all
        // filter client-side instead so counts stay honest and the audience switcher does
        // what it says. GetTopBotsAsync returns hit-count DESC; sort client-side after the
        // filter to honour the user's sortBy.
        var raw = await _eventStore.GetTopBotsAsync(
            count: _signatureCache.MaxEntries,
            startTime: DateTime.UtcNow.AddHours(-24),
            endTime: DateTime.UtcNow,
            audienceFilter: "all");
        // Mirror SbWidgetBatchMiddleware.BuildTopBotsModel: hide internal traffic
        // (BotType.Internal -> loopback / RFC1918 / docker bridge / self-traffic)
        // from the All / Bots / Humans chips by default. Surfaced only via the
        // dedicated "Internal" chip. Keeps the operator's Live Activity panel
        // focused on public traffic instead of being drowned by StyloBot self-
        // requests and OTel collector noise.
        static bool IsInternal(DashboardTopBotEntry e) =>
            string.Equals(e.BotType, "Internal", StringComparison.OrdinalIgnoreCase);
        var publicTraffic = raw.Where(b => !IsInternal(b)).ToList();
        var internalCount = raw.Count - publicTraffic.Count;
        var bots = publicTraffic.Count(b => b.IsKnownBot);
        var humans = publicTraffic.Count - bots;
        IEnumerable<DashboardTopBotEntry> filtered = filter switch
        {
            "bots"     => publicTraffic.Where(b => b.IsKnownBot),
            "humans"   => publicTraffic.Where(b => !b.IsKnownBot),
            "internal" => raw.Where(IsInternal),
            _          => publicTraffic
        };
        var sorted = WidgetRenderHelpers.SortTopBots(filtered, sortBy, sortDir).ToList();
        var grouped = WidgetRenderHelpers.CollapseGroupableIdentities(sorted);
        grouped = WidgetRenderHelpers.ApplySearchFilter(grouped, searchQuery);
        var pagedBots = grouped.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new TopBotsListModel
        {
            Bots = pagedBots,
            Page = page,
            PageSize = pageSize,
            TotalCount = grouped.Count,
            SortField = sortBy,
            SortDir = sortDir,
            BasePath = _options.BasePath.TrimEnd('/'),
            Filter = filter,
            WidgetId = widgetId,
            Counts = new TopBotsCounts(All: publicTraffic.Count, Bots: bots, Humans: humans, Internal: internalCount),
            Query = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery.Trim(),
        };
    }


    private Task<TopBotsListModel> BuildTopBotsModelFromQuery(string widgetId, IQueryCollection q)
    {
        var sortBy = q["sort"].FirstOrDefault() ?? "default";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 10, 50);
        var filter = q["filter"].FirstOrDefault() ?? "bots";
        var routeWidgetId = q["widgetId"].FirstOrDefault() ?? widgetId;
        var searchQuery = q["q"].FirstOrDefault();
        return BuildTopBotsModel(page, pageSize, sortBy, sortDir, filter, routeWidgetId, searchQuery);
    }

    private async Task<List<DashboardCountryStats>> GetCountriesDataAsync()
    {
        var cached = _aggregateCache.Current.Countries;
        return cached.Count > 0 ? cached : await _eventStore.GetCountryStatsAsync(100);
    }

    private async Task<List<DashboardEndpointStats>> GetEndpointsDataAsync(HttpContext? httpContext = null)
    {
        var cached = _aggregateCache.Current.Endpoints;
        var endpoints = cached.Count > 0 ? cached : await _eventStore.GetEndpointStatsAsync(100);

        IReadOnlyList<PinnedEndpoint> pinned = [];
        if (httpContext != null)
        {
            var pinStore = httpContext.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
            if (pinStore != null)
                pinned = await pinStore.GetAllAsync();
        }

        var pinnedLookup = pinned.ToDictionary(p => (p.Method.ToUpperInvariant(), p.Path), p => p);

        var trafficKeys = endpoints.Select(e => (e.Method.ToUpperInvariant(), e.Path)).ToHashSet();
        var pinnedOnly = pinned
            .Where(p => !trafficKeys.Contains((p.Method.ToUpperInvariant(), p.Path)))
            .Select(p => new DashboardEndpointStats
            {
                Method = p.Method,
                Path = p.Path,
                IsPinned = true,
                IsHoneypot = p.IsHoneypot,
                PinId = p.Id,
                LastSeen = p.CreatedAt.UtcDateTime
            })
            .ToList();

        endpoints = [.. pinnedOnly, .. endpoints];

        if (httpContext != null)
        {
            var policyRegistry = httpContext.RequestServices
                .GetService(typeof(BotDetection.Policies.IPolicyRegistry))
                as BotDetection.Policies.IPolicyRegistry;

            endpoints = endpoints.Select(e =>
            {
                var key = (e.Method.ToUpperInvariant(), e.Path);
                var pin = pinnedLookup.GetValueOrDefault(key);
                return e with
                {
                    IsPinned = pin != null,
                    IsHoneypot = pin?.IsHoneypot ?? false,
                    PinId = pin?.Id,
                    ActivePolicyName = e.ActivePolicyName
                        ?? policyRegistry?.GetPolicyForPath(e.Path).Name
                };
            }).ToList();
        }

        return endpoints;
    }

    private IReadOnlyList<EndpointPackCoverage> BuildEndpointDetailCoverage(HttpContext context, string path)
    {
        // IReactionPackContext is a dormant extension seam: no core service
        // registers it, so this is null today and the coverage panel stays
        // hidden. A future/commercial reaction-pack engine implements it to
        // light the panel up. See docs/architecture/reaction-packs.md.
        var packContext = context.RequestServices.GetService<IReactionPackContext>();
        if (packContext == null) return [];

        var coverage = new List<EndpointPackCoverage>();
        foreach (var state in packContext.GetActiveStates())
        {
            if (state.Scope == "global" || state.Scope == path)
                coverage.Add(new EndpointPackCoverage(state.PackName, state.Scope, state.Level, state.PolicyName));
        }

        return coverage;
    }

    private EndpointsListModel BuildEndpointsModel(HttpContext context, string sortField, string sortDir, int page, int pageSize, List<DashboardEndpointStats> all)
    {
        IEnumerable<DashboardEndpointStats> sorted = sortField.ToLowerInvariant() switch
        {
            "method" => sortDir == "asc" ? all.OrderBy(e => e.Method) : all.OrderByDescending(e => e.Method),
            "path" => sortDir == "asc" ? all.OrderBy(e => e.Path) : all.OrderByDescending(e => e.Path),
            "bots" => sortDir == "asc" ? all.OrderBy(e => e.BotCount) : all.OrderByDescending(e => e.BotCount),
            "botrate" => sortDir == "asc" ? all.OrderBy(e => e.BotRate) : all.OrderByDescending(e => e.BotRate),
            "latency" => sortDir == "asc" ? all.OrderBy(e => e.AvgProcessingTimeMs) : all.OrderByDescending(e => e.AvgProcessingTimeMs),
            "threat" => sortDir == "asc" ? all.OrderBy(e => e.AvgThreatScore) : all.OrderByDescending(e => e.AvgThreatScore),
            "unique" => sortDir == "asc" ? all.OrderBy(e => e.UniqueSignatures) : all.OrderByDescending(e => e.UniqueSignatures),
            "lastseen" => sortDir == "asc" ? all.OrderBy(e => e.LastSeen) : all.OrderByDescending(e => e.LastSeen),
            _ => sortDir == "asc" ? all.OrderBy(e => e.TotalCount) : all.OrderByDescending(e => e.TotalCount)
        };

        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new EndpointsListModel
        {
            Endpoints = paged,
            BasePath = _options.BasePath.TrimEnd('/'),
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count,
            IsCommercial = IsCommercialMode(context),
            AllowEndpointPinning = _options.EnableEndpointPinning,
        };
    }

    private UserAgentsListModel BuildUserAgentsModel(string filter, string sortField, string sortDir, int page, int pageSize, List<DashboardUserAgentSummary> all)
    {
        // Apply filter
        IEnumerable<DashboardUserAgentSummary> filtered = filter switch
        {
            "browser" => all.Where(u => u.Category == "Browser"),
            "bot" => all.Where(u => u.BotRate > 0.5),
            "ai" => all.Where(u => u.Category is "AI" or "AiBot"),
            "tool" => all.Where(u => u.Category is "Tool" or "Scraper" or "MonitoringBot"),
            _ => all
        };

        // Apply sort
        var filteredList = filtered.ToList();
        IEnumerable<DashboardUserAgentSummary> sorted = sortField.ToLowerInvariant() switch
        {
            "family" => sortDir == "asc" ? filteredList.OrderBy(u => u.Family) : filteredList.OrderByDescending(u => u.Family),
            "category" => sortDir == "asc" ? filteredList.OrderBy(u => u.Category) : filteredList.OrderByDescending(u => u.Category),
            "botrate" => sortDir == "asc" ? filteredList.OrderBy(u => u.BotRate) : filteredList.OrderByDescending(u => u.BotRate),
            "confidence" => sortDir == "asc" ? filteredList.OrderBy(u => u.AvgConfidence) : filteredList.OrderByDescending(u => u.AvgConfidence),
            "lastseen" => sortDir == "asc" ? filteredList.OrderBy(u => u.LastSeen) : filteredList.OrderByDescending(u => u.LastSeen),
            _ => sortDir == "asc" ? filteredList.OrderBy(u => u.TotalCount) : filteredList.OrderByDescending(u => u.TotalCount) // "requests"
        };

        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new UserAgentsListModel
        {
            UserAgents = paged,
            BasePath = _options.BasePath.TrimEnd('/'),
            Filter = filter,
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = filteredList.Count
        };
    }

    private CountriesListModel BuildCountriesModel(string sortField, string sortDir, int page, int pageSize, List<DashboardCountryStats> all)
    {
        IEnumerable<DashboardCountryStats> sorted = sortField.ToLowerInvariant() switch
        {
            "country" => sortDir == "asc" ? all.OrderBy(c => c.CountryCode) : all.OrderByDescending(c => c.CountryCode),
            "botrate" => sortDir == "asc" ? all.OrderBy(c => c.BotRate) : all.OrderByDescending(c => c.BotRate),
            "bots" => sortDir == "asc" ? all.OrderBy(c => c.BotCount) : all.OrderByDescending(c => c.BotCount),
            "humans" => sortDir == "asc" ? all.OrderBy(c => c.HumanCount) : all.OrderByDescending(c => c.HumanCount),
            _ => sortDir == "asc" ? all.OrderBy(c => c.TotalCount) : all.OrderByDescending(c => c.TotalCount) // "total"
        };

        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new CountriesListModel
        {
            Countries = paged,
            BasePath = _options.BasePath.TrimEnd('/'),
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        };
    }

    private async Task<SessionsListModel> BuildSessionsModel(HttpContext context, int page = 1, int pageSize = 25, string? filter = null)
    {
        var sessionStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.IDetectionArchive>();
        if (sessionStore == null)
        {
            return new SessionsListModel
            {
                Sessions = [],
                BasePath = _options.BasePath.TrimEnd('/'),
                Filter = filter
            };
        }

        bool? isBot = filter switch { "bot" => true, "human" => false, _ => null };
        var since = DateTime.UtcNow - _options.DetectionRetention;
        const int maxFetch = 500;
        var fetchCount = Math.Min(page * pageSize + pageSize, maxFetch);
        var sessions = await sessionStore.GetRecentSessionsAsync(fetchCount, isBot, since);
        var totalCount = sessions.Count < maxFetch ? sessions.Count : maxFetch;

        var sigLookup = await _eventStore.LoadSignatureLookupAsync();
        var uaLookup  = await _eventStore.LoadUserAgentLookupAsync();

        var entries = sessions
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SessionListEntry
        {
            Id = s.Id,
            Signature = s.Signature,
            StartedAt = s.StartedAt,
            EndedAt = s.EndedAt,
            RequestCount = s.RequestCount,
            DominantState = s.DominantState,
            IsBot = s.IsBot,
            AvgBotProbability = s.AvgBotProbability,
            RiskBand = s.RiskBand,
            Action = s.Action,
            BotName = sigLookup.ResolveBotName(_signatureCache, s.Signature, s.BotName),
            CountryCode = s.CountryCode,
            UserAgent = uaLookup.ResolveUserAgent(_signatureCache, s.Signature),
            ErrorCount = s.ErrorCount,
            TimingEntropy = s.TimingEntropy,
            Maturity = s.Maturity,
            TransitionCounts = s.TransitionCountsJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson)
                : null
        }).ToList();

        return new SessionsListModel
        {
            Sessions = entries,
            BasePath = _options.BasePath.TrimEnd('/'),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Filter = filter
        };
    }

    private async Task<ClustersListModel> BuildClustersModelAsync(HttpContext context)
    {
        var clusterService = context.RequestServices.GetService(typeof(IBotClusterReader))
            as IBotClusterReader;

        var ct = context.RequestAborted;
        var rawClusters = clusterService is null
            ? Array.Empty<BotCluster>()
            : await clusterService.GetClustersAsync(ct);

        var clusters = rawClusters
            .Select(cl => new ClusterViewModel
            {
                ClusterId = cl.ClusterId,
                Label = cl.Label ?? "Unknown",
                Description = cl.Description,
                Type = cl.Type.ToString(),
                MemberCount = cl.MemberCount,
                AvgBotProb = Math.Round(cl.AverageBotProbability, 3),
                Country = cl.DominantCountry,
                AverageSimilarity = Math.Round(cl.AverageSimilarity, 3),
                TemporalDensity = Math.Round(cl.TemporalDensity, 3),
                DominantIntent = cl.DominantIntent,
                AverageThreatScore = Math.Round(cl.AverageThreatScore, 3)
            })
            .ToList();

        return new ClustersListModel
        {
            Clusters = clusters,
            Diagnostics = clusterService == null ? null : await BuildClusterDiagnosticsModelAsync(clusterService, ct),
            BasePath = _options.BasePath.TrimEnd('/')
        };
    }

    private static async Task<ClusterDiagnosticsViewModel> BuildClusterDiagnosticsModelAsync(IBotClusterReader clusterService, CancellationToken ct)
    {
        var diagnostics = await clusterService.GetDiagnosticsAsync(ct);
        return new ClusterDiagnosticsViewModel
        {
            Algorithm = diagnostics.Algorithm,
            Status = diagnostics.Status,
            LastRunAt = diagnostics.LastRunAt,
            InputBehaviorCount = diagnostics.InputBehaviorCount,
            EdgeCount = diagnostics.EdgeCount,
            GraphDensity = Math.Round(diagnostics.GraphDensity, 3),
            RawCommunityCount = diagnostics.RawCommunityCount,
            ClusterCount = diagnostics.ClusterCount,
            HumanClusterCount = diagnostics.HumanCount,
            MachineClusterCount = diagnostics.ProductCount + diagnostics.NetworkCount + diagnostics.EmergentCount,
            MixedClusterCount = diagnostics.MixedCount,
            SimilarityThreshold = diagnostics.SimilarityThreshold,
            MinClusterSize = diagnostics.MinClusterSize,
            TopWeights = diagnostics.TopWeights
                .OrderByDescending(w => w.Value)
                .Take(6)
                .ToList()
        };
    }

    private async Task ServeBotBreakdownPartialAsync(HttpContext context)
    {
        var detections = await _eventStore.GetDetectionsAsync(new DashboardFilter { Limit = 5000 });
        var botDetections = detections.Where(d => d.IsBot).ToList();

        var categories = botDetections
            .GroupBy(d =>
            {
                if (d.ImportantSignals?.TryGetValue("intent.category", out var cat) == true
                    && cat is string catStr && !string.IsNullOrEmpty(catStr))
                    return catStr;
                return "unclassified";
            })
            .Select(g => new BotCategoryStats
            {
                Category = g.Key,
                DisplayName = FormatCategoryName(g.Key),
                SessionCount = g.Count(),
                Percentage = botDetections.Count > 0
                    ? Math.Round((double)g.Count() / botDetections.Count * 100, 1) : 0,
                TopEndpoints = g.Where(d => !string.IsNullOrEmpty(d.Path))
                    .GroupBy(d => d.Path!)
                    .OrderByDescending(pg => pg.Count())
                    .Take(3)
                    .Select(pg => pg.Key)
                    .ToList(),
                Color = GetCategoryColor(g.Key)
            })
            .OrderByDescending(c => c.SessionCount)
            .ToList();

        var model = new BotBreakdownModel
        {
            BasePath = _options.BasePath.TrimEnd('/'),
            Categories = categories,
            TotalBotSessions = botDetections.Count
        };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_BotBreakdown.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    private static string FormatCategoryName(string category) => category.ToLowerInvariant() switch
    {
        "scraping" => "Scrapers",
        "scanning" => "Vulnerability Scanners",
        "credential_abuse" => "Credential Attacks",
        "api_abuse" => "API Abuse",
        "reconnaissance" => "Reconnaissance",
        "monitoring" => "Monitoring Bots",
        "browsing" => "Browsing",
        "unclassified" => "Unclassified",
        _ => category
    };

    private static string GetCategoryColor(string category) => category.ToLowerInvariant() switch
    {
        "scraping" => "#f59e0b",
        "scanning" => "#ef4444",
        "credential_abuse" => "#dc2626",
        "api_abuse" => "#f97316",
        "reconnaissance" => "#8b5cf6",
        "monitoring" => "#06b6d4",
        "browsing" => "#10b981",
        _ => "#6b7280"
    };


    private async Task ServeEndpointPinsApiAsync(HttpContext context)
    {
        var pinStore = context.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
        if (pinStore == null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]");
            return;
        }
        var pins = await pinStore.GetAllAsync();
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, pins, CamelCaseJson);
    }

    private async Task HandlePinEndpointAsync(HttpContext context)
    {
        var pinStore = context.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
        if (pinStore == null) { context.Response.StatusCode = 503; return; }

        string? path, note;
        string method;
        bool isHoneypot;

        var ct = context.Request.ContentType ?? "";
        if (ct.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            PinRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<PinRequest>(
                    context.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                context.Response.StatusCode = 400;
                return;
            }
            path = req?.Path; method = req?.Method ?? "ANY"; note = req?.Note; isHoneypot = req?.IsHoneypot ?? false;
        }
        else
        {
            path = context.Request.Form["path"].FirstOrDefault();
            method = context.Request.Form["method"].FirstOrDefault() ?? "ANY";
            note = context.Request.Form["note"].FirstOrDefault();
            isHoneypot = context.Request.Form["isHoneypot"].FirstOrDefault() == "true";
        }

        if (string.IsNullOrWhiteSpace(path) || !path!.StartsWith('/'))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Path is required and must start with /\"}");
            return;
        }

        if (path.Length > 500)
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Path too long\"}");
            return;
        }

        var methodNorm = string.IsNullOrWhiteSpace(method) ? "ANY" : method.ToUpperInvariant();
        if (!AllowedPinMethods.Contains(methodNorm))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Invalid method\"}");
            return;
        }

        var pin = await pinStore.AddAsync(methodNorm, path, isHoneypot, string.IsNullOrWhiteSpace(note) ? null : note);
        if (pin == null)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Failed to persist pin\"}");
            return;
        }

        context.Response.StatusCode = 201;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, pin, CamelCaseJson);
    }

    private async Task HandleUnpinEndpointAsync(HttpContext context, string idSegment)
    {
        if (!long.TryParse(idSegment, out var id))
        {
            context.Response.StatusCode = 400;
            return;
        }
        var pinStore = context.RequestServices.GetService(typeof(IPinnedEndpointStore)) as IPinnedEndpointStore;
        if (pinStore == null) { context.Response.StatusCode = 503; return; }
        var removed = await pinStore.RemoveAsync(id);
        context.Response.StatusCode = removed ? 204 : 404;
    }

    private sealed record PinRequest(string Path, string? Method, bool IsHoneypot, string? Note);

    private static readonly HashSet<string> AllowedPinMethods =
        new(StringComparer.OrdinalIgnoreCase) { "ANY", "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" };

}
