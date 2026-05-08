# Pack-Driven Tab UX Design

**Goal:** Each registered `IMonitoringPack` automatically contributes a named tab to the dashboard, with no hardcoded pack names anywhere in the UI layer.

**Architecture:** `IMonitoringPack` owns its display name via `TabName`. The dashboard middleware discovers all registered packs at startup and passes a `PackTabInfo` list to the shell model. `Index.cshtml` loops the list to render tabs; the tab content is the existing generic `SbMetricsTabViewComponent` keyed by `pack.Id`.

**Tech Stack:** C# / ASP.NET Core, Razor views, existing `IMonitoringPack` / `IMetricSnapshotStore` / `SbMetricsTabViewComponent` infrastructure.

---

## Interface Change

Add `TabName` to `IMonitoringPack`:

```csharp
public interface IMonitoringPack
{
    string Id          { get; }
    string Name        { get; }
    string Description { get; }
    string TabName     { get; }   // display label in dashboard nav (e.g. "System")
    TimeSpan CollectionInterval { get; }
    IReadOnlyList<MeterCollectionGroup> MeterGroups { get; }
}
```

`AspNetMonitoringPack.TabName` returns `"System"`.

---

## Shell Model Change

Replace `bool MonitoringPackEnabled` with a list:

```csharp
public sealed record PackTabInfo(string Id, string TabName);

// In DashboardShellModel:
public IReadOnlyList<PackTabInfo> MonitoringPacks { get; init; }
    = Array.Empty<PackTabInfo>();
```

Helper on the model:

```csharp
public bool HasPackTabs => MonitoringPacks.Count > 0;
public bool IsPackTab(string tab) => MonitoringPacks.Any(p => p.Id == tab);
```

---

## Middleware Change

Inject `IEnumerable<IMonitoringPack>` into `StyloBotDashboardMiddleware` constructor and cache the pack list at startup:

```csharp
_packTabs = monitoringPacks
    .Select(p => new PackTabInfo(p.Id, p.TabName))
    .ToList();
```

Tab guard becomes:

```csharp
// Normalise unknown/disabled pack tabs back to overview
if (model.IsPackTab(tab) && !_packTabs.Any(p => p.Id == tab))
    tab = "overview";
```

The `bool _monitoringPackEnabled` field and `options.MonitoringPack.Enabled` check in the middleware are removed; the list being empty is the "disabled" signal.

> **RemoteClient mode:** In RemoteClient mode no `IMonitoringPack` is registered in DI - `_packTabs` will be empty and pack tabs won't appear. If RemoteClient should show tabs, the remote metadata endpoint must return pack info and the middleware must merge it. This is out of scope for now; document it as a known limitation.

---

## View Change

In `Index.cshtml`, replace the hardcoded `Metrics` tab entry with a loop:

```razor
@foreach (var pack in Model.MonitoringPacks)
{
    <li class="@(tab == pack.Id ? "tab tab-active" : "tab")">
        <a href="?tab=@pack.Id">@pack.TabName</a>
    </li>
}
```

Tab content switch adds a catch-all for any registered pack:

```razor
@{
    var activePack = Model.MonitoringPacks.FirstOrDefault(p => p.Id == tab);
}
@if (activePack is not null)
{
    <sb-metrics-tab pack-id="@activePack.Id" />
}
```

No pack names or IDs appear as string literals in the view.

---

## Config / Registration

`MonitoringPackOptions.Enabled` defaults to `true` - the System tab is on by default for all users. Set `Enabled: false` in appsettings to opt out:

```json
"StyloBot": {
  "Dashboard": {
    "MonitoringPack": { "Enabled": false }
  }
}
```

When `Enabled = false`, no `IMonitoringPack` is registered in DI, `_packTabs` is empty, and no pack tabs appear. No view or middleware code checks the flag directly.

Future packs (WordPress simulation pack, commercial network pack) implement `TabName` and are registered in DI; their tab appears automatically.

---

## What Does Not Change

- `SbMetricsTabViewComponent` - already generic on `packId`, no changes needed
- `IMetricSnapshotStore` - unchanged
- API endpoints - unchanged
- `MonitoringPackOptions` shape - unchanged (the `Enabled` flag still gates DI registration)
- Tab URL scheme - `?tab=<packId>` deep-links work as before

---

## Out of Scope

- RemoteClient mode pack tab discovery (future: metadata endpoint)
- Per-pack custom Razor views (all packs use the same metrics template for now)
- Tab ordering beyond DI registration order
