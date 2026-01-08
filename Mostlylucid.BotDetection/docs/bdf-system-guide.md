# Behavioral Description Format (BDF) System Guide

## Overview

The BDF system provides a closed-loop testing framework for bot detection. It enables you to:

1. **Capture** behavioral metrics from real traffic signatures
2. **Map** those metrics to synthetic BDF scenarios
3. **Replay** scenarios through the detection pipeline
4. **Verify** classification remains consistent
5. **Explain** behavioral patterns in human-readable format

This creates a feedback loop that validates detection accuracy and helps prevent regressions.

## Architecture

The BDF system consists of five core components:

### 1. BDF Data Models (`BdfModels.cs`)

Declarative JSON format for describing client behavior over time:

```csharp
var scenario = new BdfScenario
{
    Id = "test-periodic-bot",
    Description = "Timer-driven bot hitting API every 5 seconds",
    Client = new ClientConfig
    {
        SignatureId = "captured-from-production",
        UserAgent = "BotScanner/1.0"
    },
    Phases = new[]
    {
        new BdfPhase
        {
            Name = "main-attack",
            RequestCount = 100,
            Timing = new TimingConfig
            {
                Mode = "fixed",
                BaseRateRps = 0.2  // Every 5 seconds
            },
            Navigation = new NavigationConfig
            {
                Mode = "scanner",
                OffGraphProbability = 0.9,
                Paths = new[]
                {
                    new PathTemplate { Template = "/wp-login.php" },
                    new PathTemplate { Template = "/.git/HEAD" }
                }
            }
        }
    },
    Expectation = new ExpectationConfig
    {
        ExpectedClassification = "Bot",
        MinBotProbability = 0.8
    }
};
```

**Key Concepts:**

- **Scenarios**: Complete behavioral description with metadata and expectations
- **Phases**: Stages of the attack with distinct timing/navigation patterns
- **Timing Modes**:
    - `fixed` - Perfectly periodic (timer-driven bots)
    - `jittered` - Normally distributed delays (human-like or jittered bots)
    - `burst` - Short bursts with pauses (aggressive scrapers)
- **Navigation Modes**:
    - `ui_graph` - Follows UI links (human-like)
    - `sequential` - Deterministic path iteration (scrapers)
    - `random` - Weighted random selection (fuzzing)
    - `scanner` - Attack path discovery (security scanners)

### 2. Signature Behavior State (`SignatureBehaviorState.cs`)

Behavioral waveform snapshot captured from signature activity:

```csharp
var behaviorState = new SignatureBehaviorState
{
    // Entropy metrics
    PathEntropy = 2.8,              // High = exploratory, Low = sequential
    TimingEntropy = 0.4,            // High = human, Low = bot
    SpectralEntropy = 0.3,          // Frequency spectrum complexity

    // Statistical metrics
    CoefficientOfVariation = 0.25,  // Timing consistency (low = bot)
    BurstScore = 0.8,               // Request clustering
    SpectralPeakToNoise = 5.2,      // Periodicity strength

    // Navigation metrics
    NavAnomalyScore = 0.7,          // Non-afforded URL frequency
    AffordanceFollowThroughRatio = 0.15,  // UI link following

    // Error metrics
    FourOhFourRatio = 0.35,         // 404 errors (path probing)
    FiveOhOhRatio = 0.05,           // 5xx errors

    // Rate metrics
    AverageRps = 3.2,
    AverageSessionDurationSeconds = 120,
    AverageRequestsPerSession = 50
};
```

**Interpretation Thresholds** (from `SignatureToBdfMapperOptions`):

| Metric                  | Bot-like             | Human-like          |
|-------------------------|----------------------|---------------------|
| PathEntropy             | < 0.5 (sequential)   | > 3.0 (exploratory) |
| TimingEntropy           | < 0.3 (periodic)     | > 0.7 (variable)    |
| CoefficientOfVariation  | < 0.3 (consistent)   | > 0.7 (variable)    |
| SpectralPeakToNoise     | > 4.0 (timer-driven) | < 2.0 (irregular)   |
| NavAnomalyScore         | > 0.6 (scanner)      | < 0.3 (normal)      |
| AffordanceFollowThrough | < 0.4 (programmatic) | > 0.8 (UI-driven)   |

### 3. Signature-to-BDF Mapper (`SignatureToBdfMapper.cs`)

Maps observed behavior → synthetic BDF scenarios:

```csharp
// Inject the mapper
public class MyService
{
    private readonly SignatureToBdfMapper _mapper;

    public MyService(SignatureToBdfMapper mapper)
    {
        _mapper = mapper;
    }

    public async Task GenerateTestScenario(string signatureId)
    {
        // 1. Extract behavioral state from signature
        var behaviorState = await ExtractBehaviorFromSignature(signatureId);

        // 2. Classify the behavior
        var profile = ClassifyBehavior(behaviorState);

        // 3. Generate BDF scenario
        var scenario = _mapper.Map(behaviorState, profile);

        // 4. Save scenario for replay
        var json = JsonSerializer.Serialize(scenario, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync($"scenarios/{signatureId}.json", json);
    }
}
```

**Mapping Logic:**

- **Timing Detection**:
    - High spectral peak + low spectral entropy → `fixed` timing (periodic bot)
    - High burst score → `burst` timing (aggressive scraper)
    - Otherwise → `jittered` timing with CV-based jitter

- **Navigation Detection**:
    - High path entropy + high nav anomaly → `scanner` mode (attack paths)
    - Low path entropy + high nav anomaly → `sequential` mode (deterministic scraper)
    - High affordance ratio + low nav anomaly → `ui_graph` mode (human-like)
    - Otherwise → `random` mode

### 4. Explanation Formatter (`SignatureExplanationFormatter.cs`)

Generates human-readable explanations for dashboards:

```csharp
// Inject the formatter
public class DashboardController
{
    private readonly ISignatureExplanationFormatter _formatter;

    public DashboardController(ISignatureExplanationFormatter formatter)
    {
        _formatter = formatter;
    }

    public IActionResult ShowSignatureDetails(string signatureId)
    {
        var behaviorState = GetBehaviorState(signatureId);
        var outcome = GetDetectionOutcome(signatureId);

        var explanation = _formatter.Explain(behaviorState, outcome);

        return View(new SignatureViewModel
        {
            Summary = explanation.Summary,
            Highlights = explanation.Highlights,
            RawMetrics = explanation.RawMetrics
        });
    }
}
```

**Example Output:**

```
Summary: "This signature behaves like a high-confidence timer-driven bot."

Highlights:
- Requests are sent at highly regular intervals, typical of timer-driven scripts.
- Navigation frequently jumps to unusual or non-UI paths, consistent with scanners or crawlers.
- A large fraction of requests result in 404 errors, suggesting path probing or discovery.
- High request rate (3.2 requests/sec) exceeds typical human browsing.

Raw Metrics:
- BotProbability: 0.92
- RiskBand: High
- PathEntropy: 2.8
- SpectralPeakToNoise: 5.2
- AffordanceFollowThrough: 0.15
```

**Consistency:** The formatter uses the same `SignatureToBdfMapperOptions` thresholds as the mapper, ensuring technical
scenarios and user-facing explanations speak the same language.

### 5. BDF Runner (`BdfRunner.cs`)

Executes BDF scenarios for closed-loop validation:

```csharp
// Inject the runner
public class TestService
{
    private readonly IBdfRunner _runner;

    public TestService(IBdfRunner runner)
    {
        _runner = runner;
    }

    public async Task RunClosedLoopTest()
    {
        // 1. Load scenario from JSON
        var scenario = await _runner.LoadScenarioAsync("scenarios/periodic-bot.json");

        // 2. Execute against test environment
        var result = await _runner.RunScenarioAsync(
            scenario,
            baseUrl: "https://test.example.com");

        // 3. Validate expectations
        Console.WriteLine($"Scenario: {result.ScenarioId}");
        Console.WriteLine($"Duration: {result.Duration.TotalSeconds:F2}s");
        Console.WriteLine($"Total Requests: {result.TotalRequests}");
        Console.WriteLine($"Success Rate: {result.SuccessRate:P}");
        Console.WriteLine($"Expectation Met: {result.ExpectationMet}");

        // 4. Analyze phase results
        foreach (var phase in result.PhaseResults)
        {
            Console.WriteLine($"\nPhase: {phase.PhaseName}");
            Console.WriteLine($"  Requests: {phase.Requests.Count}");
            Console.WriteLine($"  Avg Duration: {phase.AverageRequestDuration.TotalMilliseconds:F0}ms");

            var statusCodes = phase.Requests
                .GroupBy(r => r.StatusCode)
                .OrderByDescending(g => g.Count());

            foreach (var group in statusCodes)
            {
                Console.WriteLine($"  {group.Key}: {group.Count()} requests");
            }
        }
    }
}
```

**Runner Features:**

- **Timing Simulation**: Accurately replicates fixed/jittered/burst patterns
- **Navigation Patterns**: Implements all navigation modes (ui_graph, sequential, random, scanner)
- **Request Tracking**: Captures status codes, headers, response times
- **Expectation Validation**: Compares outcomes against expected classifications
- **Detailed Reporting**: Phase-by-phase breakdowns with metrics

## Configuration

Add to `appsettings.json`:

```json
{
  "BotDetection": {
    "BdfMapper": {
      "PathEntropyLow": 0.5,
      "PathEntropyHigh": 3.0,
      "NavAnomalyHigh": 0.6,
      "SpectralPnBot": 4.0,
      "SpectralEntropyBot": 0.4,
      "BurstScoreHigh": 0.7,
      "AffordanceLow": 0.4,
      "AffordanceHigh": 0.8,
      "FourOhFourRatioHigh": 0.3,
      "FiveOhOhRatioHigh": 0.2,
      "TimingEntropyLow": 0.3,
      "CoefficientOfVariationLow": 0.3,
      "CoefficientOfVariationHigh": 0.7
    }
  }
}
```

## Complete Workflow Example

### Step 1: Capture Production Signature

```csharp
public class SignatureCaptureService
{
    public async Task<SignatureBehaviorState> CaptureSignature(string signatureId)
    {
        // Get all requests for this signature from database
        var requests = await _db.GetRequestsForSignature(signatureId);

        // Calculate behavioral metrics
        return new SignatureBehaviorState
        {
            PathEntropy = CalculatePathEntropy(requests),
            TimingEntropy = CalculateTimingEntropy(requests),
            CoefficientOfVariation = CalculateCV(requests),
            BurstScore = CalculateBurstScore(requests),
            NavAnomalyScore = CalculateNavAnomaly(requests),
            SpectralPeakToNoise = CalculateSpectralPeak(requests),
            SpectralEntropy = CalculateSpectralEntropy(requests),
            AffordanceFollowThroughRatio = CalculateAffordance(requests),
            FourOhFourRatio = requests.Count(r => r.StatusCode == 404) / (double)requests.Count,
            FiveOhOhRatio = requests.Count(r => r.StatusCode >= 500) / (double)requests.Count,
            AverageRps = requests.Count / (requests.Last().Timestamp - requests.First().Timestamp).TotalSeconds,
            AverageSessionDurationSeconds = CalculateAvgSessionDuration(requests),
            AverageRequestsPerSession = CalculateAvgRequestsPerSession(requests)
        };
    }
}
```

### Step 2: Generate BDF Scenario

```csharp
var behaviorState = await _captureService.CaptureSignature("sig-abc123");
var profile = behaviorState.SpectralPeakToNoise > 4.0
    ? SignatureBehaviorProfile.ExpectedBot
    : SignatureBehaviorProfile.ExpectedHuman;

var scenario = _mapper.Map(behaviorState, profile);

// Save for replay
var json = JsonSerializer.Serialize(scenario, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync($"test-scenarios/{scenario.Id}.json", json);
```

### Step 3: Replay and Validate

```csharp
var scenario = await _runner.LoadScenarioAsync("test-scenarios/periodic-bot.json");
var result = await _runner.RunScenarioAsync(scenario, "https://staging.example.com");

// Verify detection outcome matches original classification
if (!result.ExpectationMet)
{
    _logger.LogWarning("Detection regression detected! Original: Bot, Current: {Current}",
        result.PhaseResults.First().Requests.First().Headers?["X-Bot-Detection-Outcome"]);
}
```

### Step 4: Generate Dashboard Explanation

```csharp
var explanation = _formatter.Explain(behaviorState, new DetectionOutcome
{
    IsBot = true,
    BotProbability = 0.92,
    RiskBand = "High"
});

// Display in dashboard
ViewBag.Summary = explanation.Summary;
ViewBag.Highlights = explanation.Highlights;
ViewBag.Metrics = explanation.RawMetrics;
```

## SOLID Principles Compliance

### Single Responsibility Principle (SRP)

Each component has one clear responsibility:

- **BdfModels**: Data structures only (no logic)
- **SignatureBehaviorState**: Behavioral metrics snapshot (no formatting or mapping)
- **SignatureToBdfMapper**: Behavior → BDF mapping only
- **SignatureExplanationFormatter**: Behavior → human explanation only
- **BdfRunner**: BDF execution only

### Open/Closed Principle (OCP)

- Extensible through inheritance and composition
- New navigation modes can be added without modifying existing code
- New timing patterns can be added via configuration

### Liskov Substitution Principle (LSP)

- `ISignatureExplanationFormatter` and `IBdfRunner` interfaces enable substitution
- Mock implementations can be swapped for testing

### Interface Segregation Principle (ISP)

- Focused interfaces: `ISignatureExplanationFormatter`, `IBdfRunner`
- Clients only depend on what they use

### Dependency Inversion Principle (DIP)

- High-level modules depend on abstractions (`ISignatureExplanationFormatter`, `IBdfRunner`)
- Low-level implementations are injected via DI
- No direct instantiation of concrete classes

## Testing Strategy

### Unit Tests

```csharp
[Fact]
public void SignatureToBdfMapper_PeriodicBot_GeneratesFixedTiming()
{
    // Arrange
    var mapper = new SignatureToBdfMapper(Options.Create(new SignatureToBdfMapperOptions()));
    var state = new SignatureBehaviorState
    {
        SpectralPeakToNoise = 5.0,
        SpectralEntropy = 0.3,
        AverageRps = 0.2
    };

    // Act
    var scenario = mapper.Map(state, SignatureBehaviorProfile.ExpectedBot);

    // Assert
    Assert.Equal("fixed", scenario.Phases[0].Timing.Mode);
    Assert.Equal(0.0, scenario.Phases[0].Timing.JitterStdDevSeconds);
}
```

### Integration Tests

```csharp
[Fact]
public async Task BdfRunner_ExecutesScenario_ReturnsResults()
{
    // Arrange
    var scenario = CreateTestScenario();
    var runner = CreateRunner();

    // Act
    var result = await runner.RunScenarioAsync(scenario, "https://test.local");

    // Assert
    Assert.True(result.TotalRequests > 0);
    Assert.NotEmpty(result.PhaseResults);
}
```

### Regression Tests

Use BDF scenarios to prevent detection regressions:

```csharp
[Theory]
[InlineData("scenarios/known-bot-periodic.json", true)]
[InlineData("scenarios/known-human-browsing.json", false)]
public async Task DetectionRegression_KnownScenarios_ClassifiedCorrectly(string scenarioPath, bool expectedIsBot)
{
    var scenario = await _runner.LoadScenarioAsync(scenarioPath);
    var result = await _runner.RunScenarioAsync(scenario, TestBaseUrl);

    // Check detection outcome from response headers
    var firstRequest = result.PhaseResults[0].Requests[0];
    var isBot = ParseBotOutcome(firstRequest.Headers);

    Assert.Equal(expectedIsBot, isBot);
}
```

## Performance Considerations

- **Mapper**: O(1) - simple threshold checks
- **Formatter**: O(1) - generates fixed number of highlights
- **Runner**: O(n) - linear in request count
- **Memory**: Minimal - scenarios stream through, not stored

## Troubleshooting

### Scenario doesn't match expected behavior

Check threshold configuration in `SignatureToBdfMapperOptions`. Thresholds may need tuning based on your traffic
patterns.

### Runner timing is inaccurate

Ensure `BaseRateRps` and `JitterStdDevSeconds` are realistic values. Very high RPS may overwhelm the target server.

### Explanation doesn't match mapper output

Verify both use the same `SignatureToBdfMapperOptions` instance. They should be registered as singleton with shared
configuration.

## Future Enhancements

1. **Advanced Navigation**: UI graph parsing from sitemap/HTML
2. **Content Validation**: Check response body patterns
3. **Multi-client Scenarios**: Simulate multiple concurrent signatures
4. **Adaptive Timing**: Learn realistic timing from captured traffic
5. **Visual Reporting**: Grafana dashboards for scenario execution metrics

## References

- BDF Specification: `docs/future/bdf-behaviouralsignature.md`
- Source Code: `Mostlylucid.BotDetection/Behavioral/`
- Configuration: `appsettings.json` → `BotDetection:BdfMapper`
