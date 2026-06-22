# Client-Side Detection: Scripts, Tag Helpers, and SDKs

End-to-end reference for the client-facing detection surface: the JavaScript that runs in the visitor's browser, the ASP.NET Core tag helpers that emit it and surface the verdict in Razor pages, and the SDKs that let non-.NET hosts consume the same verdict.

For the *probes themselves* (what each one targets, what cloak browser it catches, threshold tuning) see [cloak-detection.md](cloak-detection.md). This document is about the wiring: how the probes get into the page, how the beacon comes back, and how the verdict flows out to your application.

---

## The three integration surfaces

| Surface | Lives in | Used by |
|---|---|---|
| **Detection script** (`botdetection.js`) | Browser | Every visitor who hits a document response |
| **Server-side tag helpers** (Razor) | `Mostlylucid.BotDetection.ClientSide.*`, `Mostlylucid.BotDetection.UI.TagHelpers.*` | ASP.NET Core hosts |
| **SDKs** | `sdk/node/`, `sdk/go/`, `sdk/caddy/`, `sdk/proto/` | Node (Express + Fastify), Go, Caddy, anything gRPC |

The detection script is the same on every host. The tag helpers are how a Razor host emits the script and reads the verdict. The SDKs let a non-Razor host read the same verdict from headers (Tier 1), from the REST API (Tier 2), or from gRPC.

---

## 1. The detection script

### `botdetection.js`

A single embedded resource at `src/Mostlylucid.BotDetection/ClientSide/botdetection.js`, served by the `MapBotDetectionScript` endpoint. The bootstrap inline `<script>` publishes a config object (`window.MLBotD`); the loaded script reads it, runs the probes, and beacons the result back.

The script is the artifact on the wire. The previous version inlined an ~80-line IIFE into the C# tag helper, which produced an unmaintained twin of the `.js` file. The current shape has one source of truth: the `.js` file is embedded as an assembly resource, served by the script endpoint, and referenced by the tag helper via `<script src=...>`.

**Probes that run:**

- `basics()`: navigator properties, plugin count, mime types, languages, `connType` (mobile / connection mismatch probe), `cdpRuntime` (Chrome DevTools Protocol Runtime side-effect counter), `hasChrome`
- `headless()`: classic webdriver markers (webdriver flag, plugins length, languages list, Notification permission inconsistency)
- `triple()`: canvas + WebGL vendor + renderer (the input to the shape hash)
- `webgl()`: full WebGL parameter dump (extensions, max textures, etc.)
- `touch()`: touch event support + max touch points
- `stack()`: stack trace shape (catches Chromium-fork patches)
- `ua()`: full User-Agent + UA-CH client hints
- `legit()`: legitimate-user classifiers (full-language list, etc.); these *raise* human confidence, they don't penalise
- `clamp()`: timer resolution (`performance.now()` precision)
- `iceProbe()`: WebRTC ICE no-srflx probe (UDP egress blocked = damru, Bright Data, locked-down VMs)
- `ttsProbe()`: `speechSynthesis.getVoices()` count (empty on Android = fresh emulator container)
- `botdProbe()`: optional FingerprintJS BotD integration (dynamic import; off by default)
- `mouseStats()`: up to 50 mousemove samples, computes integer-coords flag + timing CV (Kameleo synth detection)

Each probe wraps its body in `try/catch` so a single failing probe never breaks the beacon. Errored probes return a sentinel (`-1` for numerics, empty string for strings) so the analyzer can distinguish "errored" from "observed zero".

**Configuration object (`window.MLBotD`):**

```js
window.MLBotD = {
  t: '<signed token>',            // per-request token, HMAC-bound to IP
  e: '/bot-detection/fingerprint', // beacon endpoint
  cfg: {
    collectWebGL: true,
    collectCanvas: true,
    collectAudio: false,
    collectInteraction: true,
    timeout: 5000,
    iceStun: 'stun:stun.l.google.com:19302',
    botdUrl: ''                    // empty when BotD is disabled
  }
};
```

The bootstrap is the only inline JS the page emits; the detector itself loads from `/bot-detection/script.js` and can be CSP-restricted to `script-src 'self'`.

### `challenge.js`

PoW challenge solver. Loaded by the HTML page that `ChallengeActionPolicy` returns when a request is gated. Computes SHA-256 micro-puzzles whose difficulty is set by the blackboard (the harder the verdict leans bot, the more leading-zero bits are required). The solution is posted back, the original request is retried, and the result becomes a signal for future requests from the same fingerprint (the challenge-as-signal feedback loop).

Same caching + CSP contract as the detector script: served by `MapBotDetectionChallengeScript` from an embedded resource with a strong ETag.

### Script delivery

Both scripts are served from embedded assembly resources by `BotDetectionScriptEndpointExtensions`:

| Default path | Resource | Maps via |
|---|---|---|
| `/bot-detection/script.js` | `botdetection.js` | `endpoints.MapBotDetectionScript()` |
| `/bot-detection/challenge.js` | `challenge.js` | `endpoints.MapBotDetectionChallengeScript()` |
| `/bot-detection/fingerprint` (POST) | beacon endpoint | `endpoints.MapBotDetectionFingerprintEndpoint()` |

The script endpoints respond with:
- `Content-Type: application/javascript; charset=utf-8`
- Strong ETag (SHA-256 of content, first 8 bytes hex; stable per build)
- `Cache-Control: public, max-age=3600, must-revalidate`
- 304 on conditional `If-None-Match`

All three are registered automatically when you call `app.UseStyloBot()` (the canonical wire-up; covers middleware ordering, broadcast wiring, and dashboard mapping). If you bypass `UseStyloBot()` and wire pieces manually, you must call the three `Map*` extensions yourself.

### Token contract

`IBrowserTokenService` (default `BrowserTokenService`) issues an HMAC-SHA256 token per request, bound to the visitor's IP-hash + a request id + expiry. The token rides into the page via the bootstrap, comes back in the beacon body or `X-ML-BotD-Token` header, and is single-use (cached for the lifetime window to prevent replay).

Two delivery paths exist because of `sendBeacon` limitations:
- **Fetch path** (main fingerprint script): sets `X-ML-BotD-Token` header.
- **sendBeacon path** (adblocker probe): tokens cannot ride in headers via `navigator.sendBeacon`, so the token is in the JSON body under field `t`. The endpoint accepts either source.

Configure the signing secret via `BotDetection.ClientSide.TokenSecret` (any string >= 32 chars). Without one, a random per-instance key is generated and tokens do not survive process restarts; the service logs a warning.

### Beacon endpoint

`MapBotDetectionFingerprintEndpoint` exposes `POST /bot-detection/fingerprint` (configurable). The handler:

1. Deserialises the JSON body to `BrowserFingerprintData` (nested DTO; the JS payload has nested blocks so the DTO mirrors them).
2. Validates the token (header preferred, body fallback for the adblocker probe).
3. Runs `IBrowserFingerprintAnalyzer.Analyze(data, payload.RequestId)` to turn the raw payload into a `BrowserFingerprintResult` with derived signals (shape hash, integrity score, headless score, etc.).
4. Stores the result against the IP-hash via `IBrowserFingerprintStore`, where subsequent server-side detection contributors pick it up.

The endpoint is anonymous-accessible (the whole point is that it must work for every visitor) and rate-limited at the framework level.

---

## 2. ASP.NET Core integration

### DI registration

```csharp
builder.Services.AddStyloBot();   // detection + dashboard, all wired
// or, lower-level:
builder.Services.AddBotDetection(); // detection only; you map endpoints yourself
```

`AddStyloBot()` registers:
- All 57 detectors and the orchestrator
- `IBrowserTokenService`, `IBrowserFingerprintAnalyzer`, `IBrowserFingerprintStore`
- The two tag helpers below
- The dashboard hub + Razor pages

```csharp
app.UseRouting();
app.UseStyloBot();   // middleware in correct order; endpoints mapped
```

`UseStyloBot()` is the canonical wire-up. It guarantees: broadcast filter before detection, detection middleware before the dashboard, fingerprint/script/challenge endpoints mapped, SignalR hub configured, and (when enabled) admin routes gated by token.

### Tag helpers in `Mostlylucid.BotDetection.ClientSide`

These two emit JS into the page.

#### `<bot-detection-script>`

Renders the bootstrap + external script pair.

```html
@addTagHelper *, Mostlylucid.BotDetection

<bot-detection-script />

@* with overrides *@
<bot-detection-script endpoint="/bot-detection/fingerprint"
                      script-path="/bot-detection/script.js"
                      defer="true"
                      async="false"
                      nonce="@nonce" />
```

| Attribute | Default | Purpose |
|---|---|---|
| `endpoint` | `/bot-detection/fingerprint` | Beacon endpoint the detector POSTs to |
| `script-path` | `/bot-detection/script.js` | Where the loaded script lives (matches `MapBotDetectionScript`) |
| `defer` | `true` | Defer the external script (runs after parse) |
| `async` | `false` | Async-load the external script; defer is usually preferable |
| `nonce` | none | CSP nonce; attached to both emitted `<script>` tags |

**Suppression conditions** (renders nothing): `BotDetection.ClientSide.Enabled` is false, no active `HttpContext` (dashboard-viewer hosts), or `IBrowserTokenService` is not registered. The last case matters because remote dashboard viewers (`Stylobot.Ui` in REST mode) consume the verdict but do not issue tokens; the tag helper suppresses cleanly without throwing.

**CSP:** the bootstrap is a single inline `<script>` carrying a nonce. With `script-src 'self' 'nonce-...' 'strict-dynamic'` the nonce propagates to the dynamically loaded `/bot-detection/script.js` automatically; emitting the nonce on both tags is the safe default for stricter policies.

#### `<sb:adblock-probe>`

Renders an inline probe that fetches a real ad-network resource and beacons "adblocker present" when the fetch fails. The signal suppresses the no-fingerprint penalty for legitimate users whose adblocker blocked the fingerprint script alongside everything else (a routine source of false positives that this probe specifically defuses).

```html
@* AdSense publisher *@
<sb:adblock-probe provider="adsense" client-id="ca-pub-1234567890" />

@* Custom URL on any filter list (EasyList, EasyPrivacy, uBO filter hub) *@
<sb:adblock-probe probe-url="https://static.ads-twitter.com/uwt.js" />

@* Override timeout / endpoint *@
<sb:adblock-probe provider="amazon" timeout-ms="3000" beacon-path="/bot-detection/fingerprint" />
```

| Attribute | Purpose |
|---|---|
| `provider` | One of `adsense`, `amazon`, `medianet`; resolves to the named ad-network URL |
| `client-id` | Publisher ID for the provider (required for `adsense` and `medianet`) |
| `probe-url` | Exact URL to probe; overrides `provider` + `client-id` |
| `timeout-ms` | Ms before the probe declares "blocked"; clamped to 250-30000 |
| `beacon-path` | Override the beacon endpoint (default `/bot-detection/fingerprint`) |

The probe is wrapped in an IIFE with a try/catch so a CSP block, missing `sendBeacon`, or JSON serialisation error never escapes; a probe failure must not break the host page. See [adblocker-detection.md](adblocker-detection.md) for the full design and provider table.

### UI tag helpers in `Mostlylucid.BotDetection.UI.TagHelpers`

These consume the per-request verdict (read from `HttpContext.Items`) and render UI. None of them depend on the client-side script; they work on every request after server-side detection has run. They live in the UI assembly so projects that wire detection but not the dashboard can opt out.

#### Verdict gating: `<sb-gate>` and friends

The verdict-gating family conditionally renders content based on the detection outcome. Use `<sb-gate>` for compound conditions, the convenience tags for simple cases.

```html
@addTagHelper *, Mostlylucid.BotDetection.UI

@* Compound condition *@
<sb-gate human-only="true" min-risk="Low">
  <p>Welcome back. Here is your personalised content.</p>
</sb-gate>

@* Simple cases *@
<sb-human>Welcome, human.</sb-human>
<sb-bot fallback="Hello, automation.">Hello, automation.</sb-bot>

@* By risk band *@
<sb-risk min="Medium">High-risk visitor: extra friction will be applied.</sb-risk>

@* By signal *@
<sb-signal signal="clientside.botd_kind" condition="present" fallback="--">
  Visitor was classified by BotD as: ${value}
</sb-signal>
```

| Tag | Purpose | Key attributes |
|---|---|---|
| `<sb-gate>` | Compound verdict gate | `human-only`, `bot-only`, `min-risk`, `max-risk`, `bot-type`, `verified-only`, `fallback`, `negate` |
| `<sb-human>` | Render only when verdict is human | `fallback` |
| `<sb-bot>` | Render only when verdict is bot | `fallback` |
| `<sb-risk>` | Render only when in band range | `band`, `min`, `max`, `fallback` |
| `<sb-signal>` | Render based on signal presence/value | `signal`, `condition`, `value`, `fallback`, `negate` |

#### Verdict surfacing: badges, pills, summaries

These render the verdict itself (typically for the dashboard, customer-facing trust pages, or debug panels).

| Tag | Purpose |
|---|---|
| `<sb-badge>` | Compact verdict badge with variant styling |
| `<sb-confidence>` | Confidence bar (numeric % or progress bar) |
| `<sb-risk-pill>` | Single risk-band pill |
| `<sb-summary>` | Inline "Bot 87% (Medium risk)" summary |
| `<sb-tooltip>` | Universal attribute (`sb-tooltip="..."`); adds tooltip to any element |
| `<bot-detection-header>` | One-line header bar with verdict + confidence |
| `<bot-detection-details>` | Collapsible expert panel; full signal dump + breakdowns |

#### Honeypot fields: `<sb-honeypot>`

Renders zero-style fields with names from a configurable list. Real users never fill them; bots that auto-fill forms will. Submissions are inspected by the matching middleware and feed a `honeypot.tripped` signal back into detection.

```html
<sb-honeypot prefix="contact_" fields="phone,company,website" />
```

#### Live updates: `<sb-live-updates>`

Wires up the dashboard's SignalR client. Reconnect / debounce / refresh cadence is configurable; the partial knows the hub URL from the BotDetection UI options. Used inside the dashboard chrome; not normally needed in customer-facing pages.

```html
<sb-live-updates hub-url="/_stylobot/hub"
                 debounce="250"
                 refresh-interval="10000"
                 show-status="true" />
```

See [signalr-beacon-architecture.md](signalr-beacon-architecture.md) for the broadcast contract.

#### `<bot-ticker>`

Live-updating count of bots seen in the last N minutes; cosmetic widget for landing pages or status bars.

### A complete Razor page

```cshtml
@page
@addTagHelper *, Mostlylucid.BotDetection
@addTagHelper *, Mostlylucid.BotDetection.UI

@{
    var nonce = HttpContext.GetCspNonce(); // your own helper, however you do CSP
}

@* Emit the detection script *@
<bot-detection-script nonce="@nonce" />

@* Surface adblocker presence to suppress the no-fingerprint penalty *@
<sb:adblock-probe provider="adsense" client-id="ca-pub-1234567890" />

@* Honeypot fields *@
<form method="post" asp-action="Submit">
    <input name="email" type="email" />
    <sb:adblock-probe provider="amazon" />
    <sb-honeypot prefix="contact_" fields="phone,company,website" />
    <button type="submit">Send</button>
</form>

@* Gate premium content on human verdict + reasonable risk *@
<sb-gate human-only="true" max-risk="Medium">
    <partial name="_PremiumPanel" />
</sb-gate>

<sb-bot fallback="">
    <p>For best experience, please disable automation tools and try again.</p>
</sb-bot>
```

---

## 3. SDKs

The SDKs all consume the canonical REST API (`/api/v1/*`) or the gRPC `DetectionService`, with a "headers" mode for hosts sitting behind the StyloBot gateway. The gateway injects 9 `X-StyloBot-*` headers onto every upstream request; the headers mode just parses them, which is the lowest-latency path.

### `@stylobot/core`

Zero-runtime-dep types + client + header parser, in `sdk/node/packages/core`. Works in Node / Deno / Bun. MIT-licensed.

```ts
import {
  StyloBotClient,         // REST client for /api/v1/*
  StyloBotGrpcClient,     // gRPC client (optional peer dep on @grpc/grpc-js)
  parseStyloBotHeaders,   // header-mode parser
  type Verdict,
  type DetectResponse,
} from '@stylobot/core';

// REST mode
const client = new StyloBotClient({
  endpoint: 'https://api.example.com',
  apiKey: process.env.STYLOBOT_API_KEY,
  timeout: 5000,
});
const r = await client.detect({ ip: '203.0.113.5', userAgent: req.headers['user-agent'], headers: req.headers });

// gRPC mode (persistent HTTP/2 connection; sub-ms localhost calls)
const grpc = new StyloBotGrpcClient('localhost:5090', 5000);
const verdict = await grpc.detect(detectReq);

// Header mode (zero-latency; behind the gateway)
const verdict2 = parseStyloBotHeaders(req.headers);
```

The client surface includes the same endpoints the dashboard uses: `detect`, `detectBatch`, `detections`, `signatures`, `summary`, `timeseries`, `countries`, `endpoints`, `topBots`, `threats`, `me`, plus `renderWidgets` for partial rendering from a remote viewer.

### `@stylobot/node`

Express middleware + Fastify plugin, in `sdk/node/packages/node`. Depends on `@stylobot/core`. Three deployment modes:

```ts
import { styloBotMiddleware, styloBotPlugin } from '@stylobot/node';

// Express, headers mode (behind StyloBot gateway)
app.use(styloBotMiddleware({ mode: 'headers' }));

// Express, REST mode (calling a sidecar)
app.use(styloBotMiddleware({
  mode: 'api',
  endpoint: 'http://stylobot-sidecar:5080',
  apiKey: process.env.STYLOBOT_API_KEY,
}));

// Express, gRPC mode (persistent HTTP/2 to sidecar)
app.use(styloBotMiddleware({
  mode: 'grpc',
  endpoint: 'localhost:5090',
}));

// Fastify
await fastify.register(styloBotPlugin, { mode: 'headers' });

// Then in any handler:
app.get('/', (req, res) => {
  if (req.stylobot.isBot) return res.status(429).send('rate limited');
  res.send(`risk band: ${req.stylobot.verdict.riskBand}`);
});
```

All three modes attach the same `StyloBotResult` to `req.stylobot` so handlers do not care which mode is in use. On failure the middleware fails open (the verdict is "Allow") so a sidecar outage does not take the site down.

The package also exports:
- `SbSsrCoordinator`: batches widget-render requests to a remote gateway so SSR partials can be fetched in one round-trip instead of N.
- `sbVerdictInjector`: middleware that hydrates `res.locals.sbVerdict` and `res.locals.sbVerdictScript` for templating engines (EJS, Handlebars, etc.).
- `extractDetectRequest`: turns an Express/Fastify request into the canonical `DetectRequest` shape for direct client use.

### `@stylobot/elements`

Framework-agnostic web components, in `sdk/node/packages/elements`. Mirrors the Razor `sb-*` tag helpers for non-.NET hosts (any HTML page can import the bundle and use the elements). MIT-licensed.

```html
<script type="module" src="/lib/stylobot-elements.js"></script>

<sb-gate human-only>
  <p>Premium content.</p>
</sb-gate>

<sb-case verdict="bot">
  <p>Bot-only message.</p>
</sb-case>

<sb-widget id="threat-summary" template="threats-summary"></sb-widget>
```

The package registers four custom elements: `sb-gate`, `sb-case`, `sb-adapt`, `sb-widget`. `sb-widget` uses the shared `sbCoordinator` to batch partial-render requests to the gateway's `/_stylobot/partials/render` endpoint, so a page with 12 widgets makes one HTTP call.

### `sdk/caddy`

Caddy v2 module that calls the StyloBot gRPC `DetectionService` on every request, injects the 9 `X-StyloBot-*` headers onto the upstream request, and optionally blocks bots. Per-request overhead is typically under 0.5ms (persistent HTTP/2 to a localhost sidecar; no handshake per call).

```caddy
example.com {
    stylobot {
        sidecar localhost:5090
        timeout 250ms
        on_block 429
    }
    reverse_proxy upstream:8080
}
```

The handler fails open: if the sidecar is unreachable or times out the request forwards unchanged. The site stays up even if the sidecar is down.

Build with xcaddy: `xcaddy build --with github.com/mostlylucid/stylobot/sdk/caddy`. See `sdk/caddy/README.md` for the full Caddyfile reference, gRPC vs REST tradeoffs, and operational notes.

### `sdk/go`

Pure-Go client for the same gRPC `DetectionService`. Used by the Caddy module and available standalone for any Go program that wants to make detection calls.

```go
import "github.com/mostlylucid/stylobot/sdk/go"

client, _ := stylobot.NewClient(stylobot.WithEndpoint("localhost:5090"))
v, _ := client.Detect(ctx, &stylobot.DetectRequest{
    IP:        "203.0.113.5",
    UserAgent: r.UserAgent(),
})
if v.IsBot { /* ... */ }
```

### `sdk/proto`

The canonical `.proto` file (`detection.proto`) defining the gRPC service. Any language can generate a client from it: Python, Rust, Java, .NET. Run `protoc --proto_path=sdk/proto --python_out=. sdk/proto/detection.proto` (or your language's equivalent) and you have a client.

The service is what `Stylobot.Sidecar` exposes; the Node `@stylobot/core` gRPC client and the Caddy module both consume it.

---

## End-to-end wiring scenarios

### Scenario A: Razor app, all-in-one (`Stylobot.All`)

Single process: detection + dashboard + your app. The simplest deployment.

```csharp
builder.Services.AddStyloBot();
// ... your app services ...

app.UseRouting();
app.UseStyloBot();
app.MapRazorPages();
app.MapDefaultControllerRoute();
```

Razor pages use the tag helpers above. The detection script is served from the same process. Dashboard lives at `/_stylobot`.

### Scenario B: Gateway in front, Node app behind

`stylobot` (the Console binary, AOT) runs on the edge, calling YARP to your Node upstream. The gateway injects `X-StyloBot-*` headers; your Node app reads them.

```bash
stylobot 5080 http://node-app:3000 -d
```

```ts
// Node app
import express from 'express';
import { styloBotMiddleware } from '@stylobot/node';

const app = express();
app.use(styloBotMiddleware({ mode: 'headers' }));
app.get('/', (req, res) => res.send(`Bot: ${req.stylobot.isBot}`));
```

Zero latency on the Node side: the headers are already on the request. Detection ran once at the gateway.

### Scenario C: Sidecar (gRPC) for any non-.NET app

`stylobot-sidecar` (AOT) runs alongside your app on the same host. Your app calls the gRPC service per request.

```ts
app.use(styloBotMiddleware({
  mode: 'grpc',
  endpoint: 'localhost:5090',
}));
```

Or in Caddy:

```caddy
example.com {
    stylobot { sidecar localhost:5090 }
    reverse_proxy upstream:8080
}
```

### Scenario D: Remote dashboard viewer (`Stylobot.Ui`)

The viewer reads everything from a remote gateway's `/api/v1/*`. The `<bot-detection-script>` tag helper detects the absence of `IBrowserTokenService` and suppresses cleanly. The dashboard surfaces the gateway's data; no detection runs in the viewer process.

```json
{
  "StyloBot": {
    "Source": {
      "Pull": { "Type": "rest", "BaseUrl": "https://gateway.example.com" },
      "Live": { "Type": "signalr", "HubUrl": "https://gateway.example.com/api/v1/hub" }
    }
  }
}
```

---

## Security considerations

### Token spoofing

The fingerprint endpoint requires a signed token. Without one, a bot could trivially post a "perfect human" payload and game the verdict. Tokens are:

- HMAC-SHA256, bound to IP-hash + request id + expiry
- Single-use (cached for the lifetime window)
- Accepted from header OR body (the body path is the `sendBeacon` fallback)

Configure `BotDetection.ClientSide.TokenSecret` to at least 32 characters. Without it, a random per-instance key is generated and tokens do not survive restarts.

### CSP

Two things are inlined to the page: the bootstrap (a single object literal, no logic) and the adblocker probe (a tight IIFE). Both accept a nonce; emit it on both. The detector itself loads from `/bot-detection/script.js` and is `script-src 'self'`-compatible.

Recommendation:
```
script-src 'self' 'nonce-RANDOM' 'strict-dynamic';
connect-src 'self' stun:stun.example.com:3478;
```

With `'strict-dynamic'` the nonce on the bootstrap propagates to the loaded script; you do not need to allow-list the external script path. The `connect-src` line is only needed if you keep WebRTC ICE probing enabled.

### Beacon spoofing

The endpoint validates the token before accepting any field. The adblocker probe specifically takes a short-circuit path (`Adblocker=true, Timestamp=0`) so a forged adblocker beacon only flips the `clientside.adblocker_detected` signal, which suppresses the no-fingerprint penalty but does not by itself convert a bot verdict to human.

### BotD / cross-origin fetch

`Botd.Enabled = false` by default so existing CSP configurations do not suddenly attempt a cross-origin fetch to `openfpcdn.io`. Enable it after self-hosting the bundle (`/lib/botd.min.js`) to keep your CSP free of third-party origins.

### ICE STUN

The WebRTC ICE probe attempts a UDP connection to whatever `BotDetection.ClientSide.IceStunServerUrl` points to (default Google's public STUN). Privacy-sensitive deployments (finance, healthcare) should override this with a self-hosted coturn inside their compliance boundary. Set to empty string to disable the probe entirely.

### Honeypot field names

`<sb-honeypot>` reuses the configured prefix + field-name list across the site. A sophisticated bot could memoise them. The signal still works (auto-fill behaviour is a positive signal); just do not rely on it as the only line of defence. Use the standard honeypot field hygiene: random-ish names, CSS-hidden + `tabindex="-1"` + `autocomplete="off"`.

---

## What's not covered here

- **Probe details (what each probe targets, weight tuning, cloak coverage)**: [cloak-detection.md](cloak-detection.md).
- **Adblocker probe deep-dive (provider table, filter-list URLs, edge cases)**: [adblocker-detection.md](adblocker-detection.md).
- **SignalR / live-updates contract**: [signalr-beacon-architecture.md](signalr-beacon-architecture.md).
- **Server-side fingerprint/identity layer (not client-side)**: [identity-fingerprint-match.md](identity-fingerprint-match.md).
- **Full REST API surface for SDK consumers**: `/api/v1/openapi.json` (live) or the OpenAPI doc in the repo.
