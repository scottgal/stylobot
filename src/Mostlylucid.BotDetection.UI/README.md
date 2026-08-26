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
