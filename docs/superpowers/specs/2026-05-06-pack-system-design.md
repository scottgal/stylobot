# StyloBot Pack System Design

Date: 2026-05-06

## Summary

Packs are distributable units of StyloBot capability -- a zip file dropped into a directory that the runtime picks up at startup (FOSS) or live (commercial). Each pack can contribute detectors, reaction pack definitions, signal groups, pipeline hooks, and dashboard tabs. FOSS packs are entirely read-only: they can observe and display but cannot modify configuration or policies. Commercial packs can write: edit endpoint policies, modify configuration, and add reporting widgets with write capability. The Reaction Pack feature ships as the first built-in pack, demonstrating the full pattern.

---

## Pack Format (.stylopack)

A `.stylopack` file is a zip archive with a fixed layout:

```
my-pack-1.0.0.stylopack
├── manifest.yaml          # required
├── MyPack.dll             # optional (omit for YAML-only packs)
├── MyPack.deps.json       # optional (if DLL present)
└── README.md              # optional
```

**`manifest.yaml` schema:**

```yaml
name: reaction-packs
version: 1.0.0
description: Adaptive traffic shaping via upstream degradation signals
author: Stylobot
requires_tier: foss       # foss | commercial
min_core_version: 6.0.0
assembly: MyPack.dll      # optional
entry_type: Stylobot.Packs.ReactionPacks.ReactionPacksEntry  # optional, implements IStylobotPack
```

`requires_tier` is enforced at load time. If the running instance does not meet the required tier, the pack is skipped and a warning is logged. It is never silently ignored -- operators can see which packs were skipped and why in the pack registry dashboard panel.

---

## Core Interfaces

### IStylobotPack

The only interface a code-backed pack must implement:

```csharp
public interface IStylobotPack
{
    string Name { get; }
    string Version { get; }
    void ConfigureServices(IServiceCollection services, IPackCapabilities capabilities);
}
```

`IPackCapabilities` is the gating mechanism (see below). Packs call `capabilities.CanWrite` to know whether they should register writable services.

### IPackCapabilities

```csharp
public interface IPackCapabilities
{
    bool CanWrite { get; }   // false = FOSS, true = commercial
    string Tier { get; }     // "foss" | "commercial"
}
```

FOSS: `CanWrite = false`. Commercial: `CanWrite = true`. Packs that attempt to register write-capable services without `CanWrite` will have those services silently dropped by the guarded registration helpers (see `PackServiceCollection` below).

### IStylobotPreActionHook

Called by `BotDetectionMiddleware` after detection, before the action policy executes:

```csharp
public interface IStylobotPreActionHook
{
    ValueTask<string?> GetOverridePolicyAsync(string endpoint, string currentPolicy, CancellationToken ct);
}
```

Return `null` to leave the policy unchanged. Multiple hooks resolve via priority: highest priority wins non-null, ties broken by most restrictive policy severity.

### IStylobotPostResponseHook

Called in the middleware `finally` block after the response is sent:

```csharp
public interface IStylobotPostResponseHook
{
    ValueTask OnResponseCompletedAsync(ResponseContext context, CancellationToken ct);
}
```

`ResponseContext` carries status code, latency, endpoint path, and the resolved action policy name.

### IDashboardTab

Packs register dashboard tabs via this interface:

```csharp
public interface IDashboardTab
{
    string TabId { get; }
    string DisplayName { get; }
    string PartialRoute { get; }  // e.g. "/_stylobot/packs/reaction-packs/tab"
    int Order { get; }
    bool RequiresWrite { get; }   // hides tab on FOSS if true
}
```

The dashboard shell renders all registered tabs from `IEnumerable<IDashboardTab>`. Tabs with `RequiresWrite = true` are hidden on FOSS instances.

---

## FOSS vs Commercial Capabilities

**FOSS packs can contribute (read-only):**

| Contribution | Mechanism |
|---|---|
| New detectors | `services.AddSingleton<IContributingDetector, MyDetector>()` |
| Reaction pack definitions (YAML) | Embedded resource loaded by `IReactionPackLoader` |
| Signal group definitions (YAML) | Embedded resource loaded by `ISignalGroupRegistry` |
| Pipeline hooks (observe only) | `IStylobotPostResponseHook` (no mutation) |
| Dashboard tabs (view-only) | `IDashboardTab` with `RequiresWrite = false` |
| Pre-action hooks (redirect policy read) | `IStylobotPreActionHook` returning non-null only for escalation, never config changes |

**Commercial packs additionally contribute:**

| Contribution | Mechanism |
|---|---|
| Endpoint policy configuration | `IEndpointPolicyContributor` -- gated behind `CanWrite` |
| Configuration modifications | `IPackConfigContributor` -- gated behind `CanWrite` |
| Dashboard widgets with write capability | `IDashboardTab` with `RequiresWrite = true` |
| Reporting widgets | Additional `IDashboardTab` registrations |
| Live hot-reload | FileSystemWatcher + `PackRegistry<T>` mutation (commercial pack loader) |

**Hard constraint enforcement:** The `PackServiceCollection` wrapper (used inside `ConfigureServices`) intercepts any registration of `IEndpointPolicyContributor` or `IPackConfigContributor` and throws `PackCapabilityException` if `CanWrite = false`. This is a fail-fast: FOSS pack authors discover the constraint at development time, not silently at runtime.

---

## PackRegistry\<T\>

Standard DI containers are frozen at `Build()`. To support runtime add/remove without container rebuild, packs register through a mutable `PackRegistry<T>`:

```csharp
public sealed class PackRegistry<T> : IEnumerable<T>
{
    private readonly ConcurrentBag<T> _items = new();
    public void Add(T item) => _items.Add(item);
    public void Clear() => _items.Clear();
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
}
```

At startup, core registers:
- `PackRegistry<IContributingDetector>` (singleton)
- `PackRegistry<IStylobotPreActionHook>` (singleton)
- `PackRegistry<IStylobotPostResponseHook>` (singleton)
- `PackRegistry<IDashboardTab>` (singleton)

Pack loader appends to these registries after the host is built. FOSS: populated once. Commercial: FileSystemWatcher re-loads packs and updates registry contents.

Services that consume these use `IEnumerable<T>` from DI, which resolves to the registry (since `PackRegistry<T>` implements `IEnumerable<T>`).

---

## Pack Loading

### Startup (FOSS)

`PackLoader` is a hosted service that runs once at startup:

1. Scans the configured packs directory (default: `{ContentRoot}/packs/`).
2. For each `.stylopack` file: reads `manifest.yaml`, checks `requires_tier` against active license.
3. If pack has an assembly: loads it into a new `AssemblyLoadContext` (isolated, non-collectible on FOSS).
4. Instantiates the `entry_type`, calls `ConfigureServices` with a `PackServiceCollection` wrapping a fresh `ServiceCollection`.
5. For each registration: resolves the service and adds it to the appropriate `PackRegistry<T>`.
6. Logs the result: loaded, skipped (tier), or failed (exception).

### Live Reload (Commercial)

Commercial layer adds a `FileSystemWatcher` on the packs directory. On `.stylopack` creation/modification:

1. Unload the old `AssemblyLoadContext` (collectible contexts in commercial).
2. Repeat the startup load sequence.
3. Replace the registry entries for this pack's contributions.
4. No restart required.

The `PackRegistry<T>` holds `PackEntry<T>` internally (with pack name as key) to enable targeted replacement.

---

## Middleware Integration

`BotDetectionMiddleware` changes to use the hook interfaces instead of directly depending on reaction pack types:

**Before action policy lookup:**
```csharp
var overridePolicy = null as string;
foreach (var hook in _preActionHooks)
{
    overridePolicy = await hook.GetOverridePolicyAsync(endpoint, resolvedPolicy, ct);
    if (overridePolicy != null) { resolvedPolicy = overridePolicy; break; }
}
```

**In finally block:**
```csharp
var ctx = new ResponseContext(statusCode, latencyMs, endpoint, resolvedPolicy);
foreach (var hook in _postResponseHooks)
    await hook.OnResponseCompletedAsync(ctx, ct);
```

`_preActionHooks` and `_postResponseHooks` are `IEnumerable<IStylobotPreActionHook>` / `IEnumerable<IStylobotPostResponseHook>` injected via constructor. Since they resolve to the `PackRegistry<T>`, packs loaded after startup are automatically included.

---

## Reaction Packs as the Model Pack

The Reaction Packs feature (already implemented in Tasks 1-9) is refactored to use the pack interfaces:

- `ReactionPackContext` implements `IStylobotPreActionHook` (returns override policy)
- `DegradationAtom` implements `IStylobotPostResponseHook` (records response into rolling windows)
- Both are registered via `PackRegistry<T>` (directly in core startup for the built-in pack)

The built-in pack is the only pack that does NOT live in a `.stylopack` file -- it is compiled into `Mostlylucid.BotDetection` and registered in `ServiceCollectionExtensions`. External packs follow the zip format.

---

## Dashboard Integration

The `/_stylobot` shell reads `IEnumerable<IDashboardTab>` to build the tab bar dynamically. FOSS hides tabs where `RequiresWrite = true`.

The pack registry panel (always shown, built into core) displays:
- Loaded packs: name, version, tier, status (active/failed/skipped)
- Skipped packs: name, required tier, reason
- Commercial only: reload button per pack

This panel is the operator's view into what the pack system is doing.

---

## Directory Layout (New Files)

| File | Purpose |
|---|---|
| `BotDetection/Packs/IStylobotPack.cs` | Pack entry interface |
| `BotDetection/Packs/IPackCapabilities.cs` | Tier capability gate |
| `BotDetection/Packs/PackCapabilities.cs` | FOSS/commercial implementation |
| `BotDetection/Packs/PackServiceCollection.cs` | Guarded wrapper that enforces CanWrite |
| `BotDetection/Packs/PackRegistry.cs` | Mutable IEnumerable\<T\> |
| `BotDetection/Packs/PackLoader.cs` | Startup scan + load + register (FOSS) |
| `BotDetection/Packs/PackManifest.cs` | manifest.yaml deserialization model |
| `BotDetection/Packs/PackCapabilityException.cs` | Thrown when FOSS pack registers write-capable service |
| `BotDetection/Services/IStylobotPreActionHook.cs` | Pre-action hook interface |
| `BotDetection/Services/IStylobotPostResponseHook.cs` | Post-response hook interface |
| `BotDetection/Services/ResponseContext.cs` | Context passed to post-response hooks |
| `BotDetection.UI/Services/IDashboardTab.cs` | Dashboard tab interface |

### Modified Files

| File | Change |
|---|---|
| `BotDetection/Middleware/BotDetectionMiddleware.cs` | Use IEnumerable\<IStylobotPreActionHook\> + IEnumerable\<IStylobotPostResponseHook\> instead of direct ReactionPackContext/DegradationAtom |
| `BotDetection/Extensions/ServiceCollectionExtensions.cs` | Register PackRegistry\<T\> singletons, PackLoader hosted service; existing reaction pack registrations moved to implement hook interfaces |
| `BotDetection/Services/ReactionPackContext.cs` | Implement IStylobotPreActionHook |
| `BotDetection/Services/DegradationAtom.cs` | Implement IStylobotPostResponseHook |
| `BotDetection.UI` | Dashboard shell reads IEnumerable\<IDashboardTab\>; add pack registry panel |

---

## Out of Scope (this iteration)

- Third-party / community pack authoring (no pack SDK or marketplace)
- Live reload (commercial loader) -- FOSS only in this cycle
- `IPackConfigContributor` / `IEndpointPolicyContributor` implementations -- interfaces defined, commercial implementations in next cycle
- Pack signing / integrity verification
