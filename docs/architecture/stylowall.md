# StyloWall: Semantic Firewall

> Status: design-draft (v0.1 scaffold landed on `claude/stylowall-optimization-pack-HLvP7`)
>
> Owner: scott@mostlylucid.com

## Summary

StyloWall is a content-aware response layer that sits in front of any HTTP origin and rewrites responses based on what the request actually is. It runs alongside StyloBot: StyloBot decides *who* the visitor is, StyloWall decides *what content the response should carry*.

The shipping target is **"modernise the edge without touching the origin"** — drop StyloWall on a £5 VPS, point a Cloudflare Tunnel back at the legacy site, close the origin's public IP, and the site picks up markdown delivery, image optimization, minification, and bot-aware shaping without a single backend change.

This document covers the architecture, the first optimization pack (HTML → Markdown), and the roadmap for follow-on packs.

## Why a semantic firewall (and why now)

Traditional WAFs reject or pass requests. StyloWall *rewrites* them — the moral equivalent of Cloudflare's Workers / Transform Rules / Auto-Minify / Polish stack, but:

- Open-source and self-hosted (Unlicense, ships as NuGet packages)
- Driven by StyloBot's behavioural classifier (49 detectors, 129-dim session vectors), not a regex ruleset
- Stateless at the transform layer; all state lives in StyloBot's SQLite
- A response transform pipeline (`IResponseTransform`) you can extend in C#

The "now" is AI scrapers. Cloudflare ship a /llms.txt convention and an "AI Audit" product. Sites are getting hammered by GPTBot/ClaudeBot/Perplexity-User and the cheapest-correct answer — for the bot and the site — is to serve markdown when markdown is what they want. StyloBot already classifies these crawlers; StyloWall is the natural place to put the response side of that conversation.

## Position in the request pipeline

```
Request
  │
  ├── UseStyloBot()        — detection populates HttpContext.Items
  │                          (BotType, ConfidenceScore, signals, action policy)
  │
  ├── UseStyloWall()       — gate evaluates triggers; if no transform needed,
  │                          straight pass-through (zero buffering)
  │                          if yes, buffers response, runs IResponseTransform chain,
  │                          rewrites Content-Type / Content-Length / body
  │
  └── (inner pipeline)     — origin handler / YARP proxy / static files
```

Key invariants:

1. **Zero cost when off.** If the gate returns `PassThrough`, the response stream is not swapped. No allocation, no buffering, no extra middleware in the body path.
2. **Buffer once.** When a transform is selected, the response body is buffered into a pooled `ArrayPool<byte>`-backed stream up to `MaxBufferBytes` (default 4 MiB). Anything larger errors and falls through to pass-through.
3. **Transforms are pure.** `IResponseTransform.TransformAsync(ctx, ct)` takes a `ReadOnlyMemory<byte>` and returns a `TransformResult` (body + content-type + encoding). No side-effects on `HttpContext`. This keeps caching trivial.
4. **No origin changes.** The site behind StyloWall doesn't need to be modified — not its templates, not its `<head>`, not its routing.

## Triggers (what causes a transform to fire)

The gate is configured via `StyloWallOptions.Triggers`. Four sources, evaluated in this order:

| # | Trigger | Notes |
|---|---|---|
| 1 | `?format=md` query string | Dev/debug + public alternate URL convention |
| 2 | `Accept: text/markdown` header | **The primary trigger.** Any client opting in. RFC-correct content negotiation. |
| 3 | Route rules (config) | `Routes: [{ PathPrefix: "/blog/", Mode: "markdown" }]` — owner-driven. |
| 4 | Detection verdict | `BotType == AiBot && ConfidenceScore >= MinBotProbability` — optional, off-by-default-in-strict-mode. |

The **product position** for v1 is "add markdown to any site by adding `Accept: text/markdown` support without touching the origin." Verdict-driven transformation is available but should not be the default story — it surprises operators when their analytics show responses they didn't author. We document it as an opt-in.

## Configuration surface

```jsonc
{
  "StyloWall": {
    "Enabled": true,
    "MaxBufferBytes": 4194304,
    "Triggers": {
      "AcceptHeader": true,
      "QueryString": true,
      "QueryStringKey": "format",
      "DetectionVerdict": false,        // opt-in
      "MinBotProbability": 0.75
    },
    "Routes": [
      { "PathPrefix": "/blog/", "Mode": "markdown" }
    ],
    "Optimization": {
      "Markdown": {
        "PreferMainContent": true,
        "EmitTables": true,
        "EmitFrontMatter": true,
        "LinkStyle": "inline",
        "ContentSelectors": ["main", "article", "[role=main]"],
        "StripSelectors": ["nav", "footer", "aside", ".cookie-banner"],
        "CodeLanguageSources": ["class:language-", "class:lang-", "data-lang"],
        "ImageRewrite": {
          "ResolveRelativeAgainst": "request",
          "BaseUrl": null
        },
        "FrontMatter": {
          "TitleSource": "og:title|<title>",
          "DescriptionSource": "og:description|meta[name=description]",
          "ExtractCanonical": true
        }
      }
    }
  }
}
```

The point of the `Markdown` block is that **site owners can tune the extractor without writing code**. The doc-walker reads CSS-like selectors, not bespoke logic. This is what the user meant by "in config they can do stuff like add css path to make it better."

## The first pack: `Mostlylucid.StyloWall.Optimization`

Ships one transform in v0.1, named `HtmlToMarkdownTransform`, `Mode = "markdown"`. Built on the AngleSharp dep already present in `Mostlylucid.BotDetection` (no new package surface for this transform).

### Walker design

The emitter is a single-pass DOM walker, not a regex pipeline:

1. Parse with AngleSharp (HTML5-spec, tolerates broken markup).
2. Choose content root: walk `ContentSelectors` in order; first match wins. Falls back to `<body>`.
3. Walk children, dispatching on tag name to one of:
   - **Block emitters**: `h1-h6`, `p`, `pre`, `ul/ol`, `blockquote`, `hr`, `table`, `figure`
   - **Inline emitters**: `strong/b`, `em/i`, `code`, `a`, `img`, `br`, `del/s`
   - **Skipped tags**: `script`, `style`, `noscript`, `iframe`, `svg`, `canvas`, `template`, `head`, plus user-configured `StripSelectors`
   - **Transparent containers**: `div`, `section`, `article`, `main`, `header`, `figure`
4. Inline text is whitespace-collapsed and Markdown-punctuation escaped.
5. Output: GFM (tables, fenced code, strikethrough).

### Things the walker handles correctly

- Nested lists (depth-capped to prevent runaway HTML)
- Blockquote nesting (`> > foo`)
- Fenced code with language hints from `class="language-*"` / `class="lang-*"` / `data-lang`
- Tables with `<thead>`/`<tbody>`/`<tfoot>` and `<th>` headers
- Inline-content tables (the cell content is walked through the inline path)
- Per-link `title` attributes (`[text](url "title")`)
- Image `alt` text preservation

### Things v0.1 doesn't do (call out before users find them)

- Reference-style links (`[1]: url`) — inline only for now
- Footnotes (`[^1]`)
- Definition lists (`<dl>`) — passes through as paragraphs
- Math (`<math>`, KaTeX, MathJax) — passes through as text; future work
- Embedded SVG content — skipped entirely (could be base64-inlined, future work)
- Image rewriting to ImageSharp.Web URLs — separate transform (`ImageRewriteTransform`, planned)
- Front-matter generation from OpenGraph — option exists, not implemented in v0.1

## Roadmap: further packs

| Pack | Transform | Notes |
|---|---|---|
| Optimization (v0.1) | `HtmlToMarkdownTransform` | Shipped here. |
| Optimization (v0.2) | `LlmsTxtBuilder` | Background service that walks sitemap.xml, materializes `/llms.txt` + `/llms-full.txt` (markdown bundle). |
| Optimization (v0.3) | `ImageRewriteTransform` | Rewrites `<img src="...">` to ImageSharp.Web-resized URLs based on `Accept`/`Sec-CH-DPR`/`Save-Data`. Combines with Markdown emitter (URL rewriting happens before MD emission). |
| Optimization (v0.4) | `HtmlMinifyTransform` | Removes inter-tag whitespace, strips comments, collapses attributes. Off by default for `<pre>`/`<textarea>`. |
| Optimization (v0.5) | `EarlyHintsTransform` | Parses outgoing HTML for critical CSS/JS, emits `103 Early Hints` on the next request from the same client. |
| Security (future) | `CspInjector` | Auto-inject CSP based on observed inline scripts; report-only first, enforce after a learning window. |
| Compliance (future) | `PiiRedactor` | Strip leaked PII from HTML/JSON responses using StyloBot's `PiiQueryString` analyser inverted. |
| Holodeck integration | `MarkdownHolodeck` | When a request lands in holodeck *and* asks for markdown, generate markdown decoy content. Pairs `Mode = "markdown"` with `IHolodeckResponder`. |

Each pack is its own NuGet package, depends only on `Mostlylucid.StyloWall` for abstractions, and is registered with one line:

```csharp
services.AddStyloWall();
services.AddStyloWallOptimization();   // first pack
services.AddStyloWallSecurity();       // future
```

## Deployment story: £5 VPS + tunnel

The product narrative the team is leading with:

1. Spin up a £5/mo VPS (Hetzner / Netcup / Contabo, 2 vCPU / 4 GB).
2. Run `stylobot-all` (StyloBot detection + dashboard + YARP gateway + StyloWall) as a single binary.
3. Configure a Cloudflare Tunnel from the VPS back to the legacy origin.
4. Remove the origin's public DNS / firewall rules.

Outcome:

- Origin disappears from the internet — no exploit-scanner traffic ever reaches it.
- StyloBot tells you what's hitting the edge (49 detectors, world map, threat tab).
- StyloWall serves markdown to AI scrapers, optimized images to mobile, original HTML to humans.
- Site team made zero changes to the application.

This is the deployment shape the docs/marketing copy will showcase. The single-binary `stylobot-all` already exists; StyloWall middleware drops into its pipeline with two lines (`AddStyloWall()` + `UseStyloWall()`).

## What's already on the branch vs. what this doc implies

| Doc says | Branch has | Gap |
|---|---|---|
| `Accept: text/markdown` primary trigger | ✅ implemented in `StyloWallGate` | — |
| `?format=md` query trigger | ✅ implemented | — |
| Route rules trigger | ✅ implemented (string prefix match) | — |
| Detection verdict trigger | ✅ implemented (AiBot + MinBotProbability) | Off-by-default should be the doc'd default; currently `true` in `TriggerOptions`. Flip before v1. |
| AngleSharp custom walker | ✅ implemented (~400 LOC) | — |
| `ContentSelectors` / `StripSelectors` from config | ⚠️ hard-coded to `main`/`article`/`[role=main]` and a static skip-set in the walker | Promote to `MarkdownEmitterOptions`. |
| `CodeLanguageSources` config | ⚠️ hard-coded to `language-*` / `lang-*` | Promote. |
| Front-matter from OpenGraph | ⚠️ only `<title>` extracted today | Implement OG/meta extraction. |
| `LlmsTxtBuilder`, `ImageRewriteTransform`, etc. | ❌ not yet | Future packs. |

## Open questions

1. **Cache strategy.** Transformed responses are pure functions of `(body, mode, options)`. ETag-keyed LRU at the middleware level is the obvious win — but it shouldn't be on the StyloWall hot path itself. Probably a separate `IStyloWallCache` abstraction with a default no-op impl and a memory-backed impl in the Optimization pack.
2. **Streaming vs. buffering.** v0.1 buffers. For very large HTML (>4 MiB) we currently pass through unchanged. AngleSharp can't reasonably stream HTML5 parsing, so the right escape hatch is a configurable upper bound + an "always pass through these paths" exclusion list.
3. **Interaction with YARP response transforms.** When StyloWall lives inside `Stylobot.Gateway` (YARP), the body modification happens *after* YARP buffers the upstream response. Need a doc test that confirms ordering and a YARP `IResponseTransform` adapter so the same code runs in both contexts.
4. **What does the dashboard show?** A "transforms" tab summarizing modes served (HTML, markdown, image-rewritten, minified), bytes saved, latency overhead, top routes by transform. Belongs in `Mostlylucid.BotDetection.UI` once a pack is loaded.

## References

- StyloBot architecture overview — `CLAUDE.md` at repo root
- Foundation vs classifier contract — `docs/architecture/signal-contracts.md`
- Fingerprint identity layer — `docs/architecture/fingerprint-match.md`
- Cloudflare AI Audit / llms.txt convention — public docs
- W3C Save-Data header — for ImageRewriteTransform
