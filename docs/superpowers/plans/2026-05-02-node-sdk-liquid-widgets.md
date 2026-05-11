# Node SDK Liquid Widgets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Liquid template rendering to the StyloBot widget system, extend the Node SDK with an SSR coordinator and verdict injector, add a @stylobot/elements web components package, build an Express sample app demonstrating all modes, and write a complete tutorial/manual.

**Architecture:** The ASP.NET server gains a POST /_stylobot/partials/render endpoint that accepts Liquid templates, fetches its own data, renders with Fluid, and returns HTML. The Node SDK gains renderWidgets() on StyloBotClient, an SSR coordinator that collects widget declarations and fires one batch POST, and a verdict injector middleware. Web components (sb-widget, sb-gate, sb-adapt) provide a framework-agnostic client-side path with a batch coordinator that fires one request on DOMContentLoaded.

**Tech Stack:** ASP.NET 10 + Fluid.Core (Liquid), Node.js 22 + TypeScript (experimental strip-types), Express 5, Web Components (vanilla), node:test runner.

**Working directory:** .worktrees/feat-node-sdk-liquid/ for all file operations.

---

## File Map

### ASP.NET (Mostlylucid.BotDetection.UI)

| Action | File |
|--------|------|
| Modify | Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -add Fluid.Core PackageReference |
| Create | Mostlylucid.BotDetection.UI/Services/LiquidWidgetRenderer.cs -Fluid parser + per-widget context builders |
| Modify | Mostlylucid.BotDetection.UI/Middleware/SbWidgetBatchMiddleware.cs -add POST branch for Liquid rendering |
| Modify | Mostlylucid.BotDetection.UI/Extensions/ServiceCollectionExtensions.cs -register LiquidWidgetRenderer singleton |

### Node SDK @stylobot/core

| Action | File |
|--------|------|
| Modify | sdk/node/packages/core/src/types.ts -add WidgetTemplate, WidgetRenderRequest, ClientMode |
| Modify | sdk/node/packages/core/src/client.ts -add renderWidgets() and verdictGlobal() |

### Node SDK @stylobot/node

| Action | File |
|--------|------|
| Create | sdk/node/packages/node/src/coordinator.ts -SbSsrCoordinator class |
| Create | sdk/node/packages/node/src/injector.ts -sbVerdictInjector Express middleware |
| Modify | sdk/node/packages/node/src/index.ts -export coordinator and injector |

### New package @stylobot/elements

| Action | File |
|--------|------|
| Create | sdk/node/packages/elements/package.json |
| Create | sdk/node/packages/elements/tsconfig.json |
| Create | sdk/node/packages/elements/src/coordinator.ts -client-side batch coordinator singleton |
| Create | sdk/node/packages/elements/src/sb-gate.ts -sb-gate custom element |
| Create | sdk/node/packages/elements/src/sb-adapt.ts -sb-adapt and sb-case custom elements |
| Create | sdk/node/packages/elements/src/sb-widget.ts -sb-widget custom element |
| Create | sdk/node/packages/elements/src/index.ts -register all elements + export |

### Sample app

| Action | File |
|--------|------|
| Create | sdk/node/samples/express/package.json |
| Create | sdk/node/samples/express/tsconfig.json |
| Create | sdk/node/samples/express/src/server.ts |
| Create | sdk/node/samples/express/src/templates/summary.liquid |
| Create | sdk/node/samples/express/src/templates/topbots.liquid |
| Create | sdk/node/samples/express/public/csr.html |

### Documentation

| Action | File |
|--------|------|
| Create | sdk/node/README.md -full tutorial/manual |
| Create | sdk/node/docs/data-contexts.md -Liquid variable reference per widget |

---

## Task 1: Add Fluid.Core to the UI project

**Files:** Modify Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj

- [ ] Add inside the existing PackageReference ItemGroup:

```xml
<PackageReference Include="Fluid.Core" Version="2.7.0" />
```

- [ ] Restore:

```bash
dotnet restore Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: no errors.

- [ ] Commit:

```bash
git add Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
git commit -m "chore(ui): add Fluid.Core for Liquid widget rendering"
```

---

## Task 2: Create LiquidWidgetRenderer

**Files:** Create Mostlylucid.BotDetection.UI/Services/LiquidWidgetRenderer.cs

LiquidWidgetRenderer accepts a widget name, Liquid template string, and data dictionary; parses (and caches by content hash) the template; renders it with Fluid; returns the HTML string.

- [ ] Create the file:

```csharp
using System.Collections.Concurrent;
using Fluid;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.UI.Services;

public sealed class LiquidWidgetRenderer
{
    private static readonly FluidParser Parser = new();
    private static readonly TemplateOptions Options = new() { MaxSteps = 50_000 };
    private readonly ConcurrentDictionary<string, IFluidTemplate> _cache = new();
    private readonly ILogger<LiquidWidgetRenderer> _logger;

    public LiquidWidgetRenderer(ILogger<LiquidWidgetRenderer> logger)
    {
        _logger = logger;
        Options.MemberAccessStrategy.Register<Dictionary<string, object>>(
            (dict, name) => dict.TryGetValue(name, out var v)
                ? FluidValue.Create(v, Options) : NilValue.Instance);
    }

    public async Task<string?> RenderAsync(
        string widgetId, string template, Dictionary<string, object> context)
    {
        try
        {
            var compiled = _cache.GetOrAdd(ComputeKey(widgetId, template), _ =>
            {
                if (!Parser.TryParse(template, out var t, out var errors))
                    throw new InvalidOperationException(
                        $"Liquid parse error in '{widgetId}': {string.Join("; ", errors)}");
                return t;
            });

            var ctx = new TemplateContext(Options);
            foreach (var (key, value) in context)
                ctx.SetValue(key, value);

            return await compiled.RenderAsync(ctx, Options);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "LiquidWidgetRenderer: failed to render widget '{Widget}'", widgetId);
            return null;
        }
    }

    private static string ComputeKey(string widgetId, string template) =>
        $"{widgetId}:{template.GetHashCode():x8}";
}
```

- [ ] Build:

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj --no-restore -v q
```

Expected: 0 errors.

- [ ] Commit:

```bash
git add Mostlylucid.BotDetection.UI/Services/LiquidWidgetRenderer.cs
git commit -m "feat(ui): LiquidWidgetRenderer service using Fluid"
```

---

## Task 3: Register LiquidWidgetRenderer in DI

**Files:** Modify Mostlylucid.BotDetection.UI/Extensions/ServiceCollectionExtensions.cs

- [ ] Find the dashboard/widget registration method:

```bash
grep -n "AddStyloBot\|AddDashboard\|AddWidget" \
  Mostlylucid.BotDetection.UI/Extensions/ServiceCollectionExtensions.cs | head -20
```

- [ ] Add inside that method:

```csharp
services.AddSingleton<LiquidWidgetRenderer>();
```

Add using if missing:
```csharp
using Mostlylucid.BotDetection.UI.Services;
```

- [ ] Build and commit:

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj --no-restore -v q
git add Mostlylucid.BotDetection.UI/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(ui): register LiquidWidgetRenderer singleton"
```

---

## Task 4: POST render endpoint in SbWidgetBatchMiddleware

**Files:** Modify Mostlylucid.BotDetection.UI/Middleware/SbWidgetBatchMiddleware.cs

- [ ] Add field and constructor parameter for LiquidWidgetRenderer:

```csharp
private readonly LiquidWidgetRenderer _liquidRenderer;
// Add to constructor params: LiquidWidgetRenderer liquidRenderer
// Assign: _liquidRenderer = liquidRenderer;
```

- [ ] Add POST branch at top of InvokeAsync (before the GET check):

```csharp
if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
    && path.Equals($"{basePath}/partials/render", StringComparison.OrdinalIgnoreCase))
{
    await HandleLiquidRenderAsync(context);
    return;
}
```

- [ ] Add HandleLiquidRenderAsync method:

```csharp
private async Task HandleLiquidRenderAsync(HttpContext context)
{
    context.Response.ContentType = "text/html; charset=utf-8";

    Dictionary<string, string>? body;
    try
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body);
        if (!doc.RootElement.TryGetProperty("widgets", out var widgetsEl))
        {
            context.Response.StatusCode = 400;
            return;
        }
        body = widgetsEl.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }
    catch
    {
        context.Response.StatusCode = 400;
        return;
    }

    var sb = new StringBuilder();
    foreach (var (widgetId, template) in body)
    {
        var q = WidgetRenderHelpers.ExtractWidgetParams(context, widgetId);
        string html = string.IsNullOrWhiteSpace(template)
            ? await RenderWidgetAsync(context, widgetId, q)
            : await RenderLiquidWidgetAsync(context, widgetId, template) ?? "";

        if (!string.IsNullOrEmpty(html))
        {
            html = WidgetRenderHelpers.InjectOobAttribute(html);
            sb.Append(html);
        }
    }

    await context.Response.WriteAsync(sb.ToString());
}
```

- [ ] Add RenderLiquidWidgetAsync method:

```csharp
private async Task<string?> RenderLiquidWidgetAsync(
    HttpContext context, string widgetId, string template)
{
    try
    {
        var data = await BuildLiquidContextAsync(context, widgetId);
        return data == null ? null
            : await _liquidRenderer.RenderAsync(widgetId, template, data);
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex,
            "SbWidgetBatch: Liquid render failed for '{Widget}'", widgetId);
        return null;
    }
}
```

- [ ] Add BuildLiquidContextAsync and per-widget builders. Each builder returns a Dictionary<string, object> with snake_case keys matching the documented Liquid context.

NOTE: The exact C# property names on dashboard model types must be verified against the actual models. Run this before implementing to find real property names:

```bash
grep -rn "public.*int.*Bot\|public.*int.*Human\|public.*TotalRequests\|BotCount\|HumanCount" \
  Mostlylucid.BotDetection.UI/Models/ Mostlylucid.BotDetection/Dashboard/ | head -30
```

Then implement:

```csharp
private async Task<Dictionary<string, object>?> BuildLiquidContextAsync(
    HttpContext context, string widgetId)
{
    return widgetId switch
    {
        "summary"    => await BuildSummaryContextAsync(),
        "topbots"    => BuildTopBotsContext(),
        "visitors"   => BuildVisitorsContext(context),
        "countries"  => BuildCountriesContext(),
        "endpoints"  => BuildEndpointsContext(),
        "useragents" => BuildUserAgentsContext(),
        "threats"    => await BuildThreatsContextAsync(),
        _            => null
    };
}

private async Task<Dictionary<string, object>> BuildSummaryContextAsync()
{
    var s = await _eventStore.GetSummaryAsync();
    // Replace property names below with the real ones found via grep above
    return new Dictionary<string, object>
    {
        ["bot_requests"]       = s.BotCount,
        ["human_requests"]     = s.HumanCount,
        ["uncertain_requests"] = s.UncertainCount,
        ["total_requests"]     = s.TotalRequests,
        ["bot_rate"]           = s.TotalRequests > 0
                                     ? (double)s.BotCount / s.TotalRequests : 0.0,
        ["unique_signatures"]  = s.UniqueSignatures,
        ["risk_band_counts"]   = s.RiskBandCounts ?? new Dictionary<string, int>(),
        ["top_bot_types"]      = s.TopBotTypes ?? new Dictionary<string, int>()
    };
}

private Dictionary<string, object> BuildTopBotsContext()
{
    var bots = _signatureCache.GetTopBots(1, 50, "default", "desc", "bots");
    return new Dictionary<string, object>
    {
        ["bots"] = bots.Select(b => new Dictionary<string, object>
        {
            ["signature_id"] = b.SignatureId ?? "",
            ["bot_name"]     = b.BotName ?? "",
            ["bot_type"]     = b.BotType ?? "",
            ["hit_count"]    = b.HitCount,
            ["last_seen"]    = b.LastSeen.ToString("O")
        }).ToList<object>(),
        ["total_count"] = bots.Count
    };
}

private Dictionary<string, object> BuildVisitorsContext(HttpContext context)
{
    var cache = context.RequestServices.GetService<VisitorListCache>();
    if (cache == null)
        return new Dictionary<string, object>
            { ["visitors"] = new List<object>(), ["total_count"] = 0 };

    var (items, totalCount, _, _) = cache.GetFiltered("all", "lastSeen", "desc", 1, 50);
    return new Dictionary<string, object>
    {
        ["visitors"] = items.Select(v => new Dictionary<string, object>
        {
            ["signature_id"] = v.SignatureId ?? "",
            ["is_bot"]       = v.IsBot,
            ["risk_band"]    = v.RiskBand ?? "",
            ["hits"]         = v.Hits,
            ["first_seen"]   = v.FirstSeen.ToString("O"),
            ["last_seen"]    = v.LastSeen.ToString("O"),
            ["country_code"] = v.CountryCode ?? "",
            ["bot_name"]     = v.BotName ?? ""
        }).ToList<object>(),
        ["total_count"] = totalCount
    };
}

private Dictionary<string, object> BuildCountriesContext()
{
    var data = _aggregateCache.Current.Countries;
    return new Dictionary<string, object>
    {
        ["countries"] = data.Select(c => new Dictionary<string, object>
        {
            ["country_code"] = c.CountryCode,
            ["total_count"]  = c.TotalCount,
            ["bot_count"]    = c.BotCount,
            ["human_count"]  = c.HumanCount,
            ["bot_rate"]     = c.BotRate
        }).ToList<object>()
    };
}

private Dictionary<string, object> BuildEndpointsContext()
{
    var data = _aggregateCache.Current.Endpoints;
    return new Dictionary<string, object>
    {
        ["endpoints"] = data.Select(e => new Dictionary<string, object>
        {
            ["method"]                 = e.Method,
            ["path"]                   = e.Path,
            ["total_count"]            = e.TotalCount,
            ["bot_count"]              = e.BotCount,
            ["bot_rate"]               = e.BotRate,
            ["avg_threat_score"]       = e.AvgThreatScore,
            ["avg_processing_time_ms"] = e.AvgProcessingTimeMs
        }).ToList<object>()
    };
}

private Dictionary<string, object> BuildUserAgentsContext()
{
    var data = _aggregateCache.Current.UserAgents;
    return new Dictionary<string, object>
    {
        ["user_agents"] = data.Select(u => new Dictionary<string, object>
        {
            ["family"]         = u.Family,
            ["category"]       = u.Category,
            ["total_count"]    = u.TotalCount,
            ["bot_rate"]       = u.BotRate,
            ["avg_confidence"] = u.AvgConfidence,
            ["last_seen"]      = u.LastSeen.ToString("O")
        }).ToList<object>()
    };
}

private async Task<Dictionary<string, object>> BuildThreatsContextAsync()
{
    List<ThreatEntry> threats;
    try { threats = await _eventStore.GetThreatsAsync(50); }
    catch { threats = []; }

    return new Dictionary<string, object>
    {
        ["threats"] = threats.Select(t => new Dictionary<string, object>
        {
            ["timestamp"]    = t.Timestamp.ToString("O"),
            ["path"]         = t.Path,
            ["threat_type"]  = t.ThreatType ?? "",
            ["threat_score"] = t.ThreatScore,
            ["signature_id"] = t.SignatureId ?? "",
            ["in_honeypot"]  = t.InHoneypot
        }).ToList<object>(),
        ["total_count"] = threats.Count
    };
}
```

- [ ] Add using at top if missing:

```csharp
using System.Text.Json;
```

- [ ] Build:

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj --no-restore -v q
```

Expected: 0 errors.

- [ ] Commit:

```bash
git add Mostlylucid.BotDetection.UI/Middleware/SbWidgetBatchMiddleware.cs
git commit -m "feat(ui): POST /_stylobot/partials/render Liquid widget rendering"
```

---

## Task 5: Add WidgetTemplate types and renderWidgets to @stylobot/core

**Files:**
- Modify: sdk/node/packages/core/src/types.ts
- Modify: sdk/node/packages/core/src/client.ts

- [ ] Append to types.ts:

```ts
export type ClientMode = 'gateway' | 'sidecar' | 'ssr'

export interface WidgetTemplate {
  widgetId: string
  template?: string
  params?: Record<string, string>
}

export interface WidgetRenderRequest {
  widgets: Record<string, string>
}
```

- [ ] Add renderWidgets and verdictGlobal to StyloBotClient (after the me() method):

```ts
async renderWidgets(widgets: WidgetTemplate[]): Promise<Record<string, string>> {
  const body: WidgetRenderRequest = {
    widgets: Object.fromEntries(widgets.map(w => [w.widgetId, w.template ?? '']))
  }
  const html = await this.postHtml('/_stylobot/partials/render', body)
  return parseWidgetFragments(html)
}

verdictGlobal(verdict: Verdict | null): string {
  const data = verdict ?? {
    isBot: false, botProbability: 0, confidence: 0, botType: null, botName: null,
    riskBand: 'Unknown', recommendedAction: 'Allow', threatScore: 0, threatBand: 'None'
  }
  return `<script>window.__sb=${JSON.stringify(data)}</script>`
}
```

- [ ] Add postHtml private method (alongside existing post method):

```ts
private async postHtml(path: string, body: unknown): Promise<string> {
  const url = `${this.endpoint}${path}`
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), this.timeout)
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: { ...this.headers(), 'content-type': 'application/json', 'accept': 'text/html' },
      body: JSON.stringify(body),
      signal: controller.signal,
    })
    if (!res.ok)
      throw new StyloBotApiError(res.status, await res.text().catch(() => ''), url)
    return await res.text()
  } finally {
    clearTimeout(timer)
  }
}
```

- [ ] Add parseWidgetFragments at bottom of client.ts.

NOTE: This runs in the browser (via the elements package) and on the server (via the node package). Use DOMParser when available (browser); use string matching as server-side fallback.

```ts
function parseWidgetFragments(html: string): Record<string, string> {
  if (typeof DOMParser !== 'undefined') {
    const doc = new DOMParser().parseFromString(html, 'text/html')
    const result: Record<string, string> = {}
    for (const el of Array.from(doc.body.children)) {
      const id = el.getAttribute('data-sb-widget')
      if (id) result[id] = el.outerHTML
    }
    return result
  }
  // Server-side fallback: regex scan for data-sb-widget attributes
  const result: Record<string, string> = {}
  for (const match of html.matchAll(/data-sb-widget="([^"]+)"/g)) {
    const id = match[1]
    const tagStart = html.lastIndexOf('<', match.index!)
    if (tagStart !== -1) result[id] = extractElement(html, tagStart)
  }
  return result
}

function extractElement(html: string, start: number): string {
  const tagMatch = html.slice(start).match(/^<([a-zA-Z][^\s/>]*)/)
  if (!tagMatch) return ''
  const tag = tagMatch[1]
  let depth = 0
  let i = start
  while (i < html.length) {
    const nextOpen = html.indexOf(`<${tag}`, i + 1)
    const nextClose = html.indexOf(`</${tag}>`, i + 1)
    if (nextClose === -1) break
    if (nextOpen !== -1 && nextOpen < nextClose) { depth++; i = nextOpen + 1 }
    else if (depth === 0) return html.slice(start, nextClose + tag.length + 3)
    else { depth--; i = nextClose + 1 }
  }
  return html.slice(start)
}
```

- [ ] Write test. Create sdk/node/packages/core/src/__tests__/widgets.test.ts:

```ts
import { describe, it } from 'node:test'
import assert from 'node:assert/strict'
import type { WidgetTemplate, WidgetRenderRequest } from '../types.ts'

describe('WidgetTemplate types', () => {
  it('builds a WidgetRenderRequest from WidgetTemplate array', () => {
    const templates: WidgetTemplate[] = [
      { widgetId: 'summary', template: '{{ bot_requests }} bots' },
      { widgetId: 'topbots' },
    ]
    const req: WidgetRenderRequest = {
      widgets: Object.fromEntries(templates.map(w => [w.widgetId, w.template ?? '']))
    }
    assert.equal(req.widgets['summary'], '{{ bot_requests }} bots')
    assert.equal(req.widgets['topbots'], '')
  })
})
```

- [ ] Run test:

```bash
cd sdk/node/packages/core
node --experimental-strip-types --test src/__tests__/widgets.test.ts
```

Expected: 1 passing.

- [ ] Commit:

```bash
git add sdk/node/packages/core/src/
git commit -m "feat(core): WidgetTemplate types + renderWidgets/verdictGlobal on StyloBotClient"
```

---

## Task 6: SSR coordinator and verdict injector in @stylobot/node

**Files:**
- Create: sdk/node/packages/node/src/coordinator.ts
- Create: sdk/node/packages/node/src/injector.ts
- Modify: sdk/node/packages/node/src/index.ts

- [ ] Create coordinator.ts:

```ts
import { StyloBotClient, type WidgetTemplate } from '@stylobot/core'

export class SbSsrCoordinator {
  private readonly client: StyloBotClient

  constructor(client: StyloBotClient) {
    this.client = client
  }

  async renderWidgets(widgets: WidgetTemplate[]): Promise<Record<string, string>> {
    if (widgets.length === 0) return {}
    return this.client.renderWidgets(widgets)
  }

  async renderWidget(widgetId: string, template?: string): Promise<string> {
    const results = await this.renderWidgets([{ widgetId, template }])
    return results[widgetId] ?? ''
  }
}
```

- [ ] Create injector.ts:

```ts
import type { Request, Response, NextFunction, RequestHandler } from 'express'
import { parseStyloBotHeaders, type Verdict } from '@stylobot/core'

export interface SbVerdictInjectorOptions {
  mode: 'gateway' | 'sidecar'
  endpoint?: string
  apiKey?: string
  timeout?: number
}

export function sbVerdictInjector(options: SbVerdictInjectorOptions): RequestHandler {
  if (options.mode === 'sidecar') {
    if (!options.endpoint) throw new Error('endpoint is required for sidecar mode')
    const base = options.endpoint.replace(/\/$/, '')
    const { apiKey, timeout = 3000 } = options

    return async (_req: Request, res: Response, next: NextFunction) => {
      let verdict: Verdict | null = null
      try {
        const controller = new AbortController()
        const timer = setTimeout(() => controller.abort(), timeout)
        const headers: Record<string, string> = { accept: 'application/json' }
        if (apiKey) headers['x-sb-api-key'] = apiKey
        const r = await fetch(`${base}/_stylobot/me`, { headers, signal: controller.signal })
        clearTimeout(timer)
        if (r.ok) verdict = (await r.json()) as Verdict
      } catch { /* fail open */ }
      res.locals.sbVerdict = verdict
      res.locals.sbVerdictScript = buildVerdictScript(verdict)
      next()
    }
  }

  return (req: Request, res: Response, next: NextFunction) => {
    const verdict = parseStyloBotHeaders(req.headers as Record<string, string>)
    res.locals.sbVerdict = verdict
    res.locals.sbVerdictScript = buildVerdictScript(verdict)
    next()
  }
}

function buildVerdictScript(verdict: Verdict | null): string {
  const data = verdict ?? {
    isBot: false, botProbability: 0, confidence: 0, botType: null, botName: null,
    riskBand: 'Unknown', recommendedAction: 'Allow', threatScore: 0, threatBand: 'None'
  }
  return `<script>window.__sb=${JSON.stringify(data)}</script>`
}
```

- [ ] Update index.ts:

```ts
export { styloBotMiddleware, type StyloBotMiddlewareOptions, type StyloBotResult } from './middleware.js'
export { styloBotPlugin } from './fastify.js'
export { extractDetectRequest } from './extract.js'
export { SbSsrCoordinator } from './coordinator.js'
export { sbVerdictInjector, type SbVerdictInjectorOptions } from './injector.js'
```

- [ ] Write coordinator test. Create sdk/node/packages/node/src/__tests__/coordinator.test.ts:

```ts
import { describe, it, mock } from 'node:test'
import assert from 'node:assert/strict'
import { SbSsrCoordinator } from '../coordinator.ts'

describe('SbSsrCoordinator', () => {
  it('calls client.renderWidgets once with all widgets batched', async () => {
    const mockClient = {
      renderWidgets: mock.fn(async (_: any) => ({
        summary: '<div data-sb-widget="summary">42 bots</div>',
        topbots: '<div data-sb-widget="topbots"><li>BadBot</li></div>'
      }))
    }
    const coordinator = new SbSsrCoordinator(mockClient as any)
    const result = await coordinator.renderWidgets([
      { widgetId: 'summary', template: '{{ bot_requests }} bots' },
      { widgetId: 'topbots' }
    ])
    assert.equal(mockClient.renderWidgets.mock.callCount(), 1)
    assert.ok(result['summary']?.includes('42 bots'))
  })

  it('returns empty object for empty list without calling client', async () => {
    const mockClient = { renderWidgets: mock.fn() }
    const coordinator = new SbSsrCoordinator(mockClient as any)
    const result = await coordinator.renderWidgets([])
    assert.deepEqual(result, {})
    assert.equal(mockClient.renderWidgets.mock.callCount(), 0)
  })
})
```

- [ ] Run tests:

```bash
cd sdk/node/packages/node
node --experimental-strip-types --loader ../../ts-loader.mjs --test src/__tests__/coordinator.test.ts
```

Expected: 2 passing.

- [ ] Commit:

```bash
git add sdk/node/packages/node/src/
git commit -m "feat(node): SbSsrCoordinator and sbVerdictInjector"
```

---

## Task 7: Create @stylobot/elements package

**Files:** All new under sdk/node/packages/elements/

- [ ] Create package.json:

```json
{
  "name": "@stylobot/elements",
  "version": "0.1.0",
  "description": "StyloBot web components -framework-agnostic client-side widgets",
  "type": "module",
  "main": "dist/index.js",
  "types": "dist/index.d.ts",
  "files": ["dist"],
  "scripts": {
    "build": "tsc",
    "clean": "rm -rf dist"
  },
  "devDependencies": {
    "typescript": "^5.8.0"
  },
  "license": "MIT"
}
```

- [ ] Create tsconfig.json:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "lib": ["ES2022", "DOM"],
    "outDir": "dist",
    "declaration": true,
    "strict": true,
    "skipLibCheck": true
  },
  "include": ["src"]
}
```

- [ ] Create src/coordinator.ts:

The coordinator collects widget registrations via queueMicrotask (allows all elements to register before the flush runs), then fires one POST to /_stylobot/partials/render. Uses DOMParser to split the response HTML into per-widget fragments.

```ts
export interface WidgetRegistration {
  widgetId: string
  template: string
  resolve: (html: string) => void
}

class SbElementsCoordinator {
  private endpoint = ''
  private pending: WidgetRegistration[] = []
  private scheduled = false

  configure(endpoint: string) {
    this.endpoint = endpoint.replace(/\/$/, '')
  }

  register(reg: WidgetRegistration) {
    this.pending.push(reg)
    if (!this.scheduled) {
      this.scheduled = true
      queueMicrotask(() => this.flush())
    }
  }

  private async flush() {
    if (this.pending.length === 0) return
    const batch = [...this.pending]
    this.pending = []
    this.scheduled = false

    const body = {
      widgets: Object.fromEntries(batch.map(r => [r.widgetId, r.template]))
    }

    try {
      const res = await fetch(`${this.endpoint}/_stylobot/partials/render`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', 'accept': 'text/html' },
        body: JSON.stringify(body)
      })
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      const html = await res.text()
      const fragments = parseFragments(html)
      for (const reg of batch) reg.resolve(fragments[reg.widgetId] ?? '')
    } catch {
      for (const reg of batch) reg.resolve('')
    }
  }
}

function parseFragments(html: string): Record<string, string> {
  const result: Record<string, string> = {}
  const doc = new DOMParser().parseFromString(html, 'text/html')
  for (const el of Array.from(doc.body.children)) {
    const id = el.getAttribute('data-sb-widget')
    if (id) result[id] = el.outerHTML
  }
  return result
}

export const sbCoordinator = new SbElementsCoordinator()
```

- [ ] Create src/sb-gate.ts:

```ts
const RISK_ORDER: Record<string, number> = {
  Unknown: 0, VeryLow: 1, Low: 2, Elevated: 3, Medium: 4, High: 5, VeryHigh: 6, Verified: 7
}

function riskLevel(band: string): number {
  const key = Object.keys(RISK_ORDER)
    .find(k => k.toLowerCase() === band.toLowerCase())
  return key !== undefined ? RISK_ORDER[key] : 0
}

export class SbGate extends HTMLElement {
  connectedCallback() {
    this.evaluate()
    window.addEventListener('sb:verdict', () => this.evaluate())
  }

  private evaluate() {
    const maxRisk = this.getAttribute('max-risk') ?? 'low'
    const verdict = (window as any).__sb
    if (!verdict) return
    this.style.display =
      riskLevel(verdict.riskBand ?? 'Unknown') <= riskLevel(maxRisk) ? '' : 'none'
  }
}
```

- [ ] Create src/sb-adapt.ts:

```ts
const RISK_ORDER: Record<string, number> = {
  Unknown: 0, VeryLow: 1, Low: 2, Elevated: 3, Medium: 4, High: 5, VeryHigh: 6, Verified: 7
}

function riskLevel(band: string): number {
  const key = Object.keys(RISK_ORDER)
    .find(k => k.toLowerCase() === band.toLowerCase())
  return key !== undefined ? RISK_ORDER[key] : 0
}

export class SbCase extends HTMLElement {}

export class SbAdapt extends HTMLElement {
  connectedCallback() {
    this.evaluate()
    window.addEventListener('sb:verdict', () => this.evaluate())
  }

  private evaluate() {
    const verdict = (window as any).__sb
    const current = verdict ? riskLevel(verdict.riskBand ?? 'Unknown') : 0
    let matched = false

    for (const child of Array.from(this.children)) {
      if (!(child instanceof SbCase)) continue
      const el = child as HTMLElement
      const maxRisk = child.getAttribute('max-risk')
      const fits = maxRisk === null || current <= riskLevel(maxRisk)
      el.style.display = (!matched && fits) ? '' : 'none'
      if (!matched && fits) matched = true
    }
  }
}
```

- [ ] Create src/sb-widget.ts:

sb-widget reads its inner template tag content as the Liquid string, registers with the coordinator, and replaces itself with the returned HTML once the batch resolves.

```ts
import { sbCoordinator } from './coordinator.js'

export class SbWidget extends HTMLElement {
  connectedCallback() {
    const widgetId =
      this.getAttribute('data-sb-widget') ?? this.getAttribute('id') ?? ''
    if (!widgetId) return

    const templateEl = this.querySelector('template')
    const liquid = templateEl?.innerHTML.trim() ?? ''

    sbCoordinator.register({
      widgetId,
      template: liquid,
      resolve: (html) => {
        if (html) this.outerHTML = html
      }
    })
  }
}
```

- [ ] Create src/index.ts:

```ts
export { SbGate } from './sb-gate.js'
export { SbCase, SbAdapt } from './sb-adapt.js'
export { SbWidget } from './sb-widget.js'
export { sbCoordinator } from './coordinator.js'

if (typeof customElements !== 'undefined') {
  customElements.define('sb-gate', SbGate)
  customElements.define('sb-case', SbCase)
  customElements.define('sb-adapt', SbAdapt)
  customElements.define('sb-widget', SbWidget)
}
```

- [ ] Commit:

```bash
git add sdk/node/packages/elements/
git commit -m "feat(elements): @stylobot/elements package with sb-gate, sb-adapt, sb-widget"
```

---

## Task 8: Express sample app

**Files:** All new under sdk/node/samples/express/

- [ ] Create package.json:

```json
{
  "name": "@stylobot/sample-express",
  "version": "0.1.0",
  "private": true,
  "type": "module",
  "scripts": {
    "start": "node --experimental-strip-types src/server.ts",
    "dev": "node --experimental-strip-types --watch src/server.ts"
  },
  "dependencies": {
    "@stylobot/core": "0.1.0",
    "@stylobot/node": "0.1.0",
    "express": "^5.0.0"
  },
  "devDependencies": {
    "@types/express": "^5.0.0",
    "@types/node": "^22.0.0"
  }
}
```

- [ ] Create tsconfig.json:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "NodeNext",
    "moduleResolution": "NodeNext",
    "strict": true,
    "skipLibCheck": true
  }
}
```

- [ ] Create src/templates/summary.liquid:

```liquid
<div class="sb-card" data-sb-widget="summary">
  <h2>Traffic Overview</h2>
  <p><strong>{{ bot_requests }}</strong> bots / <strong>{{ human_requests }}</strong> humans</p>
  <p>Bot rate: {{ bot_rate | times: 100 | round: 1 }}%</p>
  {% if bot_rate > 0.5 %}
    <p class="alert">High bot traffic detected</p>
  {% endif %}
  <p>Unique signatures: {{ unique_signatures }}</p>
</div>
```

- [ ] Create src/templates/topbots.liquid:

```liquid
<div class="sb-card" data-sb-widget="topbots">
  <h2>Top Bots</h2>
  <ul>
    {% for bot in bots %}
      <li>
        <strong>{{ bot.bot_name | default: "Unknown" }}</strong>
        ({{ bot.bot_type }}) -{{ bot.hit_count }} hits
      </li>
    {% endfor %}
  </ul>
</div>
```

- [ ] Create src/server.ts:

```ts
import express from 'express'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { StyloBotClient } from '@stylobot/core'
import { SbSsrCoordinator, sbVerdictInjector, styloBotMiddleware } from '@stylobot/node'

const __dir = dirname(fileURLToPath(import.meta.url))
const STYLOBOT_URL = process.env.STYLOBOT_URL ?? 'http://localhost:5080'

const app = express()
const client = new StyloBotClient({ endpoint: STYLOBOT_URL })
const coordinator = new SbSsrCoordinator(client)

app.use(express.static(join(__dir, '../public')))
app.use(styloBotMiddleware({ mode: 'headers' }))
app.use(sbVerdictInjector({ mode: 'gateway' }))

app.get('/', async (req, res) => {
  const summaryTemplate = readFileSync(join(__dir, 'templates/summary.liquid'), 'utf8')
  const topbotsTemplate = readFileSync(join(__dir, 'templates/topbots.liquid'), 'utf8')

  const widgets = await coordinator.renderWidgets([
    { widgetId: 'summary', template: summaryTemplate },
    { widgetId: 'topbots', template: topbotsTemplate },
  ])

  const { isBot, verdict } = req.stylobot

  res.send(`<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>StyloBot SSR Demo</title>
  <style>
    body { font-family: sans-serif; max-width: 900px; margin: 2rem auto; padding: 0 1rem; }
    .sb-card { border: 1px solid #ddd; border-radius: 8px; padding: 1rem; margin: 1rem 0; }
    .alert { color: red; font-weight: bold; }
    .verdict { background: ${isBot ? '#fee' : '#efe'}; padding: 0.5rem 1rem; border-radius: 4px; margin-bottom: 1rem; }
  </style>
</head>
<body>
  ${res.locals.sbVerdictScript}
  <div class="verdict">
    You are: <strong>${isBot ? 'a bot' : 'human'}</strong> -risk: ${verdict.riskBand}
  </div>
  <h1>SSR Widgets (Liquid rendered server-side)</h1>
  ${widgets['summary'] ?? '<p>Summary unavailable</p>'}
  ${widgets['topbots'] ?? '<p>Top bots unavailable</p>'}
  <p><a href="/csr.html">View CSR demo (web components)</a></p>
</body>
</html>`)
})

app.listen(process.env.PORT ?? 3000, () => {
  console.log(`StyloBot sample: http://localhost:${process.env.PORT ?? 3000}`)
  console.log(`StyloBot URL: ${STYLOBOT_URL}`)
})
```

- [ ] Create public/csr.html:

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>StyloBot CSR Demo</title>
  <style>
    body { font-family: sans-serif; max-width: 900px; margin: 2rem auto; padding: 0 1rem; }
    sb-gate, sb-adapt, sb-widget { display: block; }
    .sb-card { border: 1px solid #ddd; border-radius: 8px; padding: 1rem; margin: 1rem 0; }
    .premium { background: #f0f8ff; border-color: #4a9eff; }
    .friction { background: #fffbf0; border-color: #f0a500; }
    .blocked  { background: #fff0f0; border-color: #e00; }
  </style>
</head>
<body>

  <script type="module">
    import { sbCoordinator } from '/elements.js'
    sbCoordinator.configure('http://localhost:5080')
  </script>

  <h1>CSR Demo: Web Components</h1>

  <sb-gate max-risk="low">
    <div class="sb-card premium">
      <h2>Premium Content</h2>
      <p>Visible to verified low-risk visitors only.</p>
    </div>
  </sb-gate>

  <sb-adapt>
    <sb-case max-risk="low">
      <div class="sb-card">Standard checkout, no friction.</div>
    </sb-case>
    <sb-case max-risk="elevated">
      <div class="sb-card friction">Extra verification required.</div>
    </sb-case>
    <sb-case>
      <div class="sb-card blocked">Access restricted.</div>
    </sb-case>
  </sb-adapt>

  <sb-widget data-sb-widget="summary">
    <template>
      <div class="sb-card" data-sb-widget="summary">
        <strong>{{ bot_requests }}</strong> bots out of {{ total_requests }} total
        ({{ bot_rate | times: 100 | round: 1 }}% bot rate)
      </div>
    </template>
  </sb-widget>

  <sb-widget data-sb-widget="topbots"></sb-widget>

  <p><a href="/">Back to SSR demo</a></p>

  <script type="module" src="/elements.js"></script>
</body>
</html>
```

- [ ] Commit:

```bash
git add sdk/node/samples/express/
git commit -m "feat(samples): Express sample app -SSR and CSR mode demos"
```

---

## Task 9: README tutorial and data context reference

**Files:**
- Create: sdk/node/README.md
- Create: sdk/node/docs/data-contexts.md

- [ ] Create sdk/node/README.md. This is the primary user-facing document. It covers concepts, all three integration modes, Liquid templates, web components, window.__sb, running the sample, and security. Full content:

```markdown
# StyloBot Node SDK

Bot detection and behavioural widget rendering for Node.js. Three packages, one coherent system.

| Package | Purpose |
|---------|---------|
| @stylobot/core | Types, HTTP client, header parser |
| @stylobot/node | Express/Fastify middleware, SSR coordinator, verdict injector |
| @stylobot/elements | Framework-agnostic web components |

## Concepts

StyloBot runs alongside your app as a sidecar, or in front of it as a YARP gateway. Either way it produces a verdict for every request: isBot, riskBand, confidence, threatScore. The Node SDK surfaces that verdict in three ways:

1. In server code via req.stylobot
2. In the browser via window.__sb
3. In page markup via sb-gate, sb-adapt, sb-widget

Widgets fetch aggregate dashboard data and render it as HTML. You control the markup via Liquid templates. All widgets on a page fire one network request regardless of how many you declare.

## Installation

npm install @stylobot/core @stylobot/node
npm install @stylobot/elements  (for web components)

## Gateway mode (zero latency)

Your YARP gateway stamps every request with X-StyloBot-* headers. Your server reads them directly -no fetch, no extra latency.

import { styloBotMiddleware, sbVerdictInjector } from '@stylobot/node'

app.use(styloBotMiddleware({ mode: 'headers' }))
app.use(sbVerdictInjector({ mode: 'gateway' }))

app.get('/', (req, res) => {
  const { isBot, verdict } = req.stylobot
  res.send('Risk: ' + verdict.riskBand)
})

## Sidecar mode (direct API call)

app.use(styloBotMiddleware({
  mode: 'api',
  endpoint: 'http://localhost:5080',
  apiKey: process.env.STYLOBOT_API_KEY
}))
app.use(sbVerdictInjector({
  mode: 'sidecar',
  endpoint: 'http://localhost:5080'
}))

## SSR widget mode

Fetch rendered HTML during your own render cycle. One batch request.

import { StyloBotClient } from '@stylobot/core'
import { SbSsrCoordinator } from '@stylobot/node'
import { readFileSync } from 'node:fs'

const client = new StyloBotClient({ endpoint: 'http://localhost:5080' })
const coordinator = new SbSsrCoordinator(client)

app.get('/admin', async (req, res) => {
  const template = readFileSync('templates/summary.liquid', 'utf8')

  const widgets = await coordinator.renderWidgets([
    { widgetId: 'summary', template },
    { widgetId: 'topbots' },
  ])

  res.send(`
    <html><body>
      ${res.locals.sbVerdictScript}
      ${widgets['summary'] ?? ''}
      ${widgets['topbots'] ?? ''}
    </body></html>
  `)
})

## Liquid templates

Pass a Liquid template string. StyloBot fetches the data, renders your markup, returns the HTML. You keep design control; StyloBot owns the data.

summary.liquid:
  <div class="my-card" data-sb-widget="summary">
    {{ bot_requests }} bots / {{ human_requests }} humans
    {% if bot_rate > 0.5 %}<span class="alert">High bot traffic</span>{% endif %}
  </div>

See docs/data-contexts.md for all available variables per widget.

## Web components

Configure the coordinator once:
  sbCoordinator.configure('http://localhost:5080')

sb-gate: show content to low-risk visitors only
  <sb-gate max-risk="low">
    <div class="premium">Premium content.</div>
  </sb-gate>

max-risk values: verylow, low, elevated, medium, high, veryhigh

sb-adapt: different content per risk band (first matching sb-case wins)
  <sb-adapt>
    <sb-case max-risk="low"><form>Standard checkout</form></sb-case>
    <sb-case max-risk="elevated"><form>With extra verification</form></sb-case>
    <sb-case><p>Access restricted.</p></sb-case>
  </sb-adapt>

sb-widget: fetch widget data with optional Liquid template (all widgets batch into one request)
  <sb-widget data-sb-widget="summary">
    <template>
      <div class="my-card" data-sb-widget="summary">
        {{ bot_requests }} bots / {{ human_requests }} humans
      </div>
    </template>
  </sb-widget>

  <sb-widget data-sb-widget="topbots"></sb-widget>

Available widget IDs: summary, topbots, visitors, countries, endpoints, useragents, threats, sessions

## window.__sb

sbVerdictInjector adds a script tag with window.__sb to every server response:
  window.__sb = {
    isBot: boolean,
    botProbability: number,
    confidence: number,
    botType: string | null,
    botName: string | null,
    riskBand: 'Unknown'|'VeryLow'|'Low'|'Elevated'|'Medium'|'High'|'VeryHigh'|'Verified',
    recommendedAction: 'Allow'|'Throttle'|'Challenge'|'Block',
    threatScore: number,
    threatBand: 'None'|'Low'|'Elevated'|'High'|'Critical'
  }

## Running the sample

  dotnet run --project Mostlylucid.BotDetection.Demo   (start StyloBot on port 5080)
  cd sdk/node/samples/express && npm install && npm start
  open http://localhost:3000

  GET /         SSR: server-rendered widgets with custom Liquid templates
  GET /csr.html CSR: web components (sb-gate, sb-adapt, sb-widget)

## Security

The render endpoint is read-only aggregate stats, no PII. CORS is same-origin by default.
For server-to-server calls, pass an API key:

  const client = new StyloBotClient({
    endpoint: 'http://localhost:5080',
    apiKey: process.env.STYLOBOT_API_KEY
  })
```

- [ ] Create sdk/node/docs/data-contexts.md. Full variable reference for every widget's Liquid context. Widgets: summary, topbots, visitors, countries, endpoints, useragents, threats, sessions. For each widget document all available variable names, their types, and a usage example. Refer to the spec at docs/superpowers/specs/2026-05-02-node-sdk-liquid-widgets-design.md for the canonical variable list.

- [ ] Commit:

```bash
git add sdk/node/README.md sdk/node/docs/
git commit -m "docs(sdk): tutorial/manual and Liquid data context reference"
```

---

## Task 10: End-to-end verification

- [ ] Build .NET:

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -v q
```

Expected: 0 errors.

- [ ] Run Node tests:

```bash
cd sdk/node/packages/core
node --experimental-strip-types --test src/__tests__/*.test.ts

cd sdk/node/packages/node
node --experimental-strip-types --loader ../../ts-loader.mjs --test src/__tests__/*.test.ts
```

Expected: all pass.

- [ ] Start StyloBot:

```bash
dotnet run --project Mostlylucid.BotDetection.Demo
```

Expected: dashboard available at http://localhost:5080/_stylobot

- [ ] Start sample app:

```bash
cd sdk/node/samples/express && npm install && npm start
```

Expected: running at http://localhost:3000

- [ ] Verify SSR page (http://localhost:3000):
  - window.__sb is in page source
  - Summary widget shows bot/human counts
  - Top bots list renders

- [ ] Verify CSR page (http://localhost:3000/csr.html):
  - DevTools Network: exactly one POST to /_stylobot/partials/render (not one per widget)
  - sb-gate hides/shows the premium card based on current risk
  - sb-widget elements are replaced with rendered HTML

- [ ] Final commit:

```bash
git add -A
git commit -m "chore: end-to-end verification complete"
```