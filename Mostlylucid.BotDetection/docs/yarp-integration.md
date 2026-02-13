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
