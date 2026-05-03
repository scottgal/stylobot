# Extensibility Guide

This guide explains how to extend the bot detection library with custom components.

## Architecture Overview

The library uses a pluggable architecture with three main extension points:

```
┌─────────────────────────────────────────────────────────────────┐
│                    Extension Points                              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌─────────────┐     ┌─────────────────┐     ┌───────────────┐  │
│  │  Detectors  │ --> │ Detection Policy │ --> │ Action Policy │  │
│  │   (WHAT)    │     │   (WHEN/HOW)     │     │    (THEN)     │  │
│  └─────────────┘     └─────────────────┘     └───────────────┘  │
│        │                     │                       │          │
│        v                     v                       v          │
│  IContributingDetector  IPolicyRegistry     IActionPolicyRegistry│
│                         IPolicyEvaluator    IActionPolicy        │
│                                             IActionPolicyFactory │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## 1. Custom Detectors

Detectors analyze requests and contribute evidence to the detection process.

### Creating a Custom Detector

```csharp
using Mostlylucid.BotDetection.Orchestration;

public class GeoLocationDetector : IContributingDetector
{
    // Required: unique name
    public string Name => "GeoLocation";

    // Required: detection category
    public string Category => "Infrastructure";

    // Priority determines order (higher = earlier)
    public int Priority => 50;

    // Wave for parallel execution (1 = fast, 2 = slow, 3 = AI)
    public int Wave => 1;

    // Whether to cache results
    public bool IsCacheable => true;

    public async Task<IEnumerable<DetectionContribution>> ContributeAsync(
        HttpContext context,
        DetectionBlackboard blackboard,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        // Your detection logic here
        var clientIp = context.Connection.RemoteIpAddress;
        var geoData = await _geoService.LookupAsync(clientIp);

        if (geoData.IsVpn)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = 0.2,  // Increase bot probability
                Weight = 1.0,
                Reason = $"VPN detected: {geoData.Country}",
                Signal = "VpnDetected"
            });
        }

        if (geoData.IsDatacenter)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = 0.3,
                Weight = 1.0,
                Reason = $"Datacenter IP: {geoData.AsnName}",
                Signal = "DatacenterIp"
            });
        }

        return contributions;
    }
}
```

### Registering Custom Detectors

```csharp
// In Program.cs or Startup.cs
services.AddSingleton<IContributingDetector, GeoLocationDetector>();

// Or with dependencies
services.AddSingleton<IContributingDetector>(sp =>
{
    var geoService = sp.GetRequiredService<IGeoService>();
    var logger = sp.GetRequiredService<ILogger<GeoLocationDetector>>();
    return new GeoLocationDetector(geoService, logger);
});
```

### Detection Contributions

Each contribution affects the final bot probability:

| Property          | Type   | Description                                      |
|-------------------|--------|--------------------------------------------------|
| `DetectorName`    | string | Name of the contributing detector                |
| `Category`        | string | Category (UserAgent, Header, Ip, Behavioral, AI) |
| `ConfidenceDelta` | double | Change in bot probability (-1.0 to +1.0)         |
| `Weight`          | double | Importance multiplier (default: 1.0)             |
| `Reason`          | string | Human-readable explanation                       |
| `Signal`          | string | Signal name for event-driven logic               |
| `Metadata`        | dict   | Additional data for downstream processing        |

**ConfidenceDelta values:**

- Positive: increases bot probability
- Negative: decreases bot probability (more human-like)
- Range: -1.0 to +1.0

---

## 2. Custom Action Policies

Action policies define how to respond when a bot is detected.

### Creating a Custom Action Policy

```csharp
using Mostlylucid.BotDetection.Actions;

public class TarpitActionPolicy : IActionPolicy
{
    public string Name { get; }
    public ActionType ActionType => ActionType.Custom;

    private readonly TarpitOptions _options;

    public TarpitActionPolicy(string name, TarpitOptions options)
    {
        Name = name;
        _options = options;
    }

    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        // Calculate tarpit delay based on risk
        var delay = (int)(_options.BaseDelayMs * evidence.BotProbability);
        delay = Math.Min(delay, _options.MaxDelayMs);

        // Send data very slowly (1 byte at a time)
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html";

        var content = GenerateTarpitContent();
        foreach (var chunk in content.Chunk(_options.ChunkSize))
        {
            await context.Response.Body.WriteAsync(chunk, cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            await Task.Delay(_options.ChunkDelayMs, cancellationToken);
        }

        return new ActionResult
        {
            Continue = false,
            StatusCode = 200,
            Description = $"Tarpitted for {delay}ms",
            Metadata = new Dictionary<string, object>
            {
                ["delay"] = delay,
                ["bytesServed"] = content.Length
            }
        };
    }
}

public class TarpitOptions
{
    public int BaseDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 30000;
    public int ChunkSize { get; set; } = 1;
    public int ChunkDelayMs { get; set; } = 100;
}
```

### Creating a Factory for Configuration

```csharp
public class TarpitActionPolicyFactory : IActionPolicyFactory
{
    public ActionType ActionType => ActionType.Custom;

    public IActionPolicy Create(string name, IDictionary<string, object> options)
    {
        var tarpitOptions = new TarpitOptions();

        if (options.TryGetValue("BaseDelayMs", out var baseDelay))
            tarpitOptions.BaseDelayMs = Convert.ToInt32(baseDelay);

        if (options.TryGetValue("MaxDelayMs", out var maxDelay))
            tarpitOptions.MaxDelayMs = Convert.ToInt32(maxDelay);

        if (options.TryGetValue("ChunkSize", out var chunk))
            tarpitOptions.ChunkSize = Convert.ToInt32(chunk);

        if (options.TryGetValue("ChunkDelayMs", out var chunkDelay))
            tarpitOptions.ChunkDelayMs = Convert.ToInt32(chunkDelay);

        return new TarpitActionPolicy(name, tarpitOptions);
    }
}
```

### Registering Custom Action Policies

```csharp
// Register factory for configuration-based creation
services.AddSingleton<IActionPolicyFactory, TarpitActionPolicyFactory>();

// Or register policy directly
services.AddSingleton<IActionPolicy>(sp =>
    new TarpitActionPolicy("tarpit", new TarpitOptions { BaseDelayMs = 5000 }));
```

### Using in Configuration

```json
{
  "BotDetection": {
    "ActionPolicies": {
      "slowTarpit": {
        "Type": "Custom",
        "BaseDelayMs": 5000,
        "MaxDelayMs": 60000,
        "ChunkSize": 1,
        "ChunkDelayMs": 100
      }
    }
  }
}
```

---

## 3. Custom Challenge Handlers

Implement `IChallengeHandler` for custom challenge logic:

```csharp
public class CustomCaptchaHandler : IChallengeHandler
{
    private readonly IRecaptchaService _recaptcha;

    public CustomCaptchaHandler(IRecaptchaService recaptcha)
    {
        _recaptcha = recaptcha;
    }

    public async Task<ActionResult> HandleChallengeAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        ChallengeActionOptions options,
        CancellationToken cancellationToken = default)
    {
        // Check for reCAPTCHA response
        if (context.Request.Method == "POST" &&
            context.Request.Form.TryGetValue("g-recaptcha-response", out var response))
        {
            var valid = await _recaptcha.VerifyAsync(response);
            if (valid)
            {
                // Set token cookie
                context.Response.Cookies.Append(
                    options.TokenCookieName,
                    GenerateToken(),
                    new CookieOptions
                    {
                        Expires = DateTimeOffset.UtcNow.AddMinutes(options.TokenValidityMinutes),
                        HttpOnly = true,
                        Secure = true
                    });

                // Redirect back to original URL
                var returnUrl = context.Request.Form["returnUrl"];
                context.Response.Redirect(returnUrl);
                return ActionResult.Allowed("Challenge completed");
            }
        }

        // Render challenge form
        await RenderChallengeForm(context, evidence, options);
        return ActionResult.Blocked(403, "Challenge presented");
    }
}
```

Register:

```csharp
services.AddSingleton<IChallengeHandler, CustomCaptchaHandler>();
```

---

## 4. Custom Learning Event Handlers

Handle detection events for custom processing:

```csharp
using Mostlylucid.BotDetection.Events;

public class SlackNotificationHandler : ILearningEventHandler
{
    private readonly ISlackClient _slack;

    // Subscribe to specific events
    public IEnumerable<string> SubscribedEvents =>
        new[] { "HighRiskDetected", "NewBotPattern" };

    public async Task HandleEventAsync(
        LearningEvent learningEvent,
        CancellationToken cancellationToken = default)
    {
        if (learningEvent.EventType == "HighRiskDetected" &&
            learningEvent.BotProbability >= 0.95)
        {
            await _slack.PostMessageAsync(
                channel: "#security-alerts",
                text: $"High-risk bot detected: {learningEvent.IpAddress}\n" +
                      $"Risk: {learningEvent.BotProbability:P0}\n" +
                      $"Path: {learningEvent.RequestPath}");
        }
    }
}
```

Register:

```csharp
services.AddSingleton<ILearningEventHandler, SlackNotificationHandler>();
```

---

## 5. Custom Pattern Stores

Implement custom storage for learned patterns:

```csharp
public class RedisPatternStore : ILearnedPatternStore
{
    private readonly IDatabase _redis;

    public async Task StorePatternAsync(
        LearnedPattern pattern,
        CancellationToken cancellationToken = default)
    {
        var key = $"bot:pattern:{pattern.PatternHash}";
        var value = JsonSerializer.Serialize(pattern);
        await _redis.StringSetAsync(key, value);
    }

    public async Task<LearnedPattern?> GetPatternAsync(
        string patternHash,
        CancellationToken cancellationToken = default)
    {
        var key = $"bot:pattern:{patternHash}";
        var value = await _redis.StringGetAsync(key);
        return value.HasValue
            ? JsonSerializer.Deserialize<LearnedPattern>(value!)
            : null;
    }

    // ... other methods
}
```

Register:

```csharp
services.AddSingleton<ILearnedPatternStore, RedisPatternStore>();
```

---

## 6. Custom Signal Listeners

React to detection signals in real-time:

```csharp
public class MetricsListener : IBotSignalListener
{
    private readonly IMetrics _metrics;

    public IEnumerable<string> SubscribedSignals =>
        new[] { "BotDetected", "HumanDetected", "HighRisk" };

    public Task OnSignalAsync(
        BotSignal signal,
        CancellationToken cancellationToken = default)
    {
        switch (signal.Name)
        {
            case "BotDetected":
                _metrics.Increment("bot.detections", tags: new[]
                {
                    $"category:{signal.Metadata["category"]}",
                    $"confidence:{GetConfidenceBucket(signal.Confidence)}"
                });
                break;

            case "HighRisk":
                _metrics.Increment("bot.high_risk");
                break;
        }

        return Task.CompletedTask;
    }
}
```

---

## 7. Configuration System

The library uses a unified configuration system with typed classes that support both JSON and code configuration.

### Base Configuration Class

All configurable components inherit from `BaseComponentConfig`:

```csharp
public abstract class BaseComponentConfig
{
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }
    public int Priority { get; set; } = 0;
    public List<string>? Tags { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
```

### Action Policy Configuration

Action policies can be configured via JSON or code:

```json
{
  "BotDetection": {
    "ActionPolicies": {
      "myBlock": {
        "Type": "Block",
        "StatusCode": 403,
        "Message": "Access denied",
        "Headers": { "X-Reason": "bot-detected" },
        "Description": "Custom block for API endpoints",
        "Tags": ["api", "strict"]
      },
      "stealthThrottle": {
        "Type": "Throttle",
        "BaseDelayMs": 500,
        "MaxDelayMs": 10000,
        "JitterPercent": 0.5,
        "ScaleByRisk": true,
        "IncludeHeaders": false
      }
    }
  }
}
```

Or via code:

```csharp
services.AddBotDetection(options =>
{
    options.ActionPolicies["myThrottle"] = new ActionPolicyConfig
    {
        Type = "Throttle",
        BaseDelayMs = 1000,
        JitterPercent = 0.5,
        ScaleByRisk = true,
        Description = "Custom throttle",
        Tags = new List<string> { "premium" }
    };
});
```

### Detection Policy Configuration

Detection policies define WHAT to detect:

```json
{
  "BotDetection": {
    "Policies": {
      "strict": {
        "Description": "High-security detection",
        "FastPath": ["UserAgent", "Header", "Ip"],
        "SlowPath": ["Behavioral", "Inconsistency", "ClientSide"],
        "AiPath": ["Heuristic", "Llm"],
        "ForceSlowPath": true,
        "EscalateToAi": true,
        "AiEscalationThreshold": 0.4,
        "ImmediateBlockThreshold": 0.9,
        "ActionPolicyName": "block-hard",
        "Weights": {
          "Behavioral": 2.0,
          "Inconsistency": 2.0
        },
        "Transitions": [
          { "WhenRiskExceeds": 0.9, "ActionPolicyName": "block-hard" },
          { "WhenSignal": "VerifiedGoodBot", "Action": "Allow" }
        ],
        "Tags": ["high-security", "payments"]
      }
    }
  }
}
```

Or via code:

```csharp
options.Policies["custom"] = new DetectionPolicyConfig
{
    Description = "Custom policy for API endpoints",
    FastPath = new List<string> { "UserAgent", "Header", "Ip" },
    SlowPath = new List<string> { "Behavioral" },
    ForceSlowPath = true,
    ActionPolicyName = "throttle-stealth",
    Tags = new List<string> { "api", "rate-limited" }
};
```

### Detector Configuration

Individual detectors can also be configured:

```json
{
  "BotDetection": {
    "FastPath": {
      "FastPathDetectors": [
        {
          "Name": "User-Agent Detector",
          "Signal": "UserAgentAnalyzed",
          "ExpectedLatencyMs": 0.1,
          "Weight": 1.5,
          "Wave": 1,
          "IsCacheable": true,
          "Tags": ["fast", "required"]
        }
      ]
    }
  }
}
```

---

## 8. Extension Points Summary

| Interface                 | Purpose                     | Registration                                                 |
|---------------------------|-----------------------------|--------------------------------------------------------------|
| `IContributingDetector`   | Add detection signals       | `services.AddSingleton<IContributingDetector, MyDetector>()` |
| `IActionPolicy`           | Custom response handling    | `services.AddSingleton<IActionPolicy, MyPolicy>()`           |
| `IActionPolicyFactory`    | Create policies from config | `services.AddSingleton<IActionPolicyFactory, MyFactory>()`   |
| `IChallengeHandler`       | Custom challenge logic      | `services.AddSingleton<IChallengeHandler, MyHandler>()`      |
| `ILearningEventHandler`   | React to detection events   | `services.AddSingleton<ILearningEventHandler, MyHandler>()`  |
| `ILearnedPatternStore`    | Custom pattern storage      | `services.AddSingleton<ILearnedPatternStore, MyStore>()`     |
| `IBotSignalListener`      | Real-time signal handling   | `services.AddTransient<IBotSignalListener, MyListener>()`    |
| `IPatternReputationCache` | Custom reputation storage   | `services.AddSingleton<IPatternReputationCache, MyCache>()`  |

---

## 9. Best Practices

### 1. Use Dependency Injection

Always use constructor injection for dependencies:

```csharp
public class MyDetector : IContributingDetector
{
    private readonly ILogger<MyDetector> _logger;
    private readonly IMyService _service;

    public MyDetector(ILogger<MyDetector> logger, IMyService service)
    {
        _logger = logger;
        _service = service;
    }
}
```

### 2. Handle Exceptions Gracefully

Detection should never crash the request:

```csharp
public async Task<IEnumerable<DetectionContribution>> ContributeAsync(...)
{
    try
    {
        // Your detection logic
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Detection failed, continuing without contribution");
        return Enumerable.Empty<DetectionContribution>();
    }
}
```

### 3. Use Cancellation Tokens

Respect cancellation for responsive timeouts:

```csharp
public async Task<ActionResult> ExecuteAsync(
    HttpContext context,
    AggregatedEvidence evidence,
    CancellationToken cancellationToken = default)
{
    // Check cancellation before expensive operations
    cancellationToken.ThrowIfCancellationRequested();

    await SomeLongOperation(cancellationToken);
}
```

### 4. Cache When Possible

Mark cacheable detectors appropriately:

```csharp
public bool IsCacheable => true;  // Results can be cached by request fingerprint
```

### 5. Use Appropriate Waves

Assign detectors to the right execution wave:

- **Wave 1 (Fast)**: Pattern matching, lookups (<10ms)
- **Wave 2 (Slow)**: Behavioral analysis, API calls (10-100ms)
- **Wave 3 (AI)**: ML inference, LLM calls (>100ms)

```csharp
public int Wave => 1;  // Fast path
```

### 6. Emit Signals for Event-Driven Logic

```csharp
contributions.Add(new DetectionContribution
{
    Signal = "MyCustomSignal",  // Can trigger transitions
    // ...
});
```

---

## 10. Example: Complete Custom Detection Flow

```csharp
// 1. Custom Detector
public class FraudDetector : IContributingDetector
{
    public string Name => "Fraud";
    public string Category => "Behavioral";
    public int Priority => 100;
    public int Wave => 2;
    public bool IsCacheable => false;

    public async Task<IEnumerable<DetectionContribution>> ContributeAsync(...)
    {
        // Fraud detection logic
        return new[] { new DetectionContribution { ... } };
    }
}

// 2. Custom Action Policy
public class FraudBlockPolicy : IActionPolicy
{
    public string Name => "fraud-block";
    public ActionType ActionType => ActionType.Block;

    public async Task<ActionResult> ExecuteAsync(...) { ... }
}

// 3. Custom Event Handler
public class FraudAlertHandler : ILearningEventHandler
{
    public IEnumerable<string> SubscribedEvents => new[] { "FraudDetected" };

    public async Task HandleEventAsync(...) { ... }
}

// 4. Registration
services.AddSingleton<IContributingDetector, FraudDetector>();
services.AddSingleton<IActionPolicy, FraudBlockPolicy>();
services.AddSingleton<ILearningEventHandler, FraudAlertHandler>();
```

```json
// 5. Configuration
{
  "BotDetection": {
    "Policies": {
      "fraud-sensitive": {
        "FastPath": ["UserAgent", "Header", "Ip"],
        "SlowPath": ["Fraud", "Behavioral"],
        "ActionPolicyName": "fraud-block",
        "Transitions": [
          { "WhenSignal": "FraudDetected", "ActionPolicyName": "fraud-block" }
        ]
      }
    }
  }
}
```
