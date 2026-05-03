// ============================================================================
// Behavioral Analysis Usage Examples
// ============================================================================
// This file demonstrates how to use the session/identity-level behavioral
// analysis features of Mostlylucid.BotDetection.
//
// Behavioral analysis tracks request patterns at multiple identity levels:
// - IP address (default)
// - Browser fingerprint hash (when client-side detection enabled)
// - API key (via configurable header)
// - User ID (via claim or header)
// ============================================================================

using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Demo.Examples;

/// <summary>
///     Examples of configuring and using behavioral analysis features.
/// </summary>
public static class BehavioralAnalysisExample
{
    // ============================================================================
    // Example 1: Basic Behavioral Configuration
    // ============================================================================

    /// <summary>
    ///     Configure behavioral analysis with API key and user tracking.
    /// </summary>
    /// <example>
    ///     // In Program.cs:
    ///     builder.Services.AddBotDetection(options =>
    ///     {
    ///     options.MaxRequestsPerMinute = 60;
    ///     options.Behavioral = new BehavioralOptions
    ///     {
    ///     // Track by API key header
    ///     ApiKeyHeader = "X-Api-Key",
    ///     ApiKeyRateLimit = 120, // 2x the IP limit
    ///     // Track by user ID
    ///     UserIdClaim = "sub",      // From JWT
    ///     UserIdHeader = "X-User-Id", // Fallback header
    ///     UserRateLimit = 180,      // 3x the IP limit
    ///     // Anomaly detection
    ///     EnableAnomalyDetection = true,
    ///     SpikeThresholdMultiplier = 5.0, // 5x = spike
    ///     NewPathAnomalyThreshold = 0.8   // 80% new paths = anomaly
    ///     };
    ///     });
    /// </example>
    public static void ConfigureBasicBehavioral(IServiceCollection services)
    {
        services.AddBotDetection(options =>
        {
            options.MaxRequestsPerMinute = 60;

            options.Behavioral = new BehavioralOptions
            {
                ApiKeyHeader = "X-Api-Key",
                ApiKeyRateLimit = 120,
                UserIdClaim = "sub",
                UserIdHeader = "X-User-Id",
                UserRateLimit = 180,
                EnableAnomalyDetection = true,
                SpikeThresholdMultiplier = 5.0,
                NewPathAnomalyThreshold = 0.8
            };
        });
    }

    // ============================================================================
    // Example 2: API Gateway Configuration
    // ============================================================================

    /// <summary>
    ///     Configuration optimized for API gateways with strict API key limits.
    /// </summary>
    /// <example>
    ///     // appsettings.json:
    ///     {
    ///     "BotDetection": {
    ///     "MaxRequestsPerMinute": 100,
    ///     "Behavioral": {
    ///     "ApiKeyHeader": "Authorization",
    ///     "ApiKeyRateLimit": 1000,
    ///     "EnableAnomalyDetection": true,
    ///     "SpikeThresholdMultiplier": 10.0
    ///     }
    ///     }
    ///     }
    /// </example>
    public static void ConfigureApiGateway(IServiceCollection services)
    {
        services.AddBotDetection(options =>
        {
            options.MaxRequestsPerMinute = 100;

            options.Behavioral = new BehavioralOptions
            {
                // Use Authorization header for API key tracking
                // Works with "Bearer <token>" style headers
                ApiKeyHeader = "Authorization",
                ApiKeyRateLimit = 1000, // Higher limit for API clients

                // Enable spike detection for DDoS protection
                EnableAnomalyDetection = true,
                SpikeThresholdMultiplier = 10.0 // 10x normal = attack
            };
        });
    }

    // ============================================================================
    // Example 3: Multi-Tenant SaaS Configuration
    // ============================================================================

    /// <summary>
    ///     Configuration for multi-tenant applications with per-tenant limits.
    /// </summary>
    /// <example>
    ///     // Use tenant ID from JWT claim for tracking
    ///     options.Behavioral = new BehavioralOptions
    ///     {
    ///     UserIdClaim = "tenant_id",
    ///     UserRateLimit = 500  // Per-tenant limit
    ///     };
    /// </example>
    public static void ConfigureMultiTenant(IServiceCollection services)
    {
        services.AddBotDetection(options =>
        {
            options.MaxRequestsPerMinute = 60; // Per-IP

            options.Behavioral = new BehavioralOptions
            {
                // Track by tenant ID from JWT
                UserIdClaim = "tenant_id",
                UserRateLimit = 500, // Per-tenant limit

                // Also track by API key for service accounts
                ApiKeyHeader = "X-Api-Key",
                ApiKeyRateLimit = 200,

                // Detect behavior changes per tenant
                EnableAnomalyDetection = true,
                SpikeThresholdMultiplier = 3.0
            };
        });
    }

    // ============================================================================
    // Example 4: Checking Behavioral Detection Results
    // ============================================================================

    /// <summary>
    ///     How to check behavioral detection results in your application.
    /// </summary>
    /// <example>
    ///     app.MapGet("/api/protected", (HttpContext context) =>
    ///     {
    ///     var reasons = context.GetDetectionReasons();
    ///     var behavioralReasons = reasons.Where(r => r.Category == "Behavioral");
    ///     foreach (var reason in behavioralReasons)
    ///     {
    ///     // Check for specific behavioral issues
    ///     if (reason.Detail.Contains("API key rate limit"))
    ///     return Results.StatusCode(429); // Too Many Requests
    ///     if (reason.Detail.Contains("User rate limit"))
    ///     return Results.StatusCode(429);
    ///     if (reason.Detail.Contains("Sudden request spike"))
    ///     return Results.StatusCode(503); // Service Unavailable
    ///     }
    ///     return Results.Ok("Access granted");
    ///     });
    /// </example>
    public static IResult CheckBehavioralResults(HttpContext context)
    {
        var reasons = context.GetDetectionReasons();
        var behavioralReasons = reasons.Where(r => r.Category == "Behavioral").ToList();

        foreach (var reason in behavioralReasons)
        {
            // API key rate limit exceeded
            if (reason.Detail.Contains("API key rate limit", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new
                {
                    Error = "API rate limit exceeded",
                    RetryAfter = 60
                }, statusCode: 429);

            // User rate limit exceeded
            if (reason.Detail.Contains("User rate limit", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new
                {
                    Error = "User rate limit exceeded",
                    RetryAfter = 60
                }, statusCode: 429);

            // Sudden behavior change (potential account takeover)
            if (reason.Detail.Contains("Sudden request spike", StringComparison.OrdinalIgnoreCase) ||
                reason.Detail.Contains("behavior anomaly", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new
                {
                    Error = "Unusual activity detected",
                    Action = "Please verify your identity"
                }, statusCode: 403);

            // Bot-like timing patterns
            if (reason.Detail.Contains("Too regular interval", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new
                {
                    Error = "Automated request pattern detected"
                }, statusCode: 403);
        }

        return Results.Ok(new { Status = "Access granted" });
    }

    // ============================================================================
    // Example 6: Using with Client-Side Fingerprinting
    // ============================================================================

    /// <summary>
    ///     Combined behavioral + client-side fingerprint tracking.
    /// </summary>
    /// <example>
    ///     // appsettings.json:
    ///     {
    ///     "BotDetection": {
    ///     "ClientSide": {
    ///     "Enabled": true,
    ///     "TokenSecret": "your-secret"
    ///     },
    ///     "Behavioral": {
    ///     "EnableAnomalyDetection": true
    ///     }
    ///     }
    ///     }
    ///     // When client-side is enabled, the BehavioralDetector automatically
    ///     // tracks by fingerprint hash in addition to IP/API key/user.
    ///     // This catches bots that rotate IPs but keep the same fingerprint.
    /// </example>
    public static void ConfigureWithClientSide(IServiceCollection services)
    {
        services.AddBotDetection(options =>
        {
            // Enable client-side fingerprinting
            options.ClientSide = new ClientSideOptions
            {
                Enabled = true,
                TokenSecret = "your-secret-key",
                CollectWebGL = true,
                CollectCanvas = true
            };

            // Behavioral analysis will automatically track by fingerprint hash
            options.Behavioral = new BehavioralOptions
            {
                EnableAnomalyDetection = true,
                SpikeThresholdMultiplier = 5.0
            };
        });
    }

    // ============================================================================
    // Example 5: Custom Behavioral Middleware
    // ============================================================================

    /// <summary>
    ///     Custom middleware that takes action based on behavioral analysis.
    /// </summary>
    /// <example>
    ///     // In Program.cs:
    ///     app.UseBotDetection();
    ///     app.UseMiddleware&lt;BehavioralActionMiddleware&gt;();
    /// </example>
    public class BehavioralActionMiddleware
    {
        private readonly RequestDelegate _next;

        public BehavioralActionMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Check risk level based on detection
            var riskBand = context.GetRiskBand();
            var action = context.GetRecommendedAction();

            switch (action)
            {
                case RecommendedAction.Block:
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        Error = "Access denied",
                        Reason = "High risk request blocked"
                    });
                    return;

                case RecommendedAction.Challenge:
                    // Add header to trigger CAPTCHA on frontend
                    context.Response.Headers["X-Challenge-Required"] = "true";
                    context.Response.Headers["X-Risk-Band"] = riskBand.ToString();
                    break;

                case RecommendedAction.Throttle:
                    // Add rate limit headers
                    context.Response.Headers["X-RateLimit-Remaining"] = "10";
                    context.Response.Headers["Retry-After"] = "60";
                    break;

                case RecommendedAction.Allow:
                default:
                    // Normal processing
                    break;
            }

            await _next(context);
        }
    }
}

// ============================================================================
// appsettings.json Configuration Examples
// ============================================================================
/*
// Minimal behavioral configuration:
{
  "BotDetection": {
    "Behavioral": {
      "ApiKeyHeader": "X-Api-Key"
    }
  }
}

// Full behavioral configuration:
{
  "BotDetection": {
    "MaxRequestsPerMinute": 60,
    "Behavioral": {
      "ApiKeyHeader": "X-Api-Key",
      "ApiKeyRateLimit": 120,
      "UserIdClaim": "sub",
      "UserIdHeader": "X-User-Id",
      "UserRateLimit": 180,
      "EnableAnomalyDetection": true,
      "SpikeThresholdMultiplier": 5.0,
      "NewPathAnomalyThreshold": 0.8
    }
  }
}

// API-focused configuration:
{
  "BotDetection": {
    "MaxRequestsPerMinute": 100,
    "Behavioral": {
      "ApiKeyHeader": "Authorization",
      "ApiKeyRateLimit": 1000,
      "EnableAnomalyDetection": true,
      "SpikeThresholdMultiplier": 10.0
    }
  }
}
*/