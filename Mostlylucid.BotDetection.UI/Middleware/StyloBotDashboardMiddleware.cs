using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
        var html = DashboardHtmlTemplate.GetHtml(_options);
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
    public static string GetHtml(StyloBotDashboardOptions options)
    {
        return $@"<!DOCTYPE html>
<html lang=""en"" data-theme=""dark"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Stylobot Dashboard</title>

    <!-- Tailwind + DaisyUI -->
    <link href=""https://cdn.jsdelivr.net/npm/daisyui@4.6.0/dist/full.min.css"" rel=""stylesheet"" type=""text/css"" />
    <script src=""https://cdn.tailwindcss.com""></script>

    <!-- Alpine.js -->
    <script defer src=""https://cdn.jsdelivr.net/npm/alpinejs@3.13.5/dist/cdn.min.js""></script>

    <!-- SignalR -->
    <script src=""https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.0/dist/browser/signalr.min.js""></script>

    <!-- ECharts -->
    <script src=""https://cdn.jsdelivr.net/npm/echarts@5.4.3/dist/echarts.min.js""></script>

    <!-- Tabulator -->
    <link href=""https://unpkg.com/tabulator-tables@6.2.1/dist/css/tabulator_midnight.min.css"" rel=""stylesheet"">
    <script src=""https://unpkg.com/tabulator-tables@6.2.1/dist/js/tabulator.min.js""></script>

    <!-- Boxicons -->
    <link href=""https://unpkg.com/boxicons@2.1.4/css/boxicons.min.css"" rel=""stylesheet"">

    <!-- Google Fonts -->
    <link rel=""preconnect"" href=""https://fonts.googleapis.com"">
    <link href=""https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=Raleway:wght@800;900&display=swap"" rel=""stylesheet"">

    <style>
        body {{ font-family: 'Inter', sans-serif; }}
        .brand-header {{
            background: linear-gradient(135deg, #2d3748 0%, #1a202c 50%, #2d3748 100%);
            border-bottom: 3px solid #5BA3A3;
        }}
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
            width: 8px; height: 8px; border-radius: 50%; background: #5BA3A3;
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

        /* Tabulator dark mode overrides to match DaisyUI dark theme */
        .tabulator {{
            background-color: oklch(var(--b2, 0.232 0.013 285.75)) !important;
            border: 1px solid oklch(var(--b3, 0.211 0.012 285.75)) !important;
            border-radius: 0.5rem;
            font-size: 0.8rem;
        }}
        .tabulator .tabulator-header {{
            background-color: oklch(var(--b3, 0.211 0.012 285.75)) !important;
            border-bottom: 2px solid #5BA3A3 !important;
        }}
        .tabulator .tabulator-header .tabulator-col {{
            background-color: transparent !important;
            border-right-color: oklch(var(--b1, 0.253 0.015 285.75)) !important;
        }}
        .tabulator .tabulator-header .tabulator-col .tabulator-col-content .tabulator-col-title {{
            color: oklch(var(--bc, 0.841 0.02 285.75)) !important;
            font-weight: 600;
            font-size: 0.7rem;
            text-transform: uppercase;
            letter-spacing: 0.05em;
        }}
        .tabulator .tabulator-tableholder .tabulator-table .tabulator-row {{
            background-color: transparent !important;
            border-bottom: 1px solid oklch(var(--b3, 0.211 0.012 285.75) / 0.5) !important;
        }}
        .tabulator .tabulator-tableholder .tabulator-table .tabulator-row:hover {{
            background-color: oklch(var(--b3, 0.211 0.012 285.75) / 0.6) !important;
        }}
        .tabulator .tabulator-tableholder .tabulator-table .tabulator-row .tabulator-cell {{
            border-right-color: transparent !important;
            color: oklch(var(--bc, 0.841 0.02 285.75) / 0.85) !important;
            padding: 6px 8px;
        }}
        .tabulator .tabulator-footer {{
            background-color: oklch(var(--b3, 0.211 0.012 285.75)) !important;
            border-top: 1px solid oklch(var(--b3, 0.211 0.012 285.75)) !important;
        }}
        .tabulator .tabulator-footer .tabulator-page {{
            color: oklch(var(--bc, 0.841 0.02 285.75) / 0.7) !important;
            border-color: oklch(var(--b3, 0.211 0.012 285.75)) !important;
            background-color: transparent !important;
            border-radius: 0.25rem;
            margin: 0 2px;
        }}
        .tabulator .tabulator-footer .tabulator-page.active {{
            background-color: #5BA3A3 !important;
            color: white !important;
            border-color: #5BA3A3 !important;
        }}
        .tabulator .tabulator-footer .tabulator-page:hover:not(.active) {{
            background-color: oklch(var(--b2, 0.232 0.013 285.75)) !important;
        }}
        .tabulator .tabulator-footer .tabulator-page-size {{
            background-color: oklch(var(--b2, 0.232 0.013 285.75)) !important;
            color: oklch(var(--bc, 0.841 0.02 285.75) / 0.7) !important;
            border-color: oklch(var(--b3, 0.211 0.012 285.75)) !important;
            border-radius: 0.25rem;
        }}
    </style>
</head>
<body class=""bg-base-100"">

    <div x-data=""dashboardState()"" x-init=""init()"" class=""min-h-screen"">

        <!-- Header -->
        <div class=""brand-header p-6 mb-6"">
            <div class=""container mx-auto flex items-center justify-between"">
                <div class=""flex items-center gap-4"">
                    <div>
                        <h1 class=""text-3xl font-bold text-white"" style=""font-family: 'Raleway', sans-serif;"">
                            <span style=""color: #6b7280; font-style: italic;"">stylo</span><span>bot</span>
                        </h1>
                        <p class=""text-sm text-white/60 mt-1"">Real-time bot detection dashboard</p>
                    </div>
                </div>
                <div class=""flex items-center gap-3"">
                    <span class=""live-dot""></span>
                    <span class=""text-sm font-medium"" :class=""signalrConnected ? 'text-green-400' : 'text-red-400'""
                          x-text=""signalrConnected ? 'Connected' : 'Reconnecting...'""></span>
                    <a href=""/"" class=""btn btn-ghost btn-sm text-white/70 gap-1""><i class=""bx bx-home""></i> Home</a>
                    <a href=""/Home/LiveDemo"" class=""btn btn-sm text-white/90 gap-1"" style=""border-color: #5BA3A3; background: rgba(91,163,163,0.15);""><i class=""bx bx-broadcast""></i> Live Demo</a>
                    <a href=""https://github.com/scottgal/mostlylucid.stylobot"" target=""_blank"" class=""btn btn-ghost btn-sm text-white/70 gap-1""><i class=""bx bxl-github""></i> GitHub</a>
                </div>
            </div>
        </div>

        <div class=""container mx-auto px-4 pb-8"">

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
                    <h2 class=""card-title text-base"">Detection Timeline</h2>
                    <div id=""riskTimelineChart"" style=""height: 280px;""></div>
                </div>
            </div>
            <div class=""card bg-base-200 shadow-lg"">
                <div class=""card-body"">
                    <h2 class=""card-title text-base"">Classification Distribution</h2>
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
                            <div class=""signature-item flex items-center gap-3 px-3 py-2 mb-1 rounded-lg bg-base-300/50 hover:bg-base-300 transition-colors"">
                                <span class=""font-mono text-xs opacity-70"" x-text=""(sig.primarySignature || sig.signatureId || '-').substring(0, 12) + '...'""></span>
                                <span class=""badge badge-xs""
                                      :class=""{{
                                          'badge-error': sig.riskBand === 'VeryHigh' || sig.riskBand === 'High',
                                          'badge-warning': sig.riskBand === 'Medium',
                                          'badge-info': sig.riskBand === 'Low',
                                          'badge-success': sig.riskBand === 'VeryLow'
                                      }}""
                                      x-text=""sig.riskBand || 'Unknown'""></span>
                                <template x-if=""sig.botName"">
                                    <span class=""badge badge-ghost badge-xs"" x-text=""sig.botName""></span>
                                </template>
                                <template x-if=""sig.isKnownBot && !sig.botName"">
                                    <span class=""badge badge-ghost badge-xs"">Known Bot</span>
                                </template>
                                <span class=""text-xs opacity-60 ml-auto"" x-text=""'hits: ' + (sig.hitCount || 0)""></span>
                                <span class=""text-xs opacity-60"" x-text=""sig.factorCount ? (sig.factorCount + ' factors') : ''""></span>
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
                <h2 class=""card-title text-base"">Detections Grid</h2>
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

                init() {{
                    this.initSignalR();
                    this.initCharts();
                    this.initTable();
                    this.loadInitialData();
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

                    this.connection.onclose(() => {{ this.signalrConnected = false; }});
                    this.connection.onreconnecting(() => {{ this.signalrConnected = false; }});
                    this.connection.onreconnected(() => {{ this.signalrConnected = true; }});

                    this.connection.start()
                        .then(() => {{ this.signalrConnected = true; }})
                        .catch(err => console.error('SignalR error:', err));
                }},

                initCharts() {{
                    const darkText = '#a0aec0';
                    this.riskChart = echarts.init(document.getElementById('riskTimelineChart'));
                    this.riskChart.setOption({{
                        tooltip: {{ trigger: 'axis' }},
                        legend: {{ data: ['Bots', 'Humans'], textStyle: {{ color: darkText }} }},
                        grid: {{ left: 40, right: 20, top: 40, bottom: 30 }},
                        xAxis: {{ type: 'time', axisLabel: {{ color: darkText }}, axisLine: {{ lineStyle: {{ color: '#4a5568' }} }} }},
                        yAxis: {{ type: 'value', axisLabel: {{ color: darkText }}, splitLine: {{ lineStyle: {{ color: '#2d3748' }} }} }},
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
                            label: {{ color: darkText }},
                            data: [
                                {{ value: 0, name: 'Bots', itemStyle: {{ color: '#ef4444' }} }},
                                {{ value: 0, name: 'Humans', itemStyle: {{ color: '#86B59C' }} }},
                                {{ value: 0, name: 'Uncertain', itemStyle: {{ color: '#DAA564' }} }}
                            ]
                        }}]
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
                            {{ title: 'Signature', field: 'primarySignature', width: 105, formatter: (cell) => {{
                                const sig = cell.getValue();
                                if (!sig) return '<span style=""color:#6b7280"">-</span>';
                                const color = sigColor(sig);
                                const short = sig.substring(0, 8);
                                return `<span style=""display:inline-flex;align-items:center;gap:4px""><span style=""width:8px;height:8px;border-radius:50%;background:${{color}};display:inline-block""></span><code style=""color:${{color}};font-size:0.75rem"">${{short}}</code></span>`;
                            }},
                            tooltip: (e, cell) => {{
                                const sig = cell.getValue();
                                return sig ? `Client Signature: ${{sig}}` : '';
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