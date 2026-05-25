# Endpoint Configuration View + Path Pinning Design

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Show configured policy and reaction pack coverage per endpoint in the FOSS dashboard (read-only), and let operators pin any path -including honeypot paths -to the endpoint list before any traffic arrives.

**Architecture:** Pinned endpoints stored in SQLite; merged with traffic-based endpoints at query time. Policy and reaction pack state are read from existing registries and injected into endpoint models. The "is honeypot" flag on a pinned endpoint is a read surface for simulation packs -activation logic lives in the pack, not the base product.

**Tech Stack:** ASP.NET Core, SQLite (Microsoft.Data.Sqlite), HTMX, DaisyUI/Tailwind, Razor partials, IReactionPackContext, IPolicyRegistry.

---

## Scope

### In scope (FOSS, read-only)

- `SqlitePinnedEndpointStore`: persist pinned endpoints (method, path, is_honeypot flag, optional note)
- `IPinnedEndpointStore`: interface for store; simulation packs can inject this to discover honeypot paths
- Merge pinned + traffic endpoints in `GetEndpointsDataAsync` -pinned-but-unseen paths appear with zero counts
- Endpoint list: add a **Policy** badge column showing resolved action policy name
- Endpoint detail panel: add a **Protection** section showing resolved policy + any active/configured reaction packs covering this endpoint
- Pin endpoint UI: button on endpoints tab opens an HTMX inline form (method dropdown + path input + honeypot checkbox + optional note); saves via POST; unpin via DELETE
- Pinned endpoints show a pin icon in the list; honeypot-flagged ones show a honeypot icon

### Out of scope

- Policy editing (commercial)
- Reaction pack configuration per endpoint (commercial)
- Honeypot activation / fake response generation (simulation pack)
- Simulation pack integration with `IPinnedEndpointStore` (follow-up pack work)

---

## Data Model

### `pinned_endpoints` table

```sql
CREATE TABLE IF NOT EXISTS pinned_endpoints (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    method     TEXT NOT NULL DEFAULT 'ANY',
    path       TEXT NOT NULL,
    is_honeypot INTEGER NOT NULL DEFAULT 0,
    note       TEXT,
    created_at INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pinned_endpoints_method_path
    ON pinned_endpoints (method, path);
```

### `IPinnedEndpointStore`

```csharp
public interface IPinnedEndpointStore
{
    Task<IReadOnlyList<PinnedEndpoint>> GetAllAsync(CancellationToken ct = default);
    Task<PinnedEndpoint?> AddAsync(string method, string path, bool isHoneypot, string? note, CancellationToken ct = default);
    Task<bool> RemoveAsync(long id, CancellationToken ct = default);
}

public sealed record PinnedEndpoint(long Id, string Method, string Path, bool IsHoneypot, string? Note, DateTimeOffset CreatedAt);
```

### `DashboardEndpointStats` additions

```csharp
// Added to existing record:
bool IsPinned { get; init; }
bool IsHoneypot { get; init; }
long? PinId { get; init; }
```

### `EndpointDetailModel` additions

```csharp
// Added to existing class:
public string? PolicyName { get; init; }
public IReadOnlyList<EndpointPackCoverage> PackCoverage { get; init; } = [];
public bool IsPinned { get; init; }
public bool IsHoneypot { get; init; }
public long? PinId { get; init; }
```

### `EndpointPackCoverage` (new record, in `DashboardEndpointStats.cs`)

```csharp
public sealed record EndpointPackCoverage(
    string PackName,
    string Scope,          // "global" or "endpoint"
    int CurrentLevel,      // 0 = configured but inactive
    string? CurrentPolicy);
```

---

## Endpoint Enrichment Logic

`GetEndpointsDataAsync` in `StyloBotDashboardMiddleware`:

1. Fetch traffic-based endpoints from cache/store (existing)
2. Fetch pinned endpoints from `IPinnedEndpointStore`
3. For each pinned endpoint not already in the traffic list, add a zero-count `DashboardEndpointStats` with `IsPinned = true`
4. For each endpoint (traffic + pinned), set `IsPinned`/`IsHoneypot`/`PinId` from the pinned set
5. Enrich `ActivePolicyName` via `IPolicyRegistry.GetPolicyForPath()` (already done; ensure it runs for pinned-only endpoints too)

`BuildEndpointDetailCoverage(string path)` -new private method:

1. Call `IPolicyRegistry.GetPolicyForPath(path)` → `PolicyName`
2. Call `IReactionPackContext.GetActiveStates()` → filter where `Scope == "global"` or `ScopedEndpoint` matches path
3. Also include inactive configured packs from `IEnumerable<ReactionPackDefinition>` scoped to this endpoint
4. Return list of `EndpointPackCoverage`

---

## API Endpoints (new, in middleware switch)

| Route | Method | Handler | Description |
|-------|--------|---------|-------------|
| `api/endpoint-pins` | GET | `ServeEndpointPinsApiAsync` | Returns all pinned endpoints as JSON |
| `api/endpoint-pins` | POST | `HandlePinEndpointAsync` | Adds a pin; body: `{method, path, isHoneypot, note}` |
| `api/endpoint-pins/{id}` | DELETE | `HandleUnpinEndpointAsync` | Removes pin by id |

POST returns the new `PinnedEndpoint` record as JSON (201).
DELETE returns 204 on success, 404 if not found.

---

## UI Changes

### Endpoint list (`_EndpointsCompact.cshtml`)

New column after **Sigs**: **Policy** -shows `endpoint.ActivePolicyName` as a small mono badge, or `-` if default.

Pin/honeypot indicators in the **Path** cell:
- Pinned: `bx-pin` icon (muted)
- Honeypot: `bx-bug` icon (warning tint)

### Endpoint detail panel (`_EndpointDetail.cshtml`)

Replace the current "Bot Policy / Apply Policy button" section (which calls `sbOpenPolicyModal`) with a **Protection** section:

```
Protection
──────────────────────────────────────────
Policy     [throttle-stealth]  ← policy badge
           (from path rule: /api/*)

Reaction Packs
  error-spike-protection   global    Level 0 (inactive)
  checkout-protection      endpoint  Level 1: protect  [challenge-pow]
```

- Policy badge: mono text, grey background, shows resolved policy name
- "From path rule" subtext shows the matched rule if available (or "default" if nothing matched)
- Each pack row: name, scope badge (global/endpoint), level badge (0 = grey, 1+ = color-coded), active policy name if level > 0
- No edit controls -FOSS is read-only

Pin controls at bottom of detail panel (if endpoint is pinned):
- "Pinned" indicator with unpin button (DELETE via HTMX)
- Honeypot badge if flagged

### Endpoints tab (Index.cshtml / partial)

Add a **"Pin endpoint"** button in the endpoints tab header (top right of the endpoint list card).

Clicking expands an inline form (HTMX swap on a hidden div):
```
Method: [GET ▾]   Path: [/config.php          ]
[ ] Mark as honeypot   Note: [optional         ]
[Cancel]  [Pin Endpoint]
```

- Method: dropdown with GET, POST, PUT, DELETE, PATCH, ANY
- Path: text input, must start with `/`
- Honeypot checkbox
- Note: optional free text
- Submit: POST to `/_stylobot/api/endpoint-pins`, on success refresh the endpoint list partial
- Form validation: path required, must start with `/`, max 500 chars

---

## File Map

| Action | File |
|--------|------|
| Create | `src/Mostlylucid.BotDetection/Data/IPinnedEndpointStore.cs` |
| Create | `src/Mostlylucid.BotDetection/Data/SqlitePinnedEndpointStore.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Models/DashboardEndpointStats.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` |
| Modify | `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointsCompact.cshtml` |
| Modify | `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointDetail.cshtml` |
| Modify | `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml` |
| Create | `src/Mostlylucid.BotDetection.Test/Data/SqlitePinnedEndpointStoreTests.cs` |

---

## Testing

- `SqlitePinnedEndpointStoreTests`: add, get all, remove, duplicate path returns existing (upsert), in-memory SQLite
- `EndpointCoverageTests`: mock `IReactionPackContext` + `IPolicyRegistry`, verify coverage list correctly merges global + scoped packs