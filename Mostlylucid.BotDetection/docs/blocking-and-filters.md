# Blocking and Filters

Multiple ways to block or allow bots at different levels.

## MVC Attributes

### Block Bots

```csharp
// Block all bots
[BlockBots]
public IActionResult SensitiveData() { ... }

// Block bots except verified ones (Googlebot, etc.)
[BlockBots(AllowVerifiedBots = true)]
public IActionResult PublicData() { ... }

// Block bots except search engines
[BlockBots(AllowSearchEngines = true)]
public IActionResult Indexable() { ... }

// Only block high-confidence detections
[BlockBots(MinConfidence = 0.9)]
public IActionResult ModerateProtection() { ... }

// Custom status code and message
[BlockBots(StatusCode = 429, Message = "Too many requests")]
public IActionResult RateLimited() { ... }
```

### Allow Bots

```csharp
// Explicitly allow all bots
[AllowBots]
public IActionResult RobotsFile() { ... }

// Only allow verified bots
[AllowBots(OnlyVerified = true)]
public IActionResult Sitemap() { ... }
```

### Require Human

```csharp
// Blocks ALL bots including verified ones
[RequireHuman]
public IActionResult SubmitForm() { ... }

[RequireHuman(StatusCode = 403, Message = "Human verification required")]
public IActionResult SecureAction() { ... }
```

## Minimal API Filters

```csharp
// Block all bots
app.MapGet("/api/data", () => "sensitive")
   .BlockBots();

// Block with options
app.MapGet("/api/protected", () => "protected")
   .BlockBots(allowVerifiedBots: true, minConfidence: 0.8);

// Require human
app.MapPost("/api/submit", () => "submitted")
   .RequireHuman();
```

## Global Blocking

Configure automatic blocking in appsettings:

```json
{
  "BotDetection": {
    "BlockDetectedBots": false,
    "BlockStatusCode": 403,
    "BlockMessage": "Access denied",
    "MinConfidenceToBlock": 0.8,
    "AllowVerifiedSearchEngines": true,
    "AllowSocialMediaBots": true,
    "AllowMonitoringBots": true
  }
}
```

## HttpContext Extensions

```csharp
// Basic checks
bool isBot = context.IsBot();
bool isHuman = context.IsHuman();
double confidence = context.GetBotConfidence();

// Bot type checks
bool isSearchEngine = context.IsSearchEngineBot();
bool isMalicious = context.IsMaliciousBot();
bool isSocial = context.IsSocialMediaBot();
bool isVerified = context.IsVerifiedBot();

// Threshold-based
bool highConfidence = context.IsBotWithConfidence(0.9);
bool shouldAllow = context.ShouldAllowRequest();
bool shouldBlock = context.ShouldBlockRequest();

// Risk assessment
var risk = context.GetRiskBand();
var action = context.GetRecommendedAction();
bool challenge = context.ShouldChallengeRequest();
```

## Risk Bands

| Band     | Confidence | Recommended Action    |
|----------|------------|-----------------------|
| Low      | < 0.3      | Allow                 |
| Elevated | 0.3 - 0.5  | Challenge or Throttle |
| Medium   | 0.5 - 0.7  | Challenge             |
| High     | > 0.7      | Block                 |

## Custom Middleware

```csharp
public class BotActionMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context)
    {
        var action = context.GetRecommendedAction();

        switch (action)
        {
            case RecommendedAction.Block:
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Blocked" });
                return;

            case RecommendedAction.Challenge:
                context.Response.Headers["X-Challenge-Required"] = "true";
                break;

            case RecommendedAction.Throttle:
                context.Response.Headers["Retry-After"] = "60";
                break;
        }

        await _next(context);
    }
}
```
