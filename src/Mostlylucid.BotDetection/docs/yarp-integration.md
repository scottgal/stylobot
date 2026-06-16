# YARP Integration

First-class support for [YARP (Yet Another Reverse Proxy)](https://microsoft.github.io/reverse-proxy/).

## Adding Bot Detection Headers

Pass bot detection results to backend services:

```csharp
using Mostlylucid.BotDetection.Extensions;

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        context.AddRequestTransform(transformContext =>
        {
            transformContext.HttpContext.AddBotDetectionHeaders(
                (name, value) => transformContext.ProxyRequest.Headers
                    .TryAddWithoutValidation(name, value));
            return ValueTask.CompletedTask;
        });
    });
```

### Headers Added

| Header               | Value                           |
|----------------------|---------------------------------|
| `X-Bot-Detected`     | `true` / `false`                |
| `X-Bot-Confidence`   | `0.00` - `1.00`                 |
| `X-Bot-Type`         | `SearchEngine`, `Scraper`, etc. |
| `X-Bot-Name`         | Identified bot name             |
| `X-Bot-Category`     | Detection category              |
| `X-Is-Search-Engine` | `true` / `false`                |
| `X-Is-Malicious-Bot` | `true` / `false`                |
| `X-Is-Social-Bot`    | `true` / `false`                |

## Bot-Aware Cluster Selection

Route different bot types to different backends:

```csharp
var cluster = httpContext.GetBotAwareCluster(
    defaultCluster: "main-cluster",
    crawlerCluster: "crawler-cluster",  // Optimized for search engines
    blockCluster: "blocked-cluster"     // Returns 403
);
```

## Blocking at Proxy Layer

```csharp
if (httpContext.ShouldBlockBot(
    minConfidence: 0.7,
    allowSearchEngines: true,
    allowSocialBots: true))
{
    httpContext.Response.StatusCode = 403;
    return;
}
```

## Complete Example

```csharp
using Mostlylucid.BotDetection.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add bot detection
builder.Services.AddBotDetection();

// Add YARP with bot detection transforms
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        context.AddRequestTransform(async transformContext =>
        {
            var httpContext = transformContext.HttpContext;

            // Block malicious bots before proxying
            if (httpContext.IsMaliciousBot())
            {
                httpContext.Response.StatusCode = 403;
                await httpContext.Response.WriteAsync("Access Denied");
                return;
            }

            // Add headers for backend
            httpContext.AddBotDetectionHeaders(
                (name, value) => transformContext.ProxyRequest.Headers
                    .TryAddWithoutValidation(name, value));
        });
    });

var app = builder.Build();

app.UseBotDetection();  // Must come before MapReverseProxy
app.MapReverseProxy();
app.Run();
```

## YARP Configuration

```json
{
  "ReverseProxy": {
    "Routes": {
      "api-route": {
        "ClusterId": "api-cluster",
        "Match": { "Path": "/api/{**catch-all}" },
        "Metadata": { "BotPolicy": "BlockMalicious" }
      },
      "crawler-route": {
        "ClusterId": "crawler-cluster",
        "Match": { "Path": "/sitemap.xml" },
        "Metadata": { "BotPolicy": "AllowSearchEngines" }
      }
    },
    "Clusters": {
      "api-cluster": {
        "Destinations": {
          "api-server": { "Address": "http://api-backend:8080" }
        }
      },
      "crawler-cluster": {
        "Destinations": {
          "crawler-server": { "Address": "http://crawler-backend:8080" }
        }
      }
    }
  }
}
```

See `Examples/YarpBotDetectionExample.cs` in the demo project for more integration patterns.

## Transport fingerprint headers behind a reverse proxy

If your YARP host sits behind a CDN or upstream proxy that injects transport fingerprint headers (`X-JA3-*`, `X-JA4*`, `X-Client-TLS-*`, `X-HTTP2-*`, `X-QUIC-*`, `X-TCP-*`), configure the trusted-proxy gate so those headers are honoured:

```json
{
  "BotDetection": {
    "TransportTrust": {
      "Mode": "Auto",
      "TrustedProxyIps": ["10.0.0.0/8", "203.0.113.5"]
    }
  }
}
```

- `Auto` (default): trusts loopback and RFC 1918/4193 private peers automatically. Sufficient for a local-loopback topology (nginx/Caddy → YARP on the same host).
- `Strict`: trusts only IPs in `TrustedProxyIps`. Use this when your upstream has a public IP (Cloudflare anycast, AWS ALB, etc.).
- `Off`: trusts all peers (legacy, emits a startup warning).

Public-IP edges must be added to `TrustedProxyIps` in either `Auto` or `Strict` mode; the gate never infers trust from forwarded headers such as `X-Forwarded-For`. See [`docs/REVERSE_PROXY_SIGNALS.md`](../../../docs/REVERSE_PROXY_SIGNALS.md#trusted-proxy-gate-transport-fingerprint-headers) for full details.
