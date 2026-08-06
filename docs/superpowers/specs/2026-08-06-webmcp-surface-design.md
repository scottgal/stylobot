# StyloBot WebMCP — "old site, new tools"

**Date:** 2026-08-06
**Status:** Design, pending operator sign-off on §12 (one guardrail departure)
**Package:** `Mostlylucid.BotDetection.WebMcp` (FOSS pack, AOT-compatible, SQLite-only)
**Builds on:** `2026-07-07-agent-native-web-shape.md` (locked contracts C1/C2/C4), `2026-06-20-styloextract-design.md` (shipped body-mutation pattern)

---

## 1. Thesis

A 2009 ASP.NET WebForms site cannot be made agent-native by its owner — that would mean
rewriting it. But **StyloBot already sits in front of it, already sees every request and
every response body, and already knows exactly who is asking.** Nothing else on the path
has all three.

So: the gateway synthesises an agent surface *on behalf of* an upstream site that is never
modified. The site owner changes one config block; the site itself is untouched.

Three consumers, one engine:

| Consumer | Gets |
|---|---|
| AI crawlers being throttled | A sanctioned structured endpoint instead of a 403 — the *carrot* |
| End users' assistants (Claude/ChatGPT) | A real MCP server for a site that never built one |
| In-browser agents | W3C WebMCP tool declarations injected into the page |

The differentiator is not "an MCP server." Anyone can write one. It is that **every tool
call is identity-resolved, threat-scored, policy-gated and metered by the same stack that
was already deciding whether to serve the request at all.** A verified GPTBot presenting
RFC 9421 signatures gets a different tool budget than an anonymous scraper, and that is
enforced by machinery StyloBot already ships.

## 2. Non-goals (v1)

- **No writes on the remote channel.** Remote MCP is read-only. Credential delegation
  (OAuth 2.1 / MCP authorization spec) is a later increment with its own spec.
- **No ops-MCP.** Wrapping StyloBot's own `/api/v1` for conversational operations is a
  separate sub-project; it shares nothing with this one but the word "MCP."
- **No LLM dependency.** The catalog is derived mechanically. LLM-assisted tool
  descriptions are a later, optional enhancement.
- **No commercial gating of capability.** Per `feedback_foss_never_degraded`, FOSS gets
  the whole surface. Commercial adds *scale* (pgvector/HNSW retrieval, fleet-wide catalog
  management), never capability.

## 3. Decisions taken

Confirmed with the operator:

| Decision | Choice |
|---|---|
| Tool provenance | Three lanes + operator promotion gate |
| Read/write | Read-only remote; writes in-page only (browser carries the user's own session) |
| Index backend | SQLite FTS5, FOSS ships one storage engine. `ISiteIndex` seam exists so commercial can add scale |

Taken by me, flagged for override:

| Decision | Choice | Why |
|---|---|---|
| Corpus acquisition | Passive capture (default) + opt-in sitemap warm | Passive alone leaves a cold deployment with an empty index |
| Protocol implementation | Hand-rolled JSON-RPC, not the `ModelContextProtocol` SDK | The surface is *dynamic* — tools are rows in a table, not `[McpServerTool]` methods. The SDK's attribute model is the wrong shape and its AOT story is unclear against this repo's `IsAotCompatible` + `TreatWarningsAsErrors` pack style. The needed surface is ~8 JSON-RPC methods |
| Transport | Streamable HTTP, POST-only (no SSE stream) | v1 has no server-initiated messages. The spec permits `405` on `GET`. Avoids long-lived connections through YARP |
| Injection mechanism | StyloExtract's `IActionPolicy` body-mutation pattern | `IResponseTransformer`/`ResponseTransformCoordinator` from the 2026-07-07 shape doc was **never built**; StyloExtract shipped body mutation via action policies instead. Build on the shipped pattern |

## 4. Architecture

```
                    ┌─────────────────────────────────────────┐
   AI crawler ──┐   │  Stylobot.Gateway (YARP)                │
   Assistant ───┼──▶│                                         │──▶ upstream site
   Browser  ────┘   │  detection ─▶ policy ─▶ WebMcp pack     │    (never modified)
                    └──────────────┬──────────────────────────┘
                                   │
                    ┌──────────────▼──────────────┐
                    │  IToolCatalog               │  ◀── Lane 1: IOpenApiCatalog
                    │  (promotion-gated)          │  ◀── Lane 2: ISiteIndex
                    │                             │  ◀── Lane 3: IRouteCatalogService
                    └──────────────┬──────────────┘         + observed <form>s
                                   │
              ┌────────────────────┼────────────────────┐
              ▼                    ▼                    ▼
      Channel R              Channel P            Policy: offer-mcp
      /_stylobot/mcp         injected script      403 ─▶ "here's the front door"
      JSON-RPC/HTTP          navigator.modelContext
      read-only              writes OK (user's session)
```

### 4.1 Projects

| Project | Contents |
|---|---|
| `Mostlylucid.BotDetection.WebMcp` | Everything below. One pack, referenced by `Stylobot.Gateway` / `Stylobot.All` |
| *(extends)* `Mostlylucid.BotDetection.UI` | Promotion UI via the existing pack-slot on Site detail — **not** a fork. Per `project_dashboard_dogfood_ruling`: one dashboard, extended through seams |

Dependencies: `Mostlylucid.BotDetection`, `Mostlylucid.BotDetection.OpenApi`. No new NuGet
packages beyond what the repo already carries.

### 4.2 Components

Each is independently testable and has one job.

| Component | Responsibility | Taxonomy |
|---|---|---|
| `ISiteIndex` / `Fts5SiteIndex` | Index and query extracted page text. `Index(doc)`, `Search(query, limit) → hits` | Store |
| `SiteCorpusWriter` | Write-behind drain of captured pages into the index. Off the request path | Coordinator |
| `SitemapWarmService` | Opt-in bounded crawl of upstream `sitemap.xml` to seed a cold index | Coordinator |
| `IToolCatalog` | The promotion-gated set of live tools. Composes the three lanes | Store |
| `OpenApiToolSynthesiser` | `LoadedOpenApiOperation` → `ToolDefinition` (JSON Schema from parameters) | Molecule |
| `FormToolSynthesiser` | `<form>` in captured HTML → `ToolDefinition` + DOM binding | Molecule |
| `McpJsonRpcHandler` | The 8 JSON-RPC methods. Pure dispatch over `IToolCatalog` + `IToolExecutor` | Coordinator |
| `IToolExecutor` | Executes a tool: index query, or loopback GET to upstream | Coordinator |
| `WebMcpInjectionActionPolicy` | Named `IActionPolicy` — injects the WebMCP script into proxied HTML | Guard |
| `OfferMcpActionPolicy` | Named `IActionPolicy` — the carrot response | Guard |
| `IToolInvocationLog` | Durable per-call record → metering + dashboard | Store |

## 5. The three lanes

Nothing reaches a caller without passing the promotion gate, except Lane 2, which is
inherently safe (it only ever reads public content the site already serves).

### Lane 1 — Documented (`IOpenApiCatalog`)

Already loaded at startup. Each `LoadedOpenApiOperation` maps to a candidate tool:

- Name: `operationId`, else `{method}_{path_slug}`
- Description: `summary` + `description`
- `inputSchema`: JSON Schema assembled from `parameters` (path/query/header) — the request
  body is ignored in v1 since only GET-shaped operations are promotable remotely
- Binding: `{ kind: "http", method, pathTemplate }`

**Remote-channel filter:** only `GET`/`HEAD` operations may be promoted to the remote
channel. A promoted `POST` is accepted into the catalog but `tools/call` rejects it on
Channel R with `-32000 write_not_permitted`. This is belt-and-braces: promotion should
never have offered it, and the executor refuses anyway.

### Lane 2 — Content (`ISiteIndex`)

Always on, zero config. Two tools:

- `search_site(query, limit?)` → `[{ url, title, snippet, score }]`
- `fetch_page(url)` → clean Markdown for one URL

`search_site` is served entirely from the index. `fetch_page` does **not** serve stored
bodies — it performs a revalidating loopback GET to upstream through the existing
StyloExtract markdown path (§12 explains why this matters).

### Lane 3 — Observed (candidates → promotion)

Two sources, both producing *candidates* that are invisible until an operator promotes them:

1. **Routes.** `IRouteCatalogService` already tracks observed routes with operator-assigned
   friendly names. Undocumented routes with meaningful traffic become candidates.
2. **Forms.** When the gateway proxies HTML, `FormToolSynthesiser` scans for `<form>`
   elements: `action`, `method`, and named inputs (`name`, `type`, `required`,
   `<select>` options → enum) become a JSON Schema. This is the heart of "old site, new
   tools" — a decade-old search form becomes a declared agent tool without anyone touching it.

Server-side derivation (rather than client-side DOM scanning) is deliberate: it means form
tools pass through the same promotion gate as everything else, and the operator sees them
in the dashboard before any agent does.

Promotion UI lives on the existing **Site → endpoint detail** pack slot: a candidate list
with Promote / Reject, an editable name and description, and a preview of the generated
schema. Rejections are sticky.

## 6. The two channels

### Channel R — remote MCP

Endpoint: `POST {BotDetection:WebMcp:Path}` (default `/_stylobot/mcp`).

JSON-RPC 2.0, Streamable HTTP. Methods: `initialize`, `notifications/initialized`, `ping`,
`tools/list`, `tools/call`, `resources/list`, `resources/read`,
`resources/templates/list`. `GET` returns `405` (no SSE stream in v1 — nothing is
server-initiated). Sessions via the `Mcp-Session-Id` header.

`resources/*` exposes indexed pages as MCP resources (`stylobot://page/{url_hash}`), which
gives clients that prefer resources over tools a native path to the same content.

**Discovery.** There is no ratified well-known URI for MCP server discovery. We therefore:

- serve the manifest at `/.well-known/mcp.json` — unratified, but where the ecosystem is
  heading and it costs nothing;
- emit `Link: <https://host/_stylobot/mcp>; rel="mcp-server"` on HTML responses;
- add an MCP section to `/llms.txt`. Note the `/llms.txt` synthesiser was scoped to the
  AgentContent pack, which was **never built** — so this pack either serves its own minimal
  `/llms.txt` (URL list from `documents` + an MCP pointer) or contributes a section if
  AgentContent later lands. Increment 5 owns the choice; it is not a dependency.

If a well-known path is later ratified with different semantics, only the manifest writer
changes.

### Channel P — in-page WebMCP

A named action policy injects `<script src="/_stylobot/webmcp.js">` before `</body>` of
proxied `text/html` responses. The script feature-detects `navigator.modelContext` and
registers the page's tools.

Two kinds of in-page tool:

1. **Site-wide read tools** (`search_site`, `fetch_page`) — proxied same-origin to Channel R.
2. **Page-local form tools** — the *only* place writes happen. `execute` fills the live DOM
   form and submits it, so the request carries the user's own cookies and CSRF token. The
   agent acts as the user, in the user's session, with the user's authority. StyloBot never
   holds a credential.

`navigator.modelContext` is a **W3C proposal, not a ratified standard.** The injected
script feature-detects and no-ops when absent, and its registration shim is versioned so a
spec change is a script update, not an architecture change.

**Injection safety.** Injection is skipped — leaving the response byte-identical — when
any of: content-type is not `text/html`; status is not 200; the response looks like a
partial/fragment (HTMX, `fetch`); the marker comment is already present (idempotency); or
a CSP is present from which no nonce can be derived. External `src` (not inline) keeps us
compatible with `script-src` policies that allow same-origin.

### Not a channel — `offer-mcp` action policy

When detection would throttle or block an AI scraper, `offer-mcp` returns the throttle
status *plus* the deal: the `Link: rel="mcp-server"` header and a small body pointing at
the sanctioned surface. Registered alongside `throttle-stealth` / `redirect-honeypot`;
operators opt in per-policy.

This is the commercial story in one sentence: **stop paying to fight crawlers and start
metering them instead.**

## 7. Identity, policy and metering

Per the Critical Rules, **the MCP endpoint is not exempt from detection.** It is an
endpoint like any other; requests to it run the full pipeline. There is no skip path.

Budget tiers, resolved from signals the stack already produces:

| Tier | Signal | Budget |
|---|---|---|
| Anonymous | none | Low call rate, truncated results |
| API key | `X-SB-Api-Key` (existing Tier 2 auth) | Operator-configured |
| Verified agent | `identity.verified_bot_signed` = true (locked C3 key, **shipped**) | Highest — identity is cryptographic |

The verified-agent tier is what nobody else can offer. `WebBotAuthApprovalAtom` already
verifies RFC 9421 signatures once per session and publishes `identity.verified_bot_signed`
/ `identity.verified_bot_name`. WebMcp reads those keys — it adds no crypto of its own.

Per-tool policy reuses the **shipped** `IEndpointPolicyRuleExtension` seam (contract C4a):
a `toolPolicy` rule extension matches on tool name, so tool access is configured in the
same YAML as everything else rather than in a parallel system.

`IToolInvocationLog` records every call — tool, fingerprint, tier, latency, bytes, outcome
— feeding a dashboard panel and, later, an AI-traffic bill.

## 8. Storage

One new SQLite database, `webmcp.db`, owned by the enforcement component
(gateway/sidecar/embedded), consistent with `feedback_upstream_not_state_authority`.

```sql
documents(id INTEGER PRIMARY KEY, host, url, path, title, content_hash, etag,
          last_modified, body, byte_len, indexed_utc, source)  -- source: passive|sitemap|manual
                                                   -- `body` is the bounded extract, capped
                                                   -- at Index:MaxExcerptBytes
documents_fts USING fts5(title, body, content='documents', content_rowid='id',
                         tokenize='porter unicode61')    -- bm25() ranking
tool_candidates(route_key, host, kind, source, first_seen, hit_count,
                derived_schema_json, status)             -- status: candidate|promoted|rejected
tools(name, host, kind, description, input_schema_json, binding_json, enabled, updated_utc)
tool_invocations(id, ts, tool_name, fingerprint_id, tier, ok, latency_ms, bytes_out, error_code)
```

Writes go through the existing `WriteBehindLfuStore` pattern (per
`feedback_no_unbacked_imemorycache` and `project_slim_search_writer_breach` — a single
shared drain on one connection, never `Task.Run`-per-write). `tools/list` projections are
served through `SignalShingleCache`, so a cold or concurrent list never fans out into
repeated catalog composition.

## 9. Data flow

**Indexing (passive).** Request → detection → YARP → upstream 200 `text/html` → StyloExtract
captures the body → `SiteCorpusWriter` enqueues `{url, hash, extracted text}` → write-behind
drain → FTS5. Skipped when the content hash is unchanged. Entirely off the request path.

**Search.** `tools/call search_site` → detection runs → tier resolved → `Fts5SiteIndex.Search`
(bm25, `snippet()` for excerpts) → hits → invocation logged.

**Fetch.** `tools/call fetch_page` → conditional loopback GET to upstream → StyloExtract
markdown transform → returned. Never serves a stored body.

**In-page write.** Browser loads page → injection policy adds script → agent calls
`submit_search` → script fills and submits the real form → the request comes back through
the gateway as an ordinary request and is detected normally.

## 10. Error handling

The whole pack is fail-open; it can degrade to invisibility without affecting traffic.

| Failure | Behaviour |
|---|---|
| Index unavailable/corrupt | `search_site` returns `-32000 index_unavailable`; other tools unaffected; normal traffic untouched |
| Injection throws | Original HTML served byte-identical (StyloExtract's existing posture) |
| Upstream 5xx on `fetch_page` | Mapped to an MCP error with the upstream status; never a gateway 500 |
| OpenAPI doc unreachable | Lane 1 empty; Lanes 2–3 still work (existing `ContinueOnFailure`) |
| Catalog empty | `tools/list` returns `[]` — a valid MCP server with no tools |
| Write op promoted in error | Executor refuses on Channel R regardless of promotion |
| Sitemap crawl fails | Logged; passive indexing continues |

`BotDetection:WebMcp:Enabled` defaults to **false**, matching the observe-only posture the
rest of the product ships with.

## 11. Testing

- **Unit.** OpenAPI operation → JSON Schema (path/query/header params, enums, required);
  `<form>` → schema (input types, `<select>`, `required`, missing `name`); FTS5 query
  escaping (quotes, `*`, `NEAR`, empty, oversized); cache-key/version-salt behaviour.
- **Protocol conformance.** Golden JSON-RPC transcripts for the 8 methods; error codes
  (`-32700`, `-32601`, `-32602`, `-32000`); `initialize` version negotiation; `GET` → 405.
- **Integration** (`WebApplicationFactory` over the gateway). Detection *does* run on MCP
  calls — a BDF probe asserts this, since a skip path here would violate a Critical Rule.
  A candidate is not callable until promoted. A promoted `POST` is refused on Channel R.
  Tier budgets are enforced.
- **Injection.** HTML fixtures: CSP present/absent, non-HTML, fragment responses,
  double-injection idempotency, malformed HTML. Assert byte-identical output whenever
  injection is skipped.
- **Performance.** `tools/list` under `SignalShingleCache`; `search_site` p95 on a
  10k-document index; confirm indexing adds no measurable latency to the proxied request.

## 12. Departure needing sign-off

The 2026-07-07 shape doc locked this guardrail:

> **Upstream owns content.** The markdown transformer reads the upstream HTML response and
> produces markdown; it does NOT fetch the sitemap independently or persist a shadow
> content store.

This design **does** persist derived content and **does** optionally read `sitemap.xml`.
That is a real departure and should not slip through unnoticed. Three things bear on it:

1. The two memories that guardrail cited (`feedback_upstream_owns_no_stylobot_state`,
   `feedback_no_caches_freshness_over_locality`) no longer exist in the memory index. The
   surviving related memory, `feedback_upstream_not_state_authority`, says the *enforcement
   component* owns all operational state — which this design satisfies, since `webmcp.db`
   lives in the gateway.
2. StyloExtract (2026-06-20, a month later) shipped a content cache with operator approval,
   which already relaxed the strict "no caches" line.
3. Search is impossible without an index. A retrieval index is a *derived projection* — the
   same category as `session_centroids.db` and `signature_centroids.db`, both of which the
   repo already accepts.

The design keeps the guardrail's *intent* intact by never treating the corpus as an
authority: `search_site` returns URL + snippet + score, and `fetch_page` revalidates
against upstream rather than serving stored bytes. Stale corpus content can therefore
affect *ranking*, but never what the caller is actually served.

**If you'd rather not persist extracted text at all**, the fallback is an FTS5 contentless
index (`content=''`): ranking still works, but `snippet()` does not, so search results
lose their excerpts and become URL + score only. That is a materially worse agent
experience, which is why it is not the recommendation.

## 13. Increments

Each ships independently and is useful on its own.

| # | Scope | Value at end |
|---|---|---|
| 1 | `ISiteIndex` + FTS5 + passive corpus + `search_site`/`fetch_page` + MCP endpoint | A real MCP server for any proxied site, zero config |
| 2 | Lane 1: OpenAPI → typed read tools | Documented APIs become tools |
| 3 | Lane 3: candidates + dashboard promotion | Undocumented sites get curated tools |
| 4 | Channel P: injection + form tools | In-browser agents can *act*; writes arrive |
| 5 | `offer-mcp` policy + metering panel | The crawler deal, and the numbers to price it |
| 6 | Sitemap warm | Cold deployments start useful |

Deferred to their own specs: remote writes with OAuth 2.1 delegation; ops-MCP over
`/api/v1`; commercial retrieval at scale.

## 14. Configuration

```json
{
  "BotDetection": {
    "WebMcp": {
      "Enabled": false,
      "Path": "/_stylobot/mcp",
      "ServerName": "example.com",
      "Index": { "StorePath": "webmcp.db", "MaxDocuments": 50000, "MaxExcerptBytes": 8192 },
      "Corpus": { "PassiveCapture": true, "SitemapWarm": false, "SitemapUrl": null,
                  "MaxCrawlPagesPerRun": 200, "CrawlDelayMs": 500 },
      "Injection": { "Enabled": false, "ScriptPath": "/_stylobot/webmcp.js" },
      "Tiers": {
        "Anonymous":     { "CallsPerMinute": 10,  "MaxResults": 5 },
        "ApiKey":        { "CallsPerMinute": 120, "MaxResults": 25 },
        "VerifiedAgent": { "CallsPerMinute": 600, "MaxResults": 50 }
      }
    }
  }
}
```

Every knob is on an Options class bound from `BotDetection:WebMcp`. No magic numbers, per
the repo-wide rule.

## 15. Open questions

1. **Multi-tenant hosts.** `Stylobot.All` can front several upstreams. One MCP server with
   host-scoped tools, or one endpoint per host? Leaning host-scoped (`documents.host` and
   `tools.host` are already in the schema), but the discovery story needs thought.
2. **Robots/opt-out.** Should a site's `robots.txt` disallow list suppress indexing of
   those paths? Probably yes for the *content* lane — but it is the operator's own site,
   so it may be over-eager.
3. **`tools/list` volatility.** Promotion changes the tool list live. MCP has
   `notifications/tools/list_changed`, which needs the SSE stream v1 deliberately omits.
   Polling clients converge; the notification is a Channel R v2 item.
