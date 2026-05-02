# Node SDK + Liquid Widget Rendering Design

**Date:** 2026-05-02
**Branch:** feat/node-sdk-liquid
**Scope:** FOSS only

## Goal

Give Node.js (and any HTTP client) a way to embed StyloBot data into their own UI with full markup control, while keeping the zero-config default rendering and avoiding any client-side template engine dependency.

## Core Insight

The rendering engine lives on the StyloBot server. Clients pass a Liquid template string; StyloBot fetches its own data, renders with Fluid (.NET), returns the HTML fragment. No template engine needed in the SDK. The public API contract is the documented Liquid data context per widget - stable variable names and types that developers write templates against.

## The Render Endpoint

New endpoint alongside the existing batch partials route:

```
POST /_stylobot/partials/render
Content-Type: application/json

{
  "widgets": {
    "summary": "{{ bot_requests }} bots / {{ human_requests }} humans",
    "topbots": "{% for bot in bots %}<li>{{ bot.bot_name }}</li>{% endfor %}"
  }
}
```

Response: `text/html` - concatenated rendered fragments, same shape as the existing GET batch endpoint. Each fragment has `hx-swap-oob` injected so HTMX can swap it in place.

When no template is passed for a widget, falls back to the existing Razor view. So `GET /_stylobot/partials/update?widgets=summary` continues to work unchanged.

## Server-Side: Fluid Rendering

Add `Fluid` NuGet package to `Mostlylucid.BotDetection.UI`. A new `LiquidWidgetRenderer` service:

- Parses and caches compiled Fluid templates (keyed by template string hash)
- Provides a `RenderAsync(templateString, context)` method
- `SbWidgetBatchMiddleware` gets a POST branch that reads the JSON body, routes each widget through `LiquidWidgetRenderer` if a template was provided, otherwise falls through to the existing Razor render path
- Template compilation errors return an empty string with a debug log, never a 500

## Liquid Data Contexts (Public API Contract)

These are the stable variable names available per widget. Published in docs with types and sample values.

### `summary`
```
bot_requests: int
human_requests: int
uncertain_requests: int
total_requests: int
bot_rate: float          (0.0 - 1.0)
unique_signatures: int
risk_band_counts: map    (keys: VeryLow, Low, Elevated, Medium, High, VeryHigh)
top_bot_types: map       (keys: bot type names, values: counts)
```

### `topbots`
```
bots: array of:
  signature_id: string
  bot_name: string
  bot_type: string
  hit_count: int
  last_seen: datetime
page: int
page_size: int
total_count: int
```

### `visitors`
```
visitors: array of:
  signature_id: string
  is_bot: bool
  risk_band: string
  hits: int
  first_seen: datetime
  last_seen: datetime
  country_code: string
  bot_name: string
filter: string
total_count: int
```

### `countries`
```
countries: array of:
  country_code: string
  total_count: int
  bot_count: int
  human_count: int
  bot_rate: float
```

### `endpoints`
```
endpoints: array of:
  method: string
  path: string
  total_count: int
  bot_count: int
  bot_rate: float
  avg_threat_score: float
  avg_processing_time_ms: float
```

### `threats`
```
threats: array of:
  timestamp: datetime
  path: string
  threat_type: string
  threat_score: float
  signature_id: string
  in_honeypot: bool
total_count: int
```

### `useragents`
```
user_agents: array of:
  family: string
  category: string
  total_count: int
  bot_rate: float
  avg_confidence: float
  last_seen: datetime
```

### `sessions`
```
sessions: array of:
  id: string
  started_at: datetime
  request_count: int
  is_bot: bool
  avg_bot_probability: float
  risk_band: string
  dominant_state: string
  bot_name: string
  country_code: string
```

## Node SDK Packages

### `@stylobot/core` (existing, extended)

Add `mode` discriminant to `StyloBotClientOptions`:

```ts
interface StyloBotClientOptions {
  endpoint: string
  mode: 'gateway' | 'sidecar' | 'ssr'
  apiKey?: string
  timeout?: number
}
```

- `gateway`: reads `X-StyloBot-*` headers already on the incoming request. Zero fetch.
- `sidecar`: calls `POST /api/v1/detect` with request details. One round-trip.
- `ssr`: calls `POST /_stylobot/partials/render` with templates. Returns HTML for injection.

### `@stylobot/node` (existing, extended)

**SSR Coordinator** - the main addition:

```ts
interface WidgetTemplate {
  widgetId: string
  template: string        // Liquid template string
  params?: Record<string, string>  // page, filter, sort etc.
}

class SbSsrCoordinator {
  constructor(options: StyloBotClientOptions)

  // Collect widgets declared in a template/request, fire one POST, return map of widgetId -> html
  renderWidgets(widgets: WidgetTemplate[]): Promise<Record<string, string>>

  // Express/Fastify middleware: inject window.__sb from gateway headers or /me fetch
  verdictMiddleware(): RequestHandler
}
```

The coordinator groups all widgets into a single POST to `/_stylobot/partials/render`. No widget fires its own individual fetch.

**`window.__sb` injector**: middleware that reads `X-StyloBot-*` headers (gateway mode) or calls `GET /_stylobot/me` (sidecar mode) and injects:

```html
<script>window.__sb = { isBot: false, riskBand: "Low", confidence: 0.9, ... }</script>
```

Added to the `<head>` via response stream interception (same pattern as helmet, compression middleware).

### `@stylobot/react` (new package)

**Verdict hooks** - read from `window.__sb`, subscribe to SignalR updates if connected:

```ts
function useVerdict(): Verdict
function useIsBot(): boolean
function useRiskBand(): RiskBand
```

**Gate components** - conditional rendering based on verdict:

```tsx
// Render children only if risk is at or below maxRisk
<SbGate maxRisk="elevated">
  <PremiumContent />
</SbGate>

// Render different content per risk band
<SbAdapt>
  <SbCase maxRisk="low"><NormalForm /></SbCase>
  <SbCase maxRisk="elevated"><FrictionForm /></SbCase>
  <SbCase><BlockMessage /></SbCase>
</SbAdapt>

// Wrap content in inline PoW challenge
<SbChallenge>
  <ContactForm />
</SbChallenge>
```

**SignalR connection** (optional):

```ts
// Call once at app root. Components auto-subscribe via useVerdict().
StyloBotClient.connect({ hub: '/_stylobot/hub' })
```

Without `.connect()`: verdict is static (from `window.__sb`). With it: verdict updates live as session signals accumulate.

**Aggregate widget components** - wrap the SSR coordinator output with client-side HTMX refresh:

```tsx
<SbWidget id="summary" template={myLiquidTemplate} refreshMs={5000} />
<SbWidget id="topbots" params={{ filter: 'bots', pageSize: '10' }} />
```

No template prop means default Razor rendering. With template: sends Liquid to the render endpoint.

### `@stylobot/elements` (new package)

Web components for non-React users. Mirror the React API exactly:

```html
<sb-gate max-risk="elevated">
  <premium-content></premium-content>
</sb-gate>

<sb-adapt>
  <sb-case max-risk="low"><normal-form></normal-form></sb-case>
  <sb-case max-risk="elevated"><friction-form></friction-form></sb-case>
  <sb-case><block-message></block-message></sb-case>
</sb-adapt>

<sb-widget id="summary">
  <template>
    {{ bot_requests }} bots / {{ human_requests }} humans
  </template>
</sb-widget>
```

`<sb-widget>` reads the inner `<template>` element's content as the Liquid string, sends it to the render endpoint on mount, replaces its own content with the response. Batch coordinator collects all `<sb-widget>` declarations before any fetch fires.

## Integration Modes

### SSR Only (zero client JS)

Node server calls `coordinator.renderWidgets(...)` during request handling. HTML arrives pre-rendered in the page. No browser JS, no HTMX needed for first paint. HTMX can still handle subsequent refreshes via `hx-get` on the rendered fragment.

### CSR (client-side only)

Server injects `window.__sb` via the verdict middleware. Browser JS reads it. `<sb-gate>` and `<SbGate>` evaluate synchronously. `<sb-widget>` fires the batch POST on DOMContentLoaded.

### SSR + SignalR (live)

SSR for first paint. Client calls `StyloBotClient.connect()`. Verdict updates push from hub. Gate components re-evaluate. Widget components re-fetch on verdict change if configured.

## Batching Contract

One invariant: no component, tag, or element fires its own individual fetch.

- Verdict: zero fetches in gateway mode (headers already present), one fetch in sidecar mode (`/me`), shared across all components.
- Widgets: one POST per page load to `/_stylobot/partials/render` with all widget templates bundled.
- The coordinator is responsible for this. Individual components declare their needs; the coordinator collects and dispatches.

## Out of Scope (FOSS)

- Named/stored templates (dashboard template editor)
- Template versioning
- Per-customer template storage
- Any commercial tier features
