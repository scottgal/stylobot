# StyloBot — Architecture (FOSS plane)

```mermaid C4Component
title StyloBot FOSS plane — content-cache + dashboard auth

Container_Boundary(foss, "StyloBot FOSS") {
    System_Ext(gateway, "Gateway / stylobot-all", "YARP reverse proxy + detection + dashboard")

    Container_Boundary(detection, "Detection Pipeline") {
        Component(orch, "BotDetectionOrchestrator", "IDetectorAtom[]", "Runs 67 atoms, produces AggregatedEvidence")
        Component(pdag, "PostDetectionActionGate", "IActionPolicy", "Resolves policy → executes → Continue/Blocked")
    }

    Container_Boundary(cache_plane, "Content-Cache Plane") {
        Component(ccap, "ContentCacheSearchActionPolicy", "IActionPolicy · name=content-cache-search", "Cache hit → Blocked(200); miss → interceptor → publish")
        Component(emap, "ExtractMarkdownCacheAiActionPolicy", "IActionPolicy · name=extract-markdown-cache-ai", "Miss → HTML→Markdown transform → publish; gate: AiBot only (+ ?markdown=true test action)")
        Component(mrc, "MarkdownResponseCache", "Lease-based SlidingCacheAtom wrapper", "AcquireAsync / TryBeginFill / Publish / Discard · per-policy instance")
        Component(ckb, "CacheKeyBuilder", "Pure function", "host | method | normalised path | selected query | representation | variant | salt")
        Component(cev, "CacheabilityEvaluator", "Pure function", "Rejects: auth/session cookies, personalised, Set-Cookie, 206, >=400, streamed, no-store|private")
        Component(rbc, "ResponseBodyCapture", "BodyInterceptStream", "Swaps Response.Body; buffers writes; runs transform on flush")
        Component(ccw, "CacheControlWriter", "Cache-Control + Vary", "Override | Respect | Add modes; Vary: X-StyloBot-BotType, Accept")
        Component(cct, "ContentCacheTelemetry", "Meter + IPolicyStateProvider", "Per-policy hit/miss/bypass/eviction counters → SSR + SignalR/HTMX OOB")
    }

    Container_Boundary(auth_plane, "Dashboard View-Auth") {
        Component(dvcp, "DashboardViewCredentialVerifier", "Constant-time verify", "Username + PBKDF2 password hash from config")
        Component(dvph, "DashboardPasswordHasher", "PBKDF2 · PasswordHasher<T>", "Shared by CLI (stylobot dashboard hash-password) + login verifier")
        Component(dvdp, "DashboardViewAuthDefaults", "Constants", "Scheme: StyloBotDashboardCookie · Policy: stylobot-dashboard-view")
        Component(pev, "IPolicyEvaluator gate", "Inline auth evaluation", "Authenticate + Authorize policy; no UseAuthentication() ordering dependency")
        Component(dpos, "DashboardAuthPosture", "Startup advisory", "Warns: no auth configured / Login mode incomplete")
    }
}

Rel(ccap, mrc, "cache read/write")
Rel(emap, mrc, "cache read/write · Markdown variant")
Rel(ccap, rbc, "installs interceptor on miss")
Rel(emap, rbc, "installs interceptor on miss · transforms to Markdown")
Rel(ccap, ckb, "builds key")
Rel(emap, ckb, "builds key · includes extraction profile")
Rel(ccap, cev, "evaluates response")
Rel(emap, cev, "evaluates response")
Rel(ccap, ccw, "writes headers on hit")
Rel(emap, ccw, "writes headers on hit")
Rel(ccap, cct, "emits telemetry")
Rel(emap, cct, "emits telemetry")
Rel(mrc, cct, "eviction events")

Rel(pdag, ccap, "resolves action policy")
Rel(pdag, emap, "resolves action policy")

Rel(dvcp, dvph, "verifies hash")
Rel(pev, dvdp, "evaluates policy")
```

## Component ownership

| Prefix | Colour | Components |
|--------|--------|-----------|
| `cache-` | `#FFB74D` | ContentCacheActionPolicy, ExtractMarkdownActionPolicy, MarkdownResponseCache, CacheKeyBuilder, CacheabilityEvaluator, ResponseBodyCapture, CacheControlWriter, ContentCacheTelemetry |
| `stylobot-` | (roster) | BotDetectionOrchestrator, PostDetectionActionGate |
| `stylobot-` | (roster) | DashboardViewCredentialVerifier, DashboardPasswordHasher, DashboardViewAuthDefaults, IPolicyEvaluator gate, DashboardAuthPosture |

## Design decisions

1. **Short-circuit after detection.** Cache hit returns `ActionResult.Blocked(200)` — YARP never contacts upstream, but detection still runs. Cache-hit must call `MarkResponseFromStyloBot()` so `DegradationAtom` doesn't record a synthetic upstream 200.

2. **Same primitive, per-policy stores.** `content-cache-search` (HTML) and `extract-markdown-cache-ai` (Markdown) each own a `SlidingCacheAtom`-backed `MarkdownResponseCache` instance with its OWN configured bounds (entry capacity, byte caps, sliding idle + absolute expiry, enablement) — keyed DI, one store per policy name. They share `CacheabilityEvaluator`, `CacheKeyBuilder` and the `IContentCacheTelemetry` counters. A per-policy store means `extract-markdown-cache-ai`'s configured expiry/enablement is load-bearing, never dead config. Distinct `VersionSalt` keeps old entries inert on config change.

3. **Cache key composition.** host | method | normalised path | selected query values (per-policy allow-list) | response representation | policy variant | salt. Never includes all query params — only configured safe variance.

4. **Never-cache rules in `CacheabilityEvaluator`.** Auth/session cookies, personalised responses, Set-Cookie, 206 Partial, ≥400 status, streamed, `Cache-Control: no-store|private`. Fail-open to origin; bypass counted in telemetry.

5. **Markdown gate.** Stored Markdown served only to AiBot traffic. Browser can never receive Markdown — representation + variant are key components so a browser-hit key won't match a Markdown entry. The explicit `?markdown=true` test action (`MarkdownQueryOverrideMiddleware`) is honoured as the one exception: it is separately labelled in telemetry (`content_cache.overrides`) and uses the Markdown variant's cache keys only, so it can never serve a browser or search HTML entry.

6. **Fail-open, bounded.** Slot `Filling`/`Ready` states; failed/oversized/cancelled fill → slot invalidated. Cache-full → LFU eviction (lowest access count, then oldest access). Cache failure → origin served, counted as bypass.

7. **Telemetry.** Per-policy hit/miss/bypass/eviction via `System.Diagnostics.Metrics` + `IPolicyStateProvider` read model. SSR render + SignalR/HTMX OOB swap, no polling.

8. **Dashboard visibility.** Each policy row shows name, match (BotType/Path), representation, bounds (MaxEntries/MaxEntryBytes/MaxTotalBytes), hit/miss/bypass/eviction counts. Policy not "enabled" unless action implementation registered + row resolves.

## Config (gateway `appsettings.json`)

```json
{
  "StyloExtract": {
    "Actions": {
      "content-cache-search": {
        "TransformedContentCache": {
          "Enabled": true,
          "MaxEntries": 128,
          "MaxEntryBytes": 262144,
          "MaxTotalBytes": 33554432,
          "SlidingExpiration": "00:02:00",
          "AbsoluteExpiration": "00:15:00",
          "VersionSalt": "gateway-content-v1",
          "AllowedQueryKeys": ["q", "page"]
        }
      },
      "extract-markdown-cache-ai": {
        "Profile": "RagFull",
        "TransformedContentCache": {
          "Enabled": true,
          "MaxEntries": 128,
          "MaxEntryBytes": 262144,
          "MaxTotalBytes": 33554432,
          "SlidingExpiration": "00:30:00",
          "AbsoluteExpiration": "24:00:00",
          "VersionSalt": "gateway-markdown-v2",
          "AllowedQueryKeys": ["page", "q"]
        }
      }
    }
  },
  "BotDetection": {
    "DetectionPolicies": {
      "Rules": [
        { "Name": "search-engine-docs-cache", "Path": "/docs/*", "Types": ["SearchEngine"], "Action": "content-cache-search" },
        { "Name": "ai-bot-docs-markdown", "Path": "/docs/*", "Types": ["AiBot"], "Confidence": ">= 0.85", "Action": "extract-markdown-ai" }
      ]
    }
  }
}
```

## What already exists

- `ContentCacheSearchActionPolicy` (in `ContentCacheActionPolicyBase`) — cache-hit short-circuit, miss interceptor; per-policy LFU store via keyed DI
- `ExtractMarkdownCacheAiActionPolicy` — HTML→Markdown transform; AiBot-only gate + `?markdown=true` test action
- `MarkdownResponseCache` — lease-based `SlidingCacheAtom` wrapper (one instance per policy)
- `MarkdownQueryOverrideMiddleware` — the explicit `?markdown=true` test action (gateway-wired after `UseDetectionPolicies`)
- `ResponseBodyCapture` / `BodyInterceptStream` — response buffering
- `CacheControlWriter` — header management
- `TransformedContentCacheOptions` — config binding (per-policy bounds, now all load-bearing)
- `SbPolicyStateViewComponent` — dashboard policy-state card (effective match, representation, cache mode, bounds, hit/miss/bypass/eviction/override counters; configured-but-unregistered policies render NOT ENABLED)
- Gateway rules wired: `search-engine-docs-cache` + `ai-bot-docs-markdown`

## What needs building

1. **CacheKeyBuilder** — replace today's ad-hoc `content|{salt}|{host}|{path}|{all query}` with selected-query allow-list
2. **CacheabilityEvaluator** — extract never-cache rules from the policy implementations into a shared evaluator
3. **ContentCacheTelemetry** — `Meter` + `IPolicyStateProvider` counters
4. **Dashboard row** — per-policy observability in the Configuration page or a new Policies row
5. **Query allow-list** — confirm list (`q`, `page`, `format`, …) for real routes
6. **MarkResponseFromStyloBot bug** — cache-hit 200 must be marked as not-from-upstream
