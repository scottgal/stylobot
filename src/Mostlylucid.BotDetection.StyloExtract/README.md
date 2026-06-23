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
| `extract-markdown` | Replaces HTML response body with Markdown. Content-Type becomes `text/markdown; charset=utf-8`. |
| `extract-headers` | Adds `X-StyloExtract-*` response headers. Body unchanged. |
| `extract-sidecar` | Adds `Link: <url>; rel="alternate"; type="text/markdown"` header. Body unchanged. |
| `extract-passthrough` | Explicit no-op. Returns Allowed immediately without invoking the extractor. |

All policies return `ActionResult.Allowed` - they transform the response but never block the request. Any extraction failure is logged at Warning and the original response is preserved (fail-open, always).

## Configuration

Options are read from `StyloExtract:Actions:{policyName}` in appsettings.json:

```json
{
  "StyloExtract": {
    "Actions": {
      "extract-markdown": {
        "Profile": "RagFull",
        "EnableQueryOverride": true,
        "QueryParamName": "format",
        "QueryParamValue": "markdown",
        "Cache": {
          "Mode": "Override",
          "MaxAge": 86400,
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
| `EnableQueryOverride` | `bool` | `true` | When true, `?format=markdown` triggers the markdown body swap regardless of bot-type. |
| `QueryParamName` | `string` | `"format"` | Query parameter name for the override. |
| `QueryParamValue` | `string` | `"markdown"` | Query parameter value for the override. |
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
        "Endpoints": ["/api/*"],
        "BotThreshold": 0.7,
        "ActionPolicyName": "extract-markdown"
      }
    }
  }
}
```
