# SignalR Beacon Architecture

The StyloBot dashboard uses a hybrid **SignalR + HTMX** architecture for real-time updates. SignalR acts as a lightweight beacon — it tells clients *what changed*, never *what the data is*. Clients then fetch authoritative data via HTMX partial endpoints.

## The Problem with Data-Over-SignalR

Traditional real-time dashboards push full data payloads through SignalR:

```
Server → SignalR → BroadcastDetection({ isBot, probability, country, ... })
                 → BroadcastSignature({ name, hitCount, factors, ... })
                 → BroadcastClusters([{ id, label, members, ... }, ...])
```

This creates several problems:

1. **Bandwidth waste** — Every connected client receives every detection event's full payload, even if they're on a tab that doesn't display it.
2. **State synchronization bugs** — Client-side state diverges from server-side truth. Missed messages mean stale data.
3. **Serialization coupling** — The hub interface becomes a de facto API contract. Changing a model property breaks all clients.
4. **No pagination** — You can't paginate a push stream. The client either gets everything or nothing.
5. **Duplicate rendering** — Server renders HTML for the initial page load, then clients re-render from JSON for updates. Two rendering paths to maintain.

## The Beacon-Only Solution

StyloBot separates the **notification channel** (SignalR) from the **data channel** (HTTP/HTMX):

```
Detection occurs
    ↓
Server sends: BroadcastInvalidation("signature")    ← tiny string, no payload
    ↓
HTMX coordinator receives beacon
    ↓
Debounce 500ms (coalesce rapid-fire events)
    ↓
GET /_stylobot/partials/update?widgets=topbots,recent   ← single HTTP request
    ↓
Server renders HTML fragments with hx-swap-oob="true"
    ↓
HTMX swaps them into the DOM
```

### BLE Analogy

This mirrors Bluetooth Low Energy's advertising pattern:

| BLE Concept | StyloBot Equivalent |
|---|---|
| **Advertisement packet** (tiny, frequent) | `BroadcastInvalidation("signature")` |
| **GATT read** (authoritative payload) | `GET /partials/update?widgets=...` |
| **Service UUID** | Signal name (`"signature"`, `"summary"`, `"clusters"`) |
| **Scan interval** | Debounce timer (500ms) |
| **Connection event** | HTMX partial fetch |

The beacon carries no data — it's a "something changed" ping. The client decides *when* and *what* to fetch based on its current view.

## Hub Interface

The hub contract is minimal — two methods total:

```csharp
public interface IStyloBotDashboardHub
{
    // Beacon: tells clients what category changed
    Task BroadcastInvalidation(string signal);

    // The ONLY data-carrying method: lightweight attack arc for map visualization
    Task BroadcastAttackArc(string countryCode, string riskBand);
}
```

`BroadcastAttackArc` is the single exception — it carries two strings (country code + risk band) for the world map animation. This is acceptable because:
- The data is tiny (two short strings)
- Attack arcs are ephemeral animations, not persistent state
- There's no server-rendered partial equivalent for a CSS animation

## Widget Registry

Dashboard widgets self-register via HTML `data-` attributes:

```html
<div id="topbots-widget"
     data-sb-widget="topbots"
     data-sb-depends="signature,summary"
     hx-swap-oob="true">
    <!-- server-rendered content -->
</div>
```

- `data-sb-widget="topbots"` — widget ID (used in `?widgets=topbots` query)
- `data-sb-depends="signature,summary"` — which invalidation signals trigger a refresh

The HTMX coordinator script builds a reverse map at runtime:

```javascript
// signal → [widgetId, ...]
{
    "signature": ["topbots", "recent"],
    "summary":   ["summary", "topbots"],
    "clusters":  ["clusters"],
    "countries": ["countries"]
}
```

When `BroadcastInvalidation("signature")` arrives, the coordinator looks up `["topbots", "recent"]` and fires a single batched request.

## Debounced Coalescing

Multiple rapid invalidation signals are coalesced into a single HTTP request:

```
t=0ms    BroadcastInvalidation("signature")   → pending: {topbots, recent}
t=10ms   BroadcastInvalidation("summary")     → pending: {topbots, recent, summary}
t=50ms   BroadcastInvalidation("signature")   → pending: {topbots, recent, summary}  (no-op, already pending)
t=500ms  FLUSH → GET /partials/update?widgets=topbots,recent,summary
```

The 500ms debounce window prevents thundering-herd partial fetches during bursts of bot detections.

## OOB (Out-of-Band) Swap Pattern

The server returns multiple HTML fragments in a single response, each targeting a different element:

```html
<!-- Response body contains multiple OOB fragments -->
<div id="topbots-widget" data-sb-widget="topbots" data-sb-depends="signature,summary" hx-swap-oob="true">
    <h3>Top Bots</h3>
    <table>...</table>
</div>

<div id="recent-widget" data-sb-widget="recent" data-sb-depends="signature" hx-swap-oob="true">
    <h3>Recent Activity</h3>
    <table>...</table>
</div>
```

HTMX processes each `hx-swap-oob="true"` fragment independently, swapping it into the matching `id` in the DOM. This means one HTTP request can update N widgets.

## Server-Side Invalidation Points

The server emits invalidation beacons at these points:

| Event | Signals Emitted |
|---|---|
| New detection processed | `signature`, `summary` |
| Bot detection with geo data | `signature`, `summary` + `BroadcastAttackArc(countryCode, riskBand)` |
| Cluster refresh completes | `clusters` |
| LLM generates description | `signature` |
| LLM generates signature name | `signature`, `{signatureHash}` |
| LLM generates score narrative | `{signatureHash}` |
| LLM generates cluster description | `clusters` |

Per-signature signals (like `{signatureHash}`) allow the signature detail page to update only when its specific signature changes.

## Standalone Components

Some UI components (detection header bar, compact detection details) are server-rendered Alpine.js components that maintain their own SignalR connection. Since they display YOUR detection from the initial page load and don't need live data feeds, they register a no-op `BroadcastInvalidation` listener to keep the connection alive without fetching data.

The dashboard-viz module (world map + time series chart) connects separately and listens only for `BroadcastAttackArc` to animate attack arcs on the map.

## Benefits

1. **Single rendering path** — Razor partials render both the initial page and all updates. No client-side template duplication.
2. **Server-side pagination** — Widgets like Top Bots and Recent Activity support sort, filter, and pagination because updates come via HTTP, not push.
3. **Bandwidth efficiency** — SignalR messages are a few bytes each. Data is fetched only when the client needs it and only for visible widgets.
4. **Stale-proof** — Every HTMX fetch returns the current server state. There's no possibility of missed-message drift.
5. **Simple client code** — The entire HTMX coordinator is ~40 lines of vanilla JavaScript. No framework, no state management, no JSON-to-DOM mapping.
6. **CDN/cache friendly** — Partial endpoints are regular HTTP GETs that could be cached, rate-limited, or served from edge.
