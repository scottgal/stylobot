using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Extensions;
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
    private readonly DashboardAggregateCache _aggregateCache;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly ILogger<StyloBotDashboardMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly StyloBotDashboardOptions _options;
    private readonly RazorViewRenderer _razorViewRenderer;

    // Rate limiter: per IP, per minute (used only for diagnostics endpoint)
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _rateLimits = new();
    private static volatile bool _authWarningLogged;
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
        "api/clusters",
        "api/useragents",
        "api/topbots"
    };

    private const string CountryDetailPrefix = "api/countries/";

    private static int _cleanupRunning;

    /// <summary>Shared JSON options: camelCase to match SSR initial page load and JS frontend expectations.</summary>
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public StyloBotDashboardMiddleware(
        RequestDelegate next,
        StyloBotDashboardOptions options,
        IDashboardEventStore eventStore,
        DashboardAggregateCache aggregateCache,
        SignatureAggregateCache signatureCache,
        RazorViewRenderer razorViewRenderer,
        ILogger<StyloBotDashboardMiddleware> logger)
    {
        _next = next;
        _options = options;
        _eventStore = eventStore;
        _aggregateCache = aggregateCache;
        _signatureCache = signatureCache;
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

        var relativePath = path.Substring(_options.BasePath.Length).TrimStart('/');

        // High-value dashboard data APIs are hard-blocked for detected bots.
        // This check intentionally does not bypass on API keys.
        var isDataApi = DataApiPaths.Contains(relativePath)
                        || relativePath.StartsWith(CountryDetailPrefix, StringComparison.OrdinalIgnoreCase);
        if (isDataApi && context.IsBot())
        {
            _logger.LogInformation("Blocked bot from dashboard data API: {Path} (probability={Probability:F2})",
                context.Request.Path, context.GetBotProbability());
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Access denied\"}");
            return;
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

            case var p when p.StartsWith(CountryDetailPrefix, StringComparison.OrdinalIgnoreCase):
                await ServeCountryDetailApiAsync(context, p.Substring(CountryDetailPrefix.Length));
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
        var jsonOpts = CamelCaseJson;

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
            var cached = _aggregateCache.Current.Countries;
            var dbCountries = cached.Count > 0 ? cached : await _eventStore.GetCountryStatsAsync(10);
            var countries = dbCountries.Take(10).Select(db => new
            {
                countryCode = db.CountryCode,
                countryName = db.CountryName,
                botRate = db.BotRate,
                botCount = db.BotCount,
                humanCount = db.HumanCount,
                totalCount = db.TotalCount,
                flag = GetCountryFlag(db.CountryCode)
            }).ToList();
            countriesJson = JsonSerializer.Serialize(countries, jsonOpts);
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
                signature = sigs.PrimarySignature
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
            CamelCaseJson);
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

            topBots = _signatureCache.GetTopBots(page, pageSize, sortBy, country);
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
                // 404s feed the responseBehavior detector — the dashboard calls sparkline
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
