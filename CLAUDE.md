# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**StyloBot** is an enterprise-grade bot detection framework for ASP.NET Core. It uses a blackboard architecture (via StyloFlow) with 21 detectors, AI-powered classification, and zero-PII design. The system combines fast-path detection (<1ms) with optional LLM escalation for complex cases.

## Build Commands

```bash
# Build entire solution
dotnet build mostlylucid.stylobot.sln

# Build specific project
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj

# Run the demo application
dotnet run --project Mostlylucid.BotDetection.Demo
# Visit: https://localhost:5001/SignatureDemo

# Run all tests
dotnet test

# Run specific test project
dotnet test Mostlylucid.BotDetection.Test/
dotnet test Mostlylucid.BotDetection.Orchestration.Tests/

# Run single test file
dotnet test --filter "FullyQualifiedName~UserAgentDetectorTests"

# Run tests with coverage
dotnet test /p:CollectCoverage=true

# Run benchmarks
dotnet run --project Mostlylucid.BotDetection.Benchmarks -c Release

# Pack NuGet package
dotnet pack Mostlylucid.BotDetection -c Release
```

## Solution Structure

**Main Solution**: `mostlylucid.stylobot.sln` (20 projects)

| Project | Purpose |
|---------|---------|
| `Mostlylucid.BotDetection` | Core detection library (NuGet package) |
| `Mostlylucid.BotDetection.UI` | Dashboard, TagHelpers, SignalR hub |
| `Mostlylucid.BotDetection.UI.PostgreSQL` | PostgreSQL persistence layer |
| `Mostlylucid.BotDetection.Demo` | Interactive demo with all 21 detectors |
| `Mostlylucid.BotDetection.Console` | Standalone gateway/proxy console |
| `Stylobot.Gateway` | Docker-first YARP reverse proxy |
| `Mostlylucid.GeoDetection` | Geographic routing (MaxMind, ip-api) |
| `Mostlylucid.GeoDetection.Contributor` | Geo enrichment for bot detection |
| `Mostlylucid.Common` | Shared utilities (caching, telemetry) |

**Test Projects**: `*.Test`, `*.Tests` - xUnit + Moq

**Website Solution**: `mostlylucid.stylobot.website/Stylobot.Website.sln` (separate marketing site)

## Architecture

### Blackboard Pattern (StyloFlow)

Detection uses an ephemeral blackboard where detectors write signals:
- `SignalSink` - In-memory signal store per request
- Raw PII (IP, UA) stays in `DetectionContext`, never on blackboard
- Signals use hierarchical keys: `request.ip.is_datacenter`, `detection.useragent.confidence`

### Detector Pipeline

**Fast Path (<1ms)**: UserAgent, Header, Ip, SecurityTool, Behavioral, ClientSide, Inconsistency, VersionAge, Heuristic, FastPathReputation, CacheBehavior, ReputationBias

**Slow Path (~100ms)**: ProjectHoneypot (DNS lookup)

**Advanced Fingerprinting**: TlsFingerprint (JA3/JA4), TcpIpFingerprint (p0f), Http2Fingerprint (AKAMAI), MultiLayerCorrelation, BehavioralWaveform, ResponseBehavior

### Key Files

- `Extensions/ServiceCollectionExtensions.cs` - DI registration entry points
- `Orchestration/BlackboardOrchestrator.cs` - Main detection orchestration
- `Orchestration/ContributingDetectors/` - All 21 detector implementations
- `Orchestration/Manifests/detectors/*.yaml` - Detector configurations
- `Models/BotDetectionOptions.cs` - Configuration model
- `Actions/*.cs` - Response policies (block, throttle, challenge, redirect)

### Configuration Pattern

Detectors are configured via YAML manifests with appsettings.json overrides:

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "DefaultActionPolicyName": "throttle-stealth",
    "EnableAiDetection": true,
    "Detectors": {
      "UserAgentContributor": {
        "Weights": { "BotSignal": 2.0 }
      }
    }
  }
}
```

## Service Registration

```csharp
// Default: all detectors + Heuristic AI
builder.Services.AddBotDetection();

// User-agent only (minimal)
builder.Services.AddSimpleBotDetection();

// With LLM escalation (requires Ollama)
builder.Services.AddAdvancedBotDetection("http://localhost:11434", "gemma3:4b");
```

## Key Patterns

### Zero-PII Architecture
- Raw IP/UA only in-memory, never persisted
- Signatures use HMAC-SHA256 hashing
- Blackboard contains only privacy-safe signals

### Action Policies
Separation of detection (WHAT) from response (HOW):
- `block` - HTTP 403
- `throttle-stealth` - Silent delay
- `challenge` - CAPTCHA/proof-of-work
- `redirect-honeypot` - Trap redirect
- `logonly` - Shadow mode

### HttpContext Extensions
```csharp
context.IsBot()
context.GetBotConfidence()
context.GetBotType()
```

## Adding a New Detector

Every detector touches exactly 5 files. Use `Http3FingerprintContributor` as a reference implementation.

### 5-File Checklist

1. **C# class** — `Orchestration/ContributingDetectors/{Name}Contributor.cs`
   - Inherit `ConfiguredContributorBase` (for YAML config) or `ContributingDetectorBase` (for no-config detectors)
   - Constructor takes `ILogger<T>` + `IDetectorConfigProvider` and calls `base(configProvider)`
   - Override `Name` (string), `Priority` (int), `TriggerConditions` (empty array for Wave 0, or signal triggers for later waves)
   - Implement `ContributeAsync(BlackboardState state, CancellationToken)` returning `IReadOnlyList<DetectionContribution>`
   - Use `GetParam<T>(name, default)` for all tunable values — no magic numbers in code

2. **YAML manifest** — `Orchestration/Manifests/detectors/{name}.detector.yaml`
   - Follows the schema: `name`, `priority`, `enabled`, `scope`, `taxonomy`, `input`, `output`, `triggers`, `emits`, `defaults` (weights, confidence, timing, features, parameters)
   - The `*.yaml` glob in `.csproj` auto-includes it as an embedded resource

3. **SignalKeys** — `Models/DetectionContext.cs`
   - Add constants in the `SignalKeys` class grouped with a section header comment
   - Use hierarchical naming: `h3.protocol`, `h3.client_type`, etc.

4. **DI registration** — `Extensions/ServiceCollectionExtensions.cs`
   - Add `services.AddSingleton<IContributingDetector, {Name}Contributor>();` in the appropriate wave section

5. **Narrative builder** — `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`
   - Add entries to both `DetectorFriendlyNames` and `DetectorCategories` dictionaries

### Key Rules

- **No magic numbers** — all confidence, weight, and threshold values come from YAML `defaults.parameters` via `GetParam<T>()`
- **Always add signals to the last contribution** — the orchestrator reads signals from contributions; ensure the final contribution carries the full `signals.ToImmutable()` dictionary
- **Cross-detector communication** — use `TriggerConditions` (e.g., `SignalExistsTrigger`, `AnyOfTrigger`, `AllOfTrigger`) to declare dependencies, and `GetSignal<T>(state, key)` to read signals from earlier detectors
- **Use helper methods** — `BotContribution()`, `HumanContribution()`, `NeutralContribution()`, `StrongBotContribution()` from `ConfiguredContributorBase`

## Versioning

Uses MinVer with tag prefix `botdetection-v{version}`. NuGet packages auto-version from git tags.

## Target Frameworks

.NET 10.0

## External Dependencies (Local Project References)

The solution uses local project references for development. Related repos that must be cloned as siblings:

```
D:\Source\
├── mostlylucid.stylobot\     # This repo
├── styloflow\                # StyloFlow.Core, StyloFlow.Retrieval.Core
└── mostlylucid.atoms\        # mostlylucid.ephemeral and atoms
```

**From StyloFlow** (`D:\Source\styloflow\`):
- `StyloFlow.Core` - Manifest-driven component configuration
- `StyloFlow.Retrieval.Core` - Signal/analysis wave framework

**From Ephemeral** (`D:\Source\mostlylucid.atoms\mostlylucid.ephemeral\`):
- `mostlylucid.ephemeral` - Core signal sink and coordination
- `mostlylucid.ephemeral.atoms.taxonomy` - DetectionLedger, DetectionContribution, IDetectorAtom
- `mostlylucid.ephemeral.atoms.keyedsequential` - Keyed sequential processing
- `mostlylucid.ephemeral.atoms.slidingcache` - Sliding window cache

**NuGet Packages**:
- **OllamaSharp** - LLM integration (optional)
- **YamlDotNet** - Manifest parsing
- **MathNet.Numerics** - Statistical analysis

## Documentation

Detailed docs in `Mostlylucid.BotDetection/docs/`:
- `ai-detection.md` - Heuristic model and LLM escalation
- `learning-and-reputation.md` - Adaptive learning system
- `action-policies.md` - Response handling
- `configuration.md` - Full options reference
- `yarp-integration.md` - Reverse proxy setup

