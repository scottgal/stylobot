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
                $"{d.RequestId},{d.Timestamp:O},{d.IsBot},{d.BotProbability},{d.Confidence}," +
                $"{d.RiskBand},{d.BotType},{d.BotName},{d.Action},{d.Method},{d.Path}," +
                $"{d.StatusCode},{d.ProcessingTimeMs}");
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

    <!-- HTMX -->
    <script src=""https://unpkg.com/htmx.org@1.9.10""></script>

    <!-- SignalR -->
    <script src=""https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.0/dist/browser/signalr.min.js""></script>

    <!-- ECharts -->
    <script src=""https://cdn.jsdelivr.net/npm/echarts@5.4.3/dist/echarts.min.js""></script>

    <!-- Tabulator -->
    <link href=""https://unpkg.com/tabulator-tables@6.2.1/dist/css/tabulator_midnight.min.css"" rel=""stylesheet"">
    <script src=""https://unpkg.com/tabulator-tables@6.2.1/dist/js/tabulator.min.js""></script>

    <style>
        body {{ font-family: 'Inter', sans-serif; }}
        .gradient-header {{
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
        }}
        .scrolling-signatures {{
            max-height: 300px;
            overflow-y: auto;
        }}
        .signature-item {{
            animation: slideIn 0.3s ease-out;
        }}
        @keyframes slideIn {{
            from {{ opacity: 0; transform: translateY(-10px); }}
            to {{ opacity: 1; transform: translateY(0); }}
        }}
    </style>
</head>
<body class=""bg-base-100"">

    <div x-data=""dashboardState()"" x-init=""init()"" class=""container mx-auto p-4"">

        <!-- Header -->
        <div class=""gradient-header rounded-lg shadow-lg p-6 mb-6"">
            <h1 class=""text-4xl font-bold text-white"">Stylobot Dashboard</h1>
            <p class=""text-white/80 mt-2"">Real-time bot detection monitoring</p>
        </div>

        <!-- Summary Cards -->
        <div class=""grid grid-cols-1 md:grid-cols-4 gap-4 mb-6"">
            <div class=""stat bg-base-200 rounded-lg shadow"">
                <div class=""stat-title"">Total Requests</div>
                <div class=""stat-value"" x-text=""summary.totalRequests"">0</div>
            </div>
            <div class=""stat bg-base-200 rounded-lg shadow"">
                <div class=""stat-title"">Bot Requests</div>
                <div class=""stat-value text-error"" x-text=""summary.botRequests"">0</div>
                <div class=""stat-desc"" x-text=""summary.botPercentage.toFixed(1) + '%'"">0%</div>
            </div>
            <div class=""stat bg-base-200 rounded-lg shadow"">
                <div class=""stat-title"">Human Requests</div>
                <div class=""stat-value text-success"" x-text=""summary.humanRequests"">0</div>
            </div>
            <div class=""stat bg-base-200 rounded-lg shadow"">
                <div class=""stat-title"">Unique Signatures</div>
                <div class=""stat-value"" x-text=""summary.uniqueSignatures"">0</div>
            </div>
        </div>

        <!-- Controls Bar -->
        <div class=""card bg-base-200 shadow-lg mb-6"">
            <div class=""card-body"">
                <h2 class=""card-title"">Filters</h2>
                <div class=""grid grid-cols-1 md:grid-cols-4 gap-4"">

                    <!-- Time Range -->
                    <div class=""form-control"">
                        <label class=""label""><span class=""label-text"">Time Range</span></label>
                        <select x-model=""filters.timeRange"" class=""select select-bordered"" @change=""applyFilters()"">
                            <option value=""5m"">Last 5 minutes</option>
                            <option value=""1h"">Last hour</option>
                            <option value=""24h"" selected>Last 24 hours</option>
                            <option value=""custom"">Custom</option>
                        </select>
                    </div>

                    <!-- Risk Bands -->
                    <div class=""form-control"">
                        <label class=""label""><span class=""label-text"">Risk Band</span></label>
                        <select x-model=""filters.riskBand"" class=""select select-bordered"" @change=""applyFilters()"">
                            <option value="""">All</option>
                            <option value=""VeryLow"">Very Low</option>
                            <option value=""Low"">Low</option>
                            <option value=""Medium"">Medium</option>
                            <option value=""High"">High</option>
                            <option value=""VeryHigh"">Very High</option>
                        </select>
                    </div>

                    <!-- Classification -->
                    <div class=""form-control"">
                        <label class=""label""><span class=""label-text"">Classification</span></label>
                        <select x-model=""filters.classification"" class=""select select-bordered"" @change=""applyFilters()"">
                            <option value="""">All</option>
                            <option value=""bot"">Bots</option>
                            <option value=""human"">Humans</option>
                        </select>
                    </div>

                    <!-- Export -->
                    <div class=""form-control"">
                        <label class=""label""><span class=""label-text"">Export</span></label>
                        <div class=""btn-group"">
                            <button @click=""exportData('json')"" class=""btn btn-sm btn-outline"">JSON</button>
                            <button @click=""exportData('csv')"" class=""btn btn-sm btn-outline"">CSV</button>
                        </div>
                    </div>
                </div>
            </div>
        </div>

        <!-- Charts Row -->
        <div class=""grid grid-cols-1 lg:grid-cols-2 gap-6 mb-6"">
            <!-- Risk Timeline -->
            <div class=""card bg-base-200 shadow-lg"">
                <div class=""card-body"">
                    <h2 class=""card-title"">Detection Timeline</h2>
                    <div id=""riskTimelineChart"" style=""height: 300px;""></div>
                </div>
            </div>

            <!-- Bot/Human Split -->
            <div class=""card bg-base-200 shadow-lg"">
                <div class=""card-body"">
                    <h2 class=""card-title"">Classification Distribution</h2>
                    <div id=""classificationChart"" style=""height: 300px;""></div>
                </div>
            </div>
        </div>

        <!-- Scrolling Signatures -->
        <div class=""card bg-base-200 shadow-lg mb-6"">
            <div class=""card-body"">
                <h2 class=""card-title"">Live Signatures Feed</h2>
                <div class=""scrolling-signatures"">
                    <template x-for=""sig in signatures"" :key=""sig.signatureId"">
                        <div class=""signature-item alert mb-2""
                             :class=""{{
                                'alert-error': sig.riskBand === 'VeryHigh' || sig.riskBand === 'High',
                                'alert-warning': sig.riskBand === 'Medium',
                                'alert-info': sig.riskBand === 'Low' || sig.riskBand === 'VeryLow'
                             }}"">
                            <div>
                                <span class=""font-mono text-sm"" x-text=""sig.primarySignature""></span>
                                <span class=""badge badge-sm"" x-text=""sig.riskBand""></span>
                                <span class=""text-xs ml-2"" x-text=""'Hits: ' + sig.hitCount""></span>
                                <template x-if=""sig.botName"">
                                    <span class=""text-xs ml-2"" x-text=""sig.botName""></span>
                                </template>
                            </div>
                        </div>
                    </template>
                </div>
            </div>
        </div>

        <!-- Detections Grid -->
        <div class=""card bg-base-200 shadow-lg"">
            <div class=""card-body"">
                <h2 class=""card-title"">Detections Grid</h2>
                <div id=""detectionsTable""></div>
            </div>
        </div>

    </div>

    <script>
        // Alpine.js state management
        function dashboardState() {{
            return {{
                connection: null,
                summary: {{
                    totalRequests: 0,
                    botRequests: 0,
                    humanRequests: 0,
                    uniqueSignatures: 0,
                    botPercentage: 0
                }},
                signatures: [],
                detections: [],
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
                        this.signatures.unshift(signature);
                        if (this.signatures.length > 50) this.signatures.pop();
                    }});

                    this.connection.on('BroadcastSummary', (summary) => {{
                        this.summary = summary;
                        this.updateCharts();
                    }});

                    this.connection.start()
                        .then(() => console.log('SignalR connected'))
                        .catch(err => console.error('SignalR error:', err));
                }},

                initCharts() {{
                    // Risk Timeline Chart
                    this.riskChart = echarts.init(document.getElementById('riskTimelineChart'));
                    this.riskChart.setOption({{
                        tooltip: {{ trigger: 'axis' }},
                        legend: {{ data: ['Bots', 'Humans'] }},
                        xAxis: {{ type: 'time' }},
                        yAxis: {{ type: 'value' }},
                        series: [
                            {{ name: 'Bots', type: 'line', data: [], smooth: true, areaStyle: {{}}, color: '#ef4444' }},
                            {{ name: 'Humans', type: 'line', data: [], smooth: true, areaStyle: {{}}, color: '#10b981' }}
                        ]
                    }});

                    // Classification Chart
                    this.classificationChart = echarts.init(document.getElementById('classificationChart'));
                    this.classificationChart.setOption({{
                        tooltip: {{ trigger: 'item' }},
                        series: [{{
                            type: 'pie',
                            radius: '50%',
                            data: [
                                {{ value: 0, name: 'Bots', itemStyle: {{ color: '#ef4444' }} }},
                                {{ value: 0, name: 'Humans', itemStyle: {{ color: '#10b981' }} }},
                                {{ value: 0, name: 'Uncertain', itemStyle: {{ color: '#f59e0b' }} }}
                            ]
                        }}]
                    }});
                }},

                initTable() {{
                    this.tabulatorTable = new Tabulator('#detectionsTable', {{
                        data: [],
                        layout: 'fitColumns',
                        pagination: true,
                        paginationSize: 20,
                        columns: [
                            {{ title: 'Time', field: 'timestamp', formatter: (cell) => new Date(cell.getValue()).toLocaleTimeString() }},
                            {{ title: 'Type', field: 'isBot', formatter: (cell) => cell.getValue() ? 'Bot' : 'Human' }},
                            {{ title: 'Risk', field: 'riskBand' }},
                            {{ title: 'Method', field: 'method' }},
                            {{ title: 'Path', field: 'path' }},
                            {{ title: 'Action', field: 'action' }},
                            {{ title: 'Probability', field: 'botProbability', formatter: (cell) => (cell.getValue() * 100).toFixed(1) + '%' }}
                        ]
                    }});
                }},

                async loadInitialData() {{
                    const summary = await fetch('{options.BasePath}/api/summary').then(r => r.json());
                    this.summary = summary;

                    const detections = await fetch('{options.BasePath}/api/detections?limit=100').then(r => r.json());
                    this.detections = detections;
                    this.tabulatorTable.setData(detections);

                    const signatures = await fetch('{options.BasePath}/api/signatures?limit=50').then(r => r.json());
                    this.signatures = signatures;

                    this.updateCharts();
                }},

                updateCharts() {{
                    // Update pie chart
                    this.classificationChart.setOption({{
                        series: [{{
                            data: [
                                {{ value: this.summary.botRequests, name: 'Bots' }},
                                {{ value: this.summary.humanRequests, name: 'Humans' }},
                                {{ value: this.summary.uncertainRequests, name: 'Uncertain' }}
                            ]
                        }}]
                    }});
                }},

                applyFilters() {{
                    // Reload detections with filters
                    let url = '{options.BasePath}/api/detections?limit=100';
                    if (this.filters.riskBand) url += `&riskBands=${{this.filters.riskBand}}`;
                    if (this.filters.classification === 'bot') url += '&isBot=true';
                    if (this.filters.classification === 'human') url += '&isBot=false';

                    fetch(url).then(r => r.json()).then(data => {{
                        this.detections = data;
                        this.tabulatorTable.setData(data);
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