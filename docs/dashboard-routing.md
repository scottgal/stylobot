# Dashboard routing

The FOSS dashboard lives at `/{StyloBotDashboardOptions.BasePath}/...`
(default `/stylobot/`). The left nav is grouped into five sections;
URLs are real path segments, not query-string tabs.

## Top-section routes (FOSS)

| URL | Renders |
|---|---|
| `/{BasePath}/` | Overview (default) |
| `/{BasePath}/overview` | Overview partial |
| `/{BasePath}/activity` | Activity partial |
| `/{BasePath}/visitors` | Visitors partial |
| `/{BasePath}/endpoints` | Endpoints partial |
| `/{BasePath}/sessions` | Sessions partial |
| `/{BasePath}/threats` | Threats partial |
| `/{BasePath}/policies` | Policies partial |
| `/{BasePath}/configuration` | Configuration editor |
| `/{BasePath}/compliance` | Compliance (commercial gate) |
| `/{BasePath}/investigate` | Investigation root (commercial gate) |

Commercial-gated rows render an "upgrade to commercial" panel when
the request is not running under a commercial license.

## Pack routes

Packs register an `IDashboardPack` singleton and declare sub-rows:

```csharp
services.AddSingleton<IDashboardPack>(_ => new MyPackDashboard
{
    SubRows =
    [
        new("alpha", "Alpha", "MyPackAlphaView"),
        new("beta",  "Beta",  "MyPackBetaView"),
    ]
});
```

This yields:

| URL | Renders |
|---|---|
| `/{BasePath}/{packId}` | 301 to first sub-row |
| `/{BasePath}/{packId}/{subRowId}` | View component named by sub-row |

The pack header row in the nav routes to the first sub-row by
default. Each sub-row's view component is invoked via
`Component.InvokeAsync(subRow.ViewComponentName)`.

Sub-row-level license gating is the view component's responsibility:
inspect `IStyloBotLicenseGate` inside `InvokeAsync` and return an
empty / 402 view when the sub-feature isn't licensed.

## Back-compat

`?tab=X` URLs 301 to `/{BasePath}/X` and preserve every other
query parameter. The legacy `metrics` tab name redirects to the
first registered pack's id.

Legacy rows (`countries`, `identities`, `clusters`, `threat-intel`,
`useragents`, `routes`) still dispatch on their existing URL so
external bookmarks survive; they no longer appear in the nav.

## Hidden / commercial-only contract

- `DashboardRow.IsHidden = true` -- route resolves, nav skips.
- `DashboardRow.IsCommercialOnly = true` -- nav skips when
  `Model.IsCommercial` is false, route renders an upgrade panel.

## Internal layout

| Type | Path |
|---|---|
| `IDashboardPack` | `Mostlylucid.BotDetection.UI.Dashboard.IDashboardPack` |
| `DashboardSubRow` | record `(Id, Label, ViewComponentName)` |
| `DashboardGroup` | record `(Id, Label, Rows)` |
| `DashboardRow` | record `(Id, Label, PartialPath, IsCommercialOnly, IsHidden)` |
| `DashboardRowRef` | record `(Area, Sub?)` |
| `IDashboardRowRegistry` | DI-resolved; composes FOSS groups + DI packs |
| `DashboardRoutingHelpers` | pure parsers: `ParseRowRef`, `IsDashboardRowPath`, `StripTabParam` |
| Nav partial | `Views/StyloBot/Dashboard/_LeftNav.cshtml` |