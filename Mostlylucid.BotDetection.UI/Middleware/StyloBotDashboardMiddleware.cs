using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Orchestration;
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
    private readonly RequestDelegate _next;
    private readonly StyloBotDashboardOptions _options;

    public StyloBotDashboardMiddleware(
        RequestDelegate next,
        StyloBotDashboardOptions options,
        IDashboardEventStore eventStore)
    {
        _next = next;
        _options = options;
        _eventStore = eventStore;
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
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Forbidden: Dashboard access denied");
            return;
        }

        // Route the request
        var relativePath = path.Substring(_options.BasePath.Length).TrimStart('/');

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

        // No auth configured - WARN but allow (for dev)
        // In production, this should be locked down
        return true;
    }

    private async Task ServeDashboardPageAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html";

        // Extract visitor's own detection evidence (set by BotDetection middleware)
        string yourDetectionJson = "null";
        if (context.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evidenceObj)
            && evidenceObj is AggregatedEvidence evidence)
        {
            var contributions = evidence.Contributions
                .GroupBy(c => c.DetectorName)
                .Select(g => new {
                    detector = g.Key,
                    impact = g.Sum(c => c.ConfidenceDelta * c.Weight),
                    reason = string.Join("; ", g.Select(c => c.Reason).Where(r => !string.IsNullOrEmpty(r)))
                })
                .OrderByDescending(c => Math.Abs(c.impact))
                .ToList();

            var signals = evidence.Signals != null
                ? new Dictionary<string, object>(evidence.Signals)
                : new Dictionary<string, object>();

            var yourDetection = new {
                isBot = evidence.BotProbability > 0.5,
                botProbability = Math.Round(evidence.BotProbability, 4),
                confidence = Math.Round(evidence.Confidence, 4),
                riskBand = evidence.RiskBand.ToString(),
                processingTimeMs = evidence.TotalProcessingTimeMs,
                detectorCount = evidence.Contributions.Select(c => c.DetectorName).Distinct().Count(),
                contributions = contributions,
                signals = signals
            };
            yourDetectionJson = JsonSerializer.Serialize(yourDetection,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        var html = DashboardHtmlTemplate.GetHtml(_options, yourDetectionJson);
        await context.Response.WriteAsync(html);
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
        var limit = int.TryParse(limitStr, out var l) ? l : 100;

        var signatures = await _eventStore.GetSignaturesAsync(limit);

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
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body,
                new { error = ex.Message, type = ex.GetType().Name, inner = ex.InnerException?.Message });
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

        if (int.TryParse(query["limit"].FirstOrDefault(), out var limit))
            filter = filter with { Limit = limit };

        if (int.TryParse(query["offset"].FirstOrDefault(), out var offset))
            filter = filter with { Offset = offset };

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
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

/// <summary>
///     HTML template for the dashboard page.
///     Uses DaisyUI, HTMX, Alpine, ECharts, and Tabulator.
/// </summary>
internal static class DashboardHtmlTemplate
{
    public static string GetHtml(StyloBotDashboardOptions options, string yourDetectionJson = "null")
    {
        return $@"<!DOCTYPE html>
<html lang=""en"" data-theme=""light"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>StyloBot Detection Dashboard</title>
    <script>
        (function() {{
            try {{
                const saved = localStorage.getItem('sb-theme');
                const prefersDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
                const mode = saved === 'light' || saved === 'dark' ? saved : (prefersDark ? 'dark' : 'light');
                document.documentElement.setAttribute('data-theme', mode);
            }} catch (_) {{
                document.documentElement.setAttribute('data-theme', 'light');
            }}
        }})();
    </script>

    <!-- Tailwind + DaisyUI -->
    <link href=""https://cdn.jsdelivr.net/npm/daisyui@4.6.0/dist/full.min.css"" rel=""stylesheet"" type=""text/css"" />
    <script src=""https://cdn.tailwindcss.com""></script>

    <!-- Alpine.js -->
    <script defer src=""https://cdn.jsdelivr.net/npm/alpinejs@3.13.5/dist/cdn.min.js""></script>

    <!-- SignalR -->
    <script src=""https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.0/dist/browser/signalr.min.js""></script>

    <!-- ECharts -->
    <script src=""https://cdn.jsdelivr.net/npm/echarts@5.4.3/dist/echarts.min.js""></script>

    <!-- Tabulator (native themes) -->
    <link id=""tabulator-light-css"" href=""https://unpkg.com/tabulator-tables@6.2.1/dist/css/tabulator.min.css"" rel=""stylesheet"">
    <link id=""tabulator-dark-css"" href=""https://unpkg.com/tabulator-tables@6.2.1/dist/css/tabulator_midnight.min.css"" rel=""stylesheet"">
    <script>
        (function() {{
            const mode = document.documentElement.getAttribute('data-theme') || 'light';
            const light = document.getElementById('tabulator-light-css');
            const dark = document.getElementById('tabulator-dark-css');
            if (light && dark) {{
                const darkOn = mode === 'dark';
                dark.disabled = !darkOn;
                light.disabled = darkOn;
            }}
        }})();
    </script>
    <script src=""https://unpkg.com/tabulator-tables@6.2.1/dist/js/tabulator.min.js""></script>

    <!-- Boxicons -->
    <link href=""https://unpkg.com/boxicons@2.1.4/css/boxicons.min.css"" rel=""stylesheet"">

    <!-- Google Fonts -->
    <link rel=""preconnect"" href=""https://fonts.googleapis.com"">
    <link href=""https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=Raleway:wght@800;900&display=swap"" rel=""stylesheet"">

    <style>
        [data-theme=""dark""] {{
            --sb-brand-muted: #6b7280;
            --sb-brand-strong: #ffffff;
            --sb-accent: #5ba3a3;
            --sb-accent-alt: #0f4c81;
            --sb-accent-strong: #86b59c;
            --sb-surface: #0b1220;
            --sb-card-bg: #141f33;
            --sb-card-border: rgba(91, 163, 163, 0.28);
            --sb-card-divider: rgba(148, 163, 184, 0.18);
        }}
        [data-theme=""light""] {{
            --sb-brand-muted: #475569;
            --sb-brand-strong: #0f172a;
            --sb-accent: #0f766e;
            --sb-accent-alt: #0f4c81;
            --sb-accent-strong: #059669;
            --sb-surface: #f8fafc;
            --sb-card-bg: #ffffff;
            --sb-card-border: rgba(15, 118, 110, 0.18);
            --sb-card-divider: rgba(15, 23, 42, 0.1);
        }}
        body {{
            font-family: 'Inter', sans-serif;
            background: var(--sb-surface);
        }}
        .brand-header {{
            background: linear-gradient(120deg, color-mix(in oklab, var(--sb-surface) 78%, #0f172a), color-mix(in oklab, var(--sb-card-bg) 70%, #0f172a));
            border-bottom: 1px solid var(--sb-card-divider);
        }}
        .brand-wordmark {{ font-family: 'Raleway', sans-serif; font-weight: 900; }}
        .brand-chip {{
            display: inline-flex;
            align-items: center;
            gap: 0.4rem;
            border: 1px solid var(--sb-card-divider);
            border-radius: 9999px;
            padding: 0.2rem 0.6rem;
            background: color-mix(in oklab, var(--sb-card-bg) 88%, transparent);
            color: color-mix(in oklab, var(--sb-brand-strong) 72%, var(--sb-brand-muted));
            font-size: 0.7rem;
            font-weight: 700;
        }}
        .logo-adaptive {{ filter: none; transition: filter 180ms ease; }}
        [data-theme=""light""] .logo-adaptive {{
            background: #000;
            border-radius: 9999px;
            padding: 2px;
            filter: drop-shadow(0 1px 0 rgba(0, 0, 0, 0.45)) drop-shadow(0 0 6px rgba(0, 0, 0, 0.35));
        }}
        .dashboard-shell {{ max-width: 84rem; margin: 0 auto; }}
        .scrolling-signatures {{
            max-height: 350px;
            overflow-y: auto;
        }}
        .signature-item {{
            animation: slideIn 0.3s ease-out;
        }}
        @@keyframes slideIn {{
            from {{ opacity: 0; transform: translateY(-10px); }}
            to {{ opacity: 1; transform: translateY(0); }}
        }}
        @@keyframes pulse-dot {{
            0%, 100% {{ opacity: 1; }}
            50% {{ opacity: 0.4; }}
        }}
        .live-dot {{
            width: 8px; height: 8px; border-radius: 50%; background: var(--sb-accent);
            display: inline-block; animation: pulse-dot 2s infinite;
        }}
        .risk-veryhigh {{ color: #dc2626; }}
        .risk-high {{ color: #ef4444; }}
        .risk-medium {{ color: #DAA564; }}
        .risk-elevated {{ color: #DAA564; }}
        .risk-low {{ color: #86B59C; }}
        .risk-verylow {{ color: #86B59C; }}
        .bot-pct-bar {{
            height: 6px; border-radius: 3px; transition: width 0.5s ease;
        }}

        .dashboard-card {{
            border: 1px solid var(--sb-card-divider) !important;
            background: color-mix(in oklab, var(--sb-card-bg) 94%, transparent) !important;
        }}
        .card.bg-base-200 {{
            border: 1px solid var(--sb-card-divider) !important;
            background: color-mix(in oklab, var(--sb-card-bg) 94%, transparent) !important;
        }}
        .dashboard-subtle {{
            color: color-mix(in oklab, var(--sb-brand-strong) 62%, var(--sb-brand-muted));
        }}
        .dashboard-cta {{
            border-color: var(--sb-accent) !important;
            color: var(--sb-accent) !important;
            background: color-mix(in oklab, var(--sb-accent) 12%, transparent) !important;
        }}

        /* Minimal Tabulator polish; base theme comes from Tabulator CSS */
        .tabulator {{
            border: 1px solid var(--sb-card-divider) !important;
            border-radius: 0.5rem;
            font-size: 0.8rem;
            overflow: hidden;
        }}
        .tabulator .tabulator-header {{
            border-bottom: 2px solid var(--sb-accent) !important;
        }}
        .tabulator .tabulator-header .tabulator-col .tabulator-col-content .tabulator-col-title {{
            font-weight: 600;
            font-size: 0.7rem;
            text-transform: uppercase;
            letter-spacing: 0.05em;
        }}
        .tabulator .tabulator-tableholder .tabulator-table .tabulator-row .tabulator-cell {{
            padding: 6px 8px;
        }}
        .tabulator .tabulator-footer .tabulator-page {{
            border-radius: 0.25rem;
            margin: 0 2px;
        }}
        .tabulator .tabulator-footer .tabulator-page.active {{
            background-color: var(--sb-accent) !important;
            color: white !important;
            border-color: var(--sb-accent) !important;
        }}
        .tabulator .tabulator-footer .tabulator-page:hover:not(.active) {{
            filter: brightness(1.06);
        }}
    </style>
</head>
<body class=""bg-base-100"">

    <div x-data=""dashboardState()"" x-init=""init()"" class=""min-h-screen"">

        <!-- Header -->
        <div class=""brand-header py-3 mb-4"">
            <div class=""dashboard-shell px-4 flex flex-wrap items-center justify-between gap-3"">
                <a href=""/"" class=""flex items-center gap-3 no-underline hover:opacity-90 transition-opacity"">
                    <img src=""/img/stylowall.svg"" alt=""Stylobot logo"" class=""h-9 w-auto logo-adaptive"">
                    <div>
                        <h1 class=""text-2xl font-bold brand-wordmark leading-none"">
                            <span style=""color: var(--sb-brand-muted); font-style: italic;"">stylo</span><span style=""color: var(--sb-brand-strong);"">bot</span>
                        </h1>
                        <p class=""text-xs dashboard-subtle mt-1"">Detection dashboard</p>
                    </div>
                </a>
                <div class=""flex items-center gap-2 flex-wrap"">
                    <span class=""brand-chip""><i class=""bx bx-shield-quarter text-[12px]""></i> Live telemetry</span>
                    <span class=""live-dot""></span>
                    <span class=""text-sm font-medium"" :class=""signalrConnected ? 'text-success' : 'text-error'""
                          x-text=""signalrConnected ? 'Connected' : 'Reconnecting...'""></span>
                    <button type=""button"" class=""btn btn-ghost btn-sm btn-square"" title=""Toggle theme"" @@click=""toggleTheme()"">
                        <i class=""bx"" :class=""isDark ? 'bx-sun' : 'bx-moon'""></i>
                    </button>
                    <a href=""/"" class=""btn btn-ghost btn-sm gap-1""><i class=""bx bx-home""></i> Home</a>
                    <a href=""/Home/LiveDemo"" class=""btn btn-sm gap-1 dashboard-cta""><i class=""bx bx-broadcast""></i> Live Demo</a>
                    <a href=""https://github.com/scottgal/stylobot"" target=""_blank"" class=""btn btn-ghost btn-sm gap-1""><i class=""bx bxl-github""></i> GitHub</a>
                </div>
            </div>
        </div>

        <div class=""dashboard-shell px-4 pb-8"">

        <!-- Your Detection (visitor's own) -->
        <template x-if=""yourData"">
            <div class=""card bg-base-200 shadow-lg mb-6 border-l-4"" style=""border-left-color: #5BA3A3;"">
                <div class=""card-body py-3"">
                    <div class=""flex flex-wrap items-center gap-4"">
                        <h2 class=""text-sm font-bold uppercase tracking-wider text-base-content/50"">
                            <i class=""bx bx-user-check mr-1""></i> Your Detection
                        </h2>
                        <span class=""badge font-bold text-white""
                              :class=""yourData.isBot ? 'badge-error' : 'badge-success'""
                              x-text=""yourData.isBot ? 'BOT' : 'HUMAN'""></span>
                        <span class=""text-lg font-bold"" :style=""'color:' + (yourData.botProbability >= 0.7 ? '#ef4444' : yourData.botProbability >= 0.4 ? '#DAA564' : '#86B59C')""
                              x-text=""Math.round(yourData.botProbability * 100) + '% bot'""></span>
                        <span class=""badge badge-sm badge-ghost"" x-text=""'Confidence ' + Math.round(yourData.confidence * 100) + '%'""></span>
                        <span class=""badge badge-sm badge-ghost"" x-text=""yourData.detectorCount + ' detectors'""></span>
                        <span class=""badge badge-sm"" :style=""'background-color:' + riskColor(yourData.riskBand) + '22; color:' + riskColor(yourData.riskBand)"" x-text=""yourData.riskBand""></span>
                        <span class=""text-xs text-base-content/40"" x-text=""yourData.processingTimeMs?.toFixed(0) + 'ms'""></span>
                        <button class=""btn btn-ghost btn-xs ml-auto"" x-on:click=""showYourDetail = !showYourDetail"">
                            <i class=""bx"" :class=""showYourDetail ? 'bx-chevron-up' : 'bx-chevron-down'""></i>
                            <span x-text=""showYourDetail ? 'Less' : 'Details'""></span>
                        </button>
                    </div>
                    <div x-show=""showYourDetail"" x-transition class=""mt-3 grid grid-cols-1 lg:grid-cols-2 gap-4"">
                        <!-- Detector breakdown -->
                        <div>
                            <h3 class=""text-xs font-bold uppercase tracking-wider text-base-content/40 mb-2"">
                                <i class=""bx bx-bar-chart-alt-2 mr-1""></i> Detector Breakdown
                            </h3>
                            <div class=""space-y-1"">
                                <template x-for=""c in yourData.contributions?.slice(0, 10) || []"" :key=""c.detector"">
                                    <div class=""flex items-center gap-2"">
                                        <span class=""text-xs w-20 truncate"" x-text=""c.detector""></span>
                                        <div class=""flex-1 bg-base-300 rounded-full h-2.5 overflow-hidden"">
                                            <div class=""h-full rounded-full""
                                                 :style=""'width:' + Math.max(Math.min(Math.abs(c.impact) * 1000, 100), 3) + '%; background-color:' + (c.impact > 0 ? '#ef444488' : '#86B59C88')""></div>
                                        </div>
                                        <span class=""w-12 text-right text-[10px] font-mono""
                                              :style=""'color:' + (c.impact > 0 ? '#ef4444' : '#86B59C')""
                                              x-text=""(c.impact > 0 ? '+' : '') + (c.impact * 100).toFixed(1) + '%'""></span>
                                    </div>
                                </template>
                            </div>
                        </div>
                        <!-- Signals -->
                        <div>
                            <h3 class=""text-xs font-bold uppercase tracking-wider text-base-content/40 mb-2"">
                                <i class=""bx bx-broadcast mr-1""></i> Signals
                                <span class=""badge badge-xs badge-ghost ml-1"" x-text=""Object.keys(yourData.signals || {{}}).length""></span>
                            </h3>
                            <div class=""space-y-0.5 max-h-40 overflow-y-auto"">
                                <template x-for=""[key, val] in Object.entries(yourData.signals || {{}})"" :key=""key"">
                                    <div class=""flex items-center gap-1 text-[11px]"">
                                        <code class=""font-mono opacity-50 truncate flex-1"" x-text=""key""></code>
                                        <span class=""font-medium""
                                              :style=""'color:' + (val === true ? '#86B59C' : val === false ? '#ef4444' : '#6b7280')""
                                              x-text=""String(val)""></span>
                                    </div>
                                </template>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </template>
        <script type=""application/json"" id=""your-detection-data"">{yourDetectionJson}</script>

        <!-- Summary Cards -->
        <div class=""grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-4 mb-6"">
            <div class=""stat bg-base-200 rounded-lg shadow"">
                <div class=""stat-title"">Total Requests</div>
                <div class=""stat-value text-2xl"" x-text=""summary.totalRequests"">0</div>
            </div>
            <div class=""stat bg-base-200 rounded-lg shadow"">
                <div class=""stat-title"">Bot Requests</div>
                <div class=""stat-value text-2xl text-error"" x-text=""summary.botRequests"">0</div>
                <div class=""stat-desc"">
                    <div class=""flex items-center gap-2"">
                        <div class=""flex-1 bg-base-300 rounded-full"" style=""height:6px"">
                            <div class=""bot-pct-bar bg-error"" :style=""'width:' + (summary.botPercentage || 0) + '%'""></div>
                        </div>
                        <span x-text=""(summary.botPercentage || 0).toFixed(1) + '%'""></span>
                    </div>
                </div>
            </div>
            <div class=""stat bg-base-200 rounded-lg shadow"">
                <div class=""stat-title"">Human Requests</div>
                <div class=""stat-value text-2xl"" style=""color: #86B59C;"" x-text=""summary.humanRequests"">0</div>
            </div>
            <div class=""stat bg-base-200 rounded-lg shadow"">
                <div class=""stat-title"">Unique Signatures</div>
                <div class=""stat-value text-2xl"" style=""color: #5BA3A3;"" x-text=""summary.uniqueSignatures"">0</div>
            </div>
            <div class=""stat bg-base-200 rounded-lg shadow"">
                <div class=""stat-title"">Avg Processing</div>
                <div class=""stat-value text-2xl"" x-text=""(summary.averageProcessingTimeMs || 0).toFixed(0) + 'ms'"">0ms</div>
            </div>
        </div>

        <!-- Controls Bar -->
        <div class=""card bg-base-200 shadow-lg mb-6"">
            <div class=""card-body py-3"">
                <div class=""flex flex-wrap items-end gap-4"">
                    <div class=""form-control"">
                        <label class=""label py-0""><span class=""label-text text-xs"">Time Range</span></label>
                        <select x-model=""filters.timeRange"" class=""select select-bordered select-sm"" x-on:change=""applyFilters()"">
                            <option value=""5m"">Last 5 min</option>
                            <option value=""15m"">Last 15 min</option>
                            <option value=""1h"">Last hour</option>
                            <option value=""6h"">Last 6 hours</option>
                            <option value=""24h"" selected>Last 24h</option>
                        </select>
                    </div>
                    <div class=""form-control"">
                        <label class=""label py-0""><span class=""label-text text-xs"">Risk Band</span></label>
                        <select x-model=""filters.riskBand"" class=""select select-bordered select-sm"" x-on:change=""applyFilters()"">
                            <option value="""">All</option>
                            <option value=""VeryLow"">Very Low</option>
                            <option value=""Low"">Low</option>
                            <option value=""Medium"">Medium</option>
                            <option value=""High"">High</option>
                            <option value=""VeryHigh"">Very High</option>
                        </select>
                    </div>
                    <div class=""form-control"">
                        <label class=""label py-0""><span class=""label-text text-xs"">Classification</span></label>
                        <select x-model=""filters.classification"" class=""select select-bordered select-sm"" x-on:change=""applyFilters()"">
                            <option value="""">All</option>
                            <option value=""bot"">Bots</option>
                            <option value=""human"">Humans</option>
                        </select>
                    </div>
                    <div class=""ml-auto flex gap-2"">
                        <button @click=""exportData('json')"" class=""btn btn-sm btn-outline"">Export JSON</button>
                        <button @click=""exportData('csv')"" class=""btn btn-sm btn-outline"">Export CSV</button>
                    </div>
                </div>
            </div>
        </div>

        <!-- Charts Row -->
        <div class=""grid grid-cols-1 lg:grid-cols-2 gap-6 mb-6"">
            <div class=""card bg-base-200 shadow-lg"">
                <div class=""card-body"">
                    <div class=""flex items-center justify-between mb-1"">
                        <h2 class=""card-title text-base"">Detection Timeline</h2>
                        <a href=""/docs/how-stylobot-works#temporal-intelligence-mostlylucidephemeral"" class=""text-xs dashboard-subtle hover:underline"">How this works</a>
                    </div>
                    <div id=""riskTimelineChart"" style=""height: 280px;""></div>
                </div>
            </div>
            <div class=""card bg-base-200 shadow-lg"">
                <div class=""card-body"">
                    <div class=""flex items-center justify-between mb-1"">
                        <h2 class=""card-title text-base"">Classification Distribution</h2>
                        <a href=""/docs/detectors-in-depth"" class=""text-xs dashboard-subtle hover:underline"">Detector guide</a>
                    </div>
                    <div id=""classificationChart"" style=""height: 280px;""></div>
                </div>
            </div>
        </div>

        <!-- Signatures + Top Bots Row -->
        <div class=""grid grid-cols-1 lg:grid-cols-3 gap-6 mb-6"">
            <!-- Scrolling Signatures (2 cols) -->
            <div class=""card bg-base-200 shadow-lg lg:col-span-2"">
                <div class=""card-body"">
                    <h2 class=""card-title text-base"">
                        Live Signatures Feed
                        <span class=""live-dot ml-2""></span>
                    </h2>
                    <div class=""scrolling-signatures"">
                        <template x-for=""(sig, idx) in signatures"" :key=""sig.signatureId || sig.primarySignature || idx"">
                            <div class=""signature-item mb-1 rounded-lg bg-base-300/50 hover:bg-base-300 transition-colors"">
                                <div class=""flex items-center gap-3 px-3 py-2 cursor-pointer"" x-on:click=""sig._expanded = !sig._expanded"">
                                    <i class=""bx text-xs opacity-40"" :class=""sig._expanded ? 'bx-chevron-down' : 'bx-chevron-right'""></i>
                                    <template x-if=""sig.botName"">
                                        <span class=""text-xs font-bold"" :style=""'color:' + sigColor(sig.primarySignature || sig.signatureId)"" x-text=""sig.botName""></span>
                                    </template>
                                    <template x-if=""!sig.botName && sig.isKnownBot"">
                                        <span class=""text-xs font-semibold opacity-70"">Known Bot</span>
                                    </template>
                                    <span class=""font-mono text-[10px] opacity-40"" x-text=""(sig.primarySignature || sig.signatureId || '-').substring(0, 8)""></span>
                                    <span class=""badge badge-xs""
                                          :class=""{{
                                              'badge-error': sig.riskBand === 'VeryHigh' || sig.riskBand === 'High',
                                              'badge-warning': sig.riskBand === 'Medium',
                                              'badge-info': sig.riskBand === 'Low',
                                              'badge-success': sig.riskBand === 'VeryLow'
                                          }}""
                                          x-text=""sig.riskBand || 'Unknown'""></span>
                                    <span class=""text-xs font-bold ml-auto"" x-text=""sig.hitCount || 0""></span>
                                    <span class=""text-[10px] opacity-40"">hits</span>
                                    <span class=""text-xs opacity-60"" x-show=""sig.factorCount"" x-text=""sig.factorCount + ' vectors'""></span>
                                </div>
                                <!-- Expanded detail -->
                                <div x-show=""sig._expanded"" x-transition class=""px-3 pb-3 pt-1 border-t border-base-300/50"">
                                    <div class=""grid grid-cols-2 gap-x-4 gap-y-1 text-[11px] mb-2"">
                                        <div><span class=""opacity-40"">Probability:</span> <span class=""font-bold"" :style=""'color:' + ((sig.botProbability || 0) >= 0.5 ? '#ef4444' : '#86B59C')"" x-text=""((sig.botProbability || 0) * 100).toFixed(0) + '%'""></span></div>
                                        <div><span class=""opacity-40"">Confidence:</span> <span class=""font-bold"" x-text=""((sig.confidence || 0) * 100).toFixed(0) + '%'""></span></div>
                                        <div><span class=""opacity-40"">Bot Type:</span> <span x-text=""sig.botType || '-'""></span></div>
                                        <div><span class=""opacity-40"">Action:</span> <span x-text=""sig.action || 'Allow'""></span></div>
                                        <div x-show=""sig.lastPath""><span class=""opacity-40"">Last Path:</span> <code class=""font-mono"" x-text=""sig.lastPath""></code></div>
                                        <div x-show=""sig.firstSeen""><span class=""opacity-40"">First Seen:</span> <span x-text=""new Date(sig.firstSeen).toLocaleTimeString()""></span></div>
                                    </div>
                                    <!-- Factors/Reasons -->
                                    <template x-if=""sig.topReasons && sig.topReasons.length > 0"">
                                        <div>
                                            <h4 class=""text-[10px] font-bold uppercase tracking-wider opacity-40 mb-1""><i class=""bx bx-analyse mr-1""></i>Detection Reasons</h4>
                                            <div class=""space-y-0.5"">
                                                <template x-for=""reason in sig.topReasons.slice(0, 5)"" :key=""reason"">
                                                    <div class=""text-[11px] opacity-70 pl-2 border-l-2"" style=""border-left-color: #5BA3A3;"" x-text=""reason""></div>
                                                </template>
                                            </div>
                                        </div>
                                    </template>
                                    <!-- Factor breakdown (multi-vector) -->
                                    <template x-if=""sig.factors && Object.keys(sig.factors).length > 0"">
                                        <div class=""mt-2"">
                                            <h4 class=""text-[10px] font-bold uppercase tracking-wider opacity-40 mb-1""><i class=""bx bx-layer mr-1""></i>Multi-Vector Signals</h4>
                                            <div class=""flex flex-wrap gap-1"">
                                                <template x-for=""[factor, val] in Object.entries(sig.factors)"" :key=""factor"">
                                                    <span class=""badge badge-xs badge-ghost"" x-text=""factor""></span>
                                                </template>
                                            </div>
                                        </div>
                                    </template>
                                </div>
                            </div>
                        </template>
                        <template x-if=""signatures.length === 0"">
                            <div class=""text-center text-base-content/50 py-8"">Waiting for signatures...</div>
                        </template>
                    </div>
                </div>
            </div>

            <!-- Top Bots Leaderboard (1 col) -->
            <div class=""card bg-base-200 shadow-lg border-l-4"" style=""border-left-color: #ef4444;"">
                <div class=""card-body"">
                    <h2 class=""card-title text-base"">
                        Top Bot Types
                        <span class=""badge badge-error badge-sm"" x-text=""topBots.reduce((s, b) => s + b.count, 0)""></span>
                    </h2>
                    <div class=""space-y-3"">
                        <template x-for=""(bot, i) in topBots"" :key=""i"">
                            <div class=""bg-base-300/50 rounded-lg p-2"">
                                <div class=""flex items-center gap-2 mb-1"">
                                    <span class=""text-xs font-bold w-5 text-center"" :style=""'color:' + (i < 3 ? '#ef4444' : '#6b7280')"" x-text=""'#' + (i + 1)""></span>
                                    <span class=""text-sm font-semibold flex-1 truncate"" x-text=""bot.name || 'Unknown'""></span>
                                    <span class=""badge badge-sm badge-error font-bold"" x-text=""bot.count""></span>
                                </div>
                                <div class=""ml-7"">
                                    <div class=""bg-base-300 rounded-full"" style=""height:4px"">
                                        <div class=""rounded-full"" style=""height:4px; background-color: #ef444488;""
                                             :style=""'width:' + Math.max((bot.count / (topBots[0]?.count || 1)) * 100, 5) + '%'""></div>
                                    </div>
                                </div>
                            </div>
                        </template>
                        <template x-if=""topBots.length === 0"">
                            <div class=""text-center text-base-content/50 py-4 text-sm"">No bots detected yet</div>
                        </template>
                    </div>
                </div>
            </div>
        </div>

        <!-- Detections Grid -->
        <div class=""card bg-base-200 shadow-lg"">
            <div class=""card-body"">
                <div class=""flex items-center justify-between mb-2"">
                    <h2 class=""card-title text-base"">Recent Requests</h2>
                    <span class=""text-xs text-base-content/40"">Every request analysed by the detection pipeline. Named bots shown when identified.</span>
                </div>
                <div id=""detectionsTable""></div>
            </div>
        </div>

        </div>
    </div>

    <script>
        // Generate a consistent HSL color from a signature hash for visual correlation
        function sigColor(sig) {{
            if (!sig) return '#6b7280';
            let hash = 0;
            for (let i = 0; i < sig.length; i++) {{ hash = sig.charCodeAt(i) + ((hash << 5) - hash); }}
            const hue = ((hash % 360) + 360) % 360;
            return `hsl(${{hue}}, 65%, 55%)`;
        }}

        // Normalize PascalCase API keys to camelCase for JS consumption
        function toCamel(obj) {{
            if (Array.isArray(obj)) return obj.map(toCamel);
            if (obj !== null && typeof obj === 'object') {{
                return Object.fromEntries(
                    Object.entries(obj).map(([k, v]) => [k.charAt(0).toLowerCase() + k.slice(1), toCamel(v)])
                );
            }}
            return obj;
        }}

        function dashboardState() {{
            return {{
                connection: null,
                signalrConnected: false,
                isDark: false,
                yourData: null,
                showYourDetail: false,
                summary: {{
                    totalRequests: 0,
                    botRequests: 0,
                    humanRequests: 0,
                    uniqueSignatures: 0,
                    botPercentage: 0,
                    averageProcessingTimeMs: 0,
                    uncertainRequests: 0
                }},
                signatures: [],
                detections: [],
                topBots: [],
                filters: {{
                    timeRange: '24h',
                    riskBand: '',
                    classification: ''
                }},
                tabulatorTable: null,
                riskChart: null,
                classificationChart: null,

                riskColor(band) {{
                    return {{ 'VeryLow': '#86B59C', 'Low': '#86B59C', 'Elevated': '#DAA564', 'Medium': '#DAA564', 'High': '#ef4444', 'VeryHigh': '#dc2626' }}[band] || '#6b7280';
                }},

                initTheme() {{
                    const current = document.documentElement.getAttribute('data-theme');
                    this.isDark = current === 'dark';
                }},

                toggleTheme() {{
                    this.isDark = !this.isDark;
                    const next = this.isDark ? 'dark' : 'light';
                    document.documentElement.setAttribute('data-theme', next);
                    try {{ localStorage.setItem('sb-theme', next); }} catch (_) {{}}
                    this.syncTabulatorTheme();
                    this.applyChartTheme();
                }},

                syncTabulatorTheme() {{
                    const light = document.getElementById('tabulator-light-css');
                    const dark = document.getElementById('tabulator-dark-css');
                    if (!light || !dark) return;
                    const darkOn = this.isDark;
                    dark.disabled = !darkOn;
                    light.disabled = darkOn;
                }},

                chartThemeColors() {{
                    if (this.isDark) {{
                        return {{
                            text: '#a0aec0',
                            line: '#4a5568',
                            split: '#2d3748',
                            axis: '#4a5568'
                        }};
                    }}
                    return {{
                        text: '#334155',
                        line: '#cbd5e1',
                        split: '#e2e8f0',
                        axis: '#94a3b8'
                    }};
                }},

                init() {{
                    this.initTheme();
                    this.syncTabulatorTheme();
                    // Load visitor's own detection from inline JSON
                    try {{
                        const el = document.getElementById('your-detection-data');
                        if (el) {{ const d = JSON.parse(el.textContent); if (d) this.yourData = d; }}
                    }} catch(e) {{}}

                    this.initSignalR();
                    this.initCharts();
                    this.initTable();
                    this.loadInitialData();
                    this.applyChartTheme();
                    window.addEventListener('resize', () => {{
                        this.riskChart?.resize();
                        this.classificationChart?.resize();
                    }});
                }},

                initSignalR() {{
                    this.connection = new signalR.HubConnectionBuilder()
                        .withUrl('{options.HubPath}')
                        .withAutomaticReconnect()
                        .build();

                    this.connection.on('BroadcastDetection', (detection) => {{
                        const d = toCamel(detection);
                        this.detections.unshift(d);
                        if (this.detections.length > 100) this.detections.pop();
                        this.tabulatorTable?.setData(this.detections);
                    }});

                    this.connection.on('BroadcastSignature', (signature) => {{
                        const sig = toCamel(signature);
                        const idx = this.signatures.findIndex(s => s.primarySignature === sig.primarySignature);
                        if (idx >= 0) {{ this.signatures[idx] = sig; }}
                        else {{ this.signatures.unshift(sig); }}
                        if (this.signatures.length > 50) this.signatures.pop();
                    }});

                    this.connection.on('BroadcastSummary', (summary) => {{
                        const s = toCamel(summary);
                        this.summary = {{ ...this.summary, ...s }};
                        this.updateTopBotsFromSummary();
                        this.updateCharts();
                    }});

                    this.connection.on('BroadcastDescriptionUpdate', (requestId, description) => {{
                        // Update matching detection with the LLM-generated description
                        const det = this.detections.find(d => d.requestId === requestId);
                        if (det) {{
                            det.description = description;
                            this.tabulatorTable?.updateData([det]);
                        }}
                    }});

                    this.connection.onclose(() => {{ this.signalrConnected = false; }});
                    this.connection.onreconnecting(() => {{ this.signalrConnected = false; }});
                    this.connection.onreconnected(() => {{ this.signalrConnected = true; }});

                    this.connection.start()
                        .then(() => {{ this.signalrConnected = true; }})
                        .catch(err => console.error('SignalR error:', err));
                }},

                initCharts() {{
                    const colors = this.chartThemeColors();
                    this.riskChart = echarts.init(document.getElementById('riskTimelineChart'));
                    this.riskChart.setOption({{
                        tooltip: {{ trigger: 'axis' }},
                        legend: {{ data: ['Bots', 'Humans'], textStyle: {{ color: colors.text }} }},
                        grid: {{ left: 40, right: 20, top: 40, bottom: 30 }},
                        xAxis: {{ type: 'time', axisLabel: {{ color: colors.text }}, axisLine: {{ lineStyle: {{ color: colors.axis }} }} }},
                        yAxis: {{ type: 'value', axisLabel: {{ color: colors.text }}, splitLine: {{ lineStyle: {{ color: colors.split }} }} }},
                        series: [
                            {{ name: 'Bots', type: 'line', data: [], smooth: true, areaStyle: {{ opacity: 0.15 }}, lineStyle: {{ width: 2 }}, color: '#ef4444' }},
                            {{ name: 'Humans', type: 'line', data: [], smooth: true, areaStyle: {{ opacity: 0.15 }}, lineStyle: {{ width: 2 }}, color: '#86B59C' }}
                        ]
                    }});

                    this.classificationChart = echarts.init(document.getElementById('classificationChart'));
                    this.classificationChart.setOption({{
                        tooltip: {{ trigger: 'item' }},
                        series: [{{
                            type: 'pie',
                            radius: ['40%', '65%'],
                            label: {{ color: colors.text }},
                            data: [
                                {{ value: 0, name: 'Bots', itemStyle: {{ color: '#ef4444' }} }},
                                {{ value: 0, name: 'Humans', itemStyle: {{ color: '#86B59C' }} }},
                                {{ value: 0, name: 'Uncertain', itemStyle: {{ color: '#DAA564' }} }}
                            ]
                        }}]
                    }});
                }},

                applyChartTheme() {{
                    const colors = this.chartThemeColors();
                    this.riskChart?.setOption({{
                        legend: {{ textStyle: {{ color: colors.text }} }},
                        xAxis: {{ axisLabel: {{ color: colors.text }}, axisLine: {{ lineStyle: {{ color: colors.axis }} }} }},
                        yAxis: {{ axisLabel: {{ color: colors.text }}, splitLine: {{ lineStyle: {{ color: colors.split }} }} }}
                    }});
                    this.classificationChart?.setOption({{
                        series: [{{ label: {{ color: colors.text }} }}]
                    }});
                }},

                initTable() {{
                    const riskColors = {{ VeryHigh: '#dc2626', High: '#ef4444', Medium: '#DAA564', Elevated: '#DAA564', Low: '#86B59C', VeryLow: '#86B59C' }};
                    this.tabulatorTable = new Tabulator('#detectionsTable', {{
                        data: [],
                        layout: 'fitColumns',
                        pagination: true,
                        paginationSize: 25,
                        columns: [
                            {{ title: 'Time', field: 'timestamp', width: 100, formatter: (cell) => {{
                                const v = cell.getValue();
                                if (!v) return '';
                                const d = new Date(v);
                                return isNaN(d.getTime()) ? '' : d.toLocaleTimeString();
                            }} }},
                            {{ title: 'Visitor', field: 'primarySignature', width: 160, formatter: (cell) => {{
                                const row = cell.getRow().getData();
                                const sig = cell.getValue();
                                const name = row.botName;
                                const color = sig ? sigColor(sig) : '#6b7280';
                                const short = sig ? sig.substring(0, 8) : '-';
                                const dot = `<span style=""width:8px;height:8px;border-radius:50%;background:${{color}};display:inline-block;flex-shrink:0""></span>`;
                                if (name) {{
                                    return `<span style=""display:inline-flex;align-items:center;gap:4px"">${{dot}}<span style=""font-weight:600;font-size:0.75rem"">${{name}}</span><code style=""color:${{color}};font-size:0.6rem;opacity:0.5"">${{short}}</code></span>`;
                                }}
                                return `<span style=""display:inline-flex;align-items:center;gap:4px"">${{dot}}<code style=""color:${{color}};font-size:0.75rem"">${{short}}</code></span>`;
                            }},
                            tooltip: (e, cell) => {{
                                const row = cell.getRow().getData();
                                const sig = cell.getValue();
                                const name = row.botName;
                                return name ? `${{name}} (${{sig}})` : sig ? `Client Signature: ${{sig}}` : '';
                            }} }},
                            {{ title: 'Type', field: 'isBot', width: 80, formatter: (cell) => {{
                                const isBot = cell.getValue();
                                return `<span style=""color:${{isBot ? '#ef4444' : '#86B59C'}};font-weight:600"">${{isBot ? 'Bot' : 'Human'}}</span>`;
                            }} }},
                            {{ title: 'Risk', field: 'riskBand', width: 90, formatter: (cell) => {{
                                const v = cell.getValue() || '';
                                const c = riskColors[v] || '#6b7280';
                                return `<span style=""color:${{c}};font-weight:500"">${{v || '-'}}</span>`;
                            }} }},
                            {{ title: 'Method', field: 'method', width: 70, formatter: (cell) => cell.getValue() || '' }},
                            {{ title: 'Path', field: 'path', minWidth: 150, formatter: (cell) => cell.getValue() || '' }},
                            {{ title: 'Action', field: 'action', width: 90, formatter: (cell) => cell.getValue() || '' }},
                            {{ title: 'Prob', field: 'botProbability', width: 70, hozAlign: 'right', formatter: (cell) => {{
                                const v = cell.getValue();
                                if (v == null || isNaN(v)) return '-';
                                const pct = (v * 100).toFixed(0);
                                const color = v >= 0.7 ? '#ef4444' : v >= 0.4 ? '#DAA564' : '#86B59C';
                                return `<span style=""color:${{color}};font-weight:600"">${{pct}}%</span>`;
                            }} }},
                            {{ title: 'Top Reason', field: 'topReasons', minWidth: 140, formatter: (cell) => {{
                                const v = cell.getValue();
                                if (!v || !v.length) return '<span style=""color:#6b7280"">-</span>';
                                return `<span style=""font-size:0.7rem;opacity:0.8"">${{v[0]}}</span>`;
                            }} }},
                            {{ title: 'AI Description', field: 'description', minWidth: 180, formatter: (cell) => {{
                                const v = cell.getValue();
                                const row = cell.getRow().getData();
                                if (!v && row.isBot) return '<span style=""color:#6b7280;font-size:0.65rem""><i class=""bx bx-loader-alt bx-spin"" style=""font-size:0.7rem""></i> Awaiting AI...</span>';
                                if (!v) return '<span style=""color:#6b7280;font-size:0.65rem"">-</span>';
                                return `<span style=""font-size:0.7rem;color:#5BA3A3;font-style:italic""><i class=""bx bx-bot"" style=""font-size:0.7rem;margin-right:2px""></i>${{v}}</span>`;
                            }} }},
                            {{ title: 'ms', field: 'processingTimeMs', width: 60, hozAlign: 'right', formatter: (cell) => {{
                                const v = cell.getValue();
                                return (v != null && !isNaN(v)) ? v.toFixed(0) : '0';
                            }} }}
                        ]
                    }});
                }},

                async loadInitialData() {{
                    try {{
                        const start = this.timeRangeToStart().toISOString();
                        const end = new Date().toISOString();
                        const bucket = this.timeRangeToBucket();

                        const [summaryRaw, detectionsRaw, signaturesRaw, timeseriesRaw] = await Promise.all([
                            fetch('{options.BasePath}/api/summary').then(r => r.json()),
                            fetch(`{options.BasePath}/api/detections?limit=100&start=${{start}}&end=${{end}}`).then(r => r.json()),
                            fetch('{options.BasePath}/api/signatures?limit=50').then(r => r.json()),
                            fetch(`{options.BasePath}/api/timeseries?bucket=${{bucket}}&start=${{start}}&end=${{end}}`).then(r => r.json()).catch(() => [])
                        ]);

                        const summary = toCamel(summaryRaw);
                        const detections = toCamel(detectionsRaw);
                        const signatures = toCamel(signaturesRaw);
                        const timeseries = toCamel(timeseriesRaw);

                        this.summary = {{ ...this.summary, ...summary }};
                        this.detections = detections;
                        this.tabulatorTable.setData(detections);
                        this.signatures = signatures;
                        this.updateCharts();
                        this.updateTimeline(timeseries);
                        this.updateTopBotsFromSummary();
                    }} catch (e) {{
                        console.error('Failed to load initial data:', e);
                    }}
                }},

                updateTimeline(timeseries) {{
                    if (!timeseries || !timeseries.length) return;
                    const botData = timeseries.map(t => [new Date(t.timestamp), t.botCount || 0]);
                    const humanData = timeseries.map(t => [new Date(t.timestamp), t.humanCount || 0]);
                    this.riskChart.setOption({{
                        series: [
                            {{ data: botData }},
                            {{ data: humanData }}
                        ]
                    }});
                }},

                updateTopBotsFromSummary() {{
                    const topBotTypes = this.summary.topBotTypes;
                    if (topBotTypes && typeof topBotTypes === 'object') {{
                        this.topBots = Object.entries(topBotTypes)
                            .map(([name, count]) => ({{ name, count }}))
                            .sort((a, b) => b.count - a.count)
                            .slice(0, 8);
                    }}
                }},

                updateCharts() {{
                    this.classificationChart.setOption({{
                        series: [{{
                            data: [
                                {{ value: this.summary.botRequests, name: 'Bots' }},
                                {{ value: this.summary.humanRequests, name: 'Humans' }},
                                {{ value: this.summary.uncertainRequests || 0, name: 'Uncertain' }}
                            ]
                        }}]
                    }});
                }},

                timeRangeToStart() {{
                    const now = new Date();
                    switch (this.filters.timeRange) {{
                        case '5m': return new Date(now - 5 * 60 * 1000);
                        case '15m': return new Date(now - 15 * 60 * 1000);
                        case '1h': return new Date(now - 60 * 60 * 1000);
                        case '6h': return new Date(now - 6 * 60 * 60 * 1000);
                        case '24h': return new Date(now - 24 * 60 * 60 * 1000);
                        default: return new Date(now - 24 * 60 * 60 * 1000);
                    }}
                }},

                timeRangeToBucket() {{
                    switch (this.filters.timeRange) {{
                        case '5m': return 10;
                        case '15m': return 30;
                        case '1h': return 60;
                        case '6h': return 300;
                        case '24h': return 600;
                        default: return 60;
                    }}
                }},

                async applyFilters() {{
                    const start = this.timeRangeToStart().toISOString();
                    const end = new Date().toISOString();
                    const bucket = this.timeRangeToBucket();

                    let detUrl = `{options.BasePath}/api/detections?limit=100&start=${{start}}&end=${{end}}`;
                    if (this.filters.riskBand) detUrl += `&riskBands=${{this.filters.riskBand}}`;
                    if (this.filters.classification === 'bot') detUrl += '&isBot=true';
                    if (this.filters.classification === 'human') detUrl += '&isBot=false';

                    const tsUrl = `{options.BasePath}/api/timeseries?bucket=${{bucket}}&start=${{start}}&end=${{end}}`;

                    const [detRaw, tsRaw] = await Promise.all([
                        fetch(detUrl).then(r => r.json()).catch(() => []),
                        fetch(tsUrl).then(r => r.json()).catch(() => [])
                    ]);

                    const data = toCamel(detRaw);
                    const timeseries = toCamel(tsRaw);
                    this.detections = data;
                    this.tabulatorTable?.setData(data);
                    this.updateTimeline(timeseries);

                    // Recompute local summary from filtered data
                    const bots = data.filter(d => d.isBot).length;
                    const humans = data.length - bots;
                    this.updateCharts();
                }},

                exportData(format) {{
                    window.location.href = `{options.BasePath}/api/export?format=${{format}}`;
                }}
            }};
        }}
    </script>

</body>
</html>";
    }
}
