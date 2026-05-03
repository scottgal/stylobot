# HttpContext Bot Detection Extension Methods

Convenient extension methods to access bot detection results without dealing with `HttpContext.Items` keys.

## Available Methods

### Basic Detection

```csharp
// Check if request is from a bot
if (context.IsBot())
{
    // Handle bot traffic
}

// Check if request is from a human
if (context.IsHuman())
{
    // Show human-only content
}

// Check if bot is malicious (confidence >= 0.7)
if (context.IsMaliciousBot())
{
    return Results.Forbid();
}
```

### Confidence Scoring

```csharp
// Get numeric confidence score (0.0-1.0)
var score = context.BotConfidenceScore();  // e.g., 0.85

// Get confidence level as enum
var level = context.BotConfidenceLevel();
// Returns: VeryLow, Low, Medium, High, or VeryHigh

switch (context.BotConfidenceLevel())
{
    case BotConfidenceLevel.VeryHigh:  // 90-100%
        return Results.StatusCode(403);
    case BotConfidenceLevel.High:      // 70-90%
        return Results.Json(new { limited = true });
    case BotConfidenceLevel.Medium:    // 50-70%
        // Apply rate limiting
        break;
    default:
        // Allow normally
        break;
}
```

### Bot Metadata

```csharp
// Get bot type (if detected)
var botType = context.BotType();  // BotType? enum
if (botType == BotType.SearchEngine)
{
    // Allow Googlebot, Bingbot, etc.
}

// Get bot name (if identified)
var botName = context.BotName();  // string?
if (botName == "Googlebot")
{
    // Special handling for Google
}

// Get detection reasons
var reasons = context.BotDetectionReasons();  // List<DetectionReason>?
foreach (var reason in reasons ?? [])
{
    Console.WriteLine($"{reason.Category}: {reason.Detail} ({reason.ConfidenceImpact:F2})");
}
```

### Action & Policy Information

```csharp
// Check if request was blocked
if (context.WasBlocked())
{
    // Request was blocked by policy
}

// Get the action that was taken
var action = context.BotDetectionAction();  // "Block", "Throttle", "Allow", etc.

// Get the policy that was applied
var policy = context.BotDetectionPolicy();  // "production", "demo", etc.

// Get the primary detection category
var category = context.BotDetectionCategory();  // "UserAgent", "IP", "Behavioral", etc.
```

### Performance Metrics

```csharp
// Get detection time
var detectionTime = context.BotDetectionTime();  // TimeSpan?
if (detectionTime.HasValue)
{
    Console.WriteLine($"Detection took {detectionTime.Value.TotalMilliseconds:F2}ms");
}
```

### Full Result

```csharp
// Get the complete detection result
var result = context.GetBotDetectionResult();  // BotDetectionResult?
if (result != null)
{
    // Access all properties
    Console.WriteLine($"Bot: {result.IsBot}, Score: {result.ConfidenceScore:F2}");
}
```

## Example: Custom Middleware

```csharp
app.Use(async (context, next) =>
{
    // Check bot status before processing
    if (context.IsMaliciousBot())
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Access denied");
        return;
    }

    // Add custom headers based on detection
    if (context.IsBot())
    {
        context.Response.Headers["X-Bot-Score"] = context.BotConfidenceScore().ToString("F2");
        context.Response.Headers["X-Bot-Level"] = context.BotConfidenceLevel().ToString();
    }

    await next();
});
```

## Example: Minimal API Endpoint

```csharp
app.MapGet("/api/status", (HttpContext context) =>
{
    return new
    {
        isBot = context.IsBot(),
        isHuman = context.IsHuman(),
        isMalicious = context.IsMaliciousBot(),
        confidenceScore = context.BotConfidenceScore(),
        confidenceLevel = context.BotConfidenceLevel().ToString(),
        botType = context.BotType()?.ToString(),
        botName = context.BotName(),
        wasBlocked = context.WasBlocked(),
        detectionTime = context.BotDetectionTime()?.TotalMilliseconds
    };
});
```

## Example: Rate Limiting Based on Confidence

```csharp
app.Use(async (context, next) =>
{
    var score = context.BotConfidenceScore();

    // Apply progressive rate limiting
    if (score >= 0.9)
    {
        // Very suspicious - 1 request per minute
        await ApplyRateLimit(context, maxRequests: 1, window: TimeSpan.FromMinutes(1));
    }
    else if (score >= 0.7)
    {
        // Suspicious - 10 requests per minute
        await ApplyRateLimit(context, maxRequests: 10, window: TimeSpan.FromMinutes(1));
    }
    else if (score >= 0.5)
    {
        // Moderately suspicious - 100 requests per minute
        await ApplyRateLimit(context, maxRequests: 100, window: TimeSpan.FromMinutes(1));
    }
    // Otherwise no rate limiting

    await next();
});
```

## Example: Logging with Structured Data

```csharp
app.Use(async (context, next) =>
{
    await next();

    // Log bot requests with structured data
    if (context.IsBot())
    {
        logger.LogInformation(
            "Bot request: {Method} {Path} - Type: {BotType}, Name: {BotName}, Score: {Score:F2}, Level: {Level}, Blocked: {Blocked}",
            context.Request.Method,
            context.Request.Path,
            context.BotType()?.ToString() ?? "Unknown",
            context.BotName() ?? "Unidentified",
            context.BotConfidenceScore(),
            context.BotConfidenceLevel(),
            context.WasBlocked()
        );
    }
});
```

## Migration from Old API

**Before** (clumsy):

```csharp
if (context.Items.TryGetValue(BotDetectionMiddleware.BotDetectionResultKey, out var resultObj) &&
    resultObj is BotDetectionResult result)
{
    if (result.IsBot && result.ConfidenceScore >= 0.7)
    {
        // Handle malicious bot
    }
}
```

**After** (clean):

```csharp
if (context.IsMaliciousBot())
{
    // Handle malicious bot
}
```

## Performance Notes

- All extension methods are lightweight property accessors
- Results are cached in `HttpContext.Items` by the middleware
- No additional detection is performed - these just read existing results
- Safe to call multiple times - no performance penalty
