# Version Age Detection

Version age detection analyzes browser and OS versions in User-Agent strings to identify bots using outdated or
impossible version combinations.

## How It Works

Real browser users typically run relatively recent browser versions (automatic updates). Bots often:

1. **Use outdated versions** - Hardcoded User-Agents that never update
2. **Use ancient OS versions** - Windows XP, Android 4.x
3. **Claim impossible combinations** - Chrome 130 on Windows XP (impossible)
4. **Lag behind release cycles** - Version 50+ behind current

## Detection Signals

### Outdated Browser Versions

Browser versions are compared against current releases (fetched from external APIs and cached):

| Versions Behind | Category            | Confidence Impact |
|-----------------|---------------------|-------------------|
| 5-10            | Slightly outdated   | +0.05             |
| 10-20           | Moderately outdated | +0.15             |
| 20+             | Severely outdated   | +0.35             |

Example:

```
Current Chrome: 130
Request UA: Chrome/85
Versions behind: 45
Result: Severely outdated (+0.35)
```

### Outdated OS Versions

Operating systems are classified by age:

| Classification | Examples                | Confidence Impact |
|----------------|-------------------------|-------------------|
| Ancient        | Windows XP, Android 4.x | +0.5              |
| Very Old       | Windows 7, Android 7    | +0.25             |
| Old            | Windows 8.1, Android 9  | +0.1              |

### Impossible Combinations

Some browser versions cannot run on older operating systems:

| OS            | Max Chrome Version | Example Impact       |
|---------------|--------------------|----------------------|
| Windows XP    | Chrome 49          | +0.6 for Chrome 50+  |
| Windows Vista | Chrome 50          | +0.6 for Chrome 51+  |
| Windows 7     | Chrome 109         | +0.6 for Chrome 110+ |
| Android 4.x   | Chrome 70          | +0.6 for Chrome 71+  |

Example:

```
UA: Mozilla/5.0 (Windows NT 5.1) Chrome/120.0.0.0
OS: Windows XP (NT 5.1)
Browser: Chrome 120
Max supported on XP: Chrome 49
Result: Impossible combination (+0.6), likely scraper
```

### Combined Outdated Penalty

When both browser AND OS are outdated, an additional boost is applied:

```
Chrome 85 on Windows 7:
- Browser outdated: +0.15
- OS very old: +0.25
- Combined boost: +0.1
- Total: +0.5
```

## Browser Version Data

The detector fetches current browser versions from external APIs. **Works out of the box with zero configuration** -
sensible fallback versions are built-in.

### Data Sources

The `BrowserVersionService` runs as a background service and fetches version data from:

1. **useragents.me API** (default) - Free API providing current browser User-Agent distributions
    - URL: `https://www.useragents.me/api` (configurable)
    - Parses real-world browser versions from traffic data
    - Updates automatically every 24 hours

2. **Fallback versions** (built-in) - Used when API is unavailable:
    - Chrome: 130, Firefox: 133, Safari: 18, Edge: 130, Opera: 115
    - These defaults are regularly updated with package releases

3. **Custom API** (optional) - Point to your own version data source

### How It Works

```
┌─────────────────────────────────────────────────────────────────┐
│ Startup (10 second delay to not slow app start)                │
├─────────────────────────────────────────────────────────────────┤
│ BrowserVersionService fetches from useragents.me API           │
│   ↓                                                             │
│ Parses User-Agent strings: "Chrome/130" → Chrome = 130         │
│   ↓                                                             │
│ Updates in-memory cache (ConcurrentDictionary)                  │
│   ↓                                                             │
│ Repeats every 24 hours                                          │
└─────────────────────────────────────────────────────────────────┘
         ↓
┌─────────────────────────────────────────────────────────────────┐
│ VersionAgeDetector.DetectAsync()                                │
│   ↓                                                             │
│ Calls BrowserVersionService.GetLatestVersionAsync("Chrome")    │
│   ↓                                                             │
│ Returns cached version (or fallback if not yet updated)        │
│   ↓                                                             │
│ Compares: Request Chrome 85 vs Current 130 = 45 versions behind│
└─────────────────────────────────────────────────────────────────┘
```

### Zero-Config Default

With no configuration, the service:

- Uses built-in fallback versions immediately (no startup delay)
- Attempts to fetch fresh data from useragents.me API after 10 seconds
- Silently falls back to built-ins if API is unavailable
- Updates every 24 hours in the background

### Supported Browsers

| Browser | Regex Pattern           | Notes                   |
|---------|-------------------------|-------------------------|
| Chrome  | `Chrome/(\d+)`          | Includes Chromium-based |
| Firefox | `Firefox/(\d+)`         |                         |
| Safari  | `Version/(\d+).*Safari` | Requires Version token  |
| Edge    | `Edg/(\d+)`             | Modern Edge (Chromium)  |
| Opera   | `OPR/(\d+)`             |                         |
| Brave   | `Brave/(\d+)`           |                         |

### Supported Operating Systems

| OS      | Regex Pattern            | Version Format          |
|---------|--------------------------|-------------------------|
| Windows | `Windows NT (\d+\.\d+)`  | NT version (e.g., 10.0) |
| macOS   | `Mac OS X (\d+[_\.]\d+)` | Version (e.g., 10_15)   |
| Android | `Android (\d+)`          | Major version           |
| iOS     | `iPhone OS (\d+)`        | Major version           |
| Linux   | Contains "Linux"         | No version tracked      |

## Configuration

```json
{
  "BotDetection": {
    "VersionAge": {
      "Enabled": true,
      "CheckBrowserVersion": true,
      "CheckOsVersion": true,
      "MaxBrowserVersionAge": 10,
      "BrowserSlightlyOutdatedConfidence": 0.05,
      "BrowserModeratelyOutdatedConfidence": 0.15,
      "BrowserSeverelyOutdatedConfidence": 0.35,
      "OsOldConfidence": 0.1,
      "OsVeryOldConfidence": 0.25,
      "OsAncientConfidence": 0.5,
      "ImpossibleCombinationConfidence": 0.6,
      "CombinedOutdatedBoost": 0.1,
      "OsAgeClassification": {
        "Windows NT 5.1": "ancient",
        "Windows NT 6.0": "ancient",
        "Windows NT 6.1": "very_old",
        "Windows NT 6.2": "old",
        "Windows NT 6.3": "old",
        "Android 4": "ancient",
        "Android 5": "very_old",
        "Android 6": "very_old",
        "Android 7": "very_old",
        "Android 8": "old",
        "Android 9": "old"
      },
      "MinBrowserVersionByOs": {
        "Windows NT 5.1": 49,
        "Windows NT 6.0": 50,
        "Windows NT 6.1": 109,
        "Android 4": 70,
        "Android 5": 92
      }
    }
  }
}
```

| Option                                | Type   | Default | Description                                        |
|---------------------------------------|--------|---------|----------------------------------------------------|
| `Enabled`                             | bool   | `true`  | Enable version age detection                       |
| `CheckBrowserVersion`                 | bool   | `true`  | Check browser version staleness                    |
| `CheckOsVersion`                      | bool   | `true`  | Check OS version age                               |
| `MaxBrowserVersionAge`                | int    | `10`    | Versions behind before "moderately outdated"       |
| `BrowserSlightlyOutdatedConfidence`   | double | `0.05`  | Impact for 5-10 versions behind                    |
| `BrowserModeratelyOutdatedConfidence` | double | `0.15`  | Impact for 10-20 versions behind                   |
| `BrowserSeverelyOutdatedConfidence`   | double | `0.35`  | Impact for 20+ versions behind                     |
| `OsOldConfidence`                     | double | `0.1`   | Impact for "old" OS                                |
| `OsVeryOldConfidence`                 | double | `0.25`  | Impact for "very old" OS                           |
| `OsAncientConfidence`                 | double | `0.5`   | Impact for "ancient" OS                            |
| `ImpossibleCombinationConfidence`     | double | `0.6`   | Impact for impossible browser/OS combination       |
| `CombinedOutdatedBoost`               | double | `0.1`   | Additional boost when both browser AND OS outdated |

## Integration with Other Detectors

Version age detection works with other detectors for stronger signals:

| Combination                   | Example                         | Combined Impact     |
|-------------------------------|---------------------------------|---------------------|
| Version Age + Datacenter IP   | Old Chrome from AWS             | High confidence bot |
| Version Age + Missing Headers | Old Firefox, no Accept-Language | Likely scraper      |
| Version Age + Rate Anomaly    | Old Safari with 100 req/min     | Definite bot        |

## Performance

Version age detection is fast with minimal overhead:

| Operation               | Typical Time |
|-------------------------|--------------|
| Regex extraction        | < 0.1ms      |
| Version lookup (cached) | < 0.05ms     |
| OS classification       | < 0.01ms     |
| **Total**               | **< 0.2ms**  |

Version data is cached for 24 hours, so external API calls are rare.

## Accessing Results

```csharp
// Get version-specific detection reasons
var reasons = context.GetDetectionReasons();
var versionReasons = reasons.Where(r =>
    r.Category is "BrowserVersion" or "OsVersion" or "ImpossibleCombination" or "VersionAge");

// Example reasons:
// "Chrome v85 is 45 versions behind (latest: 130)"
// "Ancient OS detected: Windows NT 5.1 (extremely rare in legitimate traffic)"
// "Impossible: Chrome v120 cannot run on Windows NT 5.1 (max supported: v49)"
// "Both browser AND OS are outdated - suspicious combination"
```

## Why This Works

Bots using hardcoded User-Agents fail to keep pace with browser updates:

1. **Chrome releases every 4 weeks** - A static UA quickly becomes outdated
2. **Bot frameworks ship with fixed UAs** - Selenium, Puppeteer templates lag behind
3. **Scraping tutorials use old examples** - Copy-pasted UAs from 2019 tutorials
4. **Impossible combinations reveal spoofing** - Can't run Chrome 130 on Windows XP

## Best Practices

1. **Combine with other signals** - Version age alone may false-positive on enterprise users with controlled updates
2. **Allow for enterprise lag** - Some organizations stay 2-3 versions behind for stability
3. **Watch for pattern changes** - If bots update their UAs, adjust thresholds
4. **Monitor false positives** - Review detections from government/education sectors (often run older systems)

## Example Bot Signatures

Common bot User-Agents caught by version age detection:

```
# Hardcoded from old Selenium default
Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/90.0.4430.212

# Impossible combination (Chrome 120 on XP)
Mozilla/5.0 (Windows NT 5.1) Chrome/120.0.0.0 Safari/537.36

# Very old Android
Mozilla/5.0 (Linux; Android 4.4.2; Nexus 5) Chrome/46.0.2490.76

# Ancient Safari version
Mozilla/5.0 (Macintosh; Intel Mac OS X 10_9_5) Version/7.0.6 Safari/537.78.2
```
