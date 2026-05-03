# Response Detection: Request-Side Triggers

## Overview

Response analysis can be **pre-configured during request detection** by early detectors (Priority 0-50). This allows the
response coordinator to be ready and waiting BEFORE the response is even generated, enabling:

1. **Streaming analysis**: Analyze response body as it's generated
2. **Adaptive thoroughness**: High-risk requests get deeper analysis
3. **Selective activation**: Only analyze responses that need it (performance)
4. **Early preparation**: Response coordinator spins up detectors while request is processing

## Architecture

```
Request Timeline:
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
│ T=0ms: Request arrives
│ T=5ms: IpDetector runs (Priority=0, EARLY)
│        ├─> Detects datacenter IP
│        └─> TRIGGERS: ResponseAnalysis(thoroughness=Thorough)
│
│ T=10ms: UserAgentDetector runs (Priority=0, EARLY)
│         ├─> Detects suspicious UA pattern
│         └─> TRIGGERS: ResponseAnalysis(thoroughness=Standard)
│
│ T=15ms: PathDetector runs (Priority=10)
│         ├─> Detects honeypot path: /.git/config
│         └─> TRIGGERS: ResponseAnalysis(mode=Inline, streaming=true)
│
│ *** ResponseAnalysisContext now configured ***
│     - EnableAnalysis: true
│     - Mode: Inline (upgraded from Async by PathDetector)
│     - Thoroughness: Deep (highest requested)
│     - EnableStreaming: true
│     - TriggerSignals: {ip.datacenter, ua.suspicious, honeypot.hit}
│
│ T=20-80ms: Other detectors run, contribute to blackboard
│
│ T=100ms: Response generation begins
│          ResponseCoordinator reads ResponseAnalysisContext
│          ├─> Spins up DEEP analysis detectors
│          ├─> Enables streaming body analysis
│          └─> Waits inline (will analyze before sending)
│
│ T=120ms: Response body generated, streamed to analyzer
│          ├─> Pattern matching runs in real-time
│          ├─> Score computed: 0.92 (honeypot + datacenter + 404)
│          └─> Feedback to heuristic: clientX → 0.92
│
│ T=125ms: Response sent to client
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

## ResponseAnalysisContext

**Created by**: First detector that wants response analysis
**Stored in**: `HttpContext.Items["BotDetection.ResponseAnalysisContext"]`
**Read by**: Response coordinator and middleware

### Properties

```csharp
public sealed class ResponseAnalysisContext
{
    // Core settings
    public bool EnableAnalysis { get; set; }
    public ResponseAnalysisMode Mode { get; set; } // Async or Inline
    public bool EnableStreaming { get; set; }
    public ResponseAnalysisThoroughness Thoroughness { get; set; }

    // Metadata
    public string ClientId { get; init; }
    public double RequestBotProbability { get; set; }
    public string? TriggeringDetector { get; set; }
    public string? TriggerReason { get; set; }
    public int Priority { get; set; }

    // Cross-detector signals
    public Dictionary<string, object> TriggerSignals { get; set; }
    public HashSet<string> EnabledDetectors { get; set; }
}
```

### Thoroughness Levels

```csharp
public enum ResponseAnalysisThoroughness
{
    Minimal,   // Status code only (~0.1ms)
    Standard,  // Status + basic patterns (~1-2ms)
    Thorough,  // All detectors, full patterns (~5-10ms)
    Deep       // Streaming, semantic, LLM if enabled (~20-50ms)
}
```

## Detector Integration

### Example 1: IpDetector Triggers Response Analysis

```csharp
public class IpDetector : ContributingDetectorBase, IResponseAnalysisTrigger
{
    public override string Name => "IpDetector";
    public override int Priority => 0; // EARLY execution

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken)
    {
        var ip = state.ClientIp;
        var datacenterInfo = await _ipService.CheckDatacenterAsync(ip);

        if (datacenterInfo.IsDatacenter)
        {
            // TRIGGER RESPONSE ANALYSIS
            state.TriggerDatacenterResponseAnalysis(
                detectorName: Name,
                datacenterName: datacenterInfo.Name);

            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Network",
                ConfidenceDelta = 0.3,
                Weight = 1.0,
                Reason = $"Datacenter IP: {datacenterInfo.Name}",
                // ... other fields
            });
        }

        return None();
    }
}
```

**What happens**:

1. IpDetector runs FIRST (Priority=0)
2. Detects datacenter IP
3. Calls `state.TriggerDatacenterResponseAnalysis(...)`
4. ResponseAnalysisContext is created in `HttpContext.Items`
5. Sets: `Thoroughness=Thorough`, `Mode=Async`
6. Later, response coordinator reads this and applies thorough analysis

### Example 2: PathDetector Triggers Honeypot Analysis

```csharp
public class PathDetector : ContributingDetectorBase
{
    public override string Name => "PathDetector";
    public override int Priority => 10; // Early-ish

    private readonly HashSet<string> _honeypotPaths = new()
    {
        "/__test-hp", "/.git/", "/.env", "/wp-admin/"
    };

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken)
    {
        var path = state.Path;

        // Check for honeypot
        var honeypotHit = _honeypotPaths.FirstOrDefault(hp =>
            path.StartsWith(hp, StringComparison.OrdinalIgnoreCase));

        if (honeypotHit != null)
        {
            // TRIGGER DEEP INLINE RESPONSE ANALYSIS WITH STREAMING
            state.TriggerHoneypotResponseAnalysis(
                detectorName: Name,
                honeypotPath: honeypotHit);

            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Path",
                ConfidenceDelta = 0.8, // VERY high confidence
                Weight = 1.5,
                Reason = $"Honeypot path accessed: {honeypotHit}",
                // ... other fields
            });
        }

        return None();
    }
}
```

**What happens**:

1. PathDetector runs early (Priority=10)
2. Detects honeypot path `/.git/config`
3. Calls `state.TriggerHoneypotResponseAnalysis(...)`
4. ResponseAnalysisContext is UPDATED (if exists) or CREATED
5. Sets: `Thoroughness=Deep`, `Mode=Inline`, `EnableStreaming=true`, `Priority=100`
6. Response coordinator will analyze response body AS IT'S GENERATED
7. Can block or modify response before sending

### Example 3: HeuristicDetector Triggers Based on Score

```csharp
public class HeuristicDetector : ContributingDetectorBase
{
    public override string Name => "HeuristicDetector";
    public override int Priority => 50; // Mid-wave

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken)
    {
        var clientId = GetClientId(state);
        var priorScore = await _heuristicStore.GetScoreAsync(clientId);

        if (priorScore > 0.6)
        {
            // High prior from previous requests - trigger response analysis
            state.TriggerHighRiskResponseAnalysis(
                detectorName: Name,
                riskScore: priorScore);

            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Heuristic",
                ConfidenceDelta = priorScore * 0.5, // Moderate contribution
                Weight = 0.8,
                Reason = $"Prior bot score: {priorScore:F2}",
                // ... other fields
            });
        }

        return None();
    }
}
```

**What happens**:

1. HeuristicDetector runs mid-wave (Priority=50)
2. Looks up prior score from previous requests (learned from response analysis!)
3. If score > 0.6, triggers response analysis
4. Thoroughness adapts to score:
    - 0.6-0.8 → Thorough, Async
    - 0.8+ → Deep, Inline, Streaming
5. Creates feedback loop: Response analysis → Heuristic → Triggers more response analysis

## Helper Methods

### Quick Triggers

```csharp
// Honeypot hit (highest priority, inline, streaming)
state.TriggerHoneypotResponseAnalysis(
    detectorName: "PathDetector",
    honeypotPath: "/.git/config");

// Datacenter IP (thorough, async)
state.TriggerDatacenterResponseAnalysis(
    detectorName: "IpDetector",
    datacenterName: "AWS-US-East-1");

// Suspicious UA (standard, async)
state.TriggerSuspiciousUaResponseAnalysis(
    detectorName: "UserAgentDetector",
    reason: "Missing sec-ch-ua headers");

// High risk score (adaptive thoroughness)
state.TriggerHighRiskResponseAnalysis(
    detectorName: "HeuristicDetector",
    riskScore: 0.85);
```

### Custom Trigger

```csharp
state.TriggerResponseAnalysis(
    detectorName: "MyCustomDetector",
    reason: "Suspicious pattern detected",
    thoroughness: ResponseAnalysisThoroughness.Thorough,
    mode: ResponseAnalysisMode.Inline,
    enableStreaming: true,
    priority: 75,
    additionalSignals: new Dictionary<string, object>
    {
        ["custom.pattern"] = "sql_injection_attempt",
        ["custom.severity"] = "high"
    });
```

## Multiple Triggers: Resolution Logic

When multiple detectors trigger response analysis, the context is **upgraded** to the highest requested level:

```csharp
// Detector 1: Standard, Async
state.TriggerResponseAnalysis(..., thoroughness: Standard, mode: Async);

// Detector 2: Thorough, Async
state.TriggerResponseAnalysis(..., thoroughness: Thorough, mode: Async);

// Detector 3: Thorough, Inline
state.TriggerResponseAnalysis(..., thoroughness: Thorough, mode: Inline);

// RESULT:
// - Thoroughness: Thorough (highest)
// - Mode: Inline (any Inline upgrades from Async)
// - EnableStreaming: true (if any requested)
// - Priority: 100 (maximum of all)
// - TriggerSignals: MERGED from all detectors
```

## Response Coordinator Integration

The response coordinator reads the context and adapts:

```csharp
public class ResponseDetectionMiddleware
{
    public async Task InvokeAsync(HttpContext context)
    {
        // ... request processing ...

        // Check if response analysis was triggered
        var analysisContext = ResponseAnalysisContext.TryGet(context);

        if (analysisContext?.EnableAnalysis == true)
        {
            if (analysisContext.Mode == ResponseAnalysisMode.Inline)
            {
                // INLINE MODE: Analyze before sending
                if (analysisContext.EnableStreaming)
                {
                    // Stream response through analyzer
                    await AnalyzeResponseStreamingAsync(context, analysisContext);
                }
                else
                {
                    // Buffer response, analyze, then send
                    await AnalyzeResponseBufferedAsync(context, analysisContext);
                }
            }
            else
            {
                // ASYNC MODE: Send response, analyze after
                await SendResponseAsync(context);
                _ = Task.Run(() => AnalyzeResponseAsync(context, analysisContext));
            }
        }
        else
        {
            // No analysis triggered - normal flow
            await _next(context);
        }
    }
}
```

## Streaming Response Analysis

For Deep thoroughness with streaming enabled:

```csharp
private async Task AnalyzeResponseStreamingAsync(
    HttpContext context,
    ResponseAnalysisContext analysisContext)
{
    var originalBodyStream = context.Response.Body;

    using var analyzingStream = new StreamingResponseAnalyzer(
        originalBodyStream,
        analysisContext,
        _responseCoordinator,
        _logger);

    // Replace response stream with analyzer
    context.Response.Body = analyzingStream;

    try
    {
        // Generate response - analyzer captures it in real-time
        await _next(context);
    }
    finally
    {
        // Ensure analyzer completes
        await analyzingStream.FlushAsync();

        // Get analysis result
        var result = analyzingStream.GetResult();

        if (result.ResponseScore > 0.8)
        {
            _logger.LogWarning(
                "High response score detected inline: {Score:F2} for {ClientId}",
                result.ResponseScore,
                analysisContext.ClientId);

            // Could block/redirect here if needed
            // (but response already sent in this case)
        }

        context.Response.Body = originalBodyStream;
    }
}
```

## Performance Impact

### Minimal Thoroughness

- **Latency**: < 0.5ms
- **Detectors**: Status code only
- **Use case**: Low-risk requests, high-volume APIs

### Standard Thoroughness (Default)

- **Latency**: 1-2ms (async), 0ms added (async)
- **Detectors**: Status + basic patterns
- **Use case**: Most requests

### Thorough Thoroughness

- **Latency**: 5-10ms (inline), 0ms added (async)
- **Detectors**: All response detectors, full patterns
- **Use case**: Medium-risk requests (datacenter IPs, suspicious UAs)

### Deep Thoroughness

- **Latency**: 20-50ms (inline), 0ms added (async)
- **Detectors**: Streaming, semantic content, LLM if enabled
- **Use case**: High-risk requests (honeypots, known bad actors)

## Configuration

### Enable Early Triggering

```json
{
  "BotDetection": {
    "ResponseCoordinator": {
      "EnableEarlyTriggering": true,
      "DefaultThoroughness": "Standard",
      "AllowInlineMode": true,
      "AllowStreamingMode": true,

      "ThoroughnessThresholds": {
        "MinimalMaxLatency": 0.5,
        "StandardMaxLatency": 2.0,
        "ThoroughMaxLatency": 10.0,
        "DeepMaxLatency": 50.0
      },

      "AutoUpgradeRules": {
        "HoneypotPathsToDeep": true,
        "HighRiskToThorough": true,
        "DatacenterIpsToThorough": true
      }
    }
  }
}
```

## Example: Full Request Flow

### Scenario: WordPress Scanner Hits Honeypot

```
1. Request: GET /.git/config
   Client: 185.220.101.50 (Tor exit node)
   UA: Mozilla/5.0 (compatible; SomeBot/1.0)

2. T=0ms: Request starts
   ├─> BlackboardOrchestrator initializes
   └─> ResponseAnalysisContext created (empty)

3. T=2ms: IpDetector (Priority=0)
   ├─> Detects: Tor exit node (datacenter category)
   ├─> Contribution: +0.4 confidence
   └─> TRIGGERS: Thoroughness=Thorough, Mode=Async

4. T=4ms: UserAgentDetector (Priority=0)
   ├─> Detects: Suspicious bot UA
   ├─> Contribution: +0.3 confidence
   └─> TRIGGERS: Thoroughness=Standard (no upgrade)

5. T=6ms: PathDetector (Priority=10)
   ├─> Detects: HONEYPOT PATH /.git/config
   ├─> Contribution: +0.8 confidence (VERY HIGH)
   └─> TRIGGERS: Thoroughness=Deep, Mode=Inline, Streaming=true, Priority=100

6. ResponseAnalysisContext NOW:
   {
     "EnableAnalysis": true,
     "Mode": "Inline",
     "EnableStreaming": true,
     "Thoroughness": "Deep",
     "Priority": 100,
     "TriggerSignals": {
       "ip.datacenter": "TorExitNode",
       "ua.suspicious": true,
       "honeypot.hit": true,
       "honeypot.path": "/.git/config"
     },
     "TriggeringDetectors": ["IpDetector", "UserAgentDetector", "PathDetector"],
     "RequestBotProbability": 0.92
   }

7. T=10-50ms: Other detectors run
   ├─> Current aggregated score: 0.92 (VERY HIGH)
   └─> All contributions recorded

8. T=60ms: Response generation begins
   ├─> Middleware reads ResponseAnalysisContext
   ├─> Mode=Inline → Response will be analyzed before sending
   ├─> EnableStreaming=true → Use streaming analyzer
   └─> ResponseCoordinator spins up DEEP detectors

9. T=65ms: Response generated: 404 Not Found
   ├─> StreamingResponseAnalyzer captures body
   ├─> Pattern matching: No sensitive data (just 404 page)
   ├─> Status code: 404
   ├─> Path: /.git/config (honeypot)
   └─> Score computed: 0.95 (honeypot + 404 + high request score)

10. T=70ms: Analysis complete
    ├─> Feedback to heuristic: clientX → 0.95
    ├─> Client flagged for aggressive blocking
    └─> Response FINALLY sent to client

11. Next request from same client:
    ├─> Heuristic returns prior: 0.95
    ├─> Policy: Block immediately (no response needed)
    └─> Total latency: < 1ms (blocked at gate)
```

## Summary

Request-side triggers enable:

1. **Predictive Analysis**: Response coordinator is ready BEFORE response generates
2. **Adaptive Thoroughness**: High-risk requests get deeper analysis
3. **Zero Latency (Async)**: Most requests have zero added latency
4. **Inline Blocking**: Honeypots and critical paths can block before sending
5. **Streaming Capability**: Deep analysis can inspect response as it generates
6. **Feedback Loop**: Response scores improve request-side detection over time

The system automatically escalates from cheap to expensive analysis based on early request signals, ensuring optimal
performance while catching sophisticated bots.
