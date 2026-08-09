# Mostlylucid.BotDetection.StyloExtract

Bridge pack between StyloExtract and StyloBot's `IActionPolicy` registry. Provides four named action policies that operators reference by name from `EndpointPolicy` rules.

> This package replaces `Mostlylucid.StyloExtract.StyloBot` (1.6.x). The source has moved into the stylobot repo so it co-evolves with `Mostlylucid.BotDetection` via ProjectReference.

## Setup

```csharp
builder.Services.AddStyloExtract(o => o.StorePath = "styloextract.db");
builder.Services.AddBotDetection(); // or AddStyloBot()
builder.Services.AddStyloExtractActionPolicies();
```

`AddStyloExtractActionPolicies()` is in the `Mostlylucid.BotDetection.StyloExtract.Extensions` namespace.

## Policies

| Name | Behaviour |
|------|-----------|
| `content-cache-search` | Bounded, process-local HTML cache for search engines. A hit short-circuits before the endpoint; a miss captures a cacheable HTML response and stores it. |
| `extract-markdown-cache-ai` | Bounded, process-local HTML→Markdown cache for verified AI-scraper traffic only (BotType=AiBot). A hit serves cached Markdown; a miss extracts from the captured HTML, stores and serves it. Never serves Markdown to browsers. |
| `extract-headers` | Adds `X-StyloExtract-*` response headers. Body unchanged. |
| `extract-sidecar` | Adds `Link: <url>; rel="alternate"; type="text/markdown"` header. Body unchanged. |
| `extract-passthrough` | Explicit no-op. Returns Allowed immediately without invoking the extractor. |

All policies return `ActionResult.Allowed` - they transform the response but never block the request. Any extraction failure is logged at Warning and the original response is preserved (fail-open, always). The two content-cache policies use a per-policy bounded LFU store (`TransformedContentCache` bounds, sliding idle + absolute expiry, entry-byte and total-byte caps) and are never persistent or distributed.

## Configuration

Options are read from `StyloExtract:Actions:{policyName}` in appsettings.json:

```json
{
  "StyloExtract": {
    "Actions": {
      "extract-markdown-cache-ai": {
        "Profile": "RagFull",
        "EnableQueryOverride": true,
        "QueryParamName": "markdown",
        "QueryParamValue": "true",
        "TransformedContentCache": {
          "Enabled": true,
          "MaxEntries": 128,
          "MaxEntryBytes": 262144,
          "MaxTotalBytes": 33554432,
          "SlidingExpiration": "00:30:00",
          "AbsoluteExpiration": "24:00:00",
          "VersionSalt": "v2",
          "AllowedQueryKeys": ["page", "q"]
        },
        "Cache": {
          "Mode": "Override",
          "MaxAge": 3600,
          "Public": true,
          "VaryByBotType": true,
          "VaryByAccept": false
        }
      },
      "extract-sidecar": {
        "SidecarRouteTemplate": "/{path}.md"
      }
    }
  }
}
```

### StyloExtractActionOptions

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Profile` | `ExtractionProfile` | `RagFull` | Controls which content appears in the Markdown output. |
| `EnableQueryOverride` | `bool` | `true` | When true, `?markdown=true` (the explicit test action) triggers the Markdown body swap regardless of bot-type. Separately labelled in telemetry; uses the Markdown variant's cache keys only. |
| `QueryParamName` | `string` | `"markdown"` | Query parameter name for the override. |
| `QueryParamValue` | `string` | `"true"` | Query parameter value for the override. |
| `TransformedContentCache.Enabled` | `bool` | `false` | Whether this policy's bounded store is active. |
| `TransformedContentCache.MaxEntries` | `int` | `128` | Entry cap for this policy's LFU store (per-policy). |
| `TransformedContentCache.MaxEntryBytes` | `int` | `262144` | Body+headers buffered per entry; larger responses pass through unstored. |
| `TransformedContentCache.MaxTotalBytes` | `int` | `33554432` | Hard payload bound; startup validates `MaxEntries * MaxEntryBytes <= MaxTotalBytes`. |
| `TransformedContentCache.SlidingExpiration` | `TimeSpan` | `00:02:00` | Idle expiry for this policy's store. |
| `TransformedContentCache.AbsoluteExpiration` | `TimeSpan` | `00:15:00` | Non-extendable absolute expiry for this policy's store. |
| `TransformedContentCache.VersionSalt` | `string` | `"v1"` | Transform/version salt in the cache key; bumping invalidates all entries. |
| `TransformedContentCache.AllowedQueryKeys` | `string[]` | `[]` | Query keys participating in the cache key. Empty = all keys (back-compat default). |
| `Cache.Mode` | `Respect\|Override\|Add` | `Respect` | How Cache-Control is modified. |
| `Cache.MaxAge` | `int?` | `null` | Maps to `max-age=N` seconds. |
| `Cache.Public` | `bool?` | `null` | Adds `public` directive. |
| `Cache.NoStore` | `bool?` | `null` | Adds `no-store`. |
| `Cache.MustRevalidate` | `bool?` | `null` | Adds `must-revalidate`. |
| `Cache.VaryByBotType` | `bool` | `false` | Appends `X-StyloBot-BotType` to `Vary`. |
| `Cache.VaryByAccept` | `bool` | `false` | Appends `Accept` to `Vary`. |
| `SidecarRouteTemplate` | `string` | `"/{path}.md"` | Template for the sidecar Link header. `{path}` = full path, `{slug}` = last segment. |

### Wiring in EndpointPolicy rules

```json
{
  "BotDetection": {
    "Policies": {
      "api-bots": {
        "Endpoints": ["/docs/*"],
        "Types": ["AiBot"],
        "ActionPolicyName": "extract-markdown-cache-ai"
      }
    }
  }
}
```
