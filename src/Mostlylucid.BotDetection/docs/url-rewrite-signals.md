# URL rewrite - inject detection signals as query params

StyloBot can rewrite each incoming request's query string to add detection signals as namespaced params before any downstream handler sees the request. The CDN, the origin, and your app code all observe the same enriched URL. This complements the header-based signal flow (`AddBotDetectionHeaders`) - headers are great for app code, query params are great for CDN cache keys.

**Disabled by default.** Flip `BotDetection:UrlRewrite:Enabled` to turn it on. See `UrlRewriteOptions` for the canonical option reference.

## Why URL params (and not just headers)?

| Concern | Headers | URL params |
|---|---|---|
| App code reads them | Yes - `X-Bot-*` | Yes - `Request.Query["sb_*"]` |
| CDN cache key | Requires `Vary: X-Bot-*` (CDNs vary in support) | Native - URL IS the cache key |
| Survives client cache | Yes | Yes |
| Survives `<a href>` follow | No | Yes - params are in the link |
| Visible in browser address bar | No | Yes (this is mostly fine, sometimes a feature) |

The big unlock is CDN behaviour: a CDN keyed on `/api/products?sb_country=GB&sb_is_bot=false` natively serves four different cached variants (GB human, GB bot, US human, US bot) with no `Vary` configuration. Headers + `Vary` works but is finicky across providers; URL params are universal.

## Configuration

```json
{
  "BotDetection": {
    "SignatureHashKey": "<base64-or-text-secret, same key used elsewhere>",
    "UrlRewrite": {
      "Enabled": true,
      "Prefix": "sb_",
      "Signals": ["country", "is_bot", "probability", "risk_band", "is_datacenter", "is_vpn"],
      "Sign": true,
      "StripExisting": true,
      "ApplyTo": "Patterns",
      "PathPatterns": ["/api/*"],
      "PathExclusions": []
    }
  }
}
```

### Recognised signal names

| Name | Source | Example value |
|---|---|---|
| `is_bot` | `IsBot()` | `true` / `false` |
| `probability` | `evidence.BotProbability` | `0.873` |
| `risk_band` | `evidence.RiskBand` | `Elevated`, `High`, … |
| `bot_type` | `BotType?` | `SearchEngine` |
| `bot_name` | `BotName?` | `Googlebot` |
| `country` | `geo.country_code` or `CF-IPCountry` / `X-Country` header fallback | `GB` |
| `is_vpn` | `geo.is_vpn` | `true` / `false` |
| `is_proxy` | `geo.is_proxy` | `true` / `false` |
| `is_tor` | `geo.is_tor` | `true` / `false` |
| `is_datacenter` | `geo.is_hosting` OR `ip.is_datacenter` | `true` / `false` |
| `fingerprint_id` | `identity.fingerprint_id` (requires `Identity:Enabled = true`) | hex id |
| `client_type` | `identity.client_type` | `chrome`, `curl`, … |
| `threat_score` | `honeypot.threat_score` | `0.42` |

Unknown names are silently skipped. A signal whose source value is absent or unknown (e.g. country is `LOCAL`) is also skipped - emission is conditional on presence so the canonical signing input always reflects real values.

## Security model

Three guarantees keep this honest:

1. **Strip-existing** (`StripExisting`, default `true`). Any inbound param whose name starts with `Prefix` is dropped before our set is added. Stops a client from pre-populating `?sb_country=US` and bypassing detection on a lenient upstream.
2. **HMAC signing** (`Sign`, default `true`). The emitted set is HMAC-SHA256'd against `BotDetection:SignatureHashKey` and the digest is appended as `{prefix}sig`. Receivers must call `UrlSignalProjection.VerifySignedQueryParams` and reject on mismatch.
3. **Explicit scope** (`ApplyTo` + `PathPatterns` + `PathExclusions`). Anything you exclude is a path where downstream sees unsigned (or no) `sb_*` params. If your origin still trusts inbound `sb_*` on those paths, you've made a bypass. **The defaults ship with empty exclusions on purpose** - every exclusion you add is your own audit obligation.

## End-to-end verification recipe

On the receiving tier (origin app, downstream microservice, anywhere that reads `sb_*`):

```csharp
using Mostlylucid.BotDetection.Extensions;

app.Use(async (ctx, next) =>
{
    var ok = UrlSignalProjection.VerifySignedQueryParams(
        ctx.Request.Query.Select(q => new KeyValuePair<string, string>(q.Key, q.Value.ToString())),
        prefix: "sb_",
        signingKey: Encoding.UTF8.GetBytes(builder.Configuration["BotDetection:SignatureHashKey"]!));

    // No signed set on the request? Either it didn't go through the gateway,
    // or someone stripped the sig in transit. Treat absence as "no trusted
    // signals" - don't fall back to inbound sb_* without verification.
    ctx.Items["sb_signals_verified"] = ok;

    await next();
});
```

## Wiring

`UseStyloBot()` already includes `UseBotDetectionUrlRewrite()`. For manual pipelines:

```csharp
app.UseBotDetection();           // detection populates the signals
app.UseBotDetectionUrlRewrite(); // mutates Request.QueryString
app.MapReverseProxy();           // YARP now forwards the rewritten URL
```

The middleware is a no-op when `Enabled = false`, so it's safe to leave in any pipeline.

## Performance

- Per-request cost: one `SortedDictionary` build + one HMAC-SHA256 over a short string (~30-200 bytes). Sub-microsecond on the fast path.
- No allocations on excluded paths - the path matcher short-circuits before any signal projection.
- The mutation is on `Request.QueryString` directly; downstream code reads the rewritten value transparently.

## Path pattern syntax

| Pattern | Matches |
|---|---|
| `/api/*` | Any path under `/api/` at any depth (`StartsWithSegments` semantics) |
| `*.html` | Any path ending in `.html` |
| `/health` | Exact match only |

These three forms cover virtually every real use case. If you need glob semantics richer than this, raise an issue - the underlying matcher is a single switch in `UrlSignalProjection.MatchesPattern`.