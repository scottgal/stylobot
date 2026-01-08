# Client-Side Fingerprinting

JavaScript-based browser integrity checking that detects headless browsers and automation frameworks.

## How It Works

1. Server generates a signed token (like XSRF) for each page load
2. JavaScript collects browser fingerprint data
3. Data is POSTed back with the token for verification
4. Server analyzes fingerprint for automation markers

The signed token prevents replay attacks and fingerprint spoofing.

## Setup

### 1. Enable in Configuration

```json
{
  "BotDetection": {
    "ClientSide": {
      "Enabled": true,
      "TokenSecret": "your-32-char-minimum-secret-key",
      "TokenLifetimeSeconds": 300,
      "CollectWebGL": true,
      "CollectCanvas": true,
      "MinIntegrityScore": 70,
      "HeadlessThreshold": 0.5
    }
  }
}
```

### 2. Add the Tag Helper

In `_ViewImports.cshtml`:

```cshtml
@addTagHelper *, Mostlylucid.BotDetection
```

### 3. Add Script to Layout

```html
<bot-detection-script />
```

With options:

```html
<bot-detection-script
    endpoint="/bot-detection/fingerprint"
    defer="true"
    nonce="@cspNonce" />
```

### 4. Map the Endpoint

In `Program.cs`:

```csharp
app.MapBotDetectionFingerprintEndpoint();
```

## Configuration Reference

| Setting                           | Default | Description                              |
|-----------------------------------|---------|------------------------------------------|
| `Enabled`                         | `false` | Enable client-side detection             |
| `TokenSecret`                     | `null`  | Secret for signing tokens (min 16 chars) |
| `TokenLifetimeSeconds`            | `300`   | Token validity period                    |
| `FingerprintCacheDurationSeconds` | `1800`  | Cache fingerprint results                |
| `CollectionTimeoutMs`             | `5000`  | JS collection timeout                    |
| `CollectWebGL`                    | `true`  | Collect WebGL renderer info              |
| `CollectCanvas`                   | `true`  | Collect canvas fingerprint               |
| `CollectAudio`                    | `false` | Collect audio context fingerprint        |
| `MinIntegrityScore`               | `70`    | Min score to consider "human"            |
| `HeadlessThreshold`               | `0.5`   | Headless likelihood threshold            |

## What It Detects

### Automation Markers

- `navigator.webdriver` flag (WebDriver/Selenium)
- PhantomJS global objects
- Nightmare.js markers
- Selenium-specific properties
- Chrome DevTools Protocol (CDP/Puppeteer) markers

### Browser Integrity

- Missing plugins in Chrome (headless has none)
- Zero outer window dimensions
- Prototype pollution (non-native `Function.bind`)
- Modified `eval.toString()` length
- Notification permission inconsistencies

## Accessing Results

```csharp
// Headless browser likelihood (0.0 - 1.0)
var headlessLikelihood = context.GetHeadlessLikelihood();

// Browser integrity score (0 - 100)
var integrityScore = context.GetBrowserIntegrityScore();

// Check in middleware
if (headlessLikelihood > 0.7)
{
    // Likely automation
}
```

## Integration with Behavioral Analysis

When client-side is enabled, the `BehavioralDetector` automatically tracks by fingerprint hash in addition to IP. This
catches bots that:

- Rotate IP addresses
- Use residential proxies
- Maintain the same browser fingerprint

## Security Considerations

1. **Token Secret**: Use a strong, unique secret (32+ characters recommended)
2. **HTTPS**: Always use HTTPS to prevent token interception
3. **CSP**: If using Content Security Policy, add script nonces
4. **Rate Limiting**: The fingerprint endpoint is automatically rate-limited

## Test Page

The demo includes a test page at `/bot-test` for testing with PuppeteerSharp or other automation tools:

```csharp
// In your test
using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true });
var page = await browser.NewPageAsync();
await page.GotoAsync("https://localhost:5001/bot-test");
// Page shows detection results
```
