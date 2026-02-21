using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.ApiHolodeck.Extensions;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Demo.Hubs;
using Mostlylucid.BotDetection.Demo.Middleware;
using Mostlylucid.BotDetection.Demo.Services;
using Mostlylucid.BotDetection.Endpoints;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.GeoDetection.Contributor.Extensions;
using Mostlylucid.GeoDetection.Extensions;
using mostlylucid.mockllmapi;

var builder = WebApplication.CreateBuilder(args);

// Add geo-location services for geo-based bot detection
// Uses simple service by default - can configure MaxMind or other providers
builder.Services.AddGeoRoutingSimple();

// Add bot detection - configuration from appsettings.json
// Full detection mode is enabled via the "full-log" ActionPolicy in config
builder.Services.AddBotDetection();

// Add telemetry instrumentation (required by BotDetectionMiddleware)
builder.Services.AddBotDetectionTelemetry();

// Add YARP learning mode - captures training data from bot detection
builder.Services.AddYarpLearningMode();

// Add geo-detection contributor for geo-based bot signals
// Enables bot origin verification (e.g., Googlebot should come from US)
// and geo-inconsistency detection (e.g., browser locale vs IP location)
builder.Services.AddGeoDetectionContributor(options =>
{
    options.EnableBotVerification = true;
    options.EnableInconsistencyDetection = true;
    options.FlagHostingIps = true;
    options.FlagVpnIps = false; // Don't flag VPNs by default
});

// Add StyloBot Dashboard UI with real-time SignalR monitoring
builder.Services.AddStyloBotDashboard(options =>
{
    options.Enabled = true;
    options.BasePath = "/stylobot";
});

// Add ApiHolodeck for honeypot detection and bot redirection
// Configuration from BotDetection:Holodeck section in appsettings.json
builder.Services.AddApiHolodeck();

// Add MockLLMApi for generating fake API responses to detected bots
// This powers the holodeck - bots get redirected here and receive LLM-generated fake data
builder.Services.AddLLMockApi(builder.Configuration);
builder.Services.AddLLMockOpenApi(builder.Configuration); // Also needed for OpenApiContextManager

builder.Services.AddControllers()
    .ConfigureApplicationPartManager(manager =>
    {
        // Remove assemblies compiled against Microsoft.OpenApi 1.x (mockllmapi uses
        // Microsoft.AspNetCore.OpenApi 8.0.0 which references types removed in OpenApi 2.x).
        // These assemblies don't contain controllers so removing them is safe.
        var incompatible = manager.ApplicationParts
            .Where(p => p.Name.Contains("mockllmapi", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Holodeck", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var part in incompatible)
            manager.ApplicationParts.Remove(part);
    });
builder.Services.AddRazorPages(); // For TagHelper support
builder.Services.AddHttpContextAccessor(); // Required for TagHelpers
builder.Services.AddOpenApi();

// Add signature capture and streaming services
builder.Services.AddSingleton<SignatureStore>();
builder.Services.AddSingleton<SignatureBroadcaster>();
builder.Services.AddSignalR();

// Add YARP reverse proxy for transparent bot detection proxy demo
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// Enable OpenAPI document endpoint
app.MapOpenApi();

// HTTPS redirection first
app.UseHttpsRedirection();

// Serve static files (test webpage)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// Add bot detection middleware (MUST be after UseRouting so endpoint metadata is visible)
app.UseBotDetection();

// Add signature capture middleware (MUST be after UseBotDetection to capture evidence)
app.UseSignatureCapture();

// Add YARP learning mode middleware (MUST be after UseBotDetection)
app.UseYarpLearningMode();

app.UseAuthorization();

// Add StyloBot Dashboard (MUST be after UseRouting/UseAuthorization)
app.UseStyloBotDashboard();

app.MapControllers();
app.MapControllerRoute("default", "{controller}/{action=Index}/{id?}"); // MVC conventional routing
app.MapRazorPages();

// Map SignalR hub for signature streaming
app.MapHub<SignatureHub>("/hubs/signatures");


// ==========================================
// Built-in diagnostic endpoints
// ==========================================
// Maps: /bot-detection/check, /bot-detection/stats, /bot-detection/health
app.MapBotDetectionEndpoints();

// Map the fingerprint endpoint for client-side JS to POST data to
app.MapBotDetectionFingerprintEndpoint();

// Map BDF replay endpoints for offline debugging and regression testing
app.MapBdfReplayEndpoints();

// Map training data export endpoints
app.MapBotTrainingEndpoints();

// Map MockLLMApi endpoints - this is where the holodeck redirects bots
// Generates LLM-powered fake API responses that look real but contain useless data
app.MapLLMockApi();

// ==========================================
// Demo endpoints using extension methods
// ==========================================

// Detection mode endpoint - shows configured action policies
app.MapGet("/api/mode", (IActionPolicyRegistry actionRegistry) =>
    {
        var policies = actionRegistry.GetAllPolicies();
        var hasFullLog = policies.ContainsKey("full-log");

        return Results.Ok(new
        {
            fullLogEnabled = hasFullLog,
            description = hasFullLog
                ? "Full-log policy active: Debug logging, full evidence, detailed response headers"
                : "Standard mode: Core detection without enhanced logging",
            configuredPolicies = policies.Keys.ToArray(),
            policySummary = policies.ToDictionary(
                p => p.Key,
                p => new { type = p.Value.ActionType.ToString() }
            )
        });
    })
    .WithName("ApiMode")
    .WithSummary("Shows configured action policies");

// Simple check using HttpContext extensions
app.MapGet("/api", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            message = "Bot Detection Demo API - v1.0.0",
            testPage = "/index.html",
            isBot = context.IsBot(),
            isHuman = context.IsHuman(),
            isSearchEngine = context.IsSearchEngineBot(),
            isVerifiedBot = context.IsVerifiedBot(),
            confidence = context.GetBotConfidence(),
            botType = context.GetBotType()?.ToString(),
            botName = context.GetBotName(),
            endpoints = new
            {
                diagnostics = new[] { "/bot-detection/check", "/bot-detection/stats", "/bot-detection/health" },
                protected_endpoints = new[] { "/api/protected", "/api/humans-only", "/api/allow-search-engines" },
                sample_user_agents = "/api/sample-user-agents",
                test_script = "/api/test-script",
                test_instructions = "Use header 'ml-bot-test-mode: googlebot' to simulate different bots"
            }
        });
    })
    .WithName("ApiRoot")
    .WithSummary("API root with detection info");

// ==========================================
// Protected endpoints using endpoint filters
// ==========================================

// Block all bots
app.MapGet("/api/protected", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            message = "This endpoint blocks all bots",
            accessGranted = true,
            visitor = context.IsHuman() ? "human" : "verified-bot"
        });
    })
    .BlockBots(true) // Allow Googlebot etc.
    .WithName("ProtectedEndpoint")
    .WithSummary("Protected endpoint - blocks unverified bots");

// Human only (blocks ALL bots)
app.MapGet("/api/humans-only", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            message = "This endpoint is for humans only",
            accessGranted = true
        });
    })
    .RequireHuman()
    .WithName("HumansOnly")
    .WithSummary("Human-only endpoint - blocks all bots including verified ones");

// Allow search engines
app.MapGet("/api/allow-search-engines", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            message = "Search engines welcome here!",
            isSearchEngine = context.IsSearchEngineBot(),
            botName = context.GetBotName()
        });
    })
    .BlockBots(allowSearchEngines: true)
    .WithName("AllowSearchEngines")
    .WithSummary("Allows search engine bots, blocks others");

// High confidence blocking only
app.MapGet("/api/strict-protection", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            message = "Only blocks high-confidence bots (>90%)",
            confidence = context.GetBotConfidence()
        });
    })
    .BlockBots(minConfidence: 0.9)
    .WithName("StrictProtection")
    .WithSummary("Only blocks bots with >90% confidence");

// ==========================================
// Test helpers
// ==========================================

app.MapGet("/api/test-modes", () =>
{
    return Results.Ok(new
    {
        message = "Test mode header values (use with 'ml-bot-test-mode' header)",
        modes = new Dictionary<string, string>
        {
            ["disable"] = "Bypass detection entirely",
            ["human"] = "Simulate human visitor",
            ["bot"] = "Simulate generic bot",
            ["googlebot"] = "Simulate Googlebot (SearchEngine, verified)",
            ["bingbot"] = "Simulate Bingbot (SearchEngine, verified)",
            ["scraper"] = "Simulate scraper bot",
            ["malicious"] = "Simulate malicious bot",
            ["social"] = "Simulate social media bot",
            ["monitor"] = "Simulate monitoring bot"
        },
        example = "curl -H 'ml-bot-test-mode: googlebot' http://localhost:5080/"
    });
});

// ==========================================
// Sample User Agents for Testing
// ==========================================
app.MapGet("/api/sample-user-agents", () =>
{
    var baseUrl = "http://localhost:5080";

    return Results.Ok(new
    {
        message = "Sample User-Agents for testing bot detection. Copy and run these curl commands.",
        categories = new
        {
            search_engines = new
            {
                description = "Major search engine crawlers - detected as SearchEngine/VerifiedBot",
                samples = new[]
                {
                    new
                    {
                        name = "Googlebot",
                        userAgent = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
                        curl =
                            $"curl -A \"Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Bingbot",
                        userAgent = "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",
                        curl =
                            $"curl -A \"Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)\" {baseUrl}/"
                    },
                    new
                    {
                        name = "DuckDuckBot", userAgent = "DuckDuckBot/1.0; (+http://duckduckgo.com/duckduckbot.html)",
                        curl = $"curl -A \"DuckDuckBot/1.0; (+http://duckduckgo.com/duckduckbot.html)\" {baseUrl}/"
                    },
                    new
                    {
                        name = "YandexBot",
                        userAgent = "Mozilla/5.0 (compatible; YandexBot/3.0; +http://yandex.com/bots)",
                        curl =
                            $"curl -A \"Mozilla/5.0 (compatible; YandexBot/3.0; +http://yandex.com/bots)\" {baseUrl}/"
                    }
                }
            },
            social_media_bots = new
            {
                description = "Social media preview bots - detected as SocialMedia type",
                samples = new[]
                {
                    new
                    {
                        name = "Facebook", userAgent = "facebookexternalhit/1.1",
                        curl = $"curl -A \"facebookexternalhit/1.1\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Twitter", userAgent = "Twitterbot/1.0", curl = $"curl -A \"Twitterbot/1.0\" {baseUrl}/"
                    },
                    new
                    {
                        name = "LinkedIn", userAgent = "LinkedInBot/1.0",
                        curl = $"curl -A \"LinkedInBot/1.0\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Slack", userAgent = "Slackbot-LinkExpanding 1.0",
                        curl = $"curl -A \"Slackbot-LinkExpanding 1.0\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Discord", userAgent = "Mozilla/5.0 (compatible; Discordbot/2.0)",
                        curl = $"curl -A \"Mozilla/5.0 (compatible; Discordbot/2.0)\" {baseUrl}/"
                    },
                    new
                    {
                        name = "WhatsApp", userAgent = "WhatsApp/2.19.81 A",
                        curl = $"curl -A \"WhatsApp/2.19.81 A\" {baseUrl}/"
                    }
                }
            },
            scrapers = new
            {
                description = "Known scrapers - detected as Scraper/Malicious",
                samples = new[]
                {
                    new { name = "Scrapy", userAgent = "Scrapy/2.5.0", curl = $"curl -A \"Scrapy/2.5.0\" {baseUrl}/" },
                    new
                    {
                        name = "MJ12bot", userAgent = "Mozilla/5.0 (compatible; MJ12bot/v1.4.8)",
                        curl = $"curl -A \"Mozilla/5.0 (compatible; MJ12bot/v1.4.8)\" {baseUrl}/"
                    },
                    new
                    {
                        name = "AhrefsBot", userAgent = "Mozilla/5.0 (compatible; AhrefsBot/7.0)",
                        curl = $"curl -A \"Mozilla/5.0 (compatible; AhrefsBot/7.0)\" {baseUrl}/"
                    },
                    new
                    {
                        name = "SemrushBot", userAgent = "Mozilla/5.0 (compatible; SemrushBot/7~bl)",
                        curl = $"curl -A \"Mozilla/5.0 (compatible; SemrushBot/7~bl)\" {baseUrl}/"
                    },
                    new
                    {
                        name = "HTTrack", userAgent = "Mozilla/4.5 (compatible; HTTrack 3.0x)",
                        curl = $"curl -A \"Mozilla/4.5 (compatible; HTTrack 3.0x)\" {baseUrl}/"
                    },
                    new { name = "Wget", userAgent = "Wget/1.21", curl = $"curl -A \"Wget/1.21\" {baseUrl}/" },
                    new { name = "curl default", userAgent = "curl/7.68.0", curl = $"curl {baseUrl}/" }
                }
            },
            automation = new
            {
                description = "Browser automation tools - detected as Automation type",
                samples = new[]
                {
                    new
                    {
                        name = "Selenium", userAgent = "Mozilla/5.0 Selenium",
                        curl = $"curl -A \"Mozilla/5.0 Selenium\" {baseUrl}/"
                    },
                    new
                    {
                        name = "PhantomJS", userAgent = "Mozilla/5.0 PhantomJS/2.1.1",
                        curl = $"curl -A \"Mozilla/5.0 PhantomJS/2.1.1\" {baseUrl}/"
                    },
                    new
                    {
                        name = "HeadlessChrome", userAgent = "Mozilla/5.0 HeadlessChrome/90.0.4430.212",
                        curl = $"curl -A \"Mozilla/5.0 HeadlessChrome/90.0.4430.212\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Playwright", userAgent = "Mozilla/5.0 Playwright/1.20.0",
                        curl = $"curl -A \"Mozilla/5.0 Playwright/1.20.0\" {baseUrl}/"
                    }
                }
            },
            monitors = new
            {
                description = "Uptime monitors - detected as Monitor type",
                samples = new[]
                {
                    new
                    {
                        name = "UptimeRobot", userAgent = "Mozilla/5.0 (compatible; UptimeRobot/2.0)",
                        curl = $"curl -A \"Mozilla/5.0 (compatible; UptimeRobot/2.0)\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Pingdom", userAgent = "Pingdom.com_bot_version_1.4",
                        curl = $"curl -A \"Pingdom.com_bot_version_1.4\" {baseUrl}/"
                    },
                    new { name = "Site24x7", userAgent = "Site24x7", curl = $"curl -A \"Site24x7\" {baseUrl}/" }
                }
            },
            browsers = new
            {
                description = "Real browsers - should be detected as human",
                samples = new[]
                {
                    new
                    {
                        name = "Chrome",
                        userAgent =
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                        curl =
                            $"curl -A \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36\" -H \"Accept: text/html\" -H \"Accept-Language: en-US\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Firefox",
                        userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
                        curl =
                            $"curl -A \"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0\" -H \"Accept: text/html\" -H \"Accept-Language: en-US\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Safari",
                        userAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_1) AppleWebKit/605.1.15 Safari/605.1.15",
                        curl =
                            $"curl -A \"Mozilla/5.0 (Macintosh; Intel Mac OS X 14_1) AppleWebKit/605.1.15 Safari/605.1.15\" -H \"Accept: text/html\" -H \"Accept-Language: en-US\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Edge",
                        userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0 Edg/120.0.0.0",
                        curl =
                            $"curl -A \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0 Edg/120.0.0.0\" -H \"Accept: text/html\" -H \"Accept-Language: en-US\" {baseUrl}/"
                    }
                }
            },
            suspicious = new
            {
                description = "Suspicious patterns - elevated bot confidence",
                samples = new[]
                {
                    new { name = "Empty UA", userAgent = "", curl = $"curl -A \"\" {baseUrl}/" },
                    new { name = "Short UA", userAgent = "Bot", curl = $"curl -A \"Bot\" {baseUrl}/" },
                    new
                    {
                        name = "Python", userAgent = "python-requests/2.28.0",
                        curl = $"curl -A \"python-requests/2.28.0\" {baseUrl}/"
                    },
                    new
                    {
                        name = "Go HTTP", userAgent = "Go-http-client/1.1",
                        curl = $"curl -A \"Go-http-client/1.1\" {baseUrl}/"
                    },
                    new { name = "Java", userAgent = "Java/11.0.11", curl = $"curl -A \"Java/11.0.11\" {baseUrl}/" },
                    new
                    {
                        name = "libwww-perl", userAgent = "libwww-perl/6.61",
                        curl = $"curl -A \"libwww-perl/6.61\" {baseUrl}/"
                    }
                }
            }
        },
        test_mode = new
        {
            description = "Quick testing using ml-bot-test-mode header",
            samples = new[]
            {
                new { mode = "human", curl = $"curl -H \"ml-bot-test-mode: human\" {baseUrl}/" },
                new { mode = "googlebot", curl = $"curl -H \"ml-bot-test-mode: googlebot\" {baseUrl}/" },
                new { mode = "scraper", curl = $"curl -H \"ml-bot-test-mode: scraper\" {baseUrl}/" },
                new { mode = "malicious", curl = $"curl -H \"ml-bot-test-mode: malicious\" {baseUrl}/" }
            }
        },
        protected_tests = new
        {
            description = "Test protected endpoints",
            samples = new[]
            {
                new
                {
                    endpoint = "/api/protected", test = "Human",
                    curl = $"curl -A \"Mozilla/5.0 Chrome/120\" -H \"Accept: text/html\" {baseUrl}/api/protected"
                },
                new
                {
                    endpoint = "/api/protected", test = "Googlebot (allowed)",
                    curl = $"curl -A \"Googlebot/2.1\" {baseUrl}/api/protected"
                },
                new
                {
                    endpoint = "/api/protected", test = "Scraper (blocked)",
                    curl = $"curl -A \"Scrapy/2.5\" {baseUrl}/api/protected"
                },
                new
                {
                    endpoint = "/api/humans-only", test = "Human",
                    curl = $"curl -A \"Mozilla/5.0 Chrome/120\" -H \"Accept: text/html\" {baseUrl}/api/humans-only"
                },
                new
                {
                    endpoint = "/api/humans-only", test = "Googlebot (blocked)",
                    curl = $"curl -A \"Googlebot/2.1\" {baseUrl}/api/humans-only"
                }
            }
        }
    });
});

// ==========================================
// API endpoints for load testing (k6, etc.)
// Different policies to test various detection behaviors
// ==========================================

// Open endpoint - no bot detection (baseline for load testing)
app.MapGet("/api/v1/open", () =>
    {
        return Results.Ok(new
        {
            policy = "none",
            timestamp = DateTimeOffset.UtcNow,
            message = "Open endpoint - no bot detection applied"
        });
    })
    .WithName("ApiOpen")
    .WithTags("Load Testing")
    .WithSummary("Baseline endpoint - no bot detection");

// Default policy - standard detection
app.MapGet("/api/v1/default", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            policy = "default",
            timestamp = DateTimeOffset.UtcNow,
            isBot = context.IsBot(),
            confidence = context.GetBotConfidence(),
            botType = context.GetBotType()?.ToString(),
            botName = context.GetBotName()
        });
    })
    .WithName("ApiDefault")
    .WithTags("Load Testing")
    .WithSummary("Default detection policy");

// Strict policy - blocks all bots including verified
app.MapGet("/api/v1/strict", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            policy = "strict",
            timestamp = DateTimeOffset.UtcNow,
            message = "Access granted - you are human"
        });
    })
    .RequireHuman()
    .WithName("ApiStrict")
    .WithTags("Load Testing")
    .WithSummary("Strict policy - humans only, blocks all bots");

// Relaxed policy - allows verified bots
app.MapGet("/api/v1/relaxed", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            policy = "relaxed",
            timestamp = DateTimeOffset.UtcNow,
            isVerifiedBot = context.IsVerifiedBot(),
            isSearchEngine = context.IsSearchEngineBot(),
            botName = context.GetBotName()
        });
    })
    .BlockBots(true, true)
    .WithName("ApiRelaxed")
    .WithTags("Load Testing")
    .WithSummary("Relaxed policy - allows verified bots and search engines");

// High confidence only - blocks only high-confidence bots
app.MapGet("/api/v1/high-confidence", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            policy = "high-confidence",
            timestamp = DateTimeOffset.UtcNow,
            confidence = context.GetBotConfidence(),
            threshold = 0.9,
            wouldBlock = context.GetBotConfidence() >= 0.9
        });
    })
    .BlockBots(minConfidence: 0.9)
    .WithName("ApiHighConfidence")
    .WithTags("Load Testing")
    .WithSummary("High confidence policy - only blocks bots with >90% confidence");

// Log-only policy - detects but never blocks
app.MapGet("/api/v1/log-only", (HttpContext context) =>
    {
        return Results.Ok(new
        {
            policy = "log-only",
            timestamp = DateTimeOffset.UtcNow,
            isBot = context.IsBot(),
            confidence = context.GetBotConfidence(),
            botType = context.GetBotType()?.ToString(),
            botName = context.GetBotName(),
            note = "Detection runs but never blocks"
        });
    })
    .WithName("ApiLogOnly")
    .WithTags("Load Testing")
    .WithSummary("Log-only policy - detects bots but never blocks");

// Full detection with detailed response
app.MapGet("/api/v1/detailed", (HttpContext context) =>
    {
        var result = context.GetBotDetectionResult();
        return Results.Ok(new
        {
            policy = "detailed",
            timestamp = DateTimeOffset.UtcNow,
            detection = new
            {
                isBot = result?.IsBot ?? false,
                confidence = context.GetBotConfidence(),
                botType = result?.BotType?.ToString(),
                botName = result?.BotName,
                riskBand = context.GetRiskBand().ToString(),
                shouldChallenge = context.ShouldChallengeRequest(),
                recommendedAction = context.GetRecommendedAction().ToString(),
                reasonCount = result?.Reasons?.Count ?? 0,
                reasons = result?.Reasons?.Select(r => new
                {
                    category = r.Category,
                    detail = r.Detail,
                    confidence = r.ConfidenceImpact
                }).ToArray()
            }
        });
    })
    .WithName("ApiDetailed")
    .WithTags("Load Testing")
    .WithSummary("Detailed detection response with all evidence");

// Security tool detection test endpoint
app.MapGet("/api/v1/security-check", (HttpContext context) =>
    {
        var result = context.GetBotDetectionResult();
        var isSecurityTool = result?.Reasons?.Any(r =>
            r.Category.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
            r.Detail.Contains("security tool", StringComparison.OrdinalIgnoreCase)) ?? false;

        return Results.Ok(new
        {
            policy = "security-check",
            timestamp = DateTimeOffset.UtcNow,
            securityToolDetected = isSecurityTool,
            userAgent = context.Request.Headers.UserAgent.ToString(),
            detection = new
            {
                isBot = result?.IsBot ?? false,
                confidence = context.GetBotConfidence(),
                botType = result?.BotType?.ToString()
            }
        });
    })
    .WithName("ApiSecurityCheck")
    .WithTags("Load Testing")
    .WithSummary("Endpoint that reports security tool detection");

// YARP Learning Mode endpoint - full pipeline for training data collection
app.MapGet("/api/v1/yarp-learning", (HttpContext context) =>
    {
        var result = context.GetBotDetectionResult();
        return Results.Ok(new
        {
            policy = "yarp-learning",
            timestamp = DateTimeOffset.UtcNow,
            message = "This endpoint uses YARP learning mode - full detection pipeline without blocking",
            note = "Check ./yarp-learning-data/ for JSONL signature files and console for real-time logging",
            detection = new
            {
                isBot = result?.IsBot ?? false,
                confidence = context.GetBotConfidence(),
                botType = result?.BotType?.ToString(),
                botName = result?.BotName,
                riskBand = context.GetRiskBand().ToString(),
                reasonCount = result?.Reasons?.Count ?? 0,
                reasons = result?.Reasons?.Take(5).Select(r => new
                {
                    category = r.Category,
                    detail = r.Detail,
                    confidence = r.ConfidenceImpact
                }).ToArray()
            },
            learningInfo = new
            {
                signaturesCaptured = "Check ./yarp-learning-data/ directory",
                consoleLogging = "Enabled - watch console for real-time signatures",
                detectorOutputs = "Captured with timing and contributions",
                blackboardSignals = "All signals logged"
            }
        });
    })
    .WithName("ApiYarpLearning")
    .WithTags("YARP Learning")
    .WithSummary("YARP learning mode endpoint - captures comprehensive training data");

// Signature lookup endpoint - get all data for a signature ID
app.MapGet("/api/v1/signature/{signatureId}", async (
        string signatureId,
        SignatureCoordinator coordinator) =>
    {
        var behavior = await coordinator.GetSignatureBehaviorAsync(signatureId);

        if (behavior == null)
            return Results.NotFound(new
            {
                error = "Signature not found",
                signatureId,
                message =
                    "This signature ID was not found in the cache. It may have expired (TTL: 30 minutes) or never existed."
            });

        return Results.Ok(new
        {
            signatureId,
            behavior = new
            {
                signature = behavior.Signature,
                requestCount = behavior.RequestCount,
                firstSeen = behavior.FirstSeen,
                lastSeen = behavior.LastSeen,
                averageInterval = behavior.AverageInterval,
                pathEntropy = behavior.PathEntropy,
                timingCoefficient = behavior.TimingCoefficient,
                averageBotProbability = behavior.AverageBotProbability,
                aberrationScore = behavior.AberrationScore,
                isAberrant = behavior.IsAberrant,
                // Request details
                requests = behavior.Requests?.Select(r => new
                {
                    requestId = r.RequestId,
                    path = r.Path,
                    botProbability = r.BotProbability,
                    timestamp = r.Timestamp,
                    detectorsRan = r.DetectorsRan.ToArray(),
                    escalated = r.Escalated,
                    signals = r.Signals
                }).ToArray()
            }
        });
    })
    .WithName("GetSignature")
    .WithTags("Bot Detection", "YARP Proxy")
    .WithSummary("Get all bot detection data for a signature ID (from SignatureCoordinator)");

// Batch endpoint for load testing multiple detections
app.MapPost("/api/v1/batch-check", (HttpContext context, BatchCheckRequest request) =>
    {
        var results = new List<object>();

        foreach (var ua in request.UserAgents ?? Array.Empty<string>())
        {
            // Note: This simulates detection - in real usage each would be a separate request
            var isBot = ua.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
                        ua.Contains("crawler", StringComparison.OrdinalIgnoreCase) ||
                        ua.Contains("spider", StringComparison.OrdinalIgnoreCase) ||
                        ua.Contains("scraper", StringComparison.OrdinalIgnoreCase);

            results.Add(new
            {
                userAgent = ua,
                likelyBot = isBot
            });
        }

        return Results.Ok(new
        {
            policy = "batch-check",
            timestamp = DateTimeOffset.UtcNow,
            count = results.Count,
            results
        });
    })
    .WithName("ApiBatchCheck")
    .WithTags("Load Testing")
    .WithSummary("Batch check multiple user agents (for testing patterns)");

// ==========================================
// PowerShell test script generator
// ==========================================
app.MapGet("/api/test-script", () =>
{
    var script = @"# Bot Detection Demo - PowerShell Test Script
# Save this and run: powershell -ExecutionPolicy Bypass -File test-bots.ps1

$baseUrl = 'http://localhost:5080'

Write-Host '=== BOT DETECTION DEMO TESTS ===' -ForegroundColor Cyan

# Test Search Engine Bot
Write-Host ""n1. Googlebot:"" -ForegroundColor Yellow
Invoke-RestMethod -Uri ""$baseUrl/"" -Headers @{'User-Agent'='Mozilla/5.0 (compatible; Googlebot/2.1)'}

# Test Social Bot
Write-Host ""n2. Facebook:"" -ForegroundColor Yellow
Invoke-RestMethod -Uri ""$baseUrl/"" -Headers @{'User-Agent'='facebookexternalhit/1.1'}

# Test Scraper
Write-Host ""n3. Scrapy:"" -ForegroundColor Yellow
Invoke-RestMethod -Uri ""$baseUrl/"" -Headers @{'User-Agent'='Scrapy/2.5.0'}

# Test HeadlessChrome
Write-Host ""n4. HeadlessChrome:"" -ForegroundColor Yellow
Invoke-RestMethod -Uri ""$baseUrl/"" -Headers @{'User-Agent'='HeadlessChrome/90'}

# Test Real Browser
Write-Host ""n5. Chrome Browser:"" -ForegroundColor Yellow
Invoke-RestMethod -Uri ""$baseUrl/"" -Headers @{
    'User-Agent'='Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0'
    'Accept'='text/html'
    'Accept-Language'='en-US'
}

# Test Protected Endpoint as Human
Write-Host ""n6. Protected (Human):"" -ForegroundColor Yellow
try {
    Invoke-RestMethod -Uri ""$baseUrl/api/protected"" -Headers @{
        'User-Agent'='Mozilla/5.0 Chrome/120'
        'Accept'='text/html'
        'Accept-Language'='en-US'
    }
} catch { Write-Host ""BLOCKED"" -ForegroundColor Red }

# Test Protected as Scraper
Write-Host ""n7. Protected (Scraper):"" -ForegroundColor Yellow
try {
    Invoke-RestMethod -Uri ""$baseUrl/api/protected"" -Headers @{'User-Agent'='Scrapy/2.5'}
} catch { Write-Host ""BLOCKED"" -ForegroundColor Red }

Write-Host ""n=== TESTS COMPLETE ==="" -ForegroundColor Cyan
";
    return Results.Text(script, "text/plain");
});

// Map YARP reverse proxy - MUST be after bot detection middleware
app.MapReverseProxy();

app.Run();

// Make Program accessible to WebApplicationFactory in test projects
public partial class Program;

// Request models
public record BatchCheckRequest(string[]? UserAgents);