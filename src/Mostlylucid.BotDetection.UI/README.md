# Mostlylucid.BotDetection.UI

**Real-time bot-detection dashboard + embeddable widgets for ASP.NET Core.** The dashboard for the **[StyloBot](https://stylo.bot)** detection engine (`Mostlylucid.BotDetection`): Traffic / Visitors / Site / Policies / Configuration, live SignalR updates, behavioural radar charts, a world threat map, Leiden cluster visualisation, and a policy-stack editor.

[![NuGet](https://img.shields.io/nuget/v/Mostlylucid.BotDetection.UI.svg)](https://www.nuget.org/packages/Mostlylucid.BotDetection.UI)
[![GitHub](https://img.shields.io/badge/GitHub-scottgal%2Fstylobot-blue)](https://github.com/scottgal/stylobot)

---

## Install

```bash
dotnet add package Mostlylucid.BotDetection.UI
```

The package depends on `Mostlylucid.BotDetection` (detection core) and `Mostlylucid.BotDetection.OpenApi` (the Routes tab's API catalog) — both resolve automatically.

**Optional packs** (install separately; the dashboard works without them):
- `Mostlylucid.BotDetection.PrometheusPack` — adds the meter-health tile + metrics surface to the overview.

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);

// Detection core + the real-time dashboard, correct middleware ordering.
builder.Services.AddBotDetection();                    // or AddStyloBot(dashboard => …)
builder.Services.AddStyloBotDashboard(builder.Configuration, options =>
{
    options.BasePath = "/stylobot";
    options.AllowUnauthenticatedAccess = true;          // dev only — gate this in production
});

// Optional: the Prometheus pack adds its meter-health tile.
builder.Services.AddPrometheusPack(opt => opt.Mode = PrometheusPackMode.Local);

var app = builder.Build();
app.UseRouting();
app.UseStyloBot();   // broadcast → detection → dashboard
app.Run();
```

The dashboard is served at `{BasePath}/traffic` (landing), `/visitors`, `/site`, `/policies`, `/configuration`.

## API-backed Traffic pilot

Set `UseApiTraffic = true` to replace only the Traffic page with the simpler MVC/HTMX/Alpine implementation. It reads `GET /api/v1/dashboard/traffic` from the backend API. The API owns SQLite; the remote viewer owns no database. The other pages and optional packs keep their existing behavior. The pilot is a standalone page rather than an embeddable fragment.

Register the remote source before the dashboard so the pilot inherits its backend URL and server-side API key:

```csharp
builder.Services.AddStyloBotDashboardRemote(new DashboardSourceOptions
{
    Pull = new()
    {
        Type = DashboardSourceType.Rest,
        Url = "https://your-backend.example/",
        ApiKey = builder.Configuration["DashboardSourceKey"]
    }
});
builder.Services.AddStyloBotDashboard(options =>
{
    options.BasePath = "/stylobot";
    options.UseApiTraffic = true;
    options.Auth.Mode = DashboardAuthMode.Login;
    options.Auth.Username = "admin";
    options.Auth.PasswordHash = builder.Configuration["DashboardPasswordHash"];
});
```

Use the existing dashboard login/policy configuration. For custom remote hosts, call `app.UseStaticFiles()`, `app.UseRouting()`, `app.UseMiddleware<StyloBotDashboardMiddleware>()`, then `app.MapStyloBotDashboardPages()`. `UseStyloBot()` maps the pilot automatically. `stylobot-ui` also maps it automatically; enable `StyloBot:Dashboard:UseApiTraffic` in configuration. Local or separately configured hosts can supply `TrafficApiUrl` and `TrafficApiKey` explicitly. `TrafficApiTimeoutSeconds` defaults to 10 and must be 1–120. Configuration changes require a restart.

The API requires the same `X-SB-Api-Key` authorization as the existing API. Query parameters are `window` (1h/6h/12h/24h/7d/30d/custom), optional ISO 8601 `from`/`to` for custom windows, repeated `domain`, `includeInternal` and `limit` (1–200). Windows are at most 30 days; all slices use the same window, domain and audience filters. Invalid queries return 400, storage failures return redacted 503 Problem Details with a trace reference, and successful empty windows return 200. Responses are not cached.

Apply refreshes the page through HTMX; browser history and ordinary GET navigation work without a separate client data store. A failed refresh preserves the last rendered snapshot and labels it stale. Initial failures display unavailable rather than zero counters. Automatic live refresh and migration of the remaining pages are subsequent steps.

For a local browser/outage harness:

```bash
dotnet run --project scripts/dashboard-harness/DashboardHarness.csproj -c Release
# Open http://127.0.0.1:5078/ops/traffic (3 seeded requests)
curl -X POST http://127.0.0.1:5078/harness/outage/true
# Apply: last successful data stays visible with a warning.
curl -X POST http://127.0.0.1:5078/harness/outage/false
# Apply: successful data replaces the stale view and clears the warning.
```

With Playwright CLI available, run `playwright-cli open http://127.0.0.1:5078/ops/traffic`, then `playwright-cli run-code --filename scripts/dashboard-harness/browser-check.js` to exercise filtering, Alpine, outage retention/recovery, network failure and history.

The harness binds to loopback, uses a temporary backend SQLite database and removes it on shutdown. Integration coverage lives in `ApiTrafficReliabilityTests`, `TrafficApiClientTests` and `DashboardAssemblyBoundaryTests`; the package consumer gate additionally proves an API-only publish has no UI assembly.

Persistence/options types retain their existing namespaces and are forwarded from the UI assembly. Reflection code that locates Razor views should use `typeof(IDashboardPack).Assembly`, not a persistence model's owning assembly. New backend-only integrations can reference Dashboard.Data; DTO-only consumers can reference Dashboard.Contracts without ASP.NET or the detection engine.

### Remaining migration

1. Give Visitors and Site their own bounded API snapshots and compare their results with the existing durable queries before enabling each page.
2. Move Policies and Configuration reads and writes to explicit API contracts, preserving authorization and CSRF protection. Keep credentials on the server.
3. Add one live refresh trigger with cancellation and coalescing; refresh HTML through HTMX rather than maintaining a second client data model. Preserve the last good view during outages.
4. Measure request count, query latency and rendered parity, then enable each page by default. Remove legacy rendering/materialization only when its replacement passes the same fixtures, package and browser gates. Keep public type forwarding for compatible releases.

## Embeddable widgets

The package ships Tag Helpers and View Components to embed detection results in your own views:

- `<sb-badge>`, `<sb-confidence>`, `<sb-gate>`, `<sb-honeypot>`, `<sb-human>`, `<sb-risk>`, `<sb-signal>`, `<sb-summary>` Tag Helpers
- View Components for visitor lists, endpoint stats, policy stacks and more

## Runtime dependencies

The dashboard persists to **SQLite** (zero-dependency, FOSS) and requires no external services. At composition time `AddStyloBotDashboard` validates that its required pack assemblies are present and fails fast with an actionable message if one is missing.

## Full documentation

- Detection engine: [`Mostlylucid.BotDetection`](https://www.nuget.org/packages/Mostlylucid.BotDetection)
- Product docs & recipes: [stylo.bot](https://stylo.bot) · [GitHub `src/Mostlylucid.BotDetection/docs`](https://github.com/scottgal/stylobot/tree/main/src/Mostlylucid.BotDetection/docs)

## License

AGPL-3.0-only. See [LICENSE](https://github.com/scottgal/stylobot/blob/main/LICENSE).
