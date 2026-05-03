# Detection Strategies

Mostlylucid.BotDetection uses multiple detection strategies that work together to provide comprehensive bot detection:

1. **User-Agent Detection** – Known bot pattern matching
2. **Header Detection** – HTTP header anomaly analysis
3. **IP Detection** – Datacenter/cloud IP identification
4. **Version Age Detection** – Browser/OS version staleness and impossible combinations
5. **Security Tools Detection** – Penetration testing tool identification (Nikto, sqlmap, etc.)
6. **Project Honeypot** – HTTP:BL IP reputation checking via DNS
7. **Behavioral Analysis** – Request pattern monitoring
8. **Client-Side Fingerprinting** – Browser integrity checking (optional)
9. **Inconsistency Detection** – Cross-signal contradiction detection
10. **Risk Assessment** – Signal aggregation into risk bands
11. **AI Detection** – ML-based classification (optional)

> **How it works:** Strategies 1–7 generate raw signals; strategies 8–9 combine those signals into risk bands and
> AI-assisted decisions.

## Architecture Overview

Detection runs on a **signal-driven, event-based architecture** with two execution paths:

### Two-Dimensional Scoring Model

StyloBot separates **bot probability** from **detection confidence**:

| Dimension | Range | Meaning |
|-----------|-------|---------|
| **Bot Probability** | 0.0 - 1.0 | Likelihood that the request is from a bot |
| **Detection Confidence** | 0.0 - 1.0 | How certain the system is in its verdict |

Detection confidence is calculated independently from three factors:

- **Agreement (40%)** -- fraction of weighted evidence pointing in the majority direction
- **Weight Coverage (35%)** -- total evidence weight vs expected baseline
- **Detector Count (25%)** -- number of distinct detectors that contributed

This means a request can have high bot probability but low confidence (e.g., only one detector fired) or low bot probability with high confidence (many detectors agree it is human). Policies can use `MinConfidence` to require a threshold of certainty before taking action.

```csharp
context.GetBotProbability()       // 0.0 - 1.0: likelihood of being a bot
context.GetDetectionConfidence()  // 0.0 - 1.0: certainty of the verdict
context.GetBotConfidence()        // backward compat, returns bot probability
```

### Fast Path (Synchronous)

Low-latency detectors that run inline with the request:

- User-Agent, Header, IP, Behavioral analysis
- Completes in <100ms
- Uses consensus-based finalisation (all detectors report before scoring)
- Can trigger early exit if detection confidence exceeds threshold

### Slow Path (Asynchronous)

Background processing for heavier analysis:

- Heuristic/LLM AI classification
- Learning and pattern discovery
- Runs via inter-request event bus (non-blocking)

```
┌─────────────────────────────────────────────────────────────────────┐
│ Request arrives                                                      │
├─────────────────────────────────────────────────────────────────────┤
│ FAST PATH (sync, <100ms)                                            │
│   ├─ Stage 0: UA → Headers → IP → VersionAge → ClientSide (parallel)│
│   ├─ Stage 1: Behavioral (depends on Stage 0)                       │
│   ├─ Stage 2: Inconsistency (reads all prior signals)               │
│   └─ Consensus check → Early exit if high confidence                │
├─────────────────────────────────────────────────────────────────────┤
│ SLOW PATH (async, background)                                        │
│   ├─ Heuristic/LLM inference (if enabled)                           │
│   ├─ Pattern learning                                                │
│   └─ Training data collection                                        │
└─────────────────────────────────────────────────────────────────────┘
```

### Configuration

```json
{
  "BotDetection": {
    "FastPath": {
      "Enabled": true,
      "EarlyExitThreshold": 0.85,
      "SkipSlowPathThreshold": 0.2,
      "SlowPathTriggerThreshold": 0.5,
      "FastPathTimeoutMs": 100,
      "FastPathDetectors": [
        { "Name": "User-Agent Detector", "Signal": "UserAgentAnalyzed" },
        { "Name": "Header Detector", "Signal": "HeadersAnalyzed" },
        { "Name": "IP Detector", "Signal": "IpAnalyzed" },
        { "Name": "Version Age Detector", "Signal": "VersionAgeAnalyzed" },
        { "Name": "Behavioral Detector", "Signal": "BehaviourSampled" },
        { "Name": "Inconsistency Detector", "Signal": "InconsistencyUpdated" }
      ],
      "SlowPathDetectors": [
        { "Name": "Heuristic Detector", "Signal": "AiClassificationCompleted" }
      ]
    }
  }
}
```

---

## 1. User-Agent Detection

Matches User-Agent strings against known bot patterns from multiple sources:

- Search engine bots (Googlebot, Bingbot, DuckDuckBot, etc.)
- Social media crawlers (FacebookBot, Twitterbot, LinkedInBot)
- SEO tools (AhrefsBot, SEMrushBot, MajesticBot)
- Scrapers and automation tools
- Known malicious bots

## 2. Header Detection

Analyzes HTTP headers for suspicious patterns:

- Missing standard browser headers
- Inconsistent Accept headers
- Missing or invalid Accept-Language
- Suspicious Connection headers
- Known bot header signatures

## 3. IP Detection

Checks client IP against:

- Known datacenter IP ranges (AWS, Azure, GCP, Oracle Cloud)
- Cloud provider ranges (auto-updated)
- Cloudflare IP ranges

## 4. Version Age Detection

Analyzes browser and OS versions to detect bots using outdated or impossible combinations:

- **Outdated browsers** – Chrome 50 when current is 130 (+0.35 confidence)
- **Ancient operating systems** – Windows XP, Android 4.x (+0.5 confidence)
- **Impossible combinations** – Chrome 120 on Windows XP (max supported is v49)
- **Combined boost** – Both browser AND OS outdated adds extra penalty

Version data is fetched from external APIs and cached for 24 hours.

See [version-age-detection.md](version-age-detection.md) for detailed configuration.

## 5. Behavioral Analysis

Monitors request patterns at multiple identity levels:

- **Per-IP rate limiting** – requests per minute per IP
- **Per-fingerprint tracking** – browser fingerprint hash (when client-side enabled)
- **Per-API key tracking** – via configurable header
- **Per-user tracking** – via claim or header

### Configure Behavioral Analysis

```json
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
```

### What Behavioral Analysis Detects

**Volume anomalies:**

- Excessive request rate per IP/fingerprint/API key/user
- Sudden request spikes (5x normal rate by default)
- Accessing many new endpoints suddenly

**Timing anomalies:**

- Rapid sequential requests (<100ms between requests)
- Suspiciously regular timing (low standard deviation in intervals)

**Session anomalies:**

- Missing cookies across multiple requests
- Missing referrer on non-initial requests

## 6. Client-Side Fingerprinting

JavaScript-based browser integrity checking that detects headless browsers and automation frameworks. Uses a signed
token system (like XSRF) to prevent spoofing.

### Setup

1. Enable in configuration:

```json
{
  "BotDetection": {
    "ClientSide": {
      "Enabled": true,
      "TokenSecret": "your-secret-key-here",
      "TokenLifetimeSeconds": 300,
      "CollectWebGL": true,
      "CollectCanvas": true
    }
  }
}
```

2. Add the Tag Helper to your `_ViewImports.cshtml`:

```cshtml
@addTagHelper *, Mostlylucid.BotDetection
```

3. Add the script to your layout:

```html
<bot-detection-script />
<!-- or with options -->
<bot-detection-script endpoint="/bot-detection/fingerprint" defer="true" nonce="@cspNonce" />
```

4. Map the fingerprint endpoint in `Program.cs`:

```csharp
app.MapBotDetectionFingerprintEndpoint();
```

### What Client-Side Detection Detects

- `navigator.webdriver` flag (WebDriver)
- PhantomJS, Nightmare, Selenium markers
- Chrome DevTools Protocol (CDP/Puppeteer)
- Missing plugins in Chrome
- Zero outer window dimensions
- Prototype pollution (non-native `Function.bind`)
- Modified `eval.toString()` length
- Notification permissions inconsistencies

### Results via HttpContext

```csharp
var headlessLikelihood = context.GetHeadlessLikelihood();  // 0.0-1.0
var integrityScore = context.GetBrowserIntegrityScore();   // 0-100
```

- `GetHeadlessLikelihood()` returns 0.0–1.0 (higher = more likely headless)
- `GetBrowserIntegrityScore()` returns 0–100 (higher = more "real browser")

## 7. Inconsistency Detection

Catches bots that spoof one signal but forget others:

- UA claims Chrome but missing modern Chrome headers (Sec-Fetch-Mode, sec-ch-ua)
- Desktop UA without Accept-Language header
- Generic `*/*` Accept header with browser UA
- Baidu/Yandex bot with wrong Accept-Language
- Referer from internal/localhost addresses
- HTTP/1.1 Connection header from modern browser

All of these contribute to an internal inconsistency score (0–100), exposed via `context.GetInconsistencyScore()` and
included in the overall risk band calculation.

## 8. Risk Assessment

Aggregates signals from strategies 1–7 into actionable risk bands:

| Risk Band  | Meaning             | Typical Action        |
|------------|---------------------|-----------------------|
| `Low`      | Looks human         | Allow                 |
| `Elevated` | Slightly suspicious | Allow or throttle     |
| `Medium`   | Clearly suspicious  | Challenge recommended |
| `High`     | Strong bot signal   | Block                 |

```csharp
// Get risk band
var risk = context.GetRiskBand(); // Low, Elevated, Medium, High

// Check if should challenge (returns true for Medium/High)
if (context.ShouldChallengeRequest())
{
    return ChallengeWithCaptcha(context);
}

// Or get recommended action
var action = context.GetRecommendedAction(); // Allow, Throttle, Challenge, Block

// Get specific scores
var inconsistencyScore = context.GetInconsistencyScore(); // 0-100
```

## 9. AI Detection (Optional)

Use AI Detection when you need to catch sophisticated or evolving bots that evade pure pattern and heuristic-based
methods.

See [ai-detection.md](ai-detection.md) for details on Heuristic and Ollama-based AI detection.

---

## Signal Bus Architecture

Detection uses a dual-bus architecture:

### Intra-Request Bus (`BotSignalBus`)

- Per-request, short-lived
- Detectors publish signals as they complete
- Listeners react to signals in real-time
- Consensus-based: waits for all detectors before finalising

Signal types:

- `UserAgentAnalyzed` – UA detection complete
- `HeadersAnalyzed` – Header analysis complete
- `IpAnalyzed` – IP check complete
- `VersionAgeAnalyzed` – Version age check complete
- `ClientFingerprintReceived` – Client-side data received
- `BehaviourSampled` – Behavioral analysis complete
- `InconsistencyUpdated` – Inconsistency check complete
- `AiClassificationCompleted` – AI/ML inference complete
- `DetectorComplete` – Generic detector completion
- `Finalising` – All detectors reported, scoring begins

### Inter-Request Bus (`LearningEventBus`)

- Long-lived, cross-request
- Background service processes events asynchronously
- Used for learning, pattern discovery, and analytics

Learning event types:

- `HighConfidenceDetection` – Bot detected with high confidence (training data)
- `PatternDiscovered` – New pattern found by AI
- `InconsistencyDetected` – Cross-signal mismatch found
- `UserFeedback` – User confirmed/denied bot detection
- `InferenceRequest` – Request for async AI inference
- `ModelUpdated` – ML model retrained

### Custom Signal Listeners

Implement `IBotSignalListener` to react to detection signals:

```csharp
public class MyCustomListener : IBotSignalListener, ISignalSubscriber
{
    public IEnumerable<BotSignalType> SubscribedSignals => new[]
    {
        BotSignalType.InconsistencyUpdated,
        BotSignalType.Finalising
    };

    public ValueTask OnSignalAsync(
        BotSignalType signal,
        DetectionContext context,
        CancellationToken ct = default)
    {
        if (signal == BotSignalType.InconsistencyUpdated)
        {
            var score = context.GetSignal<double>(SignalKeys.InconsistencyScore);
            // React to inconsistency detection...
        }
        return ValueTask.CompletedTask;
    }
}

// Register in DI
services.AddTransient<IBotSignalListener, MyCustomListener>();
```

### Custom Learning Event Handlers

Implement `ILearningEventHandler` for background processing:

```csharp
public class MyLearningHandler : ILearningEventHandler
{
    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.HighConfidenceDetection
    };

    public async Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
    {
        if (evt.Confidence >= 0.9 && evt.Features != null)
        {
            // Store for training, update patterns, etc.
            await SaveTrainingDataAsync(evt.Features, evt.Label ?? true, ct);
        }
    }
}

// Register in DI
services.AddSingleton<ILearningEventHandler, MyLearningHandler>();
```

---

## Pattern Reputation System (Learning + Forgetting)

The system learns new bot patterns AND forgets stale ones. This prevents the system from becoming paranoid over time as
infrastructure changes (IP reassignments, proxy rotations, misconfigurations get fixed).

### Core Concepts

Each pattern (UA, IP, fingerprint, behavior cluster) has:

| Property   | Description                                        |
|------------|----------------------------------------------------|
| `BotScore` | 0.0 (human) to 1.0 (bot) - current belief          |
| `Support`  | Effective sample count (decays over time)          |
| `State`    | Neutral → Suspect → ConfirmedBad (with hysteresis) |
| `LastSeen` | For time decay when pattern goes quiet             |

### States and Transitions

```
                    ┌──────────────────┐
                    │  ManuallyBlocked │ (admin override, never auto-downgrade)
                    └──────────────────┘
                             ↑ manual
┌─────────┐  score≥0.6   ┌─────────┐  score≥0.9   ┌──────────────┐
│ Neutral │ ──────────→  │ Suspect │ ──────────→  │ ConfirmedBad │
│         │  support≥10  │         │  support≥50  │              │
└─────────┘              └─────────┘              └──────────────┘
     ↑                        │                         │
     │ score≤0.4              │ score≤0.7               │
     └────────────────────────┘ support≥100 ────────────┘
                    (hysteresis: harder to forgive than accuse)
```

### Online Updates (when we see a pattern)

Each detection updates the pattern's reputation via EMA:

```
BotScore_new = (1 - α) * BotScore_old + α * label
```

Where:

- `α` = learning rate (default 0.1)
- `label` = 1.0 for bot, 0.0 for human

### Time Decay (when we DON'T see a pattern)

Stale patterns drift back toward neutral:

```
BotScore_new = BotScore_old + (prior - BotScore_old) * (1 - e^(-Δt/τ))
Support_new = Support_old * e^(-Δt/τ_support)
```

Where:

- `prior` = 0.5 (neutral)
- `τ` = 7 days (score decay)
- `τ_support` = 14 days (support decay)

### Garbage Collection

Patterns are removed when:

- `LastSeen` > 90 days ago
- `Support` < 1.0
- `State` = Neutral

### Configuration

```json
{
  "BotDetection": {
    "Reputation": {
      "LearningRate": 0.1,
      "MaxSupport": 1000,
      "ScoreDecayTauHours": 168,
      "SupportDecayTauHours": 336,
      "Prior": 0.5,
      "PromoteToBadScore": 0.9,
      "PromoteToBadSupport": 50,
      "DemoteFromBadScore": 0.7,
      "DemoteFromBadSupport": 100,
      "GcEligibleDays": 90
    }
  }
}
```

### How It Feeds Back to Fast Path

The fast path uses reputation state to determine behavior:

| State             | Fast Path Behavior                                    |
|-------------------|-------------------------------------------------------|
| `ConfirmedBad`    | Can trigger fast-path abort (full UA weight)          |
| `Suspect`         | Contributes to score (half weight), can't abort alone |
| `Neutral`         | Minimal contribution (10% weight)                     |
| `ConfirmedGood`   | Reduces suspicion                                     |
| `ManuallyBlocked` | Always blocked (admin override)                       |
| `ManuallyAllowed` | Always allowed (admin override)                       |

### Safety Rails

1. **Manual overrides are never auto-downgraded** - If admin blocks a pattern, only admin can unblock
2. **Asymmetric thresholds** - Promoting to bad needs 50 samples, demoting needs 100 (harder to forgive)
3. **Time decay prevents permanent bans** - Old patterns drift back to neutral
4. **GC only touches neutral patterns** - Active patterns are never auto-deleted

---

## Extensibility

Mostlylucid.BotDetection is designed for extensibility at multiple levels.

### Custom Detectors

Implement `IDetector` to add your own detection logic:

```csharp
public class MyCustomDetector : IDetector
{
    public string Name => "My Custom Detector";
    public DetectorStage Stage => DetectorStage.RawSignals; // or DerivedSignals, Aggregation

    public Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken ct)
    {
        var result = new DetectorResult();

        // Your detection logic
        if (IsMyBotPattern(context))
        {
            result.Confidence = 0.8;
            result.Reasons.Add(new DetectionReason
            {
                Category = "Custom",
                Detail = "My custom bot pattern detected"
            });
        }

        return Task.FromResult(result);
    }
}

// Register in DI
services.AddTransient<IDetector, MyCustomDetector>();
```

### Custom Learning Event Handlers

Process learning events for custom analytics or integrations:

```csharp
public class MyAnalyticsHandler : ILearningEventHandler
{
    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.HighConfidenceDetection,
        LearningEventType.DriftDetected
    };

    public async Task HandleAsync(LearningEvent evt, CancellationToken ct)
    {
        // Send to your analytics system
        await _analytics.TrackAsync("bot_detection", new
        {
            type = evt.Type.ToString(),
            confidence = evt.Confidence,
            timestamp = evt.Timestamp
        });
    }
}
```

### Custom Signal Listeners

React to detection signals in real-time:

```csharp
public class MySignalListener : IBotSignalListener, ISignalSubscriber
{
    public IEnumerable<BotSignalType> SubscribedSignals => new[]
    {
        BotSignalType.InconsistencyUpdated,
        BotSignalType.AiClassificationCompleted
    };

    public ValueTask OnSignalAsync(BotSignalType signal, DetectionContext context, CancellationToken ct)
    {
        // React to signals as they happen
        return ValueTask.CompletedTask;
    }
}
```

### Blackboard Architecture (0.5.0-preview1)

For complex detection scenarios, use the new blackboard architecture where detectors emit evidence and can trigger other
detectors:

```csharp
public class MyContributor : ContributingDetectorBase
{
    public override string Name => "My Contributor";
    public override string Category => "Custom";

    // Only run when UserAgent signal exists and risk is elevated
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.AllOf(
            Triggers.WhenSignalExists(SignalKeys.UserAgent),
            Triggers.WhenRiskExceeds(0.3)
        )
    ];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken ct)
    {
        var contributions = new List<DetectionContribution>();

        // Analyze and emit evidence
        if (DetectedBadPattern(state))
        {
            contributions.Add(DetectionContribution.Bot(
                Name, Category,
                confidenceDelta: 0.4,
                reason: "Bad pattern detected"));
        }

        return contributions;
    }
}
```

See [learning-and-reputation.md](learning-and-reputation.md) for full blackboard architecture documentation.

### Configuration Providers

Override configuration from any source:

```csharp
// From environment variables
services.Configure<BotDetectionOptions>(config =>
{
    config.BotThreshold = double.Parse(Environment.GetEnvironmentVariable("BOT_THRESHOLD") ?? "0.7");
});

// From a database
services.AddSingleton<IConfigureOptions<BotDetectionOptions>, DatabaseConfigProvider>();
```

### Pattern Store Implementations

Replace the default SQLite pattern store with your own:

```csharp
public class RedisLearnedPatternStore : ILearnedPatternStore
{
    // Implement using Redis for distributed pattern storage
}

services.AddSingleton<ILearnedPatternStore, RedisLearnedPatternStore>();
```

---

## Additional Documentation

- [User-Agent Detection](user-agent-detection.md) - Detailed UA pattern matching
- [Header Detection](header-detection.md) - HTTP header anomaly analysis
- [IP Detection](ip-detection.md) - Datacenter and cloud IP detection
- [Version Age Detection](version-age-detection.md) - Browser/OS version staleness
- [Security Tools Detection](security-tools-detection.md) - Penetration testing tool detection
- [Project Honeypot](project-honeypot.md) - HTTP:BL IP reputation checking
- [Behavioral Analysis](behavioral-analysis.md) - Request pattern monitoring
- [Client-Side Fingerprinting](client-side-fingerprinting.md) - Browser integrity checks
- [AI Detection](ai-detection.md) - Heuristic and Ollama AI classification
- [Learning and Reputation](learning-and-reputation.md) - Pattern learning, forgetting, and blackboard architecture
- [YARP Integration](yarp-integration.md) - Reverse proxy integration
- [Configuration](configuration.md) - Full configuration reference
- [Telemetry and Metrics](telemetry-and-metrics.md) - Observability
