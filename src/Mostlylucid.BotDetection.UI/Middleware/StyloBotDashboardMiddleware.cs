using System.Collections.Concurrent;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Middleware;
using System.Reflection;
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
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Auth;
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
    private static volatile bool _authWarningLogged;

    // Caches whether any dashboard users exist. Set to true on first confirmed user; never reverts.
    // Avoids a DB round-trip on every unauthenticated request once deployment is past first-run.
    private static volatile bool _usersExist;

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
        "api/config/schema",
        "api/endpoint-pins",
    };

    private const string CountryDetailPrefix = "api/countries/";
    private const string EndpointDetailPrefix = "api/endpoints/";
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
    private readonly IReadOnlyList<PackTabInfo> _packTabs;

    public StyloBotDashboardMiddleware(
        RequestDelegate next,
        StyloBotDashboardOptions options,
        IDashboardEventStore eventStore,
        DashboardAggregateCache aggregateCache,
        SignatureAggregateCache signatureCache,
        RazorViewRenderer razorViewRenderer,
        IMemoryCache widgetCache,
        IWebHostEnvironment env,
        IEnumerable<IMonitoringPack> monitoringPacks,
        ILogger<StyloBotDashboardMiddleware> logger)
    {
        _next = next;
        _options = options;
        _eventStore = eventStore;
        _aggregateCache = aggregateCache;
        _signatureCache = signatureCache;
        _razorViewRenderer = razorViewRenderer;
        _widgetCache = widgetCache;
        _env = env;
        _packTabs = monitoringPacks
            .Select(p => new PackTabInfo(p.Id, p.TabName))
            .ToList();
        _logger = logger;
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

            case "api/me":
                await ServeMeApiAsync(context);
                break;

            case "api/license":
                await ServeLicenseApiAsync(context);
                break;

            case "api/config/manifests":
                await ServeConfigManifestsListAsync(context);
                break;

            case "api/config/schema":
                await ServeConfigSchemaAsync(context);
                break;

            case var p when p.StartsWith("api/config/manifests/", StringComparison.OrdinalIgnoreCase):
                await ServeConfigManifestApiAsync(context, relativePath["api/config/manifests/".Length..]);
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

            case "partials/recent":
                await ServeRecentActivityPartialAsync(context);
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

            case "investigate":
                await ServeInvestigationAsync(context);
                break;

            case var p when p.StartsWith("investigate/tab/", StringComparison.OrdinalIgnoreCase):
                await ServeInvestigationTabAsync(context, relativePath["investigate/tab/".Length..]);
                break;

            case "investigate/load-preset":
                await ServeLoadPresetAsync(context);
                break;

            case var p when p.StartsWith("help/", StringComparison.OrdinalIgnoreCase):
                await ServeHelpAsync(context, relativePath["help/".Length..]);
                break;

            case var p when p.StartsWith("signature/", StringComparison.OrdinalIgnoreCase):
                if (_options.RenderPage)
                {
                    // Use original relativePath (not lowercased) to preserve signature case
                    await ServeSignatureDetailAsync(context, relativePath.Substring("signature/".Length));
                }
                else
                {
                    // Host MVC owns the signature detail page.
                    await _next(context);
                }
                break;

            case "api/endpoint-pins":
                if (context.Request.Method == "GET")
                    await ServeEndpointPinsApiAsync(context);
                else if (context.Request.Method == "POST")
                    await HandlePinEndpointAsync(context);
                else
                    context.Response.StatusCode = 405;
                break;

            case var p when p.StartsWith("api/endpoint-pins/", StringComparison.OrdinalIgnoreCase):
                if (context.Request.Method == "DELETE")
                    await HandleUnpinEndpointAsync(context, relLower["api/endpoint-pins/".Length..]);
                else
                    context.Response.StatusCode = 405;
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

        var user = new StyloBotUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, password);

        if (result.Succeeded)
        {
            _usersExist = true;
            _logger.LogInformation("StyloBot Dashboard: first admin account created for {Email}", email);
            context.Response.Redirect($"{basePath}/auth-ui");
        }
        else
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            context.Response.Redirect($"{basePath}/setup?error={Uri.EscapeDataString(errors)}");
        }
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

    /// <summary>
    ///     Checks if the current request is authorized for WRITE operations (config save/delete).
    ///     Write access is denied by default - requires explicit WriteAuthorizationFilter
    ///     or RequireWriteAuthorizationPolicy configuration. Viewing the dashboard does NOT grant write access.
    /// </summary>
    private async Task<bool> IsWriteAuthorizedAsync(HttpContext context)
    {
        // Custom write filter takes precedence
        if (_options.WriteAuthorizationFilter != null)
            return await _options.WriteAuthorizationFilter(context);

        // Policy-based write auth
        if (!string.IsNullOrEmpty(_options.RequireWriteAuthorizationPolicy))
        {
            var authService = context.RequestServices
                    .GetService(typeof(IAuthorizationService))
                as IAuthorizationService;

            if (authService != null)
            {
                var result = await authService.AuthorizeAsync(
                    context.User, null, _options.RequireWriteAuthorizationPolicy);
                return result.Succeeded;
            }
        }

        // Config editing must be explicitly enabled AND write auth configured
        if (!_options.EnableConfigEditing)
        {
            _logger.LogWarning("Config write attempt denied: EnableConfigEditing is false");
            return false;
        }

        // Default DENY - write access is never implicitly granted
        _logger.LogWarning("Config write attempt denied: no WriteAuthorizationFilter or RequireWriteAuthorizationPolicy configured");
        return false;
    }

    private async Task ServeDashboardPageAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html";

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
            "img-src 'self' data:",
            "font-src 'self' data:",
            $"style-src 'self' 'unsafe-inline'",
            $"script-src 'self' 'nonce-{cspNonce}'",
            // worker-src for Monaco's language-service web workers (loaded from CDN as
            // blob: URLs after the AMD loader rewrites them). Safe to allow on the
            // dashboard origin since Monaco is the only consumer.
            "worker-src 'self' blob:",
            "connect-src 'self' ws: wss:");
        context.Response.Headers["Content-Security-Policy"] = dashboardCsp;

        var basePath = _options.BasePath.TrimEnd('/');
        var tab = context.Request.Query["tab"].FirstOrDefault() ?? "overview";
        // "metrics" was the hardcoded tab name before pack-driven tabs; redirect old bookmarks.
        if (tab == "metrics") tab = _packTabs.Count > 0 ? _packTabs[0].Id : "overview";

        // Build all partial models server-side - fully rendered, no JSON serialization needed
        var visitorCache = context.RequestServices.GetRequiredService<VisitorListCache>();
        var (visitors, visitorTotal, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, 24);

        DashboardSummary summary;
        try { summary = await _eventStore.GetSummaryAsync(); }
        catch { summary = new DashboardSummary { Timestamp = DateTime.UtcNow, TotalRequests = 0, BotRequests = 0, HumanRequests = 0, UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(), UniqueSignatures = 0 }; }

        List<DashboardCountryStats> countriesData;
        try { countriesData = await GetCountriesDataAsync(); }
        catch { countriesData = []; }

        List<DashboardEndpointStats> endpointsData;
        try { endpointsData = await GetEndpointsDataAsync(context); }
        catch { endpointsData = []; }

        var allUserAgents = _aggregateCache.Current.UserAgents.Count > 0
            ? _aggregateCache.Current.UserAgents
            : await ComputeUserAgentsFallbackAsync();

        ShapeInvestigationViewModel? investigationVm = null;
        if (tab.Equals("investigate", StringComparison.OrdinalIgnoreCase))
        {
            var shapeFilter = ParseShapeSearchFilter(context);
            InvestigationResult invResult;
            var shapeStore = context.RequestServices.GetService<IShapeSearchStore>();
            if (shapeFilter.TargetShape is not null && shapeStore is not null)
            {
                invResult = await shapeStore.SearchByShapeAsync(shapeFilter);
            }
            else
            {
                var invFilter = new InvestigationFilter
                {
                    EntityType = context.Request.Query["type"].FirstOrDefault() ?? "signature",
                    EntityValue = context.Request.Query["value"].FirstOrDefault() ?? "",
                    Start = shapeFilter.Start,
                    End = shapeFilter.End,
                    Tab = shapeFilter.Tab,
                    Offset = shapeFilter.Offset
                };
                invResult = await _eventStore.GetInvestigationAsync(invFilter);
            }

            var invPresets = shapeStore is not null
                ? await shapeStore.GetPresetsAsync()
                : Array.Empty<InvestigationPreset>();
            var invHasCommercial = IsCommercialMode(context);
            var invTabs = new List<string> { "detections", "signatures", "endpoints", "geo", "signaltrace" };
            if (invHasCommercial) invTabs.Insert(invTabs.Count - 1, "fingerprints");

            investigationVm = new ShapeInvestigationViewModel
            {
                Filter = shapeFilter,
                Result = invResult,
                BasePath = _options.BasePath,
                FilterGroups = ShapeFilterGroups,
                Presets = invPresets.ToList(),
                AvailableTabs = invTabs,
                HasShapeSearch = shapeStore is not null,
                IsPaid = invHasCommercial
            };
        }

        var model = new DashboardShellModel
        {
            CspNonce = cspNonce,
            BasePath = basePath,
            HubPath = _options.HubPath,
            ActiveTab = tab,
            Version = DashboardVersion,
            Summary = BuildSummaryStatsModelFromVisitorCache(summary, basePath, visitorCache),
            Visitors = new VisitorListModel
            {
                Visitors = visitors, Counts = visitorCache.GetCounts(),
                Filter = "all", SortField = "lastSeen", SortDir = "desc",
                Page = 1, PageSize = 24, TotalCount = visitorTotal, BasePath = basePath
            },
            YourDetection = BuildYourDetectionPartialModel(context),
            Countries = BuildCountriesModel("total", "desc", 1, 20, countriesData),
            Endpoints = BuildEndpointsModel("total", "desc", 1, 20, endpointsData),
            Clusters = await BuildClustersModelAsync(context),
            UserAgents = BuildUserAgentsModel("all", "requests", "desc", 1, 25, allUserAgents),
            TopBots = BuildTopBotsModel(page: 1, pageSize: 10, sortBy: "default", sortDir: "desc"),
            Sessions = await BuildSessionsModel(context),
            Threats = await BuildThreatsModelAsync(),
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
            MonitoringPacks = _packTabs
        };

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/Index.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     Builds the "You" panel JSON by looking up the current visitor in the cache.
    ///     Since /_stylobot is excluded from detection, we compute the visitor's signature
    ///     using MultiFactorSignatureService and look them up in VisitorListCache.
    /// </summary>
    private string BuildYourDetectionJson(HttpContext context)
    {
        try
        {
            var sigService = context.RequestServices.GetService(typeof(MultiFactorSignatureService))
                as MultiFactorSignatureService;
            var visitorCache = context.RequestServices.GetService(typeof(VisitorListCache))
                as VisitorListCache;

            if (sigService == null || visitorCache == null)
                return "null";

            // Read the signature set the orchestrator's foundation wave wrote; fall back to
            // a fresh compute when no detection ran on this request.
            MultiFactorSignatures? sigs = null;
            if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
                && evObj is AggregatedEvidence ev
                && ev.Signals.TryGetValue(SignalKeys.SignatureMultifactor, out var multiObj)
                && multiObj is MultiFactorSignatures m)
            {
                sigs = m;
            }
            sigs ??= sigService.GenerateSignatures(context);
            var visitor = visitorCache.Get(sigs.PrimarySignature);

            if (visitor == null)
                return JsonSerializer.Serialize(new { signature = sigs.PrimarySignature }, CamelCaseJson);

            var narrativeEvent = new DashboardDetectionEvent
            {
                RequestId = "self",
                Timestamp = visitor.LastSeen,
                IsBot = visitor.IsBot,
                BotProbability = visitor.BotProbability,
                Confidence = visitor.Confidence,
                RiskBand = visitor.RiskBand,
                BotType = visitor.BotType,
                BotName = visitor.BotName,
                Action = visitor.Action,
                Method = "GET",
                Path = visitor.LastPath ?? "/",
                TopReasons = visitor.TopReasons
            };
            var narrative = DetectionNarrativeBuilder.Build(narrativeEvent);

            var yourDetection = new
            {
                isBot = visitor.IsBot,
                botProbability = Math.Round(visitor.BotProbability, 4),
                confidence = Math.Round(visitor.Confidence, 4),
                riskBand = visitor.RiskBand,
                processingTimeMs = visitor.ProcessingTimeMs,
                detectorCount = visitor.TopReasons.Count,
                narrative = visitor.Narrative ?? narrative,
                topReasons = visitor.TopReasons,
                signature = sigs.PrimarySignature,
                threatScore = visitor.ThreatScore.HasValue ? Math.Round(visitor.ThreatScore.Value, 3) : (double?)null,
                threatBand = visitor.ThreatBand
            };

            return JsonSerializer.Serialize(yourDetection,
                CamelCaseJson);
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
        var json = BuildYourDetectionJson(context);
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
        var summary = await _eventStore.GetSummaryAsync();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, summary, CamelCaseJson);
    }

    private async Task ServeTimeSeriesApiAsync(HttpContext context)
    {
        try
        {
            var startTimeStr = context.Request.Query["start"].FirstOrDefault();
            var endTimeStr = context.Request.Query["end"].FirstOrDefault();
            var bucketSizeStr = context.Request.Query["bucket"].FirstOrDefault() ?? "60";

            var startTime = DateTime.TryParse(startTimeStr, out var start)
                ? start
                : DateTime.UtcNow.AddHours(-1);

            var endTime = DateTime.TryParse(endTimeStr, out var end)
                ? end
                : DateTime.UtcNow;

            var bucketSize = TimeSpan.FromSeconds(
                int.TryParse(bucketSizeStr, out var b) ? b : 60);

            var timeSeries = await _eventStore.GetTimeSeriesAsync(startTime, endTime, bucketSize);

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

        // Get visitor cache if available
        var visitorCache = context.RequestServices
            .GetService(typeof(VisitorListCache)) as VisitorListCache;

        var topBots = visitorCache?.GetTopBots(10);
        var filterCounts = visitorCache?.GetCounts();

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

        List<DashboardCountryStats> dbCountries;
        if (startTime.HasValue || endTime.HasValue)
        {
            // Time-filtered: always query the store directly
            dbCountries = await _eventStore.GetCountryStatsAsync(count, startTime, endTime);
        }
        else
        {
            // No time filter: serve from periodic cache
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

        List<DashboardEndpointStats> endpoints;
        if (startTime.HasValue || endTime.HasValue)
        {
            endpoints = await _eventStore.GetEndpointStatsAsync(count, startTime, endTime);
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
        var sessionStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.ISessionStore>();
        if (sessionStore == null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]");
            return;
        }

        var limit = int.TryParse(context.Request.Query["limit"], out var l) ? Math.Min(l, 100) : 50;
        var botFilter = context.Request.Query["isBot"].FirstOrDefault();
        bool? isBot = botFilter switch { "true" => true, "false" => false, _ => null };

        var sessions = await sessionStore.GetRecentSessionsAsync(limit, isBot);

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

        var sessionStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.ISessionStore>();
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
                ? BotDetection.Data.SqliteSessionStore.DeserializeVector(s.Vector)
                : null)
            .ToList();

        var result = sessions.Select((s, idx) => new
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
            radarAxes = s.Vector is { Length: > 0 }
                ? BotDetection.Analysis.VectorRadarProjection.Project(sessionVectors[idx]!)
                : null
        }).ToList<object>();

        // Include live in-progress session from write-through cache if available.
        // This ensures there's always a behavioral shape, even before session finalization.
        var liveSessionStore = context.RequestServices.GetService<BotDetection.Analysis.SessionStore>();
        if (liveSessionStore != null)
        {
            var liveSession = liveSessionStore.GetCurrentSession(decodedSignature);
            if (liveSession is { Count: >= 1 })
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
                    radarAxes = liveRadar
                });
            }
        }

        // Fallback: when no finalized sessions and no live session exist, synthesize session
        // objects from dashboard_detections. This guarantees behavioral shape always shows
        // on the signature detail page even for in-flight or low-traffic signatures.
        if (result.Count == 0)
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
                    // Group into synthetic sessions by 30-min inactivity gaps
                    var ordered = detections.OrderBy(d => d.Timestamp).ToList();
                    var syntheticGroups = new List<List<DashboardDetectionEvent>>();
                    var currentGroup = new List<DashboardDetectionEvent> { ordered[0] };
                    for (var i = 1; i < ordered.Count; i++)
                    {
                        if ((ordered[i].Timestamp - ordered[i - 1].Timestamp).TotalMinutes > 30)
                        {
                            syntheticGroups.Add(currentGroup);
                            currentGroup = new List<DashboardDetectionEvent>();
                        }
                        currentGroup.Add(ordered[i]);
                    }
                    syntheticGroups.Add(currentGroup);

                    foreach (var group in syntheticGroups.OrderByDescending(g => g[^1].Timestamp))
                    {
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

                        result.Add(new
                        {
                            Id = $"det-{group[0].Timestamp:yyyyMMddHHmm}",
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
                            live = false,
                            velocity = (object?)null,
                            transitionCounts = (Dictionary<string, int>?)null,
                            paths,
                            radarAxes
                        });
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
                family = DashboardSummaryBroadcaster.ExtractBrowserFamily(d.UserAgent);
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
            Category = DashboardSummaryBroadcaster.InferUaCategory(kv.Key),
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

        // Fall back to VisitorListCache (has richer history with processing times)
        var visitorCache = context.RequestServices
            .GetService(typeof(VisitorListCache)) as VisitorListCache;
        var visitor = visitorCache?.Get(decodedSignature);

        List<double> processingTimes, botProbabilities, confidences;

        if (visitor != null)
        {
            lock (visitor.SyncRoot)
            {
                processingTimes = visitor.ProcessingTimeHistory.ToList();
                botProbabilities = visitor.BotProbabilityHistory.ToList();
                confidences = visitor.ConfidenceHistory.ToList();
            }
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

    private static (bool Allowed, int Remaining) CheckRateLimit(string clientIp, DateTime now)
        => CheckRateLimit(clientIp, now, _rateLimits, DiagnosticsRateLimit);

    private static (bool Allowed, int Remaining) CheckRateLimit(
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
        var visitorCache = context.RequestServices.GetRequiredService<VisitorListCache>();
        var filter = context.Request.Query["filter"].FirstOrDefault() ?? "all";
        var sortField = context.Request.Query["sort"].FirstOrDefault() ?? "lastSeen";
        var sortDir = context.Request.Query["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(context.Request.Query["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(context.Request.Query["pageSize"].FirstOrDefault(), out var ps) && ps is > 0 and <= 100 ? ps : 24;

        var (items, totalCount, _, _) = visitorCache.GetFiltered(filter, sortField, sortDir, page, pageSize);
        var counts = visitorCache.GetCounts();

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
            BasePath = _options.BasePath.TrimEnd('/')
        };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbVisitorList/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>Render the summary stats partial.</summary>
    private async Task ServeSummaryPartialAsync(HttpContext context)
    {
        var model = await BuildSummaryStatsModelAsync(context);
        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbSummaryStats/Default.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>Build a SummaryStatsModel with session analytics from a pre-fetched VisitorListCache.</summary>
    private static SummaryStatsModel BuildSummaryStatsModelFromVisitorCache(
        DashboardSummary summary, string basePath, VisitorListCache visitorCache)
    {
        var model = new SummaryStatsModel { Summary = summary, BasePath = basePath };
        PopulateSessionAnalytics(model, visitorCache);
        return model;
    }

    /// <summary>Populate session analytics fields on an existing SummaryStatsModel from the visitor cache.</summary>
    private static void PopulateSessionAnalytics(SummaryStatsModel model, VisitorListCache visitorCache)
    {
        var (allVisitors, totalCount, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, int.MaxValue);
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

        static double AvgDuration(IReadOnlyList<CachedVisitor> visitors)
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
        var summary = await _eventStore.GetSummaryAsync();
        var basePath = _options.BasePath.TrimEnd('/');
        var model = new SummaryStatsModel { Summary = summary, BasePath = basePath };

        var visitorCache = context.RequestServices.GetService<VisitorListCache>();
        if (visitorCache != null)
            PopulateSessionAnalytics(model, visitorCache);

        return model;
    }

    /// <summary>Render the "Your Detection" partial.</summary>
    private async Task ServeYourDetectionPartialAsync(HttpContext context)
    {
        var model = BuildYourDetectionPartialModel(context);
        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_YourDetection.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     <c>GET /api/license</c> - JSON payload with parsed claims + live entitlement stats.
    ///     Used by external tooling and tests; the dashboard itself loads <c>/partials/license</c>.
    /// </summary>
    private async Task ServeLicenseApiAsync(HttpContext context)
    {
        var model = BuildLicenseCardModel(context);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            status = model.Status.ToString(),
            stats = model.Stats,
            claims = model.Claims,
            daysUntilExpiry = model.DaysUntilExpiry,
            daysUntilGraceEnds = model.DaysUntilGraceEnds
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
        var redis = context.RequestServices.GetService(
            Type.GetType("Stylobot.Commercial.Cache.Redis.IRedisCacheProvider, Stylobot.Commercial.Cache.Redis"));
        if (redis is not null)
            services.Add(new ServiceStatus("Redis", true));

        // Check compliance pack
        var packProvider = context.RequestServices.GetService<Mostlylucid.BotDetection.Compliance.ICompliancePackProvider>();
        var packName = packProvider?.ActivePack.Name ?? "Default";

        // Check LLM
        var llm = context.RequestServices.GetService(
            Type.GetType("Mostlylucid.BotDetection.Services.ILlmClassificationService, Mostlylucid.BotDetection"));
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

        // Config editing requires explicit opt-in via EnableConfigEditing + write auth
        var canEdit = _options.EnableConfigEditing &&
            (_options.WriteAuthorizationFilter != null || !string.IsNullOrEmpty(_options.RequireWriteAuthorizationPolicy));

        return new ConfigurationEditorModel
        {
            BasePath = _options.BasePath.TrimEnd('/'),
            Detectors = await editor.ListManifestsAsync(context.RequestAborted),
            IsCommercialLicensed = commercial,
            ReadOnly = !canEdit
        };
    }

    // ====================================================================================
    // Configuration editor endpoints (FOSS YAML editor)
    // The ConfigEditorService does the heavy lifting (path safety, YAML parse validation,
    // atomic write). These methods only translate between HTTP and the service result enum.
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

    /// <summary><c>GET /api/config/schema</c> - JSON Schema for Monaco's YAML model binding.</summary>
    private async Task ServeConfigSchemaAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json";
        // Hand-written schema covering the high-traffic 80% of manifest fields. Monaco
        // will mark unknown keys as warnings, not errors - fine for the long tail
        // (Triggers / Emits / Listens). Full schema is a follow-up.
        await context.Response.WriteAsync(DetectorManifestJsonSchema);
    }

    /// <summary>
    ///     Method-dispatched route for <c>/api/config/manifests/{slug}</c>:
    ///     GET = read, PUT = save override, DELETE = revert to embedded.
    /// </summary>
    private async Task ServeConfigManifestApiAsync(HttpContext context, string slug)
    {
        // Reads go through the interface (remote-substitutable); writes need the concrete
        // class. Remote-mode hosts register only IConfigEditorService, so the concrete
        // resolution returns null and PUT/DELETE 503 cleanly.
        var reader = context.RequestServices.GetService<IConfigEditorService>();
        if (reader is null) { context.Response.StatusCode = 503; return; }

        switch (context.Request.Method)
        {
            case "GET":
                await ServeConfigManifestGetAsync(context, reader, slug);
                break;
            case "PUT":
            {
                var writer = context.RequestServices.GetService<ConfigEditorService>();
                if (writer is null) { context.Response.StatusCode = 503; return; }
                await ServeConfigManifestPutAsync(context, writer, slug);
                break;
            }
            case "DELETE":
            {
                var writer = context.RequestServices.GetService<ConfigEditorService>();
                if (writer is null) { context.Response.StatusCode = 503; return; }
                await ServeConfigManifestDeleteAsync(context, writer, slug);
                break;
            }
            default:
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers["Allow"] = "GET, PUT, DELETE";
                break;
        }
    }

    private async Task ServeConfigManifestGetAsync(HttpContext context, IConfigEditorService editor, string slug)
    {
        var doc = await editor.GetManifestAsync(slug, context.RequestAborted);
        if (doc is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, doc, CamelCaseJson);
    }

    private async Task ServeConfigManifestPutAsync(HttpContext context, ConfigEditorService editor, string slug)
    {
        // Write operations require explicit write authorization - separate from dashboard read access
        if (!await IsWriteAuthorizedAsync(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":false,\"error\":\"Write access denied. Configure WriteAuthorizationFilter or RequireWriteAuthorizationPolicy.\"}");
            return;
        }

        // Body is either text/plain YAML or JSON {"yaml":"…"} - accept both because the
        // browser sends JSON via fetch() while curl users tend to send raw YAML.
        string yaml;
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            if ((context.Request.ContentType ?? "").Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("yaml", out var yamlEl) || yamlEl.ValueKind != JsonValueKind.String)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("{\"ok\":false,\"error\":\"missing 'yaml' string field\"}");
                    return;
                }
                yaml = yamlEl.GetString() ?? string.Empty;
            }
            else
            {
                yaml = body;
            }
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await JsonSerializer.SerializeAsync(context.Response.Body,
                new { ok = false, error = ex.Message }, CamelCaseJson);
            return;
        }

        var result = editor.SaveOverride(slug, yaml);
        context.Response.StatusCode = MapSaveOutcomeToStatus(result.Outcome);
        context.Response.ContentType = "application/json";

        if (result.Ok)
        {
            await JsonSerializer.SerializeAsync(context.Response.Body, new
            {
                ok = true,
                path = result.Path,
                writtenAtUtc = result.WrittenAtUtc
            }, CamelCaseJson);
        }
        else
        {
            await JsonSerializer.SerializeAsync(context.Response.Body, new
            {
                ok = false,
                outcome = result.Outcome.ToString(),
                error = result.Error,
                line = result.Line,
                column = result.Column
            }, CamelCaseJson);
        }
    }

    private async Task ServeConfigManifestDeleteAsync(HttpContext context, ConfigEditorService editor, string slug)
    {
        // Write operations require explicit write authorization
        if (!await IsWriteAuthorizedAsync(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":false,\"error\":\"Write access denied.\"}");
            return;
        }

        var outcome = editor.DeleteOverride(slug);
        context.Response.StatusCode = outcome switch
        {
            DeleteOutcome.Ok => StatusCodes.Status200OK,
            DeleteOutcome.NotFound => StatusCodes.Status404NotFound,
            DeleteOutcome.InvalidSlug or DeleteOutcome.PathEscape => StatusCodes.Status403Forbidden,
            DeleteOutcome.IoError => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status500InternalServerError
        };
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body,
            new { ok = outcome == DeleteOutcome.Ok, outcome = outcome.ToString() }, CamelCaseJson);
    }

    private static int MapSaveOutcomeToStatus(SaveOutcome outcome) => outcome switch
    {
        SaveOutcome.Ok => StatusCodes.Status200OK,
        SaveOutcome.YamlInvalid => StatusCodes.Status400BadRequest,
        SaveOutcome.UnknownDetector => StatusCodes.Status404NotFound,
        SaveOutcome.InvalidSlug or SaveOutcome.PathEscape => StatusCodes.Status403Forbidden,
        SaveOutcome.TooLarge => StatusCodes.Status413RequestEntityTooLarge,
        SaveOutcome.IoError => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError
    };

    /// <summary>
    ///     Hand-written JSON Schema covering the high-traffic 80% of manifest fields. Monaco's
    ///     YAML language service binds this via <c>monaco-yaml</c> and offers completion +
    ///     squiggly underlines. Unknown keys (Triggers/Emits/Listens/Escalation) are accepted
    ///     because <c>additionalProperties</c> defaults to true.
    /// </summary>
    private const string DetectorManifestJsonSchema = """
    {
      "$schema": "http://json-schema.org/draft-07/schema#",
      "$id": "https://stylobot.net/schema/detector-manifest.json",
      "title": "StyloBot Detector Manifest",
      "type": "object",
      "required": ["name"],
      "properties": {
        "name": { "type": "string", "description": "Detector class name (matches the C# IContributingDetector implementation)." },
        "priority": { "type": "integer", "description": "Lower runs earlier. Wave-0 detectors use ≤20.", "minimum": 0 },
        "enabled": { "type": "boolean", "description": "Disable a detector entirely without removing it." },
        "description": { "type": "string" },
        "scope": {
          "type": "object",
          "properties": {
            "sink": { "type": "string" },
            "coordinator": { "type": "string" },
            "atom": { "type": "string" }
          }
        },
        "taxonomy": {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["sensor", "extractor", "proposer", "constrainer", "ranker"] },
            "determinism": { "type": "string", "enum": ["deterministic", "probabilistic"] },
            "persistence": { "type": "string", "enum": ["ephemeral", "escalatable", "direct_write"] }
          }
        },
        "tags": { "type": "array", "items": { "type": "string" } },
        "defaults": {
          "type": "object",
          "description": "All tunable values live here - no magic numbers in C#.",
          "properties": {
            "weights": {
              "type": "object",
              "properties": {
                "base": { "type": "number" },
                "bot_signal": { "type": "number" },
                "human_signal": { "type": "number" },
                "verified": { "type": "number" },
                "early_exit": { "type": "number" }
              }
            },
            "confidence": {
              "type": "object",
              "properties": {
                "neutral": { "type": "number", "minimum": -1, "maximum": 1 },
                "bot_detected": { "type": "number", "minimum": -1, "maximum": 1 },
                "human_indicated": { "type": "number", "minimum": -1, "maximum": 1 },
                "strong_signal": { "type": "number", "minimum": -1, "maximum": 1 },
                "high_threshold": { "type": "number", "minimum": 0, "maximum": 1 },
                "low_threshold": { "type": "number", "minimum": 0, "maximum": 1 },
                "escalation_threshold": { "type": "number", "minimum": 0, "maximum": 1 }
              }
            },
            "timing": {
              "type": "object",
              "properties": {
                "timeout_ms": { "type": "integer", "minimum": 0 },
                "cache_refresh_sec": { "type": "integer", "minimum": 0 }
              }
            },
            "features": {
              "type": "object",
              "properties": {
                "detailed_logging": { "type": "boolean" },
                "enable_cache": { "type": "boolean" },
                "can_early_exit": { "type": "boolean" },
                "can_escalate": { "type": "boolean" }
              }
            },
            "parameters": {
              "type": "object",
              "description": "Detector-specific knobs. See each manifest for the exact set."
            }
          }
        }
      }
    }
    """;

    /// <summary>Render the countries list partial with server-side sort and pagination.</summary>
    private async Task ServeCountriesPartialAsync(HttpContext context)
    {
        var sortField = context.Request.Query["sort"].FirstOrDefault() ?? "total";
        var sortDir = context.Request.Query["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(context.Request.Query["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(context.Request.Query["pageSize"].FirstOrDefault(), out var ps) && ps is > 0 and <= 50 ? ps : 20;

        var model = BuildCountriesModel(sortField, sortDir, page, pageSize, await GetCountriesDataAsync());

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

        var endpoints = await GetEndpointsDataAsync(context);

        if (excludeStatic)
            endpoints = endpoints.Where(e => !IsStaticResource(e.Path)).ToList();

        var model = BuildEndpointsModel(sortField, sortDir, page, pageSize, endpoints);

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

        var endpoints = await GetEndpointsDataAsync(context);
        if (excludeStatic)
            endpoints = endpoints.Where(e => !IsStaticResource(e.Path)).ToList();

        var model = BuildEndpointsModel(sortField, sortDir, 1, pageSize, endpoints);

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

        var model = BuildTopBotsModel(page, pageSize, sortBy, sortDir, filter, widgetId);

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
                CachedRiskBand = fp.CachedRiskBand,
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
            CachedRiskBand = fp.CachedRiskBand,
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
        try { threats = await _eventStore.GetThreatsAsync(20); }
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

        var sessionStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.ISessionStore>();
        if (sessionStore == null || string.IsNullOrEmpty(sig))
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>Session store not available</div>");
            return;
        }

        var decodedSig = Uri.UnescapeDataString(sig);
        BotDetection.Data.PersistedSession? s;

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

        if (s == null)
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>Session not found</div>");
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
        var sessionStore = context.RequestServices.GetService<BotDetection.Data.ISessionStore>();

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
                    var ordered = detections.OrderBy(d => d.Timestamp).ToList();
                    var syntheticGroups = new List<List<DashboardDetectionEvent>>();
                    var currentGroup = new List<DashboardDetectionEvent> { ordered[0] };
                    for (var i = 1; i < ordered.Count; i++)
                    {
                        if ((ordered[i].Timestamp - ordered[i - 1].Timestamp).TotalMinutes > 30)
                        {
                            syntheticGroups.Add(currentGroup);
                            currentGroup = new List<DashboardDetectionEvent>();
                        }
                        currentGroup.Add(ordered[i]);
                    }
                    syntheticGroups.Add(currentGroup);

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

                        syntheticHtml.Append("<tr class=\"hover:bg-base-200/50\">");
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
            ? BotDetection.Data.SqliteSessionStore.DeserializeVector(s.Vector) : null).ToList();

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

            html.Append("<tr class=\"hover:bg-base-200/50\">");
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

        var sessionStore = context.RequestServices.GetService<BotDetection.Data.ISessionStore>();
        if (sessionStore == null)
        {
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>Session store unavailable</div>");
            return;
        }

        var limit = compact ? 10 : 20;
        var raw = await sessionStore.GetSessionsAsync(signature, limit);

        var entries = raw.Select(s =>
        {
            var vector = BotDetection.Data.SqliteSessionStore.DeserializeVector(s.Vector);
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
            var visitorCache = context.RequestServices.GetService<VisitorListCache>();
            if (visitorCache != null)
            {
                var (visitors, _, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, 100);
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

    private async Task ServeRecentActivityPartialAsync(HttpContext context)
    {
        var visitorCache = context.RequestServices.GetRequiredService<VisitorListCache>();
        var filter = context.Request.Query["filter"].FirstOrDefault() ?? "all";
        var sortField = context.Request.Query["sort"].FirstOrDefault() ?? "lastSeen";
        var sortDir = context.Request.Query["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(context.Request.Query["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(context.Request.Query["pageSize"].FirstOrDefault(), out var ps) && ps is > 0 and <= 50 ? ps : 10;

        // Map recent activity filters to visitor cache filter categories
        var cacheFilter = filter switch
        {
            "bots" => "bot",
            "humans" => "human",
            "threats" => "bot", // threats = bots with high threat scores, filtered below
            _ => "all"
        };

        var (items, totalCount, _, _) = visitorCache.GetFiltered(cacheFilter, sortField, sortDir, page, pageSize);

        // For "threats" filter, further narrow to high threat scores
        if (filter == "threats")
        {
            items = items.Where(v =>
                (!string.IsNullOrEmpty(v.ThreatBand) && v.ThreatBand is not ("None" or "Low"))
                || v.BotProbability > 0.8).ToList();
            totalCount = items.Count;
        }

        var counts = visitorCache.GetCounts();

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
            BasePath = _options.BasePath.TrimEnd('/')
        };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_RecentActivity.cshtml", model, context);
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
                "topbots" or "top-visitors" or "live-visitors" => await RenderPartialAsync(context, "/Views/Shared/Components/SbTopBots/Default.cshtml", BuildTopBotsModelFromQuery(widgetId, q)),
                "sessions" => await RenderPartialAsync(context, "/Views/Shared/Components/SbSessionsList/Default.cshtml", await BuildSessionsModel(context)),
                "recent" => await RenderRecentActivityPartialAsync(context),
                "your-detection" => await RenderPartialAsync(context, "/Views/StyloBot/Dashboard/_YourDetection.cshtml", BuildYourDetectionPartialModel(context)),
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
        var visitorCache = context.RequestServices.GetRequiredService<VisitorListCache>();
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sortField = q["sort"].FirstOrDefault() ?? "lastSeen";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var (items, totalCount, _, _) = visitorCache.GetFiltered(filter, sortField, sortDir, page, 24);
        var model = new VisitorListModel
        {
            Visitors = items, Counts = visitorCache.GetCounts(),
            Filter = filter, SortField = sortField, SortDir = sortDir,
            Page = page, PageSize = 24, TotalCount = totalCount,
            BasePath = _options.BasePath.TrimEnd('/')
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
        var model = BuildEndpointsModel(sortField, sortDir, page, 20, data);
        return await _razorViewRenderer.RenderViewToStringAsync("/Views/Shared/Components/SbEndpointsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderRecentActivityPartialAsync(HttpContext context)
    {
        var visitorCache = context.RequestServices.GetRequiredService<VisitorListCache>();
        var (items, totalCount, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, 10);
        var model = new VisitorListModel
        {
            Visitors = items, Counts = visitorCache.GetCounts(),
            Filter = "all", SortField = "lastSeen", SortDir = "desc",
            Page = 1, PageSize = 10, TotalCount = totalCount,
            BasePath = _options.BasePath.TrimEnd('/')
        };
        return await _razorViewRenderer.RenderViewToStringAsync("/Views/StyloBot/Dashboard/_RecentActivity.cshtml", model, context);
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
        var resolverStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.ISessionStore>();
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

        if (_signatureCache.TryGet(decodedSignature, out var agg) && agg != null)
        {
            List<double>? sparkline;
            lock (agg.SyncRoot)
            {
                sparkline = agg.ScoreHistory.Count > 0 ? agg.ScoreHistory.ToList() : null;
            }

            // Enrich from CachedVisitor (paths, histories, UA, protocol)
            var visitorCache = context.RequestServices.GetRequiredService<VisitorListCache>();
            var visitor = visitorCache.Get(decodedSignature);
            List<string> paths = [];
            string? userAgent = null;
            string? protocol = null;
            DateTime firstSeen = default;
            List<double> botProbHistory = [];
            List<double> confHistory = [];
            List<double> procTimeHistory = [];

            if (visitor != null)
            {
                lock (visitor.SyncRoot)
                {
                    paths = visitor.Paths.ToList();
                    userAgent = visitor.UserAgent;
                    protocol = visitor.Protocol;
                    firstSeen = visitor.FirstSeen;
                    botProbHistory = visitor.BotProbabilityHistory.ToList();
                    confHistory = visitor.ConfidenceHistory.ToList();
                    procTimeHistory = visitor.ProcessingTimeHistory.ToList();
                }
            }

            // Enrich from DB detections (recent per-request records)
            List<SignatureDetectionRow> recentDetections = [];
            List<SignatureDetectorEntry> detectorContributions = [];
            Dictionary<string, Dictionary<string, string>> signalCategories = new();

            try
            {
                var detections = await _eventStore.GetDetectionsAsync(new DashboardFilter
                {
                    SignatureId = decodedSignature,
                    Limit = 50
                });

                recentDetections = detections.Select(d => new SignatureDetectionRow
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

                // Extract detector contributions from most recent detection
                var latest = detections.FirstOrDefault();
                if (latest?.DetectorContributions != null)
                {
                    detectorContributions = latest.DetectorContributions
                        .Select(kv => new SignatureDetectorEntry
                        {
                            Name = kv.Key,
                            ConfidenceDelta = kv.Value.ConfidenceDelta,
                            Contribution = kv.Value.Contribution,
                            Reason = kv.Value.Reason,
                            ExecutionTimeMs = kv.Value.ExecutionTimeMs
                        })
                        .OrderByDescending(e => Math.Abs(e.Contribution))
                        .ToList();
                }

                // Group ImportantSignals by key prefix into categories
                if (latest?.ImportantSignals != null)
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
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to enrich signature detail from DB for {Signature}", decodedSignature);
            }

            model = new SignatureDetailModel
            {
                SignatureId = decodedSignature,
                BasePath = basePath,
                NavBasePath = navBasePath,
                CspNonce = cspNonce,
                HubPath = _options.HubPath,
                Found = true,
                BotName = agg.BotName,
                BotType = agg.BotType,
                RiskBand = agg.RiskBand,
                BotProbability = agg.BotProbability,
                Confidence = agg.Confidence,
                HitCount = agg.HitCount,
                Action = agg.Action,
                CountryCode = agg.CountryCode,
                ProcessingTimeMs = agg.ProcessingTimeMs,
                TopReasons = agg.TopReasons,
                LastSeen = agg.LastSeen,
                Narrative = agg.Narrative,
                Description = agg.Description,
                IsBot = agg.IsBot,
                ThreatScore = agg.ThreatScore,
                ThreatBand = agg.ThreatBand,
                RiskJustification = agg.RiskJustification,
                SparklineData = sparkline,
                Paths = paths,
                UserAgent = userAgent,
                Protocol = protocol,
                FirstSeen = firstSeen,
                BotProbabilityHistory = botProbHistory,
                ConfidenceHistory = confHistory,
                ProcessingTimeHistory = procTimeHistory,
                RecentDetections = recentDetections,
                DetectorContributions = detectorContributions,
                SignalCategories = signalCategories,
                TunerEnabled = _options.EnableTuner,
                PeriodicityHeatmap = heatmap
            };
        }
        else
        {
            // Cache miss - reconstruct from persistent event store.
            // This handles signatures that have been evicted from SignatureAggregateCache
            // but still exist in the database (common when recent activity shows old signatures).
            try
            {
                var detections = await _eventStore.GetDetectionsAsync(new DashboardFilter
                {
                    SignatureId = decodedSignature,
                    Limit = 50
                });

                if (detections.Count > 0)
                {
                    var latest = detections[0]; // Most recent detection
                    var recentDetections = detections.Select(d => new SignatureDetectionRow
                    {
                        Timestamp = d.Timestamp,
                        IsBot = d.IsBot,
                        BotProbability = d.BotProbability,
                        Confidence = d.Confidence,
                        RiskBand = d.RiskBand,
                        Action = d.Action,
                        Method = d.Method,
                        Path = d.Path,
                        StatusCode = d.StatusCode,
                        ProcessingTimeMs = d.ProcessingTimeMs
                    }).ToList();

                    // Extract detector contributions from the latest detection
                    var latestContributions = latest.DetectorContributions?
                        .Select(dc => new SignatureDetectorEntry
                        {
                            Name = dc.Key,
                            ConfidenceDelta = dc.Value.ConfidenceDelta,
                            Contribution = dc.Value.Contribution,
                            Reason = dc.Value.Reason,
                            ExecutionTimeMs = dc.Value.ExecutionTimeMs
                        }).ToList() ?? [];

                    // Build signal categories from latest detection
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

                    model = new SignatureDetailModel
                    {
                        SignatureId = decodedSignature,
                        BasePath = basePath,
                        CspNonce = cspNonce,
                        HubPath = _options.HubPath,
                        Found = true,
                        BotName = latest.BotName,
                        BotType = latest.BotType,
                        RiskBand = latest.RiskBand,
                        BotProbability = latest.BotProbability,
                        Confidence = latest.Confidence,
                        HitCount = detections.Count,
                        Action = latest.Action,
                        CountryCode = latest.CountryCode,
                        ProcessingTimeMs = latest.ProcessingTimeMs,
                        TopReasons = latest.TopReasons?.ToList() ?? [],
                        LastSeen = latest.Timestamp,
                        Narrative = latest.Narrative,
                        Description = latest.Description,
                        IsBot = latest.IsBot,
                        ThreatScore = latest.ThreatScore,
                        ThreatBand = latest.ThreatBand,
                        RiskJustification = latest.RiskJustification,
                        SparklineData = detections.Select(d => d.BotProbability).ToList(),
                        Paths = detections.Where(d => d.Path != null).Select(d => d.Path!).Distinct().ToList(),
                        UserAgent = null, // Not available from event store (PII-hashed)
                        Protocol = null,
                        FirstSeen = detections.Count > 0 ? detections[^1].Timestamp : default,
                        BotProbabilityHistory = detections.Select(d => d.BotProbability).ToList(),
                        ConfidenceHistory = detections.Select(d => d.Confidence).ToList(),
                        ProcessingTimeHistory = detections.Select(d => (double)d.ProcessingTimeMs).ToList(),
                        RecentDetections = recentDetections,
                        DetectorContributions = latestContributions,
                        SignalCategories = signalCategories,
                        TunerEnabled = _options.EnableTuner,
                PeriodicityHeatmap = heatmap
                    };
                }
                else
                {
                    await WriteSignatureNotFoundAsync(context, decodedSignature, basePath);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to reconstruct signature from event store for {Signature}", decodedSignature);
                await WriteSignatureNotFoundAsync(context, decodedSignature, basePath);
                return;
            }
        }

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_SignatureDetail.cshtml", model, context);
        await context.Response.WriteAsync(html);
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

        var model = detail == null
            ? new EndpointDetailModel
            {
                Method = method,
                Path = path,
                BasePath = _options.BasePath.TrimEnd('/'),
                Found = false,
                CspNonce = epNonce,
                PolicyName = policyName,
                PackCoverage = packCoverage,
                IsPinned = pin != null,
                IsHoneypot = pin?.IsHoneypot ?? false,
                PinId = pin?.Id
            }
            : new EndpointDetailModel
            {
                Method = detail.Method,
                Path = detail.Path,
                BasePath = _options.BasePath.TrimEnd('/'),
                Found = true,
                TotalCount = detail.TotalCount,
                BotCount = detail.BotCount,
                HumanCount = detail.HumanCount,
                BotRate = detail.BotRate,
                UniqueSignatures = detail.UniqueSignatures,
                AvgProcessingTimeMs = detail.AvgProcessingTimeMs,
                AvgThreatScore = detail.AvgThreatScore,
                TopActions = detail.TopActions,
                TopCountries = detail.TopCountries,
                RiskBands = detail.RiskBands,
                TopBots = detail.TopBots,
                RecentDetections = detail.RecentDetections,
                BotProfile = detail.BotProfile,
                HumanProfile = detail.HumanProfile,
                OverallProfile = detail.OverallProfile,
                CspNonce = epNonce,
                PolicyName = policyName,
                PackCoverage = packCoverage,
                IsPinned = pin != null,
                IsHoneypot = pin?.IsHoneypot ?? false,
                PinId = pin?.Id
            };

        context.Response.ContentType = "text/html";
        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_EndpointDetail.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }

    // ─── Shared model builders ───────────────────────────────────────────

    private YourDetectionModel BuildYourDetectionPartialModel(HttpContext context)
    {
        try
        {
            var sigService = context.RequestServices.GetService(typeof(MultiFactorSignatureService))
                as MultiFactorSignatureService;
            var visitorCache = context.RequestServices.GetService(typeof(VisitorListCache))
                as VisitorListCache;

            if (sigService == null || visitorCache == null)
                return new YourDetectionModel { HasData = false, BasePath = _options.BasePath.TrimEnd('/') };

            MultiFactorSignatures? sigs = null;
            if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
                && evObj is AggregatedEvidence ev
                && ev.Signals.TryGetValue(SignalKeys.SignatureMultifactor, out var multiObj)
                && multiObj is MultiFactorSignatures m)
            {
                sigs = m;
            }
            sigs ??= sigService.GenerateSignatures(context);
            var visitor = visitorCache.Get(sigs.PrimarySignature);

            if (visitor == null)
                return new YourDetectionModel
                {
                    HasData = false, Signature = sigs.PrimarySignature,
                    BasePath = _options.BasePath.TrimEnd('/')
                };

            return new YourDetectionModel
            {
                HasData = true,
                IsBot = visitor.IsBot,
                BotProbability = visitor.BotProbability,
                Confidence = visitor.Confidence,
                RiskBand = visitor.RiskBand,
                ProcessingTimeMs = visitor.ProcessingTimeMs,
                DetectorCount = visitor.TopReasons.Count,
                Narrative = visitor.Narrative,
                TopReasons = visitor.TopReasons,
                Signature = sigs.PrimarySignature,
                ThreatScore = visitor.ThreatScore,
                ThreatBand = visitor.ThreatBand,
                BasePath = _options.BasePath.TrimEnd('/')
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to build your detection partial model");
            return new YourDetectionModel { HasData = false, BasePath = _options.BasePath.TrimEnd('/') };
        }
    }

    private TopBotsListModel BuildTopBotsModel(int page = 1, int pageSize = 10, string sortBy = "default", string sortDir = "desc", string filter = "bots", string widgetId = "topbots")
    {
        // Get all matching entries for accurate count, then take the page
        var allBots = _signatureCache.GetTopBots(page: 1, pageSize: _signatureCache.MaxEntries, sortBy: sortBy, sortDir: sortDir, filter: filter);
        var pagedBots = allBots.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new TopBotsListModel
        {
            Bots = pagedBots,
            Page = page,
            PageSize = pageSize,
            TotalCount = allBots.Count,
            SortField = sortBy,
            SortDir = sortDir,
            BasePath = _options.BasePath.TrimEnd('/'),
            Filter = filter,
            WidgetId = widgetId
        };
    }

    private TopBotsListModel BuildTopBotsModelFromQuery(string widgetId, IQueryCollection q)
    {
        var sortBy = q["sort"].FirstOrDefault() ?? "default";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 10, 50);
        var filter = q["filter"].FirstOrDefault() ?? "bots";
        var routeWidgetId = q["widgetId"].FirstOrDefault() ?? widgetId;
        return BuildTopBotsModel(page, pageSize, sortBy, sortDir, filter, routeWidgetId);
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
        // IReactionPackContext is an optional commercial/future capability.
        // If not registered, return empty list rather than throwing.
        var packContextType = Type.GetType(
            "Mostlylucid.BotDetection.Services.IReactionPackContext, Mostlylucid.BotDetection");
        if (packContextType == null) return [];

        var packContext = context.RequestServices.GetService(packContextType);
        if (packContext == null) return [];

        var coverage = new List<EndpointPackCoverage>();

        // Use reflection to call GetActiveStates() generically so this compiles
        // without a hard reference to the optional interface.
        var getActiveStates = packContextType.GetMethod("GetActiveStates");
        if (getActiveStates == null) return [];

        var activeStates = getActiveStates.Invoke(packContext, null);
        if (activeStates is not System.Collections.IEnumerable states) return [];

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            var stateType = state.GetType();
            var packName = stateType.GetProperty("PackName")?.GetValue(state) as string ?? "";
            var scope = stateType.GetProperty("Scope")?.GetValue(state) as string ?? "global";
            var level = stateType.GetProperty("Level")?.GetValue(state) as int? ?? 0;
            var policyName = stateType.GetProperty("PolicyName")?.GetValue(state) as string;
            if (scope == "global" || scope == path)
            {
                coverage.Add(new EndpointPackCoverage(packName, scope, level, policyName));
                seenNames.Add(packName);
            }
        }

        return coverage;
    }

    private EndpointsListModel BuildEndpointsModel(string sortField, string sortDir, int page, int pageSize, List<DashboardEndpointStats> all)
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
            TotalCount = all.Count
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
        var sessionStore = context.RequestServices.GetService<Mostlylucid.BotDetection.Data.ISessionStore>();
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
        var sessions = await sessionStore.GetRecentSessionsAsync(pageSize, isBot);

        var entries = sessions.Select(s => new SessionListEntry
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
            BotName = s.BotName,
            CountryCode = s.CountryCode,
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
            TotalCount = entries.Count,
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
        "browsing" => "Automated Browsing",
        "unclassified" => "Unclassified Bots",
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

    public static readonly IReadOnlyList<FilterGroup> ShapeFilterGroups = new List<FilterGroup>
    {
        new()
        {
            Id = "fingerprint", Label = "Fingerprint Signals",
            Dimensions = new List<FilterDimension>
            {
                new() { Name = "client_fingerprint", Label = "TLS/HTTP Fingerprint", AxisIndex = 7, InputType = "slider" },
                new() { Name = "ip_reputation", Label = "Datacenter", AxisIndex = 2, InputType = "toggle", Threshold = 0.7 },
                new() { Name = "inconsistency", Label = "Inconsistency", AxisIndex = 9, InputType = "slider" },
            }
        },
        new()
        {
            Id = "traffic", Label = "Traffic Pattern",
            Dimensions = new List<FilterDimension>
            {
                new() { Name = "rate_pattern", Label = "Request Rate", AxisIndex = 14, InputType = "slider" },
                new() { Name = "advanced_behavioral", Label = "Timing Regularity", AxisIndex = 4, InputType = "slider" },
                new() { Name = "behavioral", Label = "Navigation Pattern", AxisIndex = 3, InputType = "slider" },
                new() { Name = "cache_behavior", Label = "Asset Loading", AxisIndex = 5, InputType = "slider" },
            }
        },
        new()
        {
            Id = "detection", Label = "Detection Signals",
            Dimensions = new List<FilterDimension>
            {
                new() { Name = "ua_anomaly", Label = "UA Anomaly", AxisIndex = 0, InputType = "slider" },
                new() { Name = "header_anomaly", Label = "Header Anomaly", AxisIndex = 1, InputType = "slider" },
                new() { Name = "security_tool", Label = "Security Tool", AxisIndex = 6, InputType = "toggle", Threshold = 0.5 },
                new() { Name = "ai_classification", Label = "AI Classification", AxisIndex = 11, InputType = "slider" },
                new() { Name = "cluster_signal", Label = "Cluster Signal", AxisIndex = 12, InputType = "slider" },
                new() { Name = "reputation_match", Label = "Reputation Match", AxisIndex = 10, InputType = "slider" },
            }
        }
    };

    private async Task ServeInvestigationAsync(HttpContext context)
    {
        var shapeFilter = ParseShapeSearchFilter(context);

        InvestigationResult result;

        // If shape dimensions are set and we have pgvector, use HNSW
        var shapeStore = context.RequestServices.GetService<IShapeSearchStore>();
        if (shapeFilter.TargetShape is not null && shapeStore is not null)
        {
            result = await shapeStore.SearchByShapeAsync(shapeFilter);
        }
        else
        {
            // Fall back to spec 1 SQL-based investigation
            var investigationFilter = new InvestigationFilter
            {
                EntityType = context.Request.Query["type"].FirstOrDefault() ?? "signature",
                EntityValue = context.Request.Query["value"].FirstOrDefault() ?? "",
                Start = shapeFilter.Start,
                End = shapeFilter.End,
                Tab = shapeFilter.Tab,
                Offset = shapeFilter.Offset
            };
            result = await _eventStore.GetInvestigationAsync(investigationFilter);
        }

        // Enrich with FOSS ghost centroid matches and FrequencyCentroid.
        // Ghost shapes in FOSS = L1/L2 HNSW entries (crystallized campaign centroids).
        // VoidPressure stays 0 for FOSS (requires pgvector radar HNSW in commercial).
        result = await EnrichWithGhostMatchesAsync(context, result);

        // Load presets
        var presets = shapeStore is not null
            ? await shapeStore.GetPresetsAsync()
            : Array.Empty<InvestigationPreset>();

        var hasCommercial = IsCommercialMode(context);
        var tabs = new List<string> { "detections", "signatures", "endpoints", "geo", "signaltrace" };
        if (hasCommercial) tabs.Insert(tabs.Count - 1, "fingerprints");

        var vm = new ShapeInvestigationViewModel
        {
            Filter = shapeFilter,
            Result = result,
            BasePath = _options.BasePath,
            FilterGroups = ShapeFilterGroups,
            Presets = presets.ToList(),
            AvailableTabs = tabs,
            HasShapeSearch = shapeStore is not null,
            IsPaid = hasCommercial
        };

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_Investigate.cshtml", vm, context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    private async Task ServeLoadPresetAsync(HttpContext context)
    {
        var presetId = context.Request.Query["preset"].FirstOrDefault();
        if (string.IsNullOrEmpty(presetId) || !Guid.TryParse(presetId, out _))
        {
            // No preset selected -- re-render with empty filter
            await ServeInvestigationAsync(context);
            return;
        }

        var shapeStore = context.RequestServices.GetService<IShapeSearchStore>();
        if (shapeStore is null)
        {
            await ServeInvestigationAsync(context);
            return;
        }

        var presets = await shapeStore.GetPresetsAsync();
        var preset = presets.FirstOrDefault(p => p.Id.ToString() == presetId);
        if (preset is null)
        {
            await ServeInvestigationAsync(context);
            return;
        }

        // Build query string from preset and redirect back to investigate
        var qs = new List<string>();
        for (var i = 0; i < preset.TargetShape.Length; i++)
        {
            if (preset.TargetShape[i] > 0.01f)
                qs.Add($"dim_{i}={preset.TargetShape[i]:F2}");
        }
        qs.Add($"fuzz={preset.FuzzThreshold:F2}");
        var range = context.Request.Query["range"].FirstOrDefault() ?? "24h";
        qs.Add($"range={range}");

        context.Response.Headers["HX-Redirect"] = $"{_options.BasePath}/investigate?{string.Join("&", qs)}";
        context.Response.StatusCode = 200;
    }

    private async Task ServeInvestigationTabAsync(HttpContext context, string tab)
    {
        var filter = ParseInvestigationFilter(context) with { Tab = tab };
        var result = await _eventStore.GetInvestigationAsync(filter);
        result = await EnrichWithGhostMatchesAsync(context, result);
        var vm = BuildInvestigationViewModel(filter, result, context);

        var partialName = tab.ToLowerInvariant() switch
        {
            "detections" => "_InvestigateDetections",
            "signatures" => "_InvestigateSignatures",
            "endpoints" => "_InvestigateEndpoints",
            "geo" => "_InvestigateGeo",
            "fingerprints" => "_InvestigateFingerprints",
            "signaltrace" => "_InvestigateSignaltrace",
            _ => "_InvestigateDetections"
        };

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            $"/Views/StyloBot/Dashboard/{partialName}.cshtml", vm, context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    /// Enrich an InvestigationResult with FOSS ghost centroid matches and per-signature FrequencyCentroid.
    /// Ghost shapes are L1/L2 HNSW entries already in memory from VectorCompactionService.
    /// FrequencyCentroid is the mean frequency fingerprint from the signature's compacted sessions.
    /// VoidPressure is left at 0 for FOSS (requires pgvector radar-HNSW in commercial).
    /// </summary>
    private static async Task<InvestigationResult> EnrichWithGhostMatchesAsync(
        HttpContext context, InvestigationResult result)
    {
        // Skip enrichment if the result was already populated by a commercial store
        if (result.GhostMatches.Count > 0) return result;

        var vectorSearch = context.RequestServices.GetService<ISessionVectorSearch>();
        if (vectorSearch == null || result.Signatures.Count == 0) return result;

        var sessionStore = context.RequestServices.GetService<ISessionStore>();
        var ghostHits = new List<GhostCampaignHit>();
        var enrichedSignatures = new List<SignatureSummary>(result.Signatures.Count);

        // Build a per-signature HNSW metadata lookup (L1 entries keyed by signature)
        var snapshot = vectorSearch.GetAllVectorsSnapshot();
        var l1BySignature = snapshot
            .Where(e => e.Metadata.CompressionLevel == 1)
            .GroupBy(e => e.Metadata.Signature)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var sig in result.Signatures.Take(10)) // cap to avoid slow scan
        {
            // FrequencyCentroid: prefer HNSW L1 metadata (already in memory, no I/O)
            float[]? freqCentroid = null;
            float[]? queryVec = null;

            if (l1BySignature.TryGetValue(sig.PrimarySignature, out var l1Entry))
            {
                freqCentroid = l1Entry.Metadata.FrequencyFingerprint;
                queryVec = l1Entry.Vector;
            }
            else if (sessionStore != null)
            {
                // Fallback: load most recent session vector from SQLite
                var sessions = await sessionStore.GetSessionsAsync(sig.PrimarySignature, 1);
                if (sessions.Count > 0)
                    queryVec = SqliteSessionStore.DeserializeVector(sessions[0].Vector);
            }

            enrichedSignatures.Add(sig with { FrequencyCentroid = freqCentroid });

            if (queryVec == null) continue;

            // Ghost matches: find L1/L2 centroids similar to this signature's vector
            var ghosts = await vectorSearch.FindGhostCentroidsAsync(queryVec, topK: 3, minSimilarity: 0.75f);
            foreach (var g in ghosts)
            {
                // Don't match the signature to itself
                if (string.Equals(g.FamilyId, sig.PrimarySignature, StringComparison.Ordinal)) continue;
                ghostHits.Add(new GhostCampaignHit
                {
                    FamilyId = g.FamilyId,
                    Label = null,  // FOSS has no analyst labels
                    Similarity = g.Similarity,
                    MatchedSignature = sig.PrimarySignature,
                    RiskBand = g.BotProbability > 0.7 ? "High" : "Medium",
                    SignatureCount = 1,  // FOSS doesn't track merged count
                    LastSeen = sig.LastSeen
                });
            }
        }

        // Deduplicate: keep highest-similarity hit per FamilyId
        var dedupedGhosts = ghostHits
            .GroupBy(h => h.FamilyId)
            .Select(g => g.OrderByDescending(h => h.Similarity).First())
            .OrderByDescending(h => h.Similarity)
            .ToList();

        return result with
        {
            Signatures = enrichedSignatures,
            GhostMatches = dedupedGhosts
        };
    }

    private static DateTime ParseRangeStart(string range)
    {
        var now = DateTime.UtcNow;
        return range switch
        {
            "1h" => now.AddHours(-1),
            "6h" => now.AddHours(-6),
            "24h" => now.AddHours(-24),
            "7d" => now.AddDays(-7),
            "30d" => now.AddDays(-30),
            _ => now.AddHours(-24)
        };
    }

    private InvestigationFilter ParseInvestigationFilter(HttpContext context)
    {
        var query = context.Request.Query;
        var entityType = query["type"].FirstOrDefault() ?? "signature";
        var entityValue = query["value"].FirstOrDefault() ?? "";
        var tab = query["tab"].FirstOrDefault();
        var range = query["range"].FirstOrDefault() ?? "24h";
        var offset = int.TryParse(query["offset"].FirstOrDefault(), out var o) ? o : 0;

        var start = ParseRangeStart(range);

        if (DateTime.TryParse(query["start"].FirstOrDefault(), out var customStart))
            start = customStart;

        DateTime? end = null;
        if (DateTime.TryParse(query["end"].FirstOrDefault(), out var customEnd))
            end = customEnd;

        return new InvestigationFilter
        {
            EntityType = entityType,
            EntityValue = entityValue,
            Start = start,
            End = end,
            Tab = tab,
            Offset = offset
        };
    }

    private ShapeSearchFilter ParseShapeSearchFilter(HttpContext context)
    {
        var query = context.Request.Query;
        var start = ParseRangeStart(query["range"].FirstOrDefault() ?? "24h");

        // Parse dimension values from dim_0 through dim_15
        var shape = new float[RadarDimensions.Count];
        var weights = new float[RadarDimensions.Count];
        var hasAnyShape = false;

        for (var i = 0; i < RadarDimensions.Count; i++)
        {
            if (float.TryParse(query[$"dim_{i}"].FirstOrDefault(), out var dimVal))
            {
                shape[i] = Math.Max(0f, Math.Min(1f, dimVal));
                hasAnyShape = true;
            }
            weights[i] = float.TryParse(query[$"weight_{i}"].FirstOrDefault(), out var w) ? w : (shape[i] > 0.05f ? 1f : 0f);
        }

        var fuzz = double.TryParse(query["fuzz"].FirstOrDefault(), out var f) ? f : 0.2;

        return new ShapeSearchFilter
        {
            Start = start,
            EndpointPath = query["endpointPath"].FirstOrDefault(),
            HttpMethod = query["httpMethod"].FirstOrDefault(),
            UserAgent = query["userAgent"].FirstOrDefault(),
            Country = query["country"].FirstOrDefault(),
            BotName = query["botName"].FirstOrDefault(),
            IpHmac = query["ipHmac"].FirstOrDefault(),
            TargetShape = hasAnyShape ? shape : null,
            DimensionWeights = weights,
            FuzzThreshold = fuzz,
            Tab = query["tab"].FirstOrDefault(),
            Offset = int.TryParse(query["offset"].FirstOrDefault(), out var o) ? o : 0
        };
    }

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

    private InvestigationViewModel BuildInvestigationViewModel(
        InvestigationFilter filter, InvestigationResult result, HttpContext? httpContext = null)
    {
        var filters = new List<FilterOption>
        {
            new() { Value = "signature", Label = "Signature" },
            new() { Value = "country", Label = "Country" },
            new() { Value = "path", Label = "Path" },
            new() { Value = "ua_family", Label = "UA Family" }
        };

        var tabs = new List<string> { "detections", "signatures", "endpoints", "geo", "signaltrace" };

        // Commercial features -- gated by license + demo mode toggle
        if (httpContext is not null ? IsCommercialMode(httpContext) : _options.EnableConfigEditing)
        {
            filters.Add(new FilterOption { Value = "ip", Label = "IP Address" });
            filters.Add(new FilterOption { Value = "fingerprint", Label = "Fingerprint" });
            tabs.Insert(tabs.Count - 1, "fingerprints");
        }

        return new InvestigationViewModel
        {
            Filter = filter,
            Result = result,
            BasePath = _options.BasePath,
            AvailableFilters = filters,
            AvailableTabs = tabs
        };
    }
}
