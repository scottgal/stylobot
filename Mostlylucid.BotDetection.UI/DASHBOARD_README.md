# StyloBot Dashboard

Real-time bot detection monitoring dashboard with SignalR live updates, interactive world map, country analytics, bot network cluster visualization, user agent breakdown, and full detection event drill-down.

## Features

- **Real-time Updates**: SignalR-powered live feed — detections, signatures, clusters, and summary stats update instantly
- **Interactive World Map**: jsvectormap with countries colored by bot rate (green to red) and markers sized by request volume
- **Top Bots Panel**: Ranked bot list from `VisitorListCache` — shared data source with the home page, updated in real-time via SignalR
- **Countries Tab**: Country-level bot rates, reputation scores, request volumes, geographic threat intelligence
- **Clusters Tab**: Leiden-detected bot networks with similarity scores, campaign analysis, and cluster membership
- **User Agents Tab**: UA family aggregation with category badges (Browser/Search/AI/Tool), version distribution, country per UA
- **Visitors Tab**: Live signature feed with risk bands, bot probability sparklines, narrative explanations
- **Detections Tab**: Full event log with per-detector contributions, signal breakdown, and evidence chain
- **Signature Drill-Down**: Click any signature for detailed view with country pin map, detection history, and signal analysis
- **Dark Mode**: Full dark/light theme support with theme-aware map colors
- **Export**: Download detections as CSV or JSON
- **Embed Mode**: `?embed=1` hides the brand header for iframe embedding

## Quick Start

### 1. Install the Package

```bash
dotnet add package Mostlylucid.BotDetection.UI
```

### 2. Configure in `Program.cs`

```csharp
using Mostlylucid.BotDetection.UI.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBotDetection();
builder.Services.AddStyloBotDashboard(options =>
{
    options.BasePath = "/stylobot";
});

var app = builder.Build();
app.UseBotDetection();
app.UseRouting();
app.UseStyloBotDashboard();
app.Run();
```

### 3. Access the Dashboard

Navigate to: `https://your-app.com/stylobot`

## Configuration Options

### `StyloBotDashboardOptions`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BasePath` | `string` | `/stylobot` | Dashboard URL path |
| `HubPath` | `string` | `/stylobot/hub` | SignalR hub path |
| `Enabled` | `bool` | `true` | Enable/disable dashboard |
| `RequireAuthorizationPolicy` | `string?` | `null` | ASP.NET Core policy name |
| `AuthorizationFilter` | `Func<HttpContext, Task<bool>>?` | `null` | Custom auth function |
| `MaxEventsInMemory` | `int` | `1000` | In-memory event limit |
| `SummaryBroadcastIntervalSeconds` | `int` | `5` | Summary update frequency |
| `EnableSimulator` | `bool` | `false` | Enable test data generator |
| `SimulatorEventsPerSecond` | `int` | `2` | Sim event rate |

## Authorization

### Option 1: Custom Filter (Recommended)

```csharp
builder.Services.AddStyloBotDashboard(
    authFilter: async (context) =>
    {
        var allowedIps = new[] { "127.0.0.1", "::1" };
        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        return allowedIps.Contains(remoteIp);
    });
```

### Option 2: Policy-Based

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("DashboardAccess", policy =>
        policy.RequireRole("Admin"));
});

builder.Services.AddStyloBotDashboard(options =>
{
    options.RequireAuthorizationPolicy = "DashboardAccess";
});
```

### Option 3: Development Mode (Open Access)

```csharp
builder.Services.AddStyloBotDashboard(
    authFilter: async (_) => true  // WARNING: Insecure!
);
```

## Dashboard Tabs

### Overview (Home)

The default view shows:

- **Summary Cards**: Total requests, bot requests (with percentage), human requests, unique signatures
- **Detection Timeline**: ECharts area chart showing bot vs human requests over time (configurable bucket size)
- **Top Bots**: Ranked list of the most active detected bots with risk band badges, hit counts, and drill-down links
- **Your Detection**: Shows how the dashboard was classified (useful for verifying your own requests are human)

### Countries Tab

Geographic threat intelligence:

- **World Map**: Interactive jsvectormap with:
  - Countries colored by bot rate using a 3-stop green (0%) to amber (50%) to red (100%) gradient
  - Markers at country centroids sized by request volume (sqrt scale)
  - Hover tooltips showing country name, request count, and bot percentage
  - Zoom, pan, and scroll support
- **Country Table**: Sortable table with country code, name, total requests, bot rate, and reputation score
- Data sourced from `/_stylobot/api/countries`

### Clusters Tab

Bot network discovery using Leiden community detection:

- **Cluster List**: Each cluster shows member count, similarity score, and common characteristics
- **Campaign Analysis**: Identifies coordinated bot campaigns that share behavioral patterns
- Data sourced from `/_stylobot/api/clusters`
- Clusters require minimum traffic volume before detection triggers

### User Agents Tab

UA family-level analytics:

- **Top Stats**: Total UA families, browser count, bot count, tool count
- **Filter Pills**: All / Browsers / Search / AI / Tools
- **UA Table**: Sortable by any column:
  - UA Family name
  - Category badge (Browser, Search, AI, Tool, Unknown)
  - Total requests (with bar visualization)
  - Bot rate (color-coded green/amber/red)
  - Top version
  - Top country (flag emoji)
  - Average confidence
  - Last seen (relative time)
- **Detail Panel**: Click a UA row for:
  - Version distribution donut chart (ECharts)
  - Country breakdown bar chart
  - Bot vs human ratio for that specific UA
- Data sourced from `/_stylobot/api/useragents`

### Visitors Tab

Live signature feed:

- **Signature List**: All unique visitor signatures with risk band badges, bot probability, hit count, and narrative
- **Real-time Updates**: New signatures appear instantly via SignalR `BroadcastSignature`
- **Drill-Down**: Click any signature for a detailed view with:
  - Country pin map (small focused jsvectormap)
  - Detection history sparkline
  - Full signal breakdown
  - Per-detector contributions

### Detections Tab

Complete detection event log:

- **Event Table**: Time, bot type, risk band, method, path, action, probability
- **Signal Details**: Expand any detection to see all signals and per-detector evidence
- **Filters**: Filter by bot/human, risk band, bot type, time range
- **Export**: Download as CSV or JSON
- Data sourced from `/_stylobot/api/detections`

## API Endpoints

All endpoints are prefixed with `BasePath` (default `/_stylobot`). Rate-limited to 60 requests/min per IP (diagnostics: 10/min).

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/` | GET | Dashboard HTML page (server-side rendered) |
| `/api/summary` | GET | Aggregate statistics (total, bots, humans, rates, unique signatures) |
| `/api/detections` | GET | Recent detection events with filtering |
| `/api/signatures` | GET | Unique visitor signatures with enriched data |
| `/api/topbots` | GET | Top detected bots ranked by hit count (`?count=10`) |
| `/api/timeseries` | GET | Time-bucketed detection counts (`?bucket=60`) |
| `/api/countries` | GET | Country-level bot rates and reputation data |
| `/api/clusters` | GET | Leiden bot clusters with similarity scores |
| `/api/useragents` | GET | UA family aggregation with versions, countries, bot rates |
| `/api/sparkline/{sig}` | GET | Sparkline history for a specific signature |
| `/api/export` | GET | Export detections as CSV/JSON (`?format=csv`) |
| `/api/diagnostics` | GET | Comprehensive diagnostics snapshot (rate-limited: 10/min) |
| `/api/me` | GET | Current visitor's cached detection result |

### Query Parameters

#### `/api/detections`

```
?start=2026-01-01T00:00:00Z
&end=2026-01-01T23:59:59Z
&riskBands=High,VeryHigh
&isBot=true
&botType=Scraper
&path=/api
&limit=100
&offset=0
```

#### `/api/topbots`

```
?count=10   // Number of top bots to return (max 50)
```

#### `/api/useragents`

Returns all detected UA families aggregated from detection signals (`ua.browser`, `ua.browser_version`).

#### `/api/diagnostics`

Returns a comprehensive snapshot including summary, filter counts, top bots with sparkline histories, detections with per-detector contributions, and signatures.

Rate limit headers: `X-RateLimit-Limit`, `X-RateLimit-Remaining`. Returns HTTP 429 when exceeded.

## SignalR Hub

### Hub Path

Default: `/_stylobot/hub`

### Events

Clients receive these real-time broadcasts:

```javascript
// Individual detection events (batched client-side for performance)
connection.on('BroadcastDetection', (detection) => {
    // detection: { requestId, timestamp, isBot, botProbability, confidence,
    //              riskBand, botType, botName, action, path, method,
    //              primarySignature, countryCode, processingTimeMs,
    //              importantSignals, contributions }
});

// Signature updates (new or updated visitor fingerprints)
connection.on('BroadcastSignature', (signature) => {
    // signature: { primarySignature, hitCount, isBot, isKnownBot,
    //              botProbability, confidence, riskBand, botName, botType,
    //              action, countryCode, description, narrative, topReasons }
});

// Periodic summary statistics (every 5 seconds by default)
connection.on('BroadcastSummary', (summary) => {
    // summary: { totalRequests, botRequests, humanRequests,
    //            botRate, uniqueSignatures, avgProcessingTimeMs }
});

// Signature name/description updates (from LLM or narrative builder)
connection.on('BroadcastSignatureDescriptionUpdate', (signature, name, description) => {
    // Called when a signature gets a human-readable name or description
});

// Cluster updates (when Leiden detection runs)
connection.on('BroadcastClusters', (clusters) => {
    // clusters: array of bot network clusters with member signatures
});
```

## Embed Mode

Pass `?embed=1` to hide the brand header when embedding the dashboard in an iframe:

```html
<iframe src="/_stylobot?embed=1" class="w-full" style="min-height: 80vh;"></iframe>
```

The `X-Frame-Options: SAMEORIGIN` header is set automatically.

## Persistence-Only Mode (Gateway/Proxy)

Run detection on a gateway but serve the dashboard elsewhere:

```csharp
// Gateway — saves detections, no UI
builder.Services.AddBotDetection();
builder.Services.AddBotDetectionPersistence();
app.UseBotDetection();
app.UseBotDetectionPersistence();

// Dashboard host — serves UI, reads shared database
builder.Services.AddStyloBotDashboard();
builder.Services.AddStyloBotPostgreSQL(connectionString);
```

## Architecture

### Components

1. **SignalR Hub** (`StyloBotDashboardHub`) — Broadcasts real-time events to all connected clients
2. **Event Store** (`InMemoryDashboardEventStore`) — Circular buffer for recent events, thread-safe, replaceable
3. **Visitor List Cache** (`VisitorListCache`) — In-memory cache of all visitor signatures with ranking, filtering, sparklines
4. **Summary Broadcaster** (`DashboardSummaryBroadcaster`) — Background service broadcasting periodic stats
5. **Detection Broadcast Middleware** (`DetectionBroadcastMiddleware`) — Captures detections and pushes to SignalR + event store
6. **Dashboard Middleware** (`StyloBotDashboardMiddleware`) — Routes requests, serves HTML/API, handles auth + rate limiting

### Data Flow

```
HTTP Request → Bot Detection Pipeline → Detection Result
                                              ↓
                              DetectionBroadcastMiddleware
                              ↓                ↓
                        Event Store      VisitorListCache
                              ↓                ↓
                        SignalR Hub ← Summary Broadcaster
                              ↓
                     Dashboard Clients (Alpine.js)
                              ↓
              World Map · Charts · Tables · Live Feed
```

### Technology Stack

| Layer | Technology |
|-------|-----------|
| **Backend** | ASP.NET Core, SignalR (WebSockets) |
| **Frontend Framework** | Alpine.js (reactive state management) |
| **Styling** | DaisyUI + TailwindCSS (light/dark themes) |
| **Charts** | Apache ECharts (timeline, donut, bar charts) |
| **World Map** | jsvectormap (vector country outlines, markers) |
| **Interactivity** | HTMX (server-rendered partials where used) |
| **Build** | Vite (TypeScript bundling, HMR in development) |

## Custom Data Source

Implement `IDashboardEventStore` for custom storage:

```csharp
public class MyCustomEventStore : IDashboardEventStore
{
    // Fetch from your database, Redis, etc.
}

builder.Services.AddSingleton<IDashboardEventStore, MyCustomEventStore>();
```

For PostgreSQL + TimescaleDB persistence, use the `Mostlylucid.BotDetection.UI.PostgreSQL` package:

```csharp
builder.Services.AddStyloBotPostgreSQL(connectionString, options =>
{
    options.EnableTimescaleDB = true;
    options.RetentionDays = 90;
    options.CompressionAfter = TimeSpan.FromDays(7);
});
```

## Production Considerations

### Security

- **Always configure authorization** for production
- Use HTTPS for SignalR connections
- Consider IP whitelisting for internal tools
- API endpoints are rate-limited (60/min per IP, diagnostics 10/min)

### Performance

- Adjust `MaxEventsInMemory` based on traffic volume
- Use PostgreSQL + TimescaleDB for large-scale deployments
- Detection timeline chart auto-aggregates into time buckets
- SignalR detection events are batched client-side to prevent UI thrashing

### Monitoring

- All API endpoints return rate limit headers (`X-RateLimit-Limit`, `X-RateLimit-Remaining`)
- `/api/diagnostics` provides a comprehensive system snapshot
- SignalR connection status is shown in the dashboard UI footer

## Troubleshooting

### Dashboard Not Loading

1. Check `options.Enabled = true`
2. Verify `UseStyloBotDashboard()` is called after `UseRouting()`
3. Check authorization (try `authFilter: async (_) => true` temporarily)

### No Real-Time Updates

1. Check browser console for SignalR connection errors
2. Ensure WebSockets are allowed by your proxy/firewall/load balancer
3. Verify the SignalR hub path matches your `BasePath` configuration

### Countries Tab Empty

1. In Docker, internal IPs can't be geo-located — ensure upstream headers (`X-Country`, `CF-IPCountry`) are forwarded
2. Add GeoDetection contributor for local IP resolution
3. Country data requires actual traffic — won't show with simulator alone

### Clusters Tab Empty

Cluster detection requires minimum traffic volume before Leiden clustering triggers. Send varied traffic patterns to populate.

### World Map Not Rendering

1. Check browser console for JavaScript errors
2. Verify jsvectormap is included in the Vite build (`npm run build`)
3. The map requires country data from `/api/countries` — check that endpoint returns data

## License

Part of the Mostlylucid.BotDetection suite. [The Unlicense](https://unlicense.org/)
