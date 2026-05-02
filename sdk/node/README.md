# StyloBot Node SDK

The StyloBot Node SDK lets you integrate bot detection and adaptive content rendering into any Node.js application. Two packages cover all use cases:

- **`@stylobot/core`** - zero-dependency TypeScript types, `StyloBotClient`, and header parsing. Works in Node, Deno, and Bun.
- **`@stylobot/node`** - Express middleware (`styloBotMiddleware`), Fastify plugin (`styloBotPlugin`), SSR coordinator, and verdict injector.
- **`@stylobot/elements`** - browser web components for client-side rendering (`sb-gate`, `sb-adapt`, `sb-widget`).

---

## Concepts

### Gateway mode

Your application sits behind the StyloBot YARP Gateway. The gateway runs all 49 detectors and injects the verdict into request headers (`X-StyloBot-IsBot`, `X-StyloBot-Probability`, etc.) before forwarding the request. Your Node app reads these headers at zero latency: no network call, no async work.

Use gateway mode for production deployments where the gateway is in the request path.

### Sidecar mode

Your application calls the StyloBot detection API directly for each request. This has ~1-5ms added latency but works without a gateway in front of your app. Results are cached in `res.locals` for the lifetime of the request.

Use sidecar mode for standalone deployments or development.

### SSR mode (server-side widget rendering)

Your Node server fetches pre-rendered HTML widgets from the StyloBot dashboard API using Liquid templates. The server renders the widget HTML and embeds it in your response. The visitor sees fully rendered content immediately, without any client-side JavaScript for the widget data.

Use SSR mode when you want dashboard data widgets (bot traffic summary, top bots, threat feeds) embedded in your server-rendered pages.

---

## Installation

```bash
npm install @stylobot/core @stylobot/node
```

For web component support in the browser, use `@stylobot/elements` as a bundled asset (it is not an npm package you import server-side):

```bash
npm install @stylobot/elements --save-dev
# Build with your bundler (esbuild, Vite, Rollup) and serve as /elements.js
```

---

## Gateway mode

The most common production setup. The StyloBot Gateway injects detection headers; your Express app reads them.

```ts
import express from 'express'
import { styloBotMiddleware } from '@stylobot/node'

const app = express()

// Reads X-StyloBot-* headers injected by the gateway.
// req.stylobot.isBot, req.stylobot.verdict are available in all downstream handlers.
app.use(styloBotMiddleware({ mode: 'headers' }))

app.get('/', (req, res) => {
  const { isBot, verdict } = req.stylobot
  if (isBot && verdict.botProbability > 0.9) {
    res.status(403).send('Forbidden')
    return
  }
  res.send(`Hello, ${isBot ? 'bot' : 'human'}! Risk: ${verdict.riskBand}`)
})
```

TypeScript augmentation for `req.stylobot` is provided automatically by `@stylobot/node` for Express. For Fastify, add your own augmentation to a `types.d.ts` file:

```ts
import type { StyloBotResult } from '@stylobot/node'
declare module 'fastify' {
  interface FastifyRequest { stylobot: StyloBotResult }
}
```

---

## Sidecar mode

No gateway required. The middleware calls the StyloBot detection API on each request.

```ts
import express from 'express'
import { styloBotMiddleware } from '@stylobot/node'

const app = express()

app.use(styloBotMiddleware({
  mode: 'api',
  endpoint: 'http://stylobot-host:5080',
  apiKey: process.env.STYLOBOT_API_KEY,
}))

app.get('/checkout', (req, res) => {
  if (req.stylobot.verdict.riskBand === 'High') {
    res.redirect('/verify')
    return
  }
  res.send('Proceed to checkout')
})
```

---

## SSR widget mode

Render StyloBot dashboard data as HTML widgets server-side using Liquid templates. The `SbSsrCoordinator` batches all widget requests into a single POST call, so rendering a page with five widgets costs one round-trip.

```ts
import express from 'express'
import { readFileSync } from 'node:fs'
import { StyloBotClient } from '@stylobot/core'
import { SbSsrCoordinator, sbVerdictInjector, styloBotMiddleware } from '@stylobot/node'

const client = new StyloBotClient({ endpoint: 'http://stylobot-host:5080' })
const coordinator = new SbSsrCoordinator(client)

const app = express()
app.use(styloBotMiddleware({ mode: 'headers' }))
app.use(sbVerdictInjector({ mode: 'gateway' }))

app.get('/dashboard', async (req, res) => {
  const summaryTemplate = readFileSync('./templates/summary.liquid', 'utf8')
  const topbotsTemplate = readFileSync('./templates/topbots.liquid', 'utf8')

  // One POST to /_stylobot/partials/render, all widgets returned as HTML fragments.
  const widgets = await coordinator.renderWidgets([
    { widgetId: 'summary', template: summaryTemplate },
    { widgetId: 'topbots', template: topbotsTemplate },
  ])

  res.send(`
    <html><body>
      ${res.locals.sbVerdictScript}
      ${widgets['summary'] ?? ''}
      ${widgets['topbots'] ?? ''}
    </body></html>
  `)
})
```

You can also render a single widget:

```ts
const html = await coordinator.renderWidget('summary', '{{ bot_requests }} bots today')
```

---

## Liquid templates

Templates are standard Liquid (Shopify-flavor, via Fluid.Core on the .NET side). Each widget exposes a set of variables documented in `docs/data-contexts.md`.

### Example: summary widget

```liquid
<div class="card" data-sb-widget="summary">
  <h2>Traffic Overview</h2>
  <p>{{ bot_requests }} bots out of {{ total_requests }} requests</p>
  <p>Bot rate: {{ bot_rate | times: 100 | round: 1 }}%</p>
  {% if bot_rate > 0.5 %}
    <div class="alert">High bot traffic!</div>
  {% endif %}
</div>
```

### Liquid filters available

All standard Liquid filters work: `| round`, `| times`, `| default`, `| upcase`, `| truncate`, `| date`, `| size`, etc.

### Template caching

The .NET server compiles and caches Liquid templates by (widgetId + template hash). Identical templates are only compiled once per server lifetime.

### No-template mode

If you pass an empty template string (or omit `template`), the server renders the widget using its built-in Razor view instead:

```ts
const widgets = await coordinator.renderWidgets([
  { widgetId: 'summary' },  // renders built-in Razor view
  { widgetId: 'topbots', template: '<ul>{% for b in bots %}<li>{{ b.bot_name }}</li>{% endfor %}</ul>' },
])
```

---

## Web components

`@stylobot/elements` provides three custom elements that read `window.__sb` (the verdict object) and adapt the UI accordingly. They react to `sb:verdict` events if the verdict changes after page load.

### window.__sb

The verdict script (`res.locals.sbVerdictScript`) embeds a `<script>` tag that sets `window.__sb`. All three web components read this object.

```json
{
  "isBot": false,
  "botProbability": 0.12,
  "confidence": 0.88,
  "botType": null,
  "botName": null,
  "riskBand": "Low",
  "recommendedAction": "Allow",
  "threatScore": 0.02,
  "threatBand": "None"
}
```

Fields:

| Field | Type | Description |
|-------|------|-------------|
| `isBot` | boolean | True if the visitor is classified as a bot |
| `botProbability` | number (0-1) | Raw probability score |
| `confidence` | number (0-1) | Detector confidence in the score |
| `botType` | string or null | Bot taxonomy type (e.g. `AiBot`, `Scraper`) |
| `botName` | string or null | Deterministic bot name if identified |
| `riskBand` | string | One of: `Unknown`, `VeryLow`, `Low`, `Elevated`, `Medium`, `High`, `VeryHigh`, `Verified` |
| `recommendedAction` | string | `Allow`, `Throttle`, `Challenge`, or `Block` |
| `threatScore` | number (0-1) | CVE/threat probe score |
| `threatBand` | string | `None`, `Low`, `Elevated`, `High`, or `Critical` |

### sb-gate

Shows its content only if the visitor's risk is at or below `max-risk`.

```html
<sb-gate max-risk="low">
  <div class="premium-content">
    Visible only to low-risk visitors.
  </div>
</sb-gate>
```

Risk order (lowest to highest): `Unknown`, `VeryLow`, `Low`, `Elevated`, `Medium`, `High`, `VeryHigh`, `Verified`.

### sb-adapt

Shows the first `sb-case` child whose `max-risk` is satisfied. If no `max-risk` is set, that case is always shown (use as the final fallback).

```html
<sb-adapt>
  <sb-case max-risk="low">
    <div>Welcome! Normal checkout.</div>
  </sb-case>
  <sb-case max-risk="elevated">
    <div>Please verify your email before continuing.</div>
  </sb-case>
  <sb-case>
    <div>Access restricted for your risk level.</div>
  </sb-case>
</sb-adapt>
```

### sb-widget

Fetches a Liquid-rendered widget from the StyloBot server and replaces itself with the result. The `<template>` child holds the Liquid template. If no template is given, the server uses the built-in Razor view.

```html
<!-- With Liquid template -->
<sb-widget data-sb-widget="summary">
  <template>
    <div data-sb-widget="summary">
      {{ bot_requests }} bots / {{ human_requests }} humans
    </div>
  </template>
</sb-widget>

<!-- Without template (uses built-in Razor view) -->
<sb-widget data-sb-widget="topbots"></sb-widget>
```

Requests from all `sb-widget` elements on the page are automatically batched into a single POST to `/_stylobot/partials/render`.

Configure the StyloBot server endpoint before widgets connect:

```js
import { sbCoordinator } from '/elements.js'
sbCoordinator.configure('http://your-stylobot-host:5080')
```

---

## Running the sample app

Start the StyloBot .NET application first:

```bash
cd /path/to/stylobot
dotnet run --project Mostlylucid.BotDetection.Demo
# Dashboard at: http://localhost:5080/_stylobot
```

Then start the sample Express app:

```bash
cd sdk/node/samples/express-sample
npm install
STYLOBOT_URL=http://localhost:5080 npm start
# Visit: http://localhost:3000
# CSR demo: http://localhost:3000/csr.html
```

The sample demonstrates:

- SSR route (`/`): summary and topbots widgets rendered server-side with Liquid templates. The page arrives fully rendered with bot traffic data.
- CSR page (`/csr.html`): `sb-gate`, `sb-adapt`, and `sb-widget` web components. Content adapts client-side based on `window.__sb`.

---

## Verdict injector middleware

`sbVerdictInjector` is a separate middleware that populates `res.locals.sbVerdict` (the `Verdict` object) and `res.locals.sbVerdictScript` (the `<script>window.__sb=...</script>` snippet). Use it alongside `styloBotMiddleware` to embed the verdict in SSR pages.

```ts
import { sbVerdictInjector } from '@stylobot/node'

// Gateway mode: reads from request headers (zero latency)
app.use(sbVerdictInjector({ mode: 'gateway' }))

// Sidecar mode: fetches verdict from StyloBot /_stylobot/me endpoint
app.use(sbVerdictInjector({
  mode: 'sidecar',
  endpoint: 'http://stylobot-host:5080',
  apiKey: process.env.STYLOBOT_API_KEY,
  timeout: 2000,
}))
```

In your template:

```ts
res.send(`
  <html><head></head><body>
    ${res.locals.sbVerdictScript}
    <!-- web components now read window.__sb -->
    <sb-gate max-risk="low">...</sb-gate>
  </body></html>
`)
```

---

## Security model

**Detection runs on the .NET side only.** The Node SDK reads results; it does not influence detection decisions.

**The `X-SB-Api-Key` header** is for customers to exempt their own monitoring or health-check traffic from blocking. It is not for bypassing detection on general traffic.

**Liquid templates are sandboxed.** Template execution is limited to 50,000 steps (configurable). Templates cannot access the file system, execute code, or make network calls. They only interpolate data from the per-widget context provided by the server.

**The `window.__sb` object** is set server-side (by the verdict injector) and is read-only for client-side code. Web components do not trust client-supplied values for security decisions: access control enforcement happens on the server with `styloBotMiddleware` and action policies. The web components are for UI adaptation only.

**SSR widget data** is aggregated statistics only. No PII or raw visitor IP addresses are included in Liquid contexts. All signatures are HMAC-SHA256 hashes.
