// ============================================================================
// YARP Bot Detection Integration Example
// ============================================================================
// This file demonstrates how to integrate Mostlylucid.BotDetection with YARP
// (Yet Another Reverse Proxy) to implement bot filtering at the proxy layer.
//
// To use this example:
// 1. Add the Yarp.ReverseProxy package: dotnet add package Yarp.ReverseProxy
// 2. Copy the relevant code into your YARP-based application
// ============================================================================

using Mostlylucid.BotDetection.Extensions;

namespace Mostlylucid.BotDetection.Demo.Examples;

// ============================================================================
// Option 1: Custom YARP Transform (Recommended)
// ============================================================================
// This approach uses YARP's transform pipeline to add bot detection headers
// to proxied requests, allowing backend services to make decisions.

/// <summary>
///     YARP transform that adds bot detection results as headers to proxied requests.
///     Backend services can then use these headers to implement their own logic.
/// </summary>
/// <example>
///     // In Program.cs with YARP:
///     builder.Services.AddReverseProxy()
///     .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
///     .AddTransforms(context =>
///     {
///     context.AddRequestTransform(transformContext =>
///     {
///     var httpContext = transformContext.HttpContext;
///     // Add bot detection headers to proxied request
///     if (httpContext.Items.TryGetValue(BotDetectionMiddleware.BotDetectionResultKey, out var result)
///     && result is BotDetectionResult botResult)
///     {
///     transformContext.ProxyRequest.Headers.Add("X-Bot-Detected", botResult.IsBot.ToString());
///     transformContext.ProxyRequest.Headers.Add("X-Bot-Confidence", botResult.ConfidenceScore.ToString("F2"));
///     if (botResult.IsBot)
///     {
///     transformContext.ProxyRequest.Headers.Add("X-Bot-Type", botResult.BotType?.ToString() ?? "Unknown");
///     transformContext.ProxyRequest.Headers.Add("X-Bot-Name", botResult.BotName ?? "Unknown");
///     }
///     }
///     return ValueTask.CompletedTask;
///     });
///     });
/// </example>
public static class YarpBotDetectionTransforms
{
    /// <summary>
    ///     Extension method to add bot detection transforms to YARP.
    /// </summary>
    /// <remarks>
    ///     Usage:
    ///     <code>
    ///     builder.Services.AddReverseProxy()
    ///         .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    ///         .AddBotDetectionTransforms();
    ///     </code>
    /// </remarks>
    public static void AddBotDetectionHeaders(HttpContext httpContext, Action<string, string> addHeader)
    {
        // Use the extension methods for clean access
        var isBot = httpContext.IsBot();
        var confidence = httpContext.GetBotConfidence();

        addHeader("X-Bot-Detected", isBot.ToString().ToLowerInvariant());
        addHeader("X-Bot-Confidence", confidence.ToString("F2"));

        if (isBot)
        {
            addHeader("X-Bot-Type", httpContext.GetBotType()?.ToString() ?? "Unknown");
            addHeader("X-Bot-Name", httpContext.GetBotName() ?? "Unknown");
            addHeader("X-Bot-Category", httpContext.GetBotCategory() ?? "Unknown");
        }
    }
}

// ============================================================================
// Option 2: YARP Authorization Policy
// ============================================================================
// This approach blocks bots at the proxy layer before requests reach backends.

/// <summary>
///     Authorization handler that blocks bots from accessing proxied routes.
/// </summary>
/// <example>
///     // In Program.cs:
///     builder.Services.AddAuthorization(options =>
///     {
///     options.AddPolicy("BlockBots", policy =>
///     policy.Requirements.Add(new BotBlockingRequirement()));
///     });
///     builder.Services.AddSingleton&lt;IAuthorizationHandler, BotBlockingHandler&gt;();
///     // In YARP config (appsettings.json):
///     "ReverseProxy": {
///     "Routes": {
///     "api-route": {
///     "ClusterId": "api-cluster",
///     "AuthorizationPolicy": "BlockBots",
///     "Match": { "Path": "/api/{**catch-all}" }
///     }
///     }
///     }
/// </example>
public class BotBlockingRequirement // : IAuthorizationRequirement
{
    public double MinConfidenceThreshold { get; set; } = 0.7;
    public bool AllowVerifiedBots { get; set; } = true;
    public bool AllowSearchEngines { get; set; } = true;
}

// Uncomment and implement when using Microsoft.AspNetCore.Authorization
/*
public class BotBlockingHandler : AuthorizationHandler<BotBlockingRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BotBlockingRequirement requirement)
    {
        if (context.Resource is HttpContext httpContext)
        {
            var result = httpContext.GetBotDetectionResult();

            if (result == null || !result.IsBot)
            {
                // Not a bot - allow access
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            // Check confidence threshold
            if (result.ConfidenceScore < requirement.MinConfidenceThreshold)
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            // Check for allowed bot types
            if (requirement.AllowSearchEngines && result.BotType == BotType.SearchEngine)
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            if (requirement.AllowVerifiedBots && IsVerifiedBot(result))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            // Block the bot - don't call context.Succeed()
        }

        return Task.CompletedTask;
    }

    private static bool IsVerifiedBot(BotDetectionResult result)
    {
        // Add logic to verify bot identity (DNS verification, etc.)
        var verifiedBots = new[] { "Googlebot", "Bingbot", "DuckDuckBot", "YandexBot" };
        return verifiedBots.Any(vb =>
            result.BotName?.Contains(vb, StringComparison.OrdinalIgnoreCase) == true);
    }
}
*/

// ============================================================================
// Option 3: YARP Rate Limiting by Bot Type
// ============================================================================
// Apply different rate limits based on bot detection results.

/// <summary>
///     Example of bot-aware rate limiting with YARP.
/// </summary>
/// <example>
///     // In Program.cs:
///     builder.Services.AddRateLimiter(options =>
///     {
///     // Strict limit for detected bots
///     options.AddPolicy("BotRateLimit", context =>
///     {
///     if (context.IsBot())
///     {
///     return RateLimitPartition.GetFixedWindowLimiter(
///     partitionKey: "bot",
///     factory: _ => new FixedWindowRateLimiterOptions
///     {
///     PermitLimit = 10,
///     Window = TimeSpan.FromMinutes(1)
///     });
///     }
///     // Human rate limit
///     return RateLimitPartition.GetFixedWindowLimiter(
///     partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
///     factory: _ => new FixedWindowRateLimiterOptions
///     {
///     PermitLimit = 100,
///     Window = TimeSpan.FromMinutes(1)
///     });
///     });
///     });
/// </example>
public static class BotAwareRateLimiting
{
    public const string BotPolicyName = "BotRateLimit";
    public const string HumanPolicyName = "HumanRateLimit";

    /// <summary>
    ///     Gets the appropriate rate limit policy name based on bot detection.
    /// </summary>
    public static string GetRateLimitPolicy(HttpContext context)
    {
        return context.IsBot() ? BotPolicyName : HumanPolicyName;
    }
}

// ============================================================================
// Option 4: YARP Cluster Selection Based on Bot Type
// ============================================================================
// Route different bot types to different backend clusters.

/// <summary>
///     Example of routing bots to different clusters.
/// </summary>
/// <example>
///     // Concept: Route search engines to a dedicated cluster optimized for crawling,
///     // while blocking malicious bots entirely.
///     // In appsettings.json:
///     "ReverseProxy": {
///     "Routes": {
///     "default": {
///     "ClusterId": "main-cluster",
///     "Match": { "Path": "{**catch-all}" }
///     }
///     },
///     "Clusters": {
///     "main-cluster": { ... },
///     "crawler-cluster": { ... }  // Optimized for search engines
///     }
///     }
///     // Custom cluster selector in transforms:
///     builder.Services.AddReverseProxy()
///     .AddTransforms(context =>
///     {
///     context.AddRequestTransform(transformContext =>
///     {
///     var httpContext = transformContext.HttpContext;
///     if (httpContext.IsSearchEngineBot())
///     {
///     // Route to crawler-optimized cluster
///     // (Requires custom destination selection logic)
///     }
///     return ValueTask.CompletedTask;
///     });
///     });
/// </example>
public static class BotAwareClusterSelection
{
    public const string MainCluster = "main-cluster";
    public const string CrawlerCluster = "crawler-cluster";
    public const string BlockedCluster = "blocked-cluster"; // Returns 403

    /// <summary>
    ///     Determines the appropriate cluster based on bot detection.
    /// </summary>
    public static string GetClusterForRequest(HttpContext context)
    {
        if (context.IsMaliciousBot())
            return BlockedCluster;

        if (context.IsSearchEngineBot())
            return CrawlerCluster;

        return MainCluster;
    }
}

// ============================================================================
// Complete YARP Integration Example - Program.cs
// ============================================================================
/*
// Full example Program.cs for YARP with bot detection:

using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

// Add bot detection
builder.Services.AddBotDetection(options =>
{
    options.EnableUserAgentDetection = true;
    options.EnableHeaderAnalysis = true;
    options.EnableIpDetection = true;
});

// Add YARP with bot detection transforms
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        // Add bot detection headers to all proxied requests
        context.AddRequestTransform(async transformContext =>
        {
            var httpContext = transformContext.HttpContext;

            // Add headers for backend services
            YarpBotDetectionTransforms.AddBotDetectionHeaders(
                httpContext,
                (name, value) => transformContext.ProxyRequest.Headers.TryAddWithoutValidation(name, value));

            // Block malicious bots before proxying
            if (httpContext.IsMaliciousBot())
            {
                httpContext.Response.StatusCode = 403;
                await httpContext.Response.WriteAsync("Access Denied");
                return;
            }
        });
    });

var app = builder.Build();

// Bot detection middleware runs first
app.UseBotDetection();

// Then YARP proxying
app.MapReverseProxy();

app.Run();
*/

// ============================================================================
// YARP Configuration Example (appsettings.json)
// ============================================================================
/*
{
  "ReverseProxy": {
    "Routes": {
      "api-route": {
        "ClusterId": "api-cluster",
        "Match": {
          "Path": "/api/{**catch-all}"
        },
        "Metadata": {
          "BotPolicy": "BlockMalicious"  // Custom metadata for bot handling
        }
      },
      "crawler-route": {
        "ClusterId": "crawler-cluster",
        "Match": {
          "Path": "/sitemap.xml"
        },
        "Metadata": {
          "BotPolicy": "AllowSearchEngines"
        }
      }
    },
    "Clusters": {
      "api-cluster": {
        "Destinations": {
          "api-server": {
            "Address": "http://api-backend:5000"
          }
        }
      },
      "crawler-cluster": {
        "Destinations": {
          "crawler-server": {
            "Address": "http://crawler-backend:5000"
          }
        }
      }
    }
  }
}
*/