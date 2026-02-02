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
                    <a href=""https://github.com/scottgal/stylobot"" target=""_blank"" class=""btn btn-ghost btn-sm text-white/70"">GitHub</a>
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
                        <select x-model=""filters.timeRange"" class=""select select-bordered select-sm"" @change=""applyFilters()"">
                            <option value=""5m"">Last 5 min</option>
                            <option value=""1h"">Last hour</option>
                            <option value=""24h"" selected>Last 24h</option>
                        </select>
                    </div>
                    <div class=""form-control"">
                        <label class=""label py-0""><span class=""label-text text-xs"">Risk Band</span></label>
                        <select x-model=""filters.riskBand"" class=""select select-bordered select-sm"" @change=""applyFilters()"">
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
                        <select x-model=""filters.classification"" class=""select select-bordered select-sm"" @change=""applyFilters()"">
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
                        <template x-for=""sig in signatures"" :key=""sig.signatureId || sig.primarySignature"">
                            <div class=""signature-item flex items-center gap-3 px-3 py-2 mb-1 rounded-lg bg-base-300/50 hover:bg-base-300 transition-colors"">
                                <span class=""font-mono text-xs opacity-70"" x-text=""(sig.primarySignature || '').substring(0, 12) + '...'""></span>
                                <span class=""badge badge-xs""
                                      :class=""{{
                                          'badge-error': sig.riskBand === 'VeryHigh' || sig.riskBand === 'High',
                                          'badge-warning': sig.riskBand === 'Medium',
                                          'badge-info': sig.riskBand === 'Low',
                                          'badge-success': sig.riskBand === 'VeryLow'
                                      }}""
                                      x-text=""sig.riskBand""></span>
                                <template x-if=""sig.botName"">
                                    <span class=""badge badge-ghost badge-xs"" x-text=""sig.botName""></span>
                                </template>
                                <span class=""text-xs opacity-60 ml-auto"" x-text=""'x' + (sig.hitCount || 1)""></span>
                                <span class=""text-xs font-mono""
                                      :class=""(sig.botProbability || 0) >= 0.7 ? 'text-error' : (sig.botProbability || 0) >= 0.4 ? 'text-warning' : 'text-success'""
                                      x-text=""Math.round((sig.botProbability || 0) * 100) + '%'""></span>
                            </div>
                        </template>
                        <template x-if=""signatures.length === 0"">
                            <div class=""text-center text-base-content/50 py-8"">Waiting for signatures...</div>
                        </template>
                    </div>
                </div>
            </div>

            <!-- Top Bots Leaderboard (1 col) -->
            <div class=""card bg-base-200 shadow-lg"">
                <div class=""card-body"">
                    <h2 class=""card-title text-base"">Top Bot Types</h2>
                    <div class=""space-y-2"">
                        <template x-for=""(bot, i) in topBots"" :key=""i"">
                            <div class=""flex items-center gap-2"">
                                <span class=""text-xs font-bold w-5 text-center opacity-50"" x-text=""i + 1""></span>
                                <span class=""text-sm flex-1 truncate"" x-text=""bot.name || 'Unknown'""></span>
                                <span class=""badge badge-sm badge-error"" x-text=""bot.count""></span>
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
                        this.detections.unshift(detection);
                        if (this.detections.length > 100) this.detections.pop();
                        this.tabulatorTable?.setData(this.detections);
                    }});

                    this.connection.on('BroadcastSignature', (signature) => {{
                        const idx = this.signatures.findIndex(s => s.primarySignature === signature.primarySignature);
                        if (idx >= 0) {{ this.signatures[idx] = signature; }}
                        else {{ this.signatures.unshift(signature); }}
                        if (this.signatures.length > 50) this.signatures.pop();
                    }});

                    this.connection.on('BroadcastSummary', (summary) => {{
                        this.summary = {{ ...this.summary, ...summary }};
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
                            {{ title: 'Time', field: 'timestamp', width: 100, formatter: (cell) => new Date(cell.getValue()).toLocaleTimeString() }},
                            {{ title: 'Type', field: 'isBot', width: 80, formatter: (cell) => {{
                                const isBot = cell.getValue();
                                return `<span style=""color:${{isBot ? '#ef4444' : '#86B59C'}};font-weight:600"">${{isBot ? 'Bot' : 'Human'}}</span>`;
                            }} }},
                            {{ title: 'Risk', field: 'riskBand', width: 90, formatter: (cell) => {{
                                const v = cell.getValue();
                                const c = riskColors[v] || '#6b7280';
                                return `<span style=""color:${{c}};font-weight:500"">${{v}}</span>`;
                            }} }},
                            {{ title: 'Method', field: 'method', width: 70 }},
                            {{ title: 'Path', field: 'path', minWidth: 150 }},
                            {{ title: 'Action', field: 'action', width: 90 }},
                            {{ title: 'Prob', field: 'botProbability', width: 70, hozAlign: 'right', formatter: (cell) => (cell.getValue() * 100).toFixed(0) + '%' }},
                            {{ title: 'Time (ms)', field: 'processingTimeMs', width: 80, hozAlign: 'right', formatter: (cell) => (cell.getValue() || 0).toFixed(0) }}
                        ]
                    }});
                }},

                async loadInitialData() {{
                    try {{
                        const [summary, detections, signatures, timeseries] = await Promise.all([
                            fetch('{options.BasePath}/api/summary').then(r => r.json()),
                            fetch('{options.BasePath}/api/detections?limit=100').then(r => r.json()),
                            fetch('{options.BasePath}/api/signatures?limit=50').then(r => r.json()),
                            fetch('{options.BasePath}/api/timeseries?bucket=60').then(r => r.json()).catch(() => [])
                        ]);

                        this.summary = {{ ...this.summary, ...summary }};
                        this.detections = detections;
                        this.tabulatorTable.setData(detections);
                        this.signatures = signatures;
                        this.updateCharts();
                        this.updateTimeline(timeseries);
                        this.computeTopBots(detections);
                    }} catch (e) {{
                        console.error('Failed to load initial data:', e);
                    }}
                }},

                updateTimeline(timeseries) {{
                    if (!timeseries || !timeseries.length) return;
                    const botData = timeseries.map(t => [new Date(t.timestamp || t.Timestamp), t.botCount || t.BotCount || 0]);
                    const humanData = timeseries.map(t => [new Date(t.timestamp || t.Timestamp), t.humanCount || t.HumanCount || 0]);
                    this.riskChart.setOption({{
                        series: [
                            {{ data: botData }},
                            {{ data: humanData }}
                        ]
                    }});
                }},

                computeTopBots(detections) {{
                    const counts = {{}};
                    detections.filter(d => d.isBot).forEach(d => {{
                        const name = d.botName || d.botType || 'Unknown';
                        counts[name] = (counts[name] || 0) + 1;
                    }});
                    this.topBots = Object.entries(counts)
                        .map(([name, count]) => ({{ name, count }}))
                        .sort((a, b) => b.count - a.count)
                        .slice(0, 8);
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

                applyFilters() {{
                    let url = '{options.BasePath}/api/detections?limit=100';
                    if (this.filters.riskBand) url += `&riskBands=${{this.filters.riskBand}}`;
                    if (this.filters.classification === 'bot') url += '&isBot=true';
                    if (this.filters.classification === 'human') url += '&isBot=false';

                    fetch(url).then(r => r.json()).then(data => {{
                        this.detections = data;
                        this.tabulatorTable.setData(data);
                        this.computeTopBots(data);
                    }});
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