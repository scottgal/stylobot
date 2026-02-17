using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Middleware for handling Stylobot Dashboard routes.
///     Serves the dashboard UI and API endpoints.
/// </summary>
public class StyloBotDashboardMiddleware
{
    private readonly IDashboardEventStore _eventStore;
    private readonly ILogger<StyloBotDashboardMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly StyloBotDashboardOptions _options;
    private readonly RazorViewRenderer _razorViewRenderer;

    // Rate limiter: per IP, per minute
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _rateLimits = new();
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _apiRateLimits = new();
    private static volatile bool _authWarningLogged;
    private const int DiagnosticsRateLimit = 10;
    private const int ApiRateLimit = 60; // general API endpoints
    private const int MaxRateLimitEntries = 10_000; // hard cap to prevent memory exhaustion
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);
    private static int _cleanupRunning;

    public StyloBotDashboardMiddleware(
        RequestDelegate next,
        StyloBotDashboardOptions options,
        IDashboardEventStore eventStore,
        RazorViewRenderer razorViewRenderer,
        ILogger<StyloBotDashboardMiddleware> logger)
    {
        _next = next;
        _options = options;
        _eventStore = eventStore;
        _razorViewRenderer = razorViewRenderer;
        _logger = logger;
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

        // Check authorization
        if (!await IsAuthorizedAsync(context))
        {
            _logger.LogWarning("Dashboard access denied for {IP} on {Path}",
                context.Connection.RemoteIpAddress, context.Request.Path);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Forbidden: Dashboard access denied");
            return;
        }

        // Route the request
        var relativePath = path.Substring(_options.BasePath.Length).TrimStart('/');

        // Rate limit all API endpoints (diagnostics has its own stricter limit)
        if (relativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            && !relativePath.Equals("api/diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var (allowed, remaining) = CheckRateLimit(clientIp, DateTime.UtcNow, _apiRateLimits, ApiRateLimit);
            context.Response.Headers["X-RateLimit-Limit"] = ApiRateLimit.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();

            if (!allowed)
            {
                context.Response.StatusCode = 429;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"Rate limit exceeded\"}");
                return;
            }
        }

        switch (relativePath.ToLowerInvariant())
        {
            case "":
            case "index":
            case "index.html":
                await ServeDashboardPageAsync(context);
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

            case "api/clusters":
                await ServeClustersApiAsync(context);
                break;

            case "api/useragents":
                await ServeUserAgentsApiAsync(context);
                break;

            case "api/topbots":
                await ServeTopBotsApiAsync(context);
                break;

            case "api/me":
                await ServeMeApiAsync(context);
                break;

            case var p when p.StartsWith("api/sparkline/", StringComparison.OrdinalIgnoreCase):
                await ServeSparklineApiAsync(context, p.Substring("api/sparkline/".Length));
                break;

            default:
                // Static assets are served by static files middleware
                await _next(context);
                break;
        }
    }

    private async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
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
                    null, // No resource
                    _options.RequireAuthorizationPolicy);

                return result.Succeeded;
            }
        }

        // No auth configured — log warning on first request so operators notice
        if (!_authWarningLogged)
        {
            _authWarningLogged = true;
            _logger.LogWarning(
                "Dashboard has no authorization configured (AuthorizationFilter and RequireAuthorizationPolicy are both null). " +
                "In production, configure authentication via AddStyloBotDashboard(options => options.AuthorizationFilter = ...) " +
                "or options.RequireAuthorizationPolicy = \"PolicyName\"");
        }

        return true;
    }

    private async Task ServeDashboardPageAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html";

        // Allow same-origin iframing (e.g., LiveDemo page embedding the dashboard)
        context.Response.Headers.Remove("X-Frame-Options");
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        // Replace restrictive CSP with dashboard-appropriate one
        context.Response.Headers.Remove("Content-Security-Policy");
        // Use existing nonce from upstream CSP middleware, or generate one if not present
        // (e.g. when the host app skips its CSP for /_stylobot paths).
        var cspNonce = context.Items.TryGetValue("CspNonce", out var nonceObj) && nonceObj is string s && s.Length > 0
            ? s
            : Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        context.Items["CspNonce"] = cspNonce;
        var dashboardCsp = string.Join("; ",
            "default-src 'self'",
            "base-uri 'self'",
            "frame-ancestors 'self'",
            "object-src 'none'",
            "img-src 'self' data: https:",
            "font-src 'self' data: https://fonts.gstatic.com https://unpkg.com",
            $"style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://unpkg.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com",
            $"script-src 'self' 'nonce-{cspNonce}' 'unsafe-eval' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://unpkg.com https://cdn.tailwindcss.com",
            "connect-src 'self' ws: wss:");
        context.Response.Headers["Content-Security-Policy"] = dashboardCsp;

        // Look up the current visitor's cached detection via signature service.
        // The dashboard path is excluded from bot detection, so we compute the signature
        // and look up the cached result from the visitor's last non-dashboard request.
        var yourDetectionJson = BuildYourDetectionJson(context);

        // Gather all dashboard data server-side so the page renders fully on first load.
        // SignalR then provides live updates — no XHR waterfall needed.
        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var summaryJson = "null";
        var detectionsJson = "[]";
        var signaturesJson = "[]";
        var countriesJson = "[]";
        var clustersJson = "[]";

        try
        {
            var summary = await _eventStore.GetSummaryAsync();
            summaryJson = JsonSerializer.Serialize(summary, jsonOpts);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Dashboard: failed to load summary"); }

        try
        {
            var filter = new DashboardFilter { Limit = 200 };
            var detections = await _eventStore.GetDetectionsAsync(filter);
            detectionsJson = JsonSerializer.Serialize(detections, jsonOpts);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Dashboard: failed to load detections"); }

        try
        {
            var signatures = await _eventStore.GetSignaturesAsync(50);
            signaturesJson = JsonSerializer.Serialize(signatures, jsonOpts);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Dashboard: failed to load signatures"); }

        try
        {
            var tracker = context.RequestServices.GetService(typeof(CountryReputationTracker))
                as CountryReputationTracker;
            if (tracker != null)
            {
                var countries = tracker.GetTopBotCountries(10)
                    .Select(cr => new
                    {
                        countryCode = cr.CountryCode,
                        countryName = cr.CountryName,
                        botRate = Math.Round(cr.BotRate, 3),
                        botCount = (int)Math.Round(cr.DecayedBotCount),
                        totalCount = (int)Math.Round(cr.DecayedTotalCount),
                        flag = GetCountryFlag(cr.CountryCode)
                    })
                    .ToList();
                countriesJson = JsonSerializer.Serialize(countries, jsonOpts);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Dashboard: failed to load countries"); }

        try
        {
            var clusterService = context.RequestServices.GetService(typeof(BotClusterService))
                as BotClusterService;
            if (clusterService != null)
            {
                var clusters = clusterService.GetClusters()
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
                        temporalDensity = Math.Round(cl.TemporalDensity, 3)
                    })
                    .ToList();
                clustersJson = JsonSerializer.Serialize(clusters, jsonOpts);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Dashboard: failed to load clusters"); }

        var model = new DashboardViewModel
        {
            Options = _options,
            CspNonce = cspNonce,
            YourDetectionJson = yourDetectionJson,
            SummaryJson = summaryJson,
            DetectionsJson = detectionsJson,
            SignaturesJson = signaturesJson,
            CountriesJson = countriesJson,
            ClustersJson = clustersJson
        };

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Dashboard/Index.cshtml", model, context);
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

            // Use pre-computed signature from BotDetectionMiddleware if available
            var sigs = context.Items["BotDetection.Signatures"] as MultiFactorSignatures
                       ?? sigService.GenerateSignatures(context);
            var visitor = visitorCache.Get(sigs.PrimarySignature);

            if (visitor == null)
                return "null";

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
                signature = sigs.PrimarySignature
            };

            return JsonSerializer.Serialize(yourDetection,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
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

    private async Task ServeDetectionsApiAsync(HttpContext context)
    {
        var filter = ParseFilter(context.Request.Query);
        var detections = await _eventStore.GetDetectionsAsync(filter);

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, detections);
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
        await JsonSerializer.SerializeAsync(context.Response.Body, signatures);
    }

    private async Task ServeSummaryApiAsync(HttpContext context)
    {
        var summary = await _eventStore.GetSummaryAsync();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, summary);
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
            await JsonSerializer.SerializeAsync(context.Response.Body, timeSeries);
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
            await JsonSerializer.SerializeAsync(context.Response.Body, detections);
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
                topReasons = b.TopReasons
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
                d.ImportantSignals
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
                s.BotName
            })
        };

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, diagnostics,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private async Task ServeCountriesApiAsync(HttpContext context)
    {
        var countStr = context.Request.Query["count"].FirstOrDefault();
        var count = int.TryParse(countStr, out var c) ? Math.Clamp(c, 1, 50) : 20;

        // Try CountryReputationTracker first (best source — time-decayed stats)
        var tracker = context.RequestServices.GetService(typeof(CountryReputationTracker))
            as CountryReputationTracker;

        var countries = tracker?.GetTopBotCountries(count)
            .Select(cr => new
            {
                countryCode = cr.CountryCode,
                countryName = cr.CountryName,
                botRate = Math.Round(cr.BotRate, 3),
                botCount = (int)Math.Round(cr.DecayedBotCount),
                totalCount = (int)Math.Round(cr.DecayedTotalCount),
                flag = GetCountryFlag(cr.CountryCode)
            })
            .ToList();

        // Fallback: aggregate from recent detections when tracker is empty or unavailable
        if (countries == null || countries.Count == 0)
        {
            var detections = await _eventStore.GetDetectionsAsync(new DashboardFilter { Limit = 500 });
            var countryGroups = detections
                .Where(d => !string.IsNullOrEmpty(d.CountryCode)
                            && d.CountryCode != "XX"
                            && d.CountryCode != "LOCAL")
                .GroupBy(d => d.CountryCode!.ToUpperInvariant())
                .Select(g => new
                {
                    countryCode = g.Key,
                    countryName = g.Key, // We don't have a name lookup here
                    botRate = g.Count() > 0 ? Math.Round((double)g.Count(d => d.IsBot) / g.Count(), 3) : 0.0,
                    botCount = g.Count(d => d.IsBot),
                    totalCount = g.Count(),
                    flag = GetCountryFlag(g.Key)
                })
                .OrderByDescending(c => c.totalCount)
                .Take(count)
                .ToList();

            countries = countryGroups;
        }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, countries,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private async Task ServeClustersApiAsync(HttpContext context)
    {
        var clusterService = context.RequestServices.GetService(typeof(BotClusterService))
            as BotClusterService;

        if (clusterService == null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]");
            return;
        }

        var clusters = clusterService.GetClusters()
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
                temporalDensity = Math.Round(cl.TemporalDensity, 3)
            })
            .ToList();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, clusters,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private async Task ServeTopBotsApiAsync(HttpContext context)
    {
        var visitorCache = context.RequestServices
            .GetService(typeof(VisitorListCache)) as VisitorListCache;

        var countParam = context.Request.Query["count"].FirstOrDefault();
        var count = int.TryParse(countParam, out var c) && c is > 0 and <= 50 ? c : 10;

        var topBots = visitorCache?.GetTopBots(count);

        var result = topBots?.Select(b => new
        {
            b.PrimarySignature,
            HitCount = b.Hits,
            b.BotName,
            b.BotType,
            b.RiskBand,
            b.BotProbability,
            b.Confidence,
            b.Action,
            b.CountryCode,
            b.ProcessingTimeMs,
            topReasons = b.TopReasons,
            b.LastSeen,
            b.Narrative,
            b.Description,
        }) ?? Enumerable.Empty<object>();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private async Task ServeUserAgentsApiAsync(HttpContext context)
    {
        var filter = new DashboardFilter { Limit = 500 };
        var detections = await _eventStore.GetDetectionsAsync(filter);

        // Aggregate by UA family
        var uaGroups = new Dictionary<string, (int total, int bot, int human, double confSum, double procSum,
            DateTime lastSeen, Dictionary<string, int> versions, Dictionary<string, int> countries)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var d in detections)
        {
            var signals = d.ImportantSignals;
            string? family = null;
            string? version = null;

            if (signals != null)
            {
                if (signals.TryGetValue("ua.family", out var ff)) family = ff?.ToString();
                if (signals.TryGetValue("ua.family_version", out var fv)) version = fv?.ToString();
                // Fallback to bot name for bot-type UAs
                if (string.IsNullOrEmpty(family) && signals.TryGetValue("ua.bot_name", out var bn))
                    family = bn?.ToString();
            }

            // Lightweight fallback from raw UA string (only populated for bots)
            if (string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(d.UserAgent))
                family = ExtractBrowserFamily(d.UserAgent);

            if (string.IsNullOrEmpty(family))
                family = "Unknown";

            if (!uaGroups.TryGetValue(family, out var group))
                group = (0, 0, 0, 0, 0, DateTime.MinValue, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

            group.total++;
            if (d.IsBot) group.bot++;
            else group.human++;
            group.confSum += d.Confidence;
            group.procSum += d.ProcessingTimeMs;
            if (d.Timestamp > group.lastSeen) group.lastSeen = d.Timestamp;

            if (!string.IsNullOrEmpty(version))
            {
                group.versions.TryGetValue(version, out var vc);
                group.versions[version] = vc + 1;
            }

            if (!string.IsNullOrEmpty(d.CountryCode))
            {
                group.countries.TryGetValue(d.CountryCode, out var cc);
                group.countries[d.CountryCode] = cc + 1;
            }

            uaGroups[family] = group;
        }

        var result = uaGroups
            .Select(kv => new DashboardUserAgentSummary
            {
                Family = kv.Key,
                Category = InferUaCategory(kv.Key),
                TotalCount = kv.Value.total,
                BotCount = kv.Value.bot,
                HumanCount = kv.Value.human,
                BotRate = kv.Value.total > 0 ? Math.Round((double)kv.Value.bot / kv.Value.total, 4) : 0,
                Versions = kv.Value.versions,
                Countries = kv.Value.countries,
                AvgConfidence = kv.Value.total > 0 ? Math.Round(kv.Value.confSum / kv.Value.total, 4) : 0,
                AvgProcessingTimeMs = kv.Value.total > 0 ? Math.Round(kv.Value.procSum / kv.Value.total, 2) : 0,
                LastSeen = kv.Value.lastSeen,
            })
            .OrderByDescending(u => u.TotalCount)
            .ToList();

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static string ExtractBrowserFamily(string ua)
    {
        if (ua.Contains("Edg/", StringComparison.Ordinal)) return "Edge";
        if (ua.Contains("OPR/", StringComparison.Ordinal) || ua.Contains("Opera", StringComparison.Ordinal)) return "Opera";
        if (ua.Contains("Firefox/", StringComparison.Ordinal)) return "Firefox";
        if (ua.Contains("Chrome/", StringComparison.Ordinal)) return "Chrome";
        if (ua.Contains("Safari/", StringComparison.Ordinal) && !ua.Contains("Chrome/", StringComparison.Ordinal)) return "Safari";
        if (ua.Contains("curl/", StringComparison.OrdinalIgnoreCase)) return "curl";
        if (ua.Contains("python", StringComparison.OrdinalIgnoreCase)) return "Python";
        if (ua.Contains("Go-http-client", StringComparison.Ordinal)) return "Go";
        if (ua.Contains("Googlebot", StringComparison.OrdinalIgnoreCase)) return "Googlebot";
        if (ua.Contains("bingbot", StringComparison.OrdinalIgnoreCase)) return "Bingbot";
        if (ua.Contains("GPTBot", StringComparison.OrdinalIgnoreCase)) return "GPTBot";
        if (ua.Contains("ClaudeBot", StringComparison.OrdinalIgnoreCase)) return "ClaudeBot";
        return "Other";
    }

    private static string InferUaCategory(string family)
    {
        var f = family.ToLowerInvariant();
        if (f is "chrome" or "firefox" or "safari" or "edge" or "opera" or "brave" or "vivaldi" or "samsung internet")
            return "browser";
        if (f is "googlebot" or "bingbot" or "yandexbot" or "baiduspider" or "duckduckbot")
            return "search";
        if (f is "gptbot" or "claudebot" or "ccbot" or "perplexitybot" or "bytespider")
            return "ai";
        if (f is "curl" or "python" or "go" or "java" or "node" or "wget" or "other")
            return "tool";
        return "unknown";
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

        var visitorCache = context.RequestServices
            .GetService(typeof(VisitorListCache)) as VisitorListCache;

        var visitor = visitorCache?.Get(Uri.UnescapeDataString(signatureId));
        if (visitor == null)
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{}");
            return;
        }

        List<double> processingTimes, botProbabilities, confidences;
        lock (visitor.SyncRoot)
        {
            processingTimes = visitor.ProcessingTimeHistory.ToList();
            botProbabilities = visitor.BotProbabilityHistory.ToList();
            confidences = visitor.ConfidenceHistory.ToList();
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
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
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
            filter = filter with { Limit = Math.Clamp(limit, 1, 1000) };

        if (int.TryParse(query["offset"].FirstOrDefault(), out var offset))
            filter = filter with { Offset = Math.Max(0, offset) };

        return filter;
    }

    private async Task WriteCsvAsync(Stream stream, List<DashboardDetectionEvent> detections)
    {
        using var writer = new StreamWriter(stream, leaveOpen: true);

        // Header
        await writer.WriteLineAsync(
            "RequestId,Timestamp,IsBot,BotProbability,Confidence,RiskBand,BotType,BotName,Action,Method,Path,StatusCode,ProcessingTimeMs");

        // Rows
        foreach (var d in detections)
            await writer.WriteLineAsync(
                $"{EscapeCsv(d.RequestId)},{d.Timestamp:O},{d.IsBot},{d.BotProbability},{d.Confidence}," +
                $"{EscapeCsv(d.RiskBand)},{EscapeCsv(d.BotType)},{EscapeCsv(d.BotName)},{EscapeCsv(d.Action)},{EscapeCsv(d.Method)},{EscapeCsv(d.Path)}," +
                $"{d.StatusCode},{d.ProcessingTimeMs}");
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
}
