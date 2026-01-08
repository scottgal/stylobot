# LLM-Powered Bot Traffic Generator

## Overview

An intelligent traffic generator that uses Ollama LLMs to create realistic bot and human traffic patterns based on the bot detection system's knowledge base (honeypot IPs, bot UAs, scraper patterns, behavioral signatures).

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│           LLM Traffic Generator                         │
│                                                         │
│  ┌──────────────┐    ┌──────────────┐   ┌───────────┐ │
│  │ Data Sources │───>│ LLM Planner  │──>│ Executor  │ │
│  │              │    │              │   │           │ │
│  │ • Honeypot   │    │ • Generate   │   │ • HTTP    │ │
│  │ • Bot UAs    │    │   traffic    │   │ • Headers │ │
│  │ • Scrapers   │    │   specs      │   │ • Timing  │ │
│  │ • Behaviors  │    │ • Vary       │   │ • Cookies │ │
│  │              │    │   patterns   │   │           │ │
│  └──────────────┘    └──────────────┘   └───────────┘ │
│                                                         │
│  ┌──────────────┐    ┌──────────────┐   ┌───────────┐ │
│  │   Analyzer   │<───│  Collector   │<──│ Response  │ │
│  │              │    │              │   │           │ │
│  │ • Detection  │    │ • Results    │   │ • Status  │ │
│  │   rates      │    │ • Patterns   │   │ • Headers │ │
│  │ • False pos  │    │ • Metrics    │   │ • Timing  │ │
│  │ • Adapt      │    │              │   │           │ │
│  └──────────────┘    └──────────────┘   └───────────┘ │
└─────────────────────────────────────────────────────────┘
```

## Data Sources

### 1. Honeypot IP Database
Extract from Project Honeypot integration:
- Known malicious IPs
- Threat scores
- Visitor types (harvester, spammer, suspicious)
- Activity timestamps

### 2. Bot User-Agent Patterns
Extract from `BotSignatures.cs`:
- Good bots (Googlebot, Bingbot, etc.)
- Bad bots (scrapers, harvesters)
- Automation frameworks (Puppeteer, Selenium)
- Security scanners (sqlmap, nikto)

### 3. Scraper Behavioral Patterns
Extract from `BehavioralDetector.cs`:
- Request rates
- Cookie behavior
- Referrer patterns
- Path traversal
- Session characteristics

### 4. HTTP Header Fingerprints
Extract from `HeaderDetector.cs`:
- Accept headers
- Language preferences
- Connection patterns
- Security headers (Sec-Fetch-*)

### 5. Browser Fingerprints
Extract from client-side detection:
- Screen resolutions
- Timezone data
- Plugin lists
- WebDriver flags
- Automation markers

## LLM Planner

Uses Ollama to generate diverse, realistic traffic patterns.

### Prompt Template

```
You are a bot traffic simulator. Generate realistic HTTP request patterns based on these bot detection data sources:

# Known Bot User Agents
{bot_user_agents}

# Scraper Behaviors
{scraper_behaviors}

# Honeypot Threat IPs
{honeypot_ips}

# Browser Fingerprints
{fingerprints}

# Task
Generate {count} {bot_type} traffic patterns that would:
- {detection_goal}
- Vary request timing by {timing_variation}%
- Use {behavior_complexity} behavioral complexity

Output as JSON array of traffic specs:
[
  {
    "type": "bot|human|hybrid",
    "userAgent": "...",
    "ip": "...",
    "fingerprint": {...},
    "behavior": {
      "requestCount": 10,
      "requestInterval": 2.5,
      "pathPattern": "sequential|random|focused",
      "cookies": true|false,
      "referrer": true|false
    },
    "headers": {...},
    "timing": {
      "thinkTime": 1000,
      "variance": 500
    },
    "probability": {
      "shouldBeDetected": 0.95,
      "expectedConfidence": 0.85
    }
  }
]
```

### Bot Type Presets

```csharp
public enum BotTrafficType
{
    /// <summary>Well-behaved search engine crawler</summary>
    GoodBot,

    /// <summary>Aggressive scraper with obvious patterns</summary>
    ObviousScraper,

    /// <summary>Stealthy scraper mimicking human behavior</summary>
    StealthyScraper,

    /// <summary>Security scanner (sqlmap, nikto)</summary>
    SecurityScanner,

    /// <summary>Headless browser automation</summary>
    HeadlessBrowser,

    /// <summary>Normal human user</summary>
    HumanUser,

    /// <summary>Mixed bot/human characteristics</summary>
    HybridTraffic,

    /// <summary>Known malicious IP from honeypot</summary>
    HoneypotThreat,

    /// <summary>Content theft bot</summary>
    ContentThief,

    /// <summary>Form spam bot</summary>
    FormSpammer
}
```

### Detection Goals

```csharp
public enum DetectionGoal
{
    /// <summary>Should be detected with high confidence</summary>
    ShouldDetect,

    /// <summary>Should NOT be detected (false positive test)</summary>
    ShouldNotDetect,

    /// <summary>Edge case - uncertain detection</summary>
    EdgeCase,

    /// <summary>Gradually evolve from human to bot</summary>
    GradualTransition,

    /// <summary>Stress test with high volume</summary>
    StressTest
}
```

## Implementation

### Core Classes

#### TrafficGeneratorOptions

```csharp
public class TrafficGeneratorOptions
{
    /// <summary>Enable LLM-powered traffic generation</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Ollama endpoint</summary>
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>Model for traffic generation (needs good reasoning)</summary>
    public string Model { get; set; } = "qwen2.5:14b";

    /// <summary>Target URL for generated traffic</summary>
    public string TargetUrl { get; set; } = "http://localhost:5000";

    /// <summary>Concurrent request limit</summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>Request timeout (ms)</summary>
    public int TimeoutMs { get; set; } = 30000;

    /// <summary>Collect detection results for analysis</summary>
    public bool CollectResults { get; set; } = true;

    /// <summary>Adapt patterns based on detection rates</summary>
    public bool AdaptiveGeneration { get; set; } = true;

    /// <summary>Temperature for LLM generation (higher = more creative)</summary>
    public double Temperature { get; set; } = 0.8;

    /// <summary>Include data source context in prompts</summary>
    public bool IncludeDataSources { get; set; } = true;
}
```

#### TrafficSpec

```csharp
public class TrafficSpec
{
    public string Type { get; set; } = "bot"; // bot|human|hybrid
    public string UserAgent { get; set; } = "";
    public string? IP { get; set; }
    public BrowserFingerprint? Fingerprint { get; set; }
    public BehaviorPattern Behavior { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();
    public TimingPattern Timing { get; set; } = new();
    public ExpectedDetection Probability { get; set; } = new();
    public List<string> Paths { get; set; } = new();
}

public class BehaviorPattern
{
    public int RequestCount { get; set; } = 10;
    public double RequestInterval { get; set; } = 2.5; // seconds
    public string PathPattern { get; set; } = "random"; // sequential|random|focused
    public bool SendCookies { get; set; }
    public bool SendReferrer { get; set; }
    public double PathDiversity { get; set; } = 0.5; // 0-1
}

public class TimingPattern
{
    public int ThinkTime { get; set; } = 1000; // ms
    public int Variance { get; set; } = 500; // ms
    public bool Realistic { get; set; } = true;
}

public class ExpectedDetection
{
    public double ShouldBeDetected { get; set; } = 0.5; // 0-1
    public double ExpectedConfidence { get; set; } = 0.75; // 0-1
    public string? ExpectedBotType { get; set; }
}
```

#### LlmTrafficGenerator

```csharp
public class LlmTrafficGenerator
{
    private readonly ILogger<LlmTrafficGenerator> _logger;
    private readonly TrafficGeneratorOptions _options;
    private readonly HttpClient _httpClient;
    private readonly OllamaApiClient _ollama;
    private readonly DataSourceCollector _dataSources;

    public async Task<List<TrafficSpec>> GenerateTrafficPatternsAsync(
        BotTrafficType botType,
        DetectionGoal goal,
        int count,
        CancellationToken ct = default)
    {
        // 1. Collect data sources
        var context = await _dataSources.CollectContextAsync(ct);

        // 2. Build LLM prompt
        var prompt = BuildPrompt(botType, goal, count, context);

        // 3. Generate with Ollama
        var response = await _ollama.GenerateAsync(new GenerateRequest
        {
            Model = _options.Model,
            Prompt = prompt,
            Temperature = _options.Temperature,
            Format = "json"
        }, ct);

        // 4. Parse JSON response
        var specs = JsonSerializer.Deserialize<List<TrafficSpec>>(response.Response);

        // 5. Validate and enhance
        return specs.Select(EnhanceSpec).ToList();
    }

    public async Task<TrafficExecutionResult> ExecuteTrafficAsync(
        List<TrafficSpec> specs,
        CancellationToken ct = default)
    {
        var results = new List<TrafficResult>();
        var semaphore = new SemaphoreSlim(_options.MaxConcurrency);

        var tasks = specs.Select(async spec =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await ExecuteSingleSpecAsync(spec, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        results.AddRange(await Task.WhenAll(tasks));

        return new TrafficExecutionResult
        {
            TotalRequests = specs.Count,
            SuccessfulRequests = results.Count(r => r.Success),
            DetectionResults = results.Select(r => r.Detection).ToList(),
            AverageConfidence = results.Average(r => r.Detection?.Confidence ?? 0),
            DetectionRate = results.Count(r => r.Detection?.IsBot == true) / (double)results.Count
        };
    }

    private async Task<TrafficResult> ExecuteSingleSpecAsync(
        TrafficSpec spec,
        CancellationToken ct)
    {
        var result = new TrafficResult { Spec = spec };
        var cookieContainer = new CookieContainer();
        string? lastUrl = null;

        for (int i = 0; i < spec.Behavior.RequestCount; i++)
        {
            // Select path
            var path = SelectPath(spec, i);
            var url = $"{_options.TargetUrl}{path}";

            // Build request
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(spec.UserAgent);

            // Add custom headers
            foreach (var (key, value) in spec.Headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }

            // Add referrer
            if (spec.Behavior.SendReferrer && lastUrl != null)
            {
                request.Headers.Referrer = new Uri(lastUrl);
            }

            // Add simulator headers for fingerprint/IP override
            if (spec.Fingerprint != null)
            {
                request.Headers.Add("X-Bot-Sim-Mode", "learning");
                request.Headers.Add("X-Bot-Sim-Fingerprint",
                    JsonSerializer.Serialize(spec.Fingerprint));
            }

            if (spec.IP != null)
            {
                request.Headers.Add("X-Bot-Sim-IP", spec.IP);
            }

            try
            {
                // Execute request
                using var response = await _httpClient.SendAsync(request, ct);

                // Collect cookies if enabled
                if (spec.Behavior.SendCookies)
                {
                    // Extract Set-Cookie headers and store
                }

                // Extract detection result from response headers
                var detection = ExtractDetectionResult(response);
                result.Requests.Add(new RequestResult
                {
                    Url = url,
                    StatusCode = (int)response.StatusCode,
                    Detection = detection,
                    Timestamp = DateTime.UtcNow
                });

                lastUrl = url;

                // Think time with variance
                if (i < spec.Behavior.RequestCount - 1)
                {
                    var delay = spec.Timing.ThinkTime +
                                Random.Shared.Next(-spec.Timing.Variance, spec.Timing.Variance);
                    await Task.Delay(Math.Max(0, delay), ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Request failed for spec {Type}", spec.Type);
                result.Errors.Add(ex.Message);
            }
        }

        result.Success = result.Errors.Count == 0;
        result.Detection = result.Requests.LastOrDefault()?.Detection;

        return result;
    }

    private DetectionResult? ExtractDetectionResult(HttpResponseMessage response)
    {
        // Parse from response headers added by simulator
        if (!response.Headers.TryGetValues("X-Bot-Detection-Result", out var values))
            return null;

        var json = values.FirstOrDefault();
        if (string.IsNullOrEmpty(json))
            return null;

        return JsonSerializer.Deserialize<DetectionResult>(json);
    }
}
```

#### DataSourceCollector

```csharp
public class DataSourceCollector
{
    private readonly BotListFetcher _botListFetcher;
    private readonly IMemoryCache _cache;

    public async Task<DataSourceContext> CollectContextAsync(CancellationToken ct)
    {
        var context = new DataSourceContext();

        // Collect bot UAs
        var botPatterns = await _botListFetcher.GetBotPatternsAsync(ct);
        context.BotUserAgents = botPatterns
            .Take(20)
            .Select(p => p.Pattern)
            .ToList();

        // Collect good bots
        var crawlers = await _botListFetcher.GetCrawlerUserAgentsAsync(ct);
        context.GoodBotUserAgents = crawlers
            .Take(10)
            .Select(c => c.UserAgent)
            .ToList();

        // Collect scraper patterns
        context.ScraperBehaviors = new List<string>
        {
            "High request rate (>10 req/min)",
            "No cookies maintained",
            "No referrer on subsequent requests",
            "Sequential path traversal",
            "Low path diversity (<0.3)"
        };

        // Collect common fingerprints
        context.BrowserFingerprints = GetCommonFingerprints();

        // Collect honeypot IPs (if available)
        context.HoneypotIPs = new List<string>
        {
            "203.0.113.0/24", // Example ranges
            "198.51.100.0/24"
        };

        return context;
    }

    private List<BrowserFingerprint> GetCommonFingerprints()
    {
        return new List<BrowserFingerprint>
        {
            // Real Chrome
            new BrowserFingerprint
            {
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                Timezone = "America/Los_Angeles",
                Languages = new[] { "en-US", "en" },
                Platform = "Win32",
                Plugins = new[] { "PDF Viewer", "Chrome PDF Viewer" },
                Webdriver = false,
                Headless = false
            },
            // Headless Chrome
            new BrowserFingerprint
            {
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                Timezone = "America/Los_Angeles",
                Languages = new[] { "en-US" },
                Platform = "Linux x86_64",
                Plugins = Array.Empty<string>(),
                Webdriver = true,
                Headless = true
            }
        };
    }
}
```

## Usage Examples

### Example 1: Generate Obvious Scraper Traffic

```csharp
var generator = services.GetRequiredService<LlmTrafficGenerator>();

// Generate 10 obvious scraper patterns
var specs = await generator.GenerateTrafficPatternsAsync(
    BotTrafficType.ObviousScraper,
    DetectionGoal.ShouldDetect,
    count: 10);

// Execute and analyze
var result = await generator.ExecuteTrafficAsync(specs);

Console.WriteLine($"Detection Rate: {result.DetectionRate:P}");
Console.WriteLine($"Avg Confidence: {result.AverageConfidence:F2}");
Console.WriteLine($"Expected: {specs.Average(s => s.Probability.ShouldBeDetected):F2}");
```

### Example 2: Test False Positive Rate

```csharp
// Generate realistic human traffic that should NOT be detected
var humanSpecs = await generator.GenerateTrafficPatternsAsync(
    BotTrafficType.HumanUser,
    DetectionGoal.ShouldNotDetect,
    count: 50);

var result = await generator.ExecuteTrafficAsync(humanSpecs);

// Should be close to 0%
var falsePositiveRate = result.DetectionRate;
Console.WriteLine($"False Positive Rate: {falsePositiveRate:P}");
```

### Example 3: Adaptive Stealthy Scraper

```csharp
var options = new TrafficGeneratorOptions
{
    AdaptiveGeneration = true, // Learn from detection results
    Temperature = 0.9 // High creativity
};

// Initial attempt - will likely be detected
var round1 = await generator.GenerateTrafficPatternsAsync(
    BotTrafficType.StealthyScraper,
    DetectionGoal.EdgeCase,
    count: 10);

var result1 = await generator.ExecuteTrafficAsync(round1);
Console.WriteLine($"Round 1 Detection Rate: {result1.DetectionRate:P}");

// Adaptive round - learns from failures
var round2 = await generator.GenerateAdaptiveTrafficAsync(
    result1,
    BotTrafficType.StealthyScraper,
    count: 10);

var result2 = await generator.ExecuteTrafficAsync(round2);
Console.WriteLine($"Round 2 Detection Rate: {result2.DetectionRate:P}");
```

### Example 4: Stress Test with Mixed Traffic

```csharp
// Generate realistic mix of bots and humans
var mixedSpecs = new List<TrafficSpec>();

// 60% human
mixedSpecs.AddRange(await generator.GenerateTrafficPatternsAsync(
    BotTrafficType.HumanUser,
    DetectionGoal.ShouldNotDetect,
    count: 60));

// 30% good bots
mixedSpecs.AddRange(await generator.GenerateTrafficPatternsAsync(
    BotTrafficType.GoodBot,
    DetectionGoal.ShouldNotDetect,
    count: 30));

// 10% bad bots
mixedSpecs.AddRange(await generator.GenerateTrafficPatternsAsync(
    BotTrafficType.ObviousScraper,
    DetectionGoal.ShouldDetect,
    count: 10));

// Shuffle and execute
mixedSpecs = mixedSpecs.OrderBy(_ => Random.Shared.Next()).ToList();
var result = await generator.ExecuteTrafficAsync(mixedSpecs);

// Analyze by type
var byType = result.DetectionResults
    .GroupBy(d => d.BotType)
    .Select(g => new { Type = g.Key, Count = g.Count() });

foreach (var group in byType)
{
    Console.WriteLine($"{group.Type}: {group.Count}");
}
```

## CLI Tool

```bash
# Generate and execute traffic
dotnet run --project BotDetection.TrafficGen -- generate \
  --type StealthyScraper \
  --goal ShouldDetect \
  --count 20 \
  --target http://localhost:5000 \
  --execute

# Adaptive training mode
dotnet run --project BotDetection.TrafficGen -- adaptive \
  --type HybridTraffic \
  --rounds 5 \
  --target http://localhost:5000 \
  --report adaptive-results.json

# Stress test
dotnet run --project BotDetection.TrafficGen -- stress \
  --mix "human:60,goodbot:30,scraper:10" \
  --duration 5m \
  --target http://localhost:5000 \
  --concurrency 50
```

## Integration with Simulator

The LLM traffic generator integrates with the 3-mode simulator:

1. **Signature Injection**: LLM generates signature combinations to test coordinator
2. **Learning Mode**: LLM generates training data for detector calibration
3. **Policy Execution**: LLM creates edge cases to test policy transitions

```csharp
// Generate signatures for testing coordinator
var signatureSpecs = await generator.GenerateSignaturePatternsAsync(
    count: 50,
    includeEdgeCases: true);

foreach (var spec in signatureSpecs)
{
    var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
    request.Headers.Add("X-Bot-Sim-Mode", "signature");
    request.Headers.Add("X-Bot-Sim-Signatures", JsonSerializer.Serialize(spec));

    var response = await httpClient.SendAsync(request);
    var result = await response.Content.ReadFromJsonAsync<SimulatorResponse>();

    // Analyze signature matching
}
```

## Benefits

1. **Realistic Testing**: Traffic patterns based on real bot detection data
2. **Coverage**: LLM generates diverse edge cases automatically
3. **Adaptive**: Learns from detection results to evolve patterns
4. **Scale**: Generate thousands of unique patterns quickly
5. **Training**: Build datasets for detector calibration
6. **Regression**: Validate changes don't break existing detection

## Next Steps

1. Implement core traffic generator classes
2. Add Ollama integration for pattern generation
3. Create data source collector
4. Build CLI tool
5. Add adaptive learning loop
6. Create visualization dashboard
7. Write comprehensive tests
