# User-Agent Detection

User-Agent detection is the first line of defense, providing fast pattern-based bot identification through multiple
matching strategies.

## How It Works

The detector analyzes the `User-Agent` header against a three-tier pattern source chain:

1. **YAML bot-pattern catalog** (`Definitions/BotPatterns/*.bot-patterns.yaml`) - Substring match over every catalogued search engine, AI scraper, fediverse server, developer tool, social media, monitoring, and SEO tool. This is the primary source and the one edited to add new patterns.
2. **Heuristic checks** - Structural analysis of the UA string (length, presence of URL, automation-framework keywords, missing platform details).
3. **WellKnownBotIndex fallback** (7.5+) - When neither tier above matches, `BotPatternLoader.MatchUserAgent` falls through to `WellKnownBotIndex.TryMatch`. This runs a three-tier scan against ~635 arcjet patterns: a SIMD `SearchValues<string>` pre-filter rejects non-bot UAs with zero allocations, then `string.Contains` handles the ~81% of patterns that are pure literals, and only the remaining ~19% (patterns containing regex metacharacters) hit the actual `Regex` engine. Results are cached in a 4 000-entry LFU `BoundedCache`. The index is empty until the first successful download by `WellKnownBotRefreshService`. YAML remains the authoritative source; the arcjet catalog is a safety net for bots not yet in YAML.

## Detection Flow

```
User-Agent → YAML bot-pattern catalog (substring match) → bot name + type identified
          ↓ (no match)
          → Heuristic checks (length, URL, keywords) → confidence contribution
          ↓ (no YAML match)
          → WellKnownBotIndex (arcjet catalog, regex, 7.5+) → bot name + type
          ↓
          → Signals written: ua.bot_name, ua.bot_type, ua.family, ua.bot_instance
```

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "UserAgentContributor": {
        "Enabled": true,
        "Parameters": {
          "short_ua_threshold": 20,
          "short_ua_confidence": 0.4,
          "url_in_ua_confidence": 0.3
        }
      }
    }
  }
}
```

Bot patterns live in `Definitions/BotPatterns/*.bot-patterns.yaml` (embedded resources). To add a new bot, edit the appropriate YAML file -- not C# code. The arcjet catalog fallback is populated automatically by `WellKnownBotRefreshService` on startup; no configuration is needed to enable it.

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

### YAML bot-pattern catalog (primary, build-time embedded)

All bot patterns ship as embedded YAML files in `Definitions/BotPatterns/`. Each file covers one category (search engines, AI scrapers, fediverse servers, social media, developer tools, monitoring, SEO). `BotPatternLoader` reads them once at startup and builds an O(1) substring match table and a name-to-type index. Adding a new pattern is a YAML edit; no C# change is required.

### WellKnownBotIndex (arcjet catalog fallback, 7.5+)

`WellKnownBotRefreshService` downloads the arcjet well-known-bots catalog on startup and on a configurable refresh interval. The downloaded entries are compiled to regexes and loaded into `WellKnownBotIndex.Default` atomically. `BotPatternLoader.MatchUserAgent` consults this index after the YAML catalog when no match is found. The index starts empty and becomes available after the first successful download; the YAML catalog always fires first.

## Performance

User-Agent detection is optimized for speed:

| Check                       | Typical Time |
|-----------------------------|--------------|
| String contains (whitelist) | < 0.01ms     |
| String contains (patterns)  | < 0.1ms      |
| Source-generated regex      | < 0.5ms      |
| WellKnownBotIndex (arcjet)  | < 1ms        |

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

Add new bot patterns by editing the appropriate YAML file in `Definitions/BotPatterns/`. Each entry needs a `pattern` (substring to match), `bot_name` (display name), and `bot_type`. For AI bots add an `ai_category` field. The YAML change is picked up at next startup.

For custom detection logic beyond UA pattern matching, implement `IContributingDetector` (see the 5-file checklist in CLAUDE.md) and register it in DI.

## Accessing Results

```csharp
// Get all detection reasons
var reasons = context.GetDetectionReasons();
var uaReasons = reasons.Where(r => r.Category == "User-Agent");

// Check if bot was identified
var botName = context.GetBotName();
var botType = context.GetBotType();
```
