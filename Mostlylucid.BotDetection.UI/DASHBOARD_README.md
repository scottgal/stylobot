# Stylobot Dashboard

Real-time bot detection monitoring dashboard with SignalR, HTMX, DaisyUI, ECharts, and Tabulator.

## Features

- **Real-time Updates**: SignalR-powered live feed of bot detection events
- **Interactive Grid**: Sortable, filterable detections table with Tabulator
- **Visualizations**: ECharts for timeline and distribution charts
- **Scrolling Signatures**: Live feed of unique bot signatures
- **HTMX-First**: Minimal JavaScript, server-rendered partials
- **Modern UI**: DaisyUI + Tailwind for beautiful, responsive design
- **Export**: Download detections as CSV or JSON
- **Configurable**: Flexible routing, auth, and feature toggles

## Quick Start

### 1. Install the Package

```bash
dotnet add package Mostlylucid.BotDetection.UI
```

### 2. Configure in `Program.cs`

```csharp
using Mostlylucid.BotDetection.UI.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add Stylobot Dashboard with authorization
builder.Services.AddStyloBotDashboard(
    authFilter: async (context) =>
    {
        // Your authorization logic here
        // Return true to allow, false to deny
        return context.User.IsInRole("Admin");
    },
    configure: options =>
    {
        options.BasePath = "/stylobot";           // Dashboard URL
        options.MaxEventsInMemory = 1000;         // Event history size
        options.EnableSimulator = true;           // Enable test data
        options.SimulatorEventsPerSecond = 2;     // Sim rate
    });

var app = builder.Build();

// Enable the dashboard
app.UseStyloBotDashboard();

app.Run();
```

### 3. Access the Dashboard

Navigate to: `https://your-app.com/stylobot`

## Configuration Options

### `StyloBotDashboardOptions`

| Property                          | Type                             | Default         | Description                |
|-----------------------------------|----------------------------------|-----------------|----------------------------|
| `BasePath`                        | `string`                         | `/stylobot`     | Dashboard URL path         |
| `HubPath`                         | `string`                         | `/stylobot/hub` | SignalR hub path           |
| `Enabled`                         | `bool`                           | `true`          | Enable/disable dashboard   |
| `RequireAuthorizationPolicy`      | `string?`                        | `null`          | ASP.NET Core policy name   |
| `AuthorizationFilter`             | `Func<HttpContext, Task<bool>>?` | `null`          | Custom auth function       |
| `MaxEventsInMemory`               | `int`                            | `1000`          | In-memory event limit      |
| `SummaryBroadcastIntervalSeconds` | `int`                            | `5`             | Summary update frequency   |
| `EnableSimulator`                 | `bool`                           | `false`         | Enable test data generator |
| `SimulatorEventsPerSecond`        | `int`                            | `2`             | Sim event rate             |

## Authorization

### Option 1: Custom Filter (Recommended)

```csharp
builder.Services.AddStyloBotDashboard(
    authFilter: async (context) =>
    {
        // IP whitelist
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

## Dashboard UI

### Summary Cards

- **Total Requests**: All requests processed
- **Bot Requests**: Detected bots (with percentage)
- **Human Requests**: Legitimate users
- **Unique Signatures**: Distinct bot signatures seen

### Filters

- **Time Range**: 5m, 1h, 24h, custom
- **Risk Band**: VeryLow, Low, Medium, High, VeryHigh
- **Classification**: All, Bots, Humans
- **Export**: JSON or CSV download

### Charts

- **Detection Timeline**: Bot vs Human requests over time
- **Classification Distribution**: Pie chart of bot/human/uncertain

### Live Signatures Feed

Scrolling list of unique bot signatures with:

- Primary signature hash (truncated)
- Risk band badge
- Hit count
- Bot name (if known)

### Detections Grid

Tabulator-powered grid with:

- Time, Type, Risk, Method, Path
- Action, Probability
- Sortable columns
- Client-side filtering
- Pagination

## API Endpoints

All endpoints are prefixed with `BasePath` (default `/stylobot`):

| Endpoint          | Method | Description                  |
|-------------------|--------|------------------------------|
| `/`               | GET    | Dashboard HTML page          |
| `/api/detections` | GET    | Get detection events         |
| `/api/signatures` | GET    | Get signature events         |
| `/api/summary`    | GET    | Get summary statistics       |
| `/api/timeseries` | GET    | Get time-series data         |
| `/api/export`     | GET    | Export detections (CSV/JSON) |

### Query Parameters

#### `/api/detections`

```
?start=2025-12-12T00:00:00Z
&end=2025-12-12T23:59:59Z
&riskBands=High,VeryHigh
&isBot=true
&path=/api
&highRiskOnly=true
&limit=100
&offset=0
```

#### `/api/export`

```
?format=csv  // or json
// + all detection filters
```

## SignalR Hub

### Hub Path

Default: `/stylobot/hub`

### Client Methods

Clients receive these broadcasts:

```javascript
connection.on('BroadcastDetection', (detection) => {
    // New detection event
    // detection: DashboardDetectionEvent
});

connection.on('BroadcastSignature', (signature) => {
    // New signature observation
    // signature: DashboardSignatureEvent
});

connection.on('BroadcastSummary', (summary) => {
    // Updated summary stats
    // summary: DashboardSummary
});
```

## Simulator Mode

Enable simulator to generate test data without real traffic:

```csharp
builder.Services.AddStyloBotDashboard(options =>
{
    options.EnableSimulator = true;
    options.SimulatorEventsPerSecond = 5;  // Higher rate
});
```

The simulator generates realistic:

- Bot and human detections
- Risk bands and actions
- Signatures with hit counts
- Various paths and methods

**TODO**: Integrate with [LLMApi](https://github.com/scottgal/LLMApi) for AI-powered test data generation.

## Customization

### Custom Styling

The dashboard uses DaisyUI themes. Override in your app:

```html
<html data-theme="light">  <!-- or "dark", "cupcake", etc. -->
```

### Custom Charts

Extend the dashboard by modifying the embedded HTML template in
`Middleware/StyloBotDashboardMiddleware.cs:DashboardHtmlTemplate.GetHtml()`.

### Custom Data Source

Implement `IDashboardEventStore` for custom storage:

```csharp
public class MyCustomEventStore : IDashboardEventStore
{
    // Fetch from your database, Redis, etc.
}

builder.Services.AddSingleton<IDashboardEventStore, MyCustomEventStore>();
```

## Architecture

### Components

1. **SignalR Hub** (`StyloBotDashboardHub`)
    - Broadcasts real-time events to clients
    - Manages client connections

2. **Event Store** (`InMemoryDashboardEventStore`)
    - Circular buffer for recent events
    - Thread-safe, configurable size
    - Can be replaced with persistent storage

3. **Summary Broadcaster** (`DashboardSummaryBroadcaster`)
    - Background service
    - Periodically calculates and broadcasts stats

4. **Simulator** (`DashboardSimulatorService`)
    - Generates test data
    - Only active when `EnableSimulator = true`

5. **Middleware** (`StyloBotDashboardMiddleware`)
    - Routes dashboard requests
    - Serves HTML and API endpoints
    - Handles authorization

### Data Flow

```
Bot Detection → Event Store → SignalR Hub → Dashboard Clients
                     ↓
              Summary Broadcaster → Hub → Clients
```

### Technology Stack

- **Backend**: ASP.NET Core, SignalR
- **Frontend**: HTMX, Alpine.js, DaisyUI, Tailwind
- **Charts**: Apache ECharts
- **Grid**: Tabulator
- **Transport**: SignalR (WebSockets)

## Production Considerations

### Security

- **Always configure authorization** for production
- Use HTTPS for SignalR connections
- Consider IP whitelisting for internal tools
- Validate all user inputs in custom filters

### Performance

- Adjust `MaxEventsInMemory` based on traffic
- Use persistent storage for large-scale deployments
- Consider Redis for distributed scenarios
- Monitor SignalR connection count

### Monitoring

The dashboard itself is a monitoring tool, but you should also:

- Log dashboard access
- Track SignalR connection metrics
- Monitor memory usage of event store

## Examples

### Minimal Setup (Dev)

```csharp
builder.Services.AddStyloBotDashboard(options =>
{
    options.EnableSimulator = true;
});
app.UseStyloBotDashboard();
```

### Production Setup

```csharp
builder.Services.AddStyloBotDashboard(
    authFilter: async (context) =>
    {
        if (!context.User.Identity?.IsAuthenticated ?? true)
            return false;

        var allowedRoles = new[] { "Admin", "SecurityTeam" };
        return allowedRoles.Any(r => context.User.IsInRole(r));
    },
    configure: options =>
    {
        options.BasePath = "/admin/bot-detection";
        options.MaxEventsInMemory = 5000;
        options.SummaryBroadcastIntervalSeconds = 10;
        options.EnableSimulator = false;
    });
```

### Custom Path

```csharp
builder.Services.AddStyloBotDashboard(options =>
{
    options.BasePath = "/internal/security/bots";
    options.HubPath = "/internal/security/bots/hub";
});
```

## Troubleshooting

### Dashboard Not Loading

1. Check `options.Enabled = true`
2. Verify `UseStyloBotDashboard()` is called after routing
3. Check authorization (try `authFilter: async (_) => true` temporarily)

### No Real-Time Updates

1. Verify SignalR hub is mapped: `endpoints.MapHub<StyloBotDashboardHub>(hubPath)`
2. Check browser console for SignalR errors
3. Ensure WebSockets are allowed (proxy/firewall)

### No Data Showing

1. Enable simulator: `options.EnableSimulator = true`
2. Check that events are being generated
3. Verify event store is registered: `IDashboardEventStore`

## Roadmap

- [ ] Integrate LLMApi for realistic test data
- [ ] HTMX partial updates for grid (reduce full page loads)
- [ ] Additional chart types (heatmaps, sparklines)
- [ ] Export to more formats (Excel, PDF)
- [ ] Signature drill-down (click to see all events)
- [ ] Alert rules (notify on high-risk patterns)
- [ ] Historical data playback
- [ ] Multi-tenant support

## License

Part of Mostlylucid.BotDetection suite.

## Support

- Documentation: [Insert URL]
- Issues: [Insert GitHub Issues URL]
- Examples: [Insert Examples URL]
