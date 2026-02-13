# Bot Simulator Design

## Overview

A comprehensive bot simulator for testing, training, and debugging the bot detection system. Supports three modes:

1. **Signature Injection Mode** - Feed specific signature/signal data to test detector interactions
2. **Learning Mode** - Override UA/IP/behavior via headers to train individual detectors
3. **Arbitrary Policy Mode** - Execute custom pipelines and policies for targeted testing

## Architecture

### 1. Signature Injection Mode

**Purpose**: Test how the system processes specific signatures and signal combinations without running full detection.

**Headers**:
```http
X-Bot-Sim-Mode: signature
X-Bot-Sim-Signatures: <json>
```

**JSON Format**:
```json
{
  "clientIp": "192.168.1.100",
  "ipSignature": "a1b2c3d4e5f6",
  "userAgent": "Mozilla/5.0...",
  "uaSignature": "x9y8z7",
  "behaviorSignature": "req_pattern_001",
  "fingerprintHash": "fp123abc",
  "signals": {
    "ip:datacenter": true,
    "ua:headless": true,
    "fp:webdriver": true,
    "behavior:no_cookies": true
  },
  "contributions": [
    {
      "detectorName": "IpDetector",
      "category": "IP",
      "confidenceDelta": 0.75,
      "weight": 1.5,
      "reason": "[INJECTED] Datacenter IP detected"
    },
    {
      "detectorName": "UserAgentDetector",
      "category": "UserAgent",
      "confidenceDelta": 0.85,
      "weight": 1.8,
      "reason": "[INJECTED] Headless browser detected"
    }
  ]
}
```

**Implementation**:
- Add `SignatureInjectionMiddleware` that runs before `BotDetectionMiddleware`
- When `X-Bot-Sim-Mode: signature` is detected:
  - Skip normal detector execution
  - Create `BlackboardState` from injected JSON
  - Populate with signatures and signals
  - Add injected contributions directly
  - Run through `PolicyEvaluator` only
  - Skip `SignatureCoordinator` updates (read-only mode)
- Response includes full state + evaluation results

**Use Cases**:
- Test policy transitions without running detectors
- Test signature coordinator matching logic
- Test action policy execution
- Verify quorum/early-exit logic
- Test wave-based execution order

### 2. Learning Mode (Header Overrides)

**Purpose**: Train and test individual detectors with controlled input data.

**Headers**:
```http
X-Bot-Sim-Mode: learning
X-Bot-Sim-UA: <user-agent-string>
X-Bot-Sim-IP: <ip-address>
X-Bot-Sim-IP-Range: <cidr-or-asn>
X-Bot-Sim-Fingerprint: <json>
X-Bot-Sim-Behavior: <json>
X-Bot-Sim-Headers: <json>
X-Bot-Sim-Timing: <json>
```

**Detailed Header Formats**:

#### User Agent Override
```http
X-Bot-Sim-UA: Mozilla/5.0 (compatible; Googlebot/2.1)
```

#### IP Override
```http
# Single IP
X-Bot-Sim-IP: 203.0.113.42

# With datacenter flag
X-Bot-Sim-IP: 203.0.113.42
X-Bot-Sim-IP-Datacenter: true

# With geolocation
X-Bot-Sim-IP: 203.0.113.42
X-Bot-Sim-IP-Geo: {"country":"US","region":"CA","city":"San Francisco"}
```

#### IP Range Testing
```http
# CIDR range
X-Bot-Sim-IP-Range: 203.0.113.0/24

# ASN
X-Bot-Sim-IP-Range: AS15169  # Google

# Cloud provider
X-Bot-Sim-IP-Range: aws:us-east-1
```

#### Browser Fingerprint
```http
X-Bot-Sim-Fingerprint: {
  "screenWidth": 1920,
  "screenHeight": 1080,
  "timezone": "America/Los_Angeles",
  "languages": ["en-US", "en"],
  "platform": "Win32",
  "plugins": ["PDF Viewer"],
  "webdriver": false,
  "headless": false,
  "automation": {
    "detectedFrameworks": [],
    "suspiciousFlags": []
  }
}
```

#### Behavioral Data
```http
X-Bot-Sim-Behavior: {
  "requestCount": 15,
  "sessionAge": 300,
  "hasReferrer": true,
  "hasCookies": true,
  "requestRate": 3.5,
  "pathDiversity": 0.7,
  "avgRequestInterval": 2.5
}
```

#### Custom Headers
```http
X-Bot-Sim-Headers: {
  "Accept": "text/html",
  "Accept-Language": "en-US,en;q=0.9",
  "Accept-Encoding": "gzip, deflate, br",
  "Connection": "keep-alive",
  "Upgrade-Insecure-Requests": "1",
  "Sec-Fetch-Dest": "document",
  "Sec-Fetch-Mode": "navigate",
  "Sec-Fetch-Site": "none",
  "Sec-Fetch-User": "?1"
}
```

#### Request Timing
```http
X-Bot-Sim-Timing: {
  "firstByteTime": 150,
  "requestDuration": 450,
  "thinkTime": 2000,
  "navigationTiming": {
    "domContentLoaded": 800,
    "loadComplete": 1200
  }
}
```

**Implementation**:
- Add `LearningModeMiddleware` that runs before detectors
- Inject overrides into `HttpContext.Items` with special keys:
  - `BotSim.OverrideUA`
  - `BotSim.OverrideIP`
  - `BotSim.OverrideFingerprint`
  - `BotSim.OverrideBehavior`
  - `BotSim.OverrideHeaders`
  - `BotSim.OverrideTiming`
- Contributors check for override keys before using real data
- Run full detection pipeline with overridden inputs
- Enable learning/feedback loops to update weights
- Response includes:
  - Detector-by-detector results
  - Learning updates applied
  - Weight store changes
  - Signature coordinator updates

**Use Cases**:
- Train IP reputation with known datacenter IPs
- Test UA detector with edge cases
- Verify behavioral detector thresholds
- Test header analysis with crafted requests
- Validate fingerprint detection logic
- Train pattern reputation system

### 3. Arbitrary Policy Execution Mode

**Purpose**: Execute custom detection pipelines and policies without modifying configuration.

**Headers**:
```http
X-Bot-Sim-Mode: policy
X-Bot-Sim-Pipeline: <pipeline-name-or-json>
X-Bot-Sim-Policy: <policy-name-or-json>
```

**Pipeline Definition**:

#### Named Pipeline
```http
X-Bot-Sim-Pipeline: local
```

Built-in pipelines:
- `local` - Fast path only (UA + Header)
- `standard` - Default wave-based execution
- `paranoid` - All detectors, no early exit
- `learning` - Include AI detectors + feedback
- `minimal` - Single detector (specify in policy)

#### Custom Pipeline JSON
```http
X-Bot-Sim-Pipeline: {
  "name": "custom-test",
  "stages": [
    {
      "wave": 0,
      "detectors": ["UserAgent", "Header"],
      "quorum": 1,
      "earlyExitOnConfidence": 0.95
    },
    {
      "wave": 1,
      "detectors": ["IP", "Behavioral"],
      "quorum": 2,
      "earlyExitOnConfidence": 0.90
    },
    {
      "wave": 2,
      "detectors": ["Fingerprint", "ProjectHoneypot"],
      "parallel": true,
      "timeout": 1000
    }
  ],
  "aiEscalation": {
    "enabled": true,
    "threshold": 0.6,
    "detector": "Llm"
  }
}
```

**Policy Definition**:

#### Named Policy
```http
X-Bot-Sim-Policy: strict
```

Built-in policies: `default`, `strict`, `relaxed`, `static`, `learning`, `monitor`, `api`

#### Custom Policy JSON
```http
X-Bot-Sim-Policy: {
  "name": "test-policy",
  "enabled": true,
  "detectorWeights": {
    "UserAgent": 2.0,
    "IP": 1.5,
    "Behavioral": 1.0,
    "Fingerprint": 1.8
  },
  "thresholds": {
    "botThreshold": 0.75,
    "highRiskThreshold": 0.90,
    "immediateBlockThreshold": 0.95
  },
  "transitions": [
    {
      "trigger": "onRisk",
      "threshold": 0.90,
      "action": "block",
      "reason": "High risk bot detected"
    },
    {
      "trigger": "onSignal",
      "signalKey": "verified_bad_bot",
      "action": "block"
    },
    {
      "trigger": "onSignal",
      "signalKey": "verified_good_bot",
      "action": "allow"
    }
  ],
  "actions": {
    "block": {
      "type": "status",
      "statusCode": 403,
      "message": "Access denied - bot detected"
    },
    "throttle": {
      "type": "delay",
      "delayMs": 2000
    }
  }
}
```

**Combined Example**:
```http
POST /api/test HTTP/1.1
X-Bot-Sim-Mode: policy
X-Bot-Sim-Pipeline: {
  "name": "ua-only-test",
  "stages": [
    {
      "wave": 0,
      "detectors": ["UserAgent"],
      "quorum": 1
    }
  ]
}
X-Bot-Sim-Policy: {
  "name": "aggressive-ua",
  "botThreshold": 0.5,
  "detectorWeights": {
    "UserAgent": 3.0
  }
}
User-Agent: curl/8.0.0
```

**Implementation**:
- Add `PolicyExecutionMiddleware` before `BotDetectionMiddleware`
- Parse pipeline/policy from headers
- Validate JSON schemas
- Create temporary policy instances
- Override `BlackboardOrchestrator` configuration for this request
- Execute with custom pipeline
- Restore normal configuration after request
- Response includes:
  - Policy used
  - Pipeline execution trace
  - Stage-by-stage results
  - Transition triggers
  - Action taken

**Use Cases**:
- Test single detector in isolation
- Prototype new policy configurations
- Verify wave-based execution logic
- Test AI escalation triggers
- Debug transition logic
- Benchmark detector performance

## Response Format

All simulator modes return enhanced responses:

```http
HTTP/1.1 200 OK
X-Bot-Sim-Active: true
X-Bot-Sim-Mode: <mode>
Content-Type: application/json

{
  "simulator": {
    "mode": "signature|learning|policy",
    "injected": {
      "signatures": {...},
      "overrides": {...},
      "pipeline": {...},
      "policy": {...}
    }
  },
  "detection": {
    "isBot": true,
    "confidence": 0.85,
    "botType": "Scraper",
    "policy": "test-policy",
    "action": "block"
  },
  "state": {
    "signals": {...},
    "contributions": [...],
    "signatures": {...}
  },
  "execution": {
    "pipeline": "custom-test",
    "stages": [
      {
        "wave": 0,
        "detectors": ["UserAgent", "Header"],
        "results": [...],
        "duration": 15,
        "earlyExit": false
      }
    ],
    "totalDuration": 45,
    "transitions": [...]
  },
  "learning": {
    "weightsUpdated": 5,
    "signaturesRecorded": 3,
    "feedbackApplied": true
  }
}
```

## Configuration

Add to `BotDetectionOptions`:

```csharp
public class BotSimulatorOptions
{
    /// <summary>Enable bot simulator features (disable in production)</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Allow signature injection mode</summary>
    public bool AllowSignatureInjection { get; set; } = true;

    /// <summary>Allow learning mode with header overrides</summary>
    public bool AllowLearningMode { get; set; } = true;

    /// <summary>Allow arbitrary policy execution</summary>
    public bool AllowPolicyExecution { get; set; } = true;

    /// <summary>Require API key for simulator access</summary>
    public string? ApiKey { get; set; }

    /// <summary>Maximum pipeline execution time (ms)</summary>
    public int MaxExecutionTime { get; set; } = 10000;

    /// <summary>Include detailed execution trace in response</summary>
    public bool IncludeExecutionTrace { get; set; } = true;

    /// <summary>Enable learning/feedback in simulator modes</summary>
    public bool EnableLearning { get; set; } = false;

    /// <summary>Allowed IP addresses for simulator access</summary>
    public List<string> AllowedIPs { get; set; } = new() { "127.0.0.1", "::1" };
}
```

## Security Considerations

1. **Production Safety**:
   - `BotSimulator.Enabled` must be `false` in production
   - Add warning logs when simulator is enabled
   - Check `AllowedIPs` whitelist
   - Require API key via `X-Bot-Sim-ApiKey` header

2. **Resource Limits**:
   - Enforce `MaxExecutionTime` timeout
   - Limit JSON payload sizes
   - Rate limit simulator requests
   - Prevent infinite loops in custom pipelines

3. **Validation**:
   - Validate all JSON schemas strictly
   - Sanitize injected signatures/signals
   - Prevent code injection in policy expressions
   - Limit detector list to registered detectors only

## Implementation Files

New files to create:

```
Mostlylucid.BotDetection/
├── Simulator/
│   ├── BotSimulatorOptions.cs
│   ├── BotSimulatorMiddleware.cs
│   ├── Modes/
│   │   ├── SignatureInjectionMode.cs
│   │   ├── LearningMode.cs
│   │   └── PolicyExecutionMode.cs
│   ├── Models/
│   │   ├── SignatureInjectionRequest.cs
│   │   ├── LearningModeOverrides.cs
│   │   ├── CustomPipeline.cs
│   │   └── SimulatorResponse.cs
│   └── Validation/
│       ├── PipelineValidator.cs
│       └── PolicyValidator.cs
└── Extensions/
    └── BotSimulatorServiceExtensions.cs

Mostlylucid.BotDetection.Demo/
├── Controllers/
│   └── SimulatorController.cs  # REST API for simulator
└── Pages/
    └── Simulator.cshtml  # UI for testing

Mostlylucid.BotDetection.Test/
└── Simulator/
    ├── SignatureInjectionTests.cs
    ├── LearningModeTests.cs
    └── PolicyExecutionTests.cs
```

## Usage Examples

### Example 1: Test Signature Coordinator Matching

```bash
curl -X POST http://localhost:5080/api/test \
  -H "X-Bot-Sim-Mode: signature" \
  -H "X-Bot-Sim-Signatures: {
    \"ipSignature\": \"dc_aws_us-east-1\",
    \"uaSignature\": \"headless_chrome_120\",
    \"signals\": {
      \"ip:datacenter\": true,
      \"ua:headless\": true,
      \"ua:automation\": true
    }
  }"
```

### Example 2: Train IP Detector

```bash
curl -X POST http://localhost:5080/api/test \
  -H "X-Bot-Sim-Mode: learning" \
  -H "X-Bot-Sim-IP: 203.0.113.42" \
  -H "X-Bot-Sim-IP-Range: AS15169" \
  -H "X-Bot-Sim-IP-Datacenter: true" \
  -H "User-Agent: curl/8.0.0"
```

### Example 3: Test Custom Pipeline

```bash
curl -X POST http://localhost:5080/api/test \
  -H "X-Bot-Sim-Mode: policy" \
  -H "X-Bot-Sim-Pipeline: {
    \"name\": \"ua-only\",
    \"stages\": [{
      \"wave\": 0,
      \"detectors\": [\"UserAgent\"],
      \"quorum\": 1
    }]
  }" \
  -H "User-Agent: suspicious-bot/1.0"
```

### Example 4: Test Policy Transitions

```bash
curl -X POST http://localhost:5080/api/test \
  -H "X-Bot-Sim-Mode: policy" \
  -H "X-Bot-Sim-Policy: {
    \"name\": \"test\",
    \"botThreshold\": 0.5,
    \"transitions\": [{
      \"trigger\": \"onRisk\",
      \"threshold\": 0.6,
      \"action\": \"throttle\"
    }]
  }" \
  -H "User-Agent: curl/8.0.0"
```

## Benefits

1. **Development**:
   - Test detectors in isolation
   - Validate policy logic without full pipeline
   - Prototype new detection strategies
   - Debug signature coordinator matching

2. **Training**:
   - Feed known-good/bad traffic patterns
   - Train pattern reputation system
   - Calibrate detector weights
   - Build IP/UA reputation databases

3. **Testing**:
   - Integration tests with controlled inputs
   - Regression testing for detector changes
   - Performance benchmarking
   - Policy validation

4. **Debugging**:
   - Reproduce production issues locally
   - Trace execution through pipeline
   - Identify false positives/negatives
   - Optimize detector performance

## Future Enhancements

1. **Batch Mode**: Submit multiple test cases at once
2. **Replay Mode**: Record/replay production traffic
3. **Diff Mode**: Compare two pipeline/policy configurations
4. **Benchmark Mode**: Load testing with simulated bots
5. **Export Mode**: Generate test datasets for ML training
6. **Visualization**: Real-time pipeline execution graphs
