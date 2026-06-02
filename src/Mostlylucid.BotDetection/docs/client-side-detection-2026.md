# Client-Side Detection in 2026

> Captured from a research pass June 2026. Drives the upcoming client-side
> script rewrite (`botdetection.js` modernisation, the PoW + adblocker probe
> variants). Source URLs in the synthesis at the bottom — keep them; the
> 2025–2026 landscape moves fast.

## What still works (collect these)

| Signal | Notes |
|---|---|
| **CDP fingerprinting** — `console.debug` getter trap, `Error().stack` foreign-frame inspection | The 2026 anchor signal for headless detection. Catches Puppeteer / Playwright / Selenium reliably; Nodriver bypasses `Runtime.Enable` but still leaks stack frames. |
| **UA-CH `getHighEntropyValues`** | Request `architecture, bitness, model, platformVersion, fullVersionList, formFactors`. Bots routinely stub or inconsistent-with-UA. |
| **Touch + pointer consistency** | Mobile UA + `maxTouchPoints === 0` + `'ontouchstart' in window === false` is the single best mobile-bot tell. Combine with `PointerEvent.prototype` presence. |
| **BroadcastChannel + Web Locks timing** | Underused. Stealth shims don't touch them yet. Open a BroadcastChannel + Web Lock from the same context and measure echo latency. |
| **`Permissions.query` state vectors** | Query 6 permissions (clipboard, notifications, geolocation, camera, microphone, push). Real users have at least one `"granted"`; all-`"prompt"` is suspicious. |
| **`performance.now()` clamp-residue distribution** | Chrome 100µs, Firefox 20µs, Safari 1ms. Sample 10k subtractions; the modal delta-of-deltas distribution leaks the actual browser. Spoofed UAs fail this badly. |
| **Chromium-only feature cross-checks** | `document.startViewTransition`, Speculation Rules API, `document.hasStorageAccess`. Present in Chromium, absent in Safari/Firefox. Cheap UA-spoofing detection. |
| **AudioContext fingerprint** | Still usable on Chrome + Safari. Firefox RFP / Brave farbles. FFT-implementation entropy per architecture is real. OfflineAudioContext + DynamicsCompressor is the classic hash. |
| **Headless Chrome `--headless=new` markers** | Combine 3+: `navigator.plugins.length === 0`, `Notification.permission === "denied"`, missing `chrome.runtime`, GPU renderer `SwiftShader` / `ANGLE (Google, Vulkan)`. No single one is enough. |
| **`window.parent !== window` + `hasStorageAccess`** | Distinguishes legitimate top-frame from sandboxed bot. |

## Dead weight (delete these)

| Signal | Why it's dead |
|---|---|
| Raw `User-Agent` parsing for build / OS minor version | Chrome UA Reduction froze all of it. 100% of Chrome. Use UA-CH. |
| Naive `navigator.webdriver === true` as sole verdict | Patched by every modern bot framework. Keep collecting (~5% of bots still leave it) but never treat `false` as evidence of a human. |
| Third-party iframe fingerprint persistence | Storage partitioning killed it across all majors. First-party only. |
| Canvas-hash drift as a bot signal | Brave farbles legitimately; adaptive farbling in 2026 specifically increases drift when detection attempts are observed. Treat drift as a Brave signal, not a bot signal. |
| Long-lived `document.cookie` sessions on Safari | ITP capped JS-set cookies at 7 days. Session-scoped only. |
| Topics / PAAPI / Attribution Reporting | Privacy Sandbox shut down October 2025. **FedCM + CHIPS survived** and are useful (FedCM presence on a "Safari" UA = spoofing). |

## New collection targets (2026-fresh)

Add these to the client-side script:

- **BroadcastChannel echo latency** — open channel, post message, measure round-trip
- **Web Lock acquisition timing** — same idea via `navigator.locks.request`
- **`getHighEntropyValues({hints})`** — collect six high-entropy UA-CH dimensions
- **Permissions-API state vector** — query 6 permissions, record the all-prompt vs has-granted pattern
- **CDP getter trap** — `Object.defineProperty(console, 'debug', { get() { /* called by DevTools */ } })`
- **Error stack foreign-frame probe** — synthesise an Error, look for `at Object.apply` / `at <anonymous>` patterns that bot frameworks leak
- **Chromium-feature presence triple** — `startViewTransition`, Speculation Rules, Storage Access. Cross-check against UA brand.
- **`performance.now()` clamp probe** — 10k tight-loop subtractions; bucket the delta-of-deltas; export the modal value.

## Legitimate-user classifiers (don't penalise these)

- **`navigator.brave?.isBrave()`** returns true → score canvas drift differently, expect noised audio
- **Lockdown Mode** detection (WebGL + WebAudio + JIT all disabled simultaneously on iOS UA) → don't fail-closed on missing fingerprint surfaces
- **Firefox ETP Strict** detected via blocked Disconnect-list scripts → treat font/hardware enumeration spoofs as legitimate
- **Pi-hole / adblocker** (already shipped via `<sb:adblock-probe>`) → suppress no-fingerprint penalty

## Constraints on the script itself

- **CSP-friendly**: support `script-src 'self'` deployments via nonce + `'strict-dynamic'`. Inline-only scripts fail silently on ~15% of enterprise targets (banking, gov).
- **First-party hosting only**: storage partitioning means a third-party detection CDN can't share fingerprint state across origins anyway. Host same-origin.
- **No cross-site state assumption**: every fingerprint session is per-origin. Server-side correlation does the cross-request work.

## Browser-restriction cheat sheet

| Browser / mode | Affected % | What breaks | What still works |
|---|---|---|---|
| Safari ITP (all Safari users) | ~18% global | JS cookies > 7 days, localStorage > 7 days on idle sites | HttpOnly server-set cookies, server-side fingerprint correlation |
| Brave Shields | ~1-2% (heavy bot-detection-aware) | Canvas / WebGL / audio hashes farbled per-eTLD+1; adaptive | UA-CH, behaviour, headers, all server-side signals |
| Firefox ETP Strict (+ FF 145 Phase 2) | ~3% growing | Disconnect-listed fingerprint script blocks, font / hardware enumeration | Anything served first-party from a non-fingerprinter domain |
| Chrome UA Reduction (default) | 100% Chrome | Raw UA minor version frozen | UA-CH `getHighEntropyValues` |
| iOS Lockdown Mode | <1% but high-value | WebGL, WebAudio, JIT, SpeechRecognition | TLS, header order, server signals |
| Strict CSP (enterprise) | ~15% | Inline scripts | Same-origin file + nonce + `'strict-dynamic'` |

## Sources

DataDome (Headless Chrome & CDP) · Castle.io (Puppeteer-stealth → Nodriver) ·
MDN (NavigatorUAData) · Chromium UA Reduction · Brave farbling +
adaptive parity (2026) · Mozilla FF 145 fingerprinting protections · WebKit
ITP cookie cap · AdExchanger (Privacy Sandbox shutdown October 2025) ·
Cloudflare ZK-WebAuthn · Mozilla performance.now() clamping bug · MDN
Speculation Rules · Ian Paterson anti-detect benchmark 2026 · Scrapfly
audio fingerprint reference.
