# User-Agent Detection

User-Agent detection is the first line of defense, providing fast pattern-based bot identification through multiple
matching strategies.

## How It Works

The detector analyzes the `User-Agent` header against several pattern sources:

1. **Known Good Bots** - Verified search engine crawlers that should be allowed
2. **Malicious Bot Patterns** - String patterns associated with bad actors
3. **Automation Frameworks** - Tools like Selenium, Puppeteer, Playwright
4. **Source-Generated Regex** - Build-time compiled patterns for maximum speed
5. **Downloaded Patterns** - Runtime patterns from external bot databases

## Detection Flow

```
User-Agent → Whitelist Check → Skip (verified bot)
          ↓
          → Malicious Pattern Check → +0.3 confidence
          ↓
          → Automation Framework Check → +0.5 confidence
          ↓
          → Compiled Regex Patterns → +0.2 confidence per match
          ↓
          → Downloaded Patterns → +0.25 confidence
          ↓
          → Heuristics (length, URL in UA) → +0.3-0.4 confidence
```

## Configuration

```json
{
  "BotDetection": {
    "EnableUserAgentDetection": true,
    "BotThreshold": 0.7,
    "WhitelistedBotPatterns": [
      "Googlebot",
      "Bingbot",
      "DuckDuckBot",
      "Slackbot"
    ]
  }
}
```

## Whitelisted Patterns

By default, verified search engine bots are whitelisted. When detected, they receive:

- `Confidence: 0.0` (not a threat)
- `BotType: VerifiedBot`
- `BotName`: The identified bot name

Add patterns to `WhitelistedBotPatterns` to allow specific bots:

```csharp
services.AddBotDetection(options =>
{
    options.WhitelistedBotPatterns.Add("MyMonitoringBot");
});
```

## Detection Signals

### Malicious Bot Patterns

Matches strings known to be associated with malicious bots:

- Fake user agents claiming to be browsers
- Known scraper signatures
- Generic bot identifiers

Impact: +0.3 confidence per match

### Automation Frameworks

Detects automation tools commonly used for scraping:

| Framework      | Impact | Bot Type |
|----------------|--------|----------|
| Selenium       | +0.5   | Scraper  |
| Puppeteer      | +0.5   | Scraper  |
| Playwright     | +0.5   | Scraper  |
| PhantomJS      | +0.5   | Scraper  |
| HeadlessChrome | +0.5   | Scraper  |

### Heuristic Checks

**Short User-Agent** (< 20 characters):

- Real browsers have verbose UAs
- Impact: +0.4 confidence

**URL in User-Agent**:

- Common in crawler bots identifying themselves
- Impact: +0.3 confidence

## Pattern Sources

### Static Patterns (Build-Time)

Source-generated regex patterns compiled at build time for optimal performance. These are embedded in
`BotSignatures.cs`:

```csharp
// Source-generated - compiled at build time
[GeneratedRegex(@"bot|crawler|spider|scraper", RegexOptions.IgnoreCase)]
public static partial Regex BotKeywordPattern { get; }
```

### Downloaded Patterns (Runtime)

The system can download patterns from external sources on startup:

```json
{
  "BotDetection": {
    "PatternUpdateUrl": "https://raw.githubusercontent.com/.../bot-patterns.txt",
    "PatternUpdateIntervalHours": 24
  }
}
```

Downloaded patterns are:

- Compiled once with `RegexOptions.Compiled`
- Cached in memory
- Auto-refreshed based on interval
- Falls back to static patterns if download fails

## Performance

User-Agent detection is optimized for speed:

| Check                       | Typical Time |
|-----------------------------|--------------|
| String contains (whitelist) | < 0.01ms     |
| String contains (patterns)  | < 0.1ms      |
| Source-generated regex      | < 0.5ms      |
| Compiled regex (downloaded) | < 1ms        |

Total typical time: **< 2ms**

## Integration with Pattern Reputation

When enabled, detected patterns feed into the reputation system:

```
UA matches pattern → Reputation lookup → Score adjustment
                  ↓
                  → Learning event published → Reputation updated
```

Reputation states affect UA detection weight:

| State         | Weight Multiplier    |
|---------------|----------------------|
| ConfirmedBad  | 1.0 (full weight)    |
| Suspect       | 0.5                  |
| Neutral       | 0.1                  |
| ConfirmedGood | -0.5 (reduces score) |

## Common Patterns Detected

### Search Engines (Whitelisted by Default)

- Googlebot, Bingbot, YandexBot, DuckDuckBot
- Facebookbot, Twitterbot, LinkedInBot
- Slackbot, Discordbot, TelegramBot

### Scrapers/Automation

- curl, wget, python-requests, python-urllib
- scrapy, requests, axios, node-fetch
- Java HTTP client, Go HTTP client, OkHttp

### Suspicious Indicators

- Generic bot/crawler/spider keywords
- Missing platform details (bare Mozilla/5.0)
- Non-standard version formats

## Extending User-Agent Detection

Add custom patterns via configuration:

```csharp
services.AddBotDetection(options =>
{
    // Block specific user agents
    options.BlockedUserAgentPatterns.Add("MyBadBot");

    // Allow specific bots
    options.WhitelistedBotPatterns.Add("MyGoodBot");
});
```

Or implement a custom detector for complex logic:

```csharp
public class CustomUaDetector : IDetector
{
    public string Name => "Custom UA Detector";
    public DetectorStage Stage => DetectorStage.RawSignals;

    public Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken ct)
    {
        var ua = context.Request.Headers.UserAgent.ToString();
        // Custom logic...
    }
}
```

## Accessing Results

```csharp
// Get all detection reasons
var reasons = context.GetDetectionReasons();
var uaReasons = reasons.Where(r => r.Category == "User-Agent");

// Check if bot was identified
var botName = context.GetBotName();
var botType = context.GetBotType();
```
