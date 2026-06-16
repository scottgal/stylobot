# robots.txt and sitemap.xml Extensions

StyloBot provides two minimal-API extension methods that replace static file serving of `robots.txt` and `sitemap.xml` with detection-aware endpoints. Both are in `Mostlylucid.BotDetection.Extensions.RouteBuilderExtensions` and ship in the core `Mostlylucid.BotDetection` package.

## Why serve these via detection?

Static files bypass detection middleware entirely. A crawler hitting `/robots.txt` or `/sitemap.xml` directly from a `.txt` / `.xml` file gets the static-asset detection policy (fast-path only, neutral verdict). The extensions solve two concrete problems:

- **Adaptive sitemap**: serve the full URL list to verified crawlers and humans, but serve only a honeypot path to high-probability bots, so automated scanners waste time on bait paths.
- **Policy-derived robots.txt disallows**: keep the public robots contract consistent with live block-action rules. Paths the gateway will block are also marked `Disallow:` in the document well-behaved crawlers consult first.

Both endpoints call `handler.WithNotStaticAsset()` internally so the `.txt` / `.xml` file extension does not divert them to the `static` detection policy.

## MapStyloBotRobotsTxt

```csharp
// Program.cs - call inside the endpoint mapping block, after app.UseStyloBot()
app.MapStyloBotRobotsTxt("/robots.txt", options =>
{
    options.HeaderComments = ["Managed by StyloBot"];
    options.Rules =
    [
        new RobotsRule
        {
            UserAgent = "*",
            Disallow = ["/admin/", "/private/"],
            CrawlDelaySeconds = 10
        },
        new RobotsRule
        {
            UserAgent = "Googlebot",
            Allow = ["/"]
        }
    ];
    // options.SitemapUrl = "https://example.com/sitemap.xml"; // auto-derived when null
    // options.Host = "example.com"; // optional Host: directive
    // options.IncludePolicyDerivedDisallows = true; // default
});
```

### Options (`StyloBotRobotsTxtOptions`)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Rules` | `IList<RobotsRule>` | single permissive `*` block | One block per `User-agent:` directive |
| `SitemapUrl` | `string?` | null (auto) | `Sitemap:` directive value. When null, synthesised as `{scheme}://{host}/sitemap.xml` at request time |
| `Host` | `string?` | null | Optional `Host:` directive. Most crawlers ignore it |
| `HeaderComments` | `IList<string>` | empty | Lines emitted at the top of the file; `#` is prepended automatically |
| `IncludePolicyDerivedDisallows` | `bool` | `true` | When true, consults `IPolicyRuleStore` (if registered) and appends a `Disallow:` for every live Block-action rule scoped to an endpoint path |

### RobotsRule properties

| Property | Type | Description |
|----------|------|-------------|
| `UserAgent` | `string` | `User-agent:` token. Use `*` for catch-all |
| `Allow` | `IList<string>` | `Allow:` directives |
| `Disallow` | `IList<string>` | `Disallow:` directives |
| `CrawlDelaySeconds` | `int?` | `Crawl-delay:` (honoured by Bing/Yandex, ignored by Google) |

### Policy-derived disallows

When `IncludePolicyDerivedDisallows = true`, the endpoint reads `IPolicyRuleStore.GetAllRulesAsync()` at request time and appends a `Disallow:` for every rule where:

- `Mode == PolicyMode.Live`, AND
- `Action == PolicyAction.Block`, AND
- `Scope.Host` is an `HostScope.Endpoint` (single endpoint scope, not global or signature).

The path template is normalised (HTTP verb stripped if present). Derived paths are de-duplicated against the static `Rules` list. If `IPolicyRuleStore` is not registered or the store call fails, the static rules are served unchanged.

### Output shape

```
# Managed by StyloBot

User-agent: *
Disallow: /admin/
Disallow: /private/
Crawl-delay: 10

User-agent: Googlebot
Allow: /

Sitemap: https://example.com/sitemap.xml
```

## MapStyloBotSitemap

```csharp
app.MapStyloBotSitemap("/sitemap.xml", options =>
{
    options.PublicUrls =
    [
        "/", "/about", "/products", "/blog"
    ];
    options.UncertainUrls =
    [
        "/", "/about", "/products"  // admin/api surfaces excluded
    ];
    options.HoneypotPath = "/honeypot/admin";      // served to high-probability bots
    options.BotProbabilityThreshold = 0.7;          // default
    options.HumanProbabilityCeiling = 0.4;          // default
    options.EmitVerdictComment = true;              // default
});
```

### Options (`StyloBotSitemapOptions`)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PublicUrls` | `IList<string>` | `["/"]` | Served to verified crawlers and high-confidence humans |
| `UncertainUrls` | `IList<string>` | empty | Served when probability is between the two thresholds. Falls back to `PublicUrls` when empty |
| `HoneypotPath` | `string` | `"/honeypot/admin"` | Single path served to high-probability bots |
| `BotProbabilityThreshold` | `double` | `0.7` | Probability at or above which the visitor is treated as a bot and served only `HoneypotPath` |
| `HumanProbabilityCeiling` | `double` | `0.4` | Probability below which the visitor is treated as human and served `PublicUrls` |
| `EmitVerdictComment` | `bool` | `true` | Prepend an XML comment with the verdict, risk band, probability, and confidence |

### Verdict routing

| Condition | URL list served | Verdict label |
|-----------|----------------|---------------|
| `IsVerifiedBot() == true` OR `IsSearchEngineBot() == true` | `PublicUrls` | `verified-crawler` |
| `BotProbability >= BotProbabilityThreshold` | `[HoneypotPath]` | `high-probability-bot` |
| `BotProbability < HumanProbabilityCeiling` | `PublicUrls` | `human` |
| anything in between | `UncertainUrls` (or `PublicUrls` if empty) | `uncertain` |

`IsVerifiedBot()` / `IsSearchEngineBot()` are the standard `HttpContext` extension methods from `Mostlylucid.BotDetection`. Detection middleware must have run before the sitemap handler executes (guaranteed when using `app.UseStyloBot()`).

### Output shape (with `EmitVerdictComment = true`)

```xml
<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
  <!-- stylobot verdict: human (risk=Low, probability=0.12, confidence=0.84) -->
  <url><loc>https://example.com/</loc></url>
  <url><loc>https://example.com/about</loc></url>
  <url><loc>https://example.com/products</loc></url>
  <url><loc>https://example.com/blog</loc></url>
</urlset>
```

For a high-probability bot (verdict `high-probability-bot`):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
  <!-- stylobot verdict: high-probability-bot (risk=High, probability=0.91, confidence=0.77) -->
  <url><loc>https://example.com/honeypot/admin</loc></url>
</urlset>
```

## Registration and middleware ordering

Both endpoints are minimal-API routes and must be registered after `app.UseStyloBot()` (which installs the detection middleware):

```csharp
builder.Services.AddStyloBot();

app.UseRouting();
app.UseStyloBot();  // detection must run before the endpoints handle the request

app.MapStyloBotRobotsTxt();
app.MapStyloBotSitemap("/sitemap.xml", o =>
{
    o.PublicUrls = ["/", "/about", "/products"];
});
```

If you already serve `/robots.txt` or `/sitemap.xml` as static files (via `UseStaticFiles`), add those exclusions to the static file middleware so it skips them, or remove the files from `wwwroot`. Static file middleware short-circuits before the endpoint routing layer.