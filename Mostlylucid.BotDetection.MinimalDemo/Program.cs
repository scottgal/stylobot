using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// Minimum viable bot detection setup.
// No external dependencies: no Qdrant, no Ollama, no PostgreSQL.
// All config is driven by appsettings.json ("BotDetection" section).
// ============================================================
builder.Services.AddBotDetection();
builder.Services.AddControllers();

var app = builder.Build();

// Bot detection middleware — runs on every request, populates HttpContext
app.UseBotDetection();
app.MapControllers();

// ============================================================
// Minimal API: per-endpoint protection with fluent filters
// ============================================================

// Open endpoint — detection runs (results in HttpContext) but nothing blocks
app.MapGet("/", (HttpContext ctx) => Results.Ok(new
{
    message = "StyloBot Minimal Demo — zero external dependencies",
    you = new
    {
        isBot = ctx.IsBot(),
        confidence = ctx.GetBotConfidence(),
        botType = ctx.GetBotType()?.ToString(),
        botName = ctx.GetBotName(),
        riskBand = ctx.GetRiskBand().ToString()
    },
    endpoints = new Dictionary<string, string>
    {
        // Minimal API (fluent filters)
        ["GET /"] = "Open — detection runs, never blocks",
        ["GET /api/data"] = ".BlockBots(allowSearchEngines: true)",
        ["GET /api/submit"] = ".RequireHuman()",
        ["GET /api/premium"] = ".BlockBots(minConfidence: 0.9)",

        // MVC attributes
        ["GET /products"] = "[BlockBots(AllowSearchEngines = true)]",
        ["GET /products/{id}"] = "No protection (open)",
        ["GET /admin"] = "[RequireHuman] on entire controller",
        ["GET /admin/settings"] = "[RequireHuman] inherited from controller",
        ["GET /checkout"] = "[BotPolicy(\"default\", ActionPolicy = \"api-throttle\")]",
        ["GET /checkout/confirm"] = "[BotPolicy(\"default\", ActionPolicy = \"api-block\")]",
        ["GET /health"] = "[SkipBotDetection] — no detection at all",

        // Diagnostics
        ["GET /bot-detection/check"] = "Full detection breakdown (built-in)",
        ["GET /bot-detection/health"] = "Health check (built-in)"
    },
    testCommands = new[]
    {
        "curl http://localhost:5090/                                        # curl detected as bot",
        "curl -H 'User-Agent: Googlebot/2.1' http://localhost:5090/api/data # search engine allowed",
        "curl -H 'User-Agent: Scrapy/2.5' http://localhost:5090/api/data    # scraper blocked",
        "curl http://localhost:5090/admin                                   # bot blocked",
        "curl http://localhost:5090/health                                  # skips detection",
        "curl http://localhost:5090/bot-detection/check                     # full breakdown"
    }
}));

// .BlockBots() — blocks bots, allows search engines
app.MapGet("/api/data", (HttpContext ctx) => Results.Ok(new
{
    message = "API data — search engines welcome, scrapers blocked",
    data = new[] { "item1", "item2", "item3" }
}))
.BlockBots(allowSearchEngines: true);

// .RequireHuman() — blocks ALL bots including verified crawlers
app.MapGet("/api/submit", (HttpContext ctx) => Results.Ok(new
{
    message = "Form submitted successfully",
    timestamp = DateTimeOffset.UtcNow
}))
.RequireHuman();

// .BlockBots(minConfidence: 0.9) — only blocks high-confidence bots
app.MapGet("/api/premium", (HttpContext ctx) => Results.Ok(new
{
    message = "Premium content — only high-confidence bots are blocked",
    confidence = ctx.GetBotConfidence()
}))
.BlockBots(minConfidence: 0.9);

// Built-in diagnostics
app.MapBotDetectionEndpoints();

app.Run();
