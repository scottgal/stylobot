# Configuration Reference

Complete configuration options for Mostlylucid.BotDetection.

## Quick Start Configurations

### Minimal Configuration

Basic detection without blocking - good for getting started:

```json
{
  "BotDetection": {
    "BotThreshold": 0.7

    // Uncomment to enable AI-powered detection with learning (RECOMMENDED):
    // "EnableAiDetection": true,
    // "AiDetection": {
    //   "Provider": "Heuristic",
    //   "Heuristic": { "Enabled": true, "EnableWeightLearning": true }
    // },
    // "Learning": { "Enabled": true }
  }
}
```

### Typical Production Configuration (RECOMMENDED)

Full detection with Heuristic AI, learning enabled, and stealth throttling:

```json
{
  "BotDetection": {
    // === Core Detection ===
    "BotThreshold": 0.7,
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth",

    // === AI Detection with Learning (KEY FEATURE) ===
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "TimeoutMs": 1000,
      "Heuristic": {
        "Enabled": true,
        "EnableWeightLearning": true,
        "LoadLearnedWeights": true
      }
    },

    // === Learning System (Continuous Improvement) ===
    "Learning": {
      "Enabled": true,
      "LearningRate": 0.1,
      "EnableDriftDetection": true
    },

    // === Path-Based Policies ===
    "PathPolicies": {
      "/api/login": "strict",
      "/api/checkout/*": "strict",
      "/api/admin/**": "strict",
      "/sitemap.xml": "allowVerifiedBots",
      "/robots.txt": "allowVerifiedBots"
    },

    // === Action Policies ===
    "ActionPolicies": {
      "api-throttle": {
        "Type": "Throttle",
        "BaseDelayMs": 500,
        "MaxDelayMs": 10000,
        "ScaleByRisk": true,
        "JitterPercent": 0.5,
        "IncludeHeaders": false
      }
    }
  }
}
```

### Full Configuration (All Options)

Complete reference with all available options:

```json
{
  "BotDetection": {
    // ==========================================
    // CORE SETTINGS
    // ==========================================
    "BotThreshold": 0.7,
    "EnableUserAgentDetection": true,
    "EnableHeaderAnalysis": true,
    "EnableIpDetection": true,
    "EnableBehavioralAnalysis": true,
    "EnableLlmDetection": true,
    "EnableTestMode": false,
    "MaxRequestsPerMinute": 60,
    "CacheDurationSeconds": 300,

    // ==========================================
    // BLOCKING SETTINGS
    // ==========================================
    "BlockDetectedBots": true,
    "BlockStatusCode": 403,
    "BlockMessage": "Access denied",
    "MinConfidenceToBlock": 0.8,
    "AllowVerifiedSearchEngines": true,
    "AllowSocialMediaBots": true,
    "AllowMonitoringBots": true,
    "DefaultActionPolicyName": "throttle-stealth",

    // ==========================================
    // AI DETECTION (KEY FEATURE)
    // ==========================================
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "TimeoutMs": 15000,
      "MaxConcurrentRequests": 5,

      // Heuristic Settings (recommended - fast, learns)
      "Heuristic": {
        "Enabled": true,
        "LoadLearnedWeights": true,
        "EnableWeightLearning": true,
        "MinConfidenceForLearning": 0.8,
        "LearningRate": 0.01,
        "WeightReloadIntervalMinutes": 60
      },

      // Ollama Settings (for LLM escalation)
      "Ollama": {
        "Endpoint": "http://localhost:11434",
        "Model": "gemma3:4b",
        "UseJsonMode": true,
        "Temperature": 0.1,
        "MaxTokens": 256
      }
    },

    // ==========================================
    // LEARNING SYSTEM (KEY FEATURE)
    // ==========================================
    "Learning": {
      "Enabled": true,
      "LearningRate": 0.1,
      "MaxSupport": 1000,
      "ScoreDecayTauHours": 168,
      "SupportDecayTauHours": 336,
      "Prior": 0.5,
      "PromoteToBadScore": 0.9,
      "PromoteToBadSupport": 50,
      "DemoteFromBadScore": 0.7,
      "DemoteFromBadSupport": 100,
      "GcEligibleDays": 90,
      "EnableDriftDetection": true,
      "DriftThreshold": 0.05
    },

    // ==========================================
    // DETECTION POLICIES
    // ==========================================
    "DefaultPolicyName": "default",
    "PathPolicies": {
      "/api/login": "strict",
      "/api/checkout/*": "strict",
      "/api/admin/**": "strict",
      "/static/*": "relaxed",
      "/sitemap.xml": "allowVerifiedBots"
    },
    "Policies": {
      "default": {
        "Description": "All detectors + Heuristic, LLM for uncertain",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "ProjectHoneypot", "Behavioral", "ClientSide", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "SlowPath": [],
        "AiPath": ["Llm", "HeuristicLate"],
        "UseFastPath": true,
        "ForceSlowPath": false,
        "EscalateToAi": true,
        "AiEscalationThreshold": 0.0,
        "EarlyExitThreshold": 0.85,
        "ImmediateBlockThreshold": 0.95,
        "Tags": ["production", "all-detectors", "heuristic", "llm"]
      },
      "fastpath": {
        "Description": "No LLM, heuristic only - lowest latency",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "ProjectHoneypot", "Behavioral", "ClientSide", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "SlowPath": [],
        "AiPath": [],
        "UseFastPath": true,
        "ForceSlowPath": false,
        "EscalateToAi": false,
        "EarlyExitThreshold": 0.15,
        "ImmediateBlockThreshold": 0.90,
        "Tags": ["production", "heuristic-only"]
      },
      "strict": {
        "Description": "Lower thresholds for sensitive endpoints",
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Heuristic"],
        "EarlyExitThreshold": 0.70,
        "ImmediateBlockThreshold": 0.80
      }
    },

    // ==========================================
    // ACTION POLICIES
    // ==========================================
    "ActionPolicies": {
      "custom-block": {
        "Type": "Block",
        "StatusCode": 403,
        "Message": "Access denied",
        "IncludeRiskScore": false,
        "Headers": { "X-Reason": "bot-detected" }
      },
      "custom-throttle": {
        "Type": "Throttle",
        "BaseDelayMs": 500,
        "MaxDelayMs": 10000,
        "JitterPercent": 0.5,
        "ScaleByRisk": true,
        "IncludeHeaders": false
      }
    },

    // ==========================================
    // FAST PATH SETTINGS
    // ==========================================
    "FastPath": {
      "Enabled": true,
      "MaxParallelDetectors": 8,
      "EnableWaveParallelism": true,
      "WaveTimeoutMs": 500,
      "FastPathTimeoutMs": 1000,
      "EarlyExitThreshold": 0.99,
      "SkipSlowPathThreshold": 0.0,
      "SlowPathTriggerThreshold": 0.0,
      "AlwaysRunFullOnPaths": ["/api/checkout", "/api/login"],
      "EnableDriftDetection": true,
      "DriftThreshold": 0.005,
      "EnableFeedbackLoop": true,
      "FeedbackMinConfidence": 0.9,
      "FeedbackMinOccurrences": 5,
      "SlowPathDetectors": [
        { "Name": "Heuristic Detector", "Signal": "AiClassificationCompleted", "ExpectedLatencyMs": 1, "Wave": 1, "Category": "AI", "Weight": 2.0 },
        { "Name": "LLM Detector", "Signal": "LlmClassificationCompleted", "ExpectedLatencyMs": 500, "Wave": 2, "Category": "AI", "Weight": 2.5 },
        { "Name": "Inconsistency Detector", "Signal": "InconsistencyUpdated", "ExpectedLatencyMs": 2, "Wave": 1, "Category": "Inconsistency", "Weight": 1.5 },
        { "Name": "Version Age Detector", "Signal": "VersionAgeAnalyzed", "ExpectedLatencyMs": 1, "Wave": 1, "Category": "VersionAge", "Weight": 1.0 }
      ]
    },

    // ==========================================
    // VERSION AGE SETTINGS
    // ==========================================
    "VersionAge": {
      "Enabled": true,
      "CheckBrowserVersion": true,
      "CheckOsVersion": true,
      "MaxBrowserVersionAge": 10
    },

    // ==========================================
    // SECURITY TOOLS SETTINGS
    // ==========================================
    "SecurityTools": {
      "Enabled": true,
      "BlockSecurityTools": true,
      "LogDetections": true,
      "CustomPatterns": [],
      "ExcludedPatterns": [],
      "EnabledCategories": [],
      "HoneypotRedirectUrl": null
    },

    // ==========================================
    // PROJECT HONEYPOT SETTINGS
    // ==========================================
    "ProjectHoneypot": {
      "Enabled": true,
      "AccessKey": null,
      "HighThreatThreshold": 25,
      "MaxDaysAge": 90,
      "TimeoutMs": 1000,
      "CacheDurationSeconds": 1800,
      "SkipLocalIps": true,
      "TreatHarvestersAsMalicious": true,
      "TreatCommentSpammersAsMalicious": true,
      "TreatSuspiciousAsSuspicious": true
    },

    // ==========================================
    // BEHAVIORAL SETTINGS
    // ==========================================
    "Behavioral": {
      "ApiKeyHeader": "X-Api-Key",
      "ApiKeyRateLimit": 120,
      "UserIdClaim": "sub",
      "UserIdHeader": "X-User-Id",
      "UserRateLimit": 180,
      "EnableAnomalyDetection": true,
      "SpikeThresholdMultiplier": 5.0,
      "NewPathAnomalyThreshold": 0.8
    },

    // ==========================================
    // CLIENT-SIDE SETTINGS
    // ==========================================
    "ClientSide": {
      "Enabled": true,
      "TokenSecret": "your-secret-key-min-32-chars-long",
      "TokenLifetimeSeconds": 300,
      "FingerprintCacheDurationSeconds": 1800,
      "CollectWebGL": true,
      "CollectCanvas": true,
      "CollectAudio": false,
      "MinIntegrityScore": 70,
      "HeadlessThreshold": 0.5
    },

    // ==========================================
    // RESPONSE HEADERS
    // ==========================================
    "ResponseHeaders": {
      "Enabled": true,
      "HeaderPrefix": "X-Bot-",
      "IncludePolicyName": true,
      "IncludeConfidence": true,
      "IncludeDetectors": true,
      "IncludeProcessingTime": true,
      "IncludeBotName": true,
      "IncludeFullJson": false
    },

    // ==========================================
    // DATA SOURCES
    // ==========================================
    "DataSources": {
      "IsBot": {
        "Enabled": true,
        "Url": "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json"
      },
      "Matomo": {
        "Enabled": false,
        "Url": "https://raw.githubusercontent.com/matomo-org/device-detector/master/regexes/bots.yml"
      },
      "CrawlerUserAgents": {
        "Enabled": false,
        "Url": "https://raw.githubusercontent.com/monperrus/crawler-user-agents/master/crawler-user-agents.json"
      },
      "AwsIpRanges": {
        "Enabled": true,
        "Url": "https://ip-ranges.amazonaws.com/ip-ranges.json"
      },
      "GcpIpRanges": {
        "Enabled": true,
        "Url": "https://www.gstatic.com/ipranges/cloud.json"
      },
      "AzureIpRanges": {
        "Enabled": false,
        "Url": ""
      },
      "CloudflareIpv4": {
        "Enabled": true,
        "Url": "https://www.cloudflare.com/ips-v4"
      },
      "CloudflareIpv6": {
        "Enabled": true,
        "Url": "https://www.cloudflare.com/ips-v6"
      },
      "ScannerUserAgents": {
        "Enabled": true,
        "Url": "https://raw.githubusercontent.com/digininja/scanner_user_agents/main/list.json"
      },
      "CoreRuleSetScanners": {
        "Enabled": true,
        "Url": "https://raw.githubusercontent.com/coreruleset/coreruleset/main/rules/scanners-user-agents.data"
      }
    },

    // ==========================================
    // BACKGROUND UPDATES
    // ==========================================
    "EnableBackgroundUpdates": true,
    "UpdateIntervalHours": 24,
    "UpdateCheckIntervalMinutes": 60,
    "StartupDelaySeconds": 5,
    "ListDownloadTimeoutSeconds": 30,
    "MaxDownloadRetries": 3,

    // ==========================================
    // THROTTLING
    // ==========================================
    "Throttling": {
      "Enabled": false,
      "MinConfidenceToThrottle": 0.5,
      "BaseDelayMs": 1000,
      "MaxDelayMs": 10000,
      "EnableJitter": true,
      "JitterPercentage": 0.2
    },

    // ==========================================
    // DATABASE
    // ==========================================
    "DatabasePath": null,
    "EnableDatabaseWalMode": true,

    // ==========================================
    // PATTERN LEARNING (EXPERIMENTAL)
    // ==========================================
    "EnablePatternLearning": false,
    "MinConfidenceToLearn": 0.9,
    "MaxLearnedPatterns": 1000,
    "PatternConsolidationIntervalHours": 24,

    // ==========================================
    // LOGGING
    // ==========================================
    "LogAllRequests": false,
    "LogDetailedReasons": true,
    "LogPerformanceMetrics": false,
    "LogIpAddresses": true,
    "LogUserAgents": true,

    // ==========================================
    // WHITELISTS / BLOCKLISTS
    // ==========================================
    "WhitelistedBotPatterns": ["Googlebot", "Bingbot", "Slackbot"],
    "WhitelistedIps": ["192.168.1.0/24"],
    "BlacklistedIps": [],
    "DatacenterIpPrefixes": []
  }
}
```

---

## Core Settings

| Option                     | Type   | Default | Description                                       |
|----------------------------|--------|---------|---------------------------------------------------|
| `BotThreshold`             | double | `0.7`   | Confidence threshold to classify as bot (0.0-1.0) |
| `EnableUserAgentDetection` | bool   | `true`  | Enable User-Agent pattern matching                |
| `EnableHeaderAnalysis`     | bool   | `true`  | Enable HTTP header inspection                     |
| `EnableIpDetection`        | bool   | `true`  | Enable IP-based detection                         |
| `EnableBehavioralAnalysis` | bool   | `true`  | Enable behavioral rate analysis                   |
| `EnableAiDetection`        | bool   | `true`  | **Enable AI-based classification (RECOMMENDED)**  |
| `EnableLlmDetection`       | bool   | `true`  | Enable LLM escalation for uncertain cases         |
| `EnableTestMode`           | bool   | `false` | Enable test mode headers (dev only!)              |
| `MaxRequestsPerMinute`     | int    | `60`    | Rate limit threshold (1-10000)                    |
| `CacheDurationSeconds`     | int    | `300`   | Cache duration for results (0-86400)              |

---

## AI Detection Settings (KEY FEATURE)

AI detection provides machine learning-based classification with continuous learning. **This is a key differentiator** -
the system improves over time.

### Providers

| Provider                  | Description                                        | Latency           | Use Case                     |
|---------------------------|----------------------------------------------------|-------------------|------------------------------|
| `Heuristic`               | Feature-weighted logistic regression with learning | <1ms              | **Default - fast, learns**   |
| `Ollama`                  | LLM-based analysis with full reasoning             | 50-500ms          | Escalation for complex cases |
| `HeuristicWithEscalation` | Heuristic first, LLM for uncertain                 | <1ms + escalation | Best accuracy                |

### Heuristic Provider (Recommended)

```json
{
  "BotDetection": {
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "TimeoutMs": 1000,
      "MaxConcurrentRequests": 10,
      "Heuristic": {
        "Enabled": true,
        "LoadLearnedWeights": true,
        "EnableWeightLearning": true,
        "MinConfidenceForLearning": 0.8,
        "LearningRate": 0.01,
        "WeightReloadIntervalMinutes": 60
      }
    }
  }
}
```

| Option                        | Type   | Default | Description                            |
|-------------------------------|--------|---------|----------------------------------------|
| `Enabled`                     | bool   | `true`  | Enable heuristic detection             |
| `LoadLearnedWeights`          | bool   | `true`  | Load weights from database on startup  |
| `EnableWeightLearning`        | bool   | `true`  | Update weights from detection feedback |
| `MinConfidenceForLearning`    | double | `0.8`   | Minimum confidence for weight updates  |
| `LearningRate`                | double | `0.01`  | Learning rate for weight adjustments   |
| `WeightReloadIntervalMinutes` | int    | `60`    | How often to reload weights from store |

### Ollama Provider (LLM Escalation)

```json
{
  "BotDetection": {
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Ollama",
      "TimeoutMs": 15000,
      "MaxConcurrentRequests": 5,
      "Ollama": {
        "Endpoint": "http://localhost:11434",
        "Model": "gemma3:4b",
        "UseJsonMode": true,
        "Temperature": 0.1,
        "MaxTokens": 256
      }
    }
  }
}
```

| Option        | Type   | Default                  | Description                        |
|---------------|--------|--------------------------|------------------------------------|
| `Endpoint`    | string | `http://localhost:11434` | Ollama API endpoint                |
| `Model`       | string | `gemma3:4b`              | Model name (gemma3:4b recommended) |
| `UseJsonMode` | bool   | `true`                   | Request JSON output                |
| `Temperature` | double | `0.1`                    | Randomness (0.0-1.0)               |
| `MaxTokens`   | int    | `256`                    | Max response tokens                |

See [ai-detection.md](ai-detection.md) for full details on models and learning.

---

## Learning System Settings (KEY FEATURE)

The learning system enables continuous improvement. Patterns are tracked, reputations evolve, and the model improves
over time.

```json
{
  "BotDetection": {
    "Learning": {
      "Enabled": true,
      "LearningRate": 0.1,
      "MaxSupport": 1000,
      "ScoreDecayTauHours": 168,
      "SupportDecayTauHours": 336,
      "Prior": 0.5,
      "PromoteToBadScore": 0.9,
      "PromoteToBadSupport": 50,
      "DemoteFromBadScore": 0.7,
      "DemoteFromBadSupport": 100,
      "GcEligibleDays": 90,
      "EnableDriftDetection": true,
      "DriftThreshold": 0.05
    }
  }
}
```

| Option                 | Type   | Default | Description                           |
|------------------------|--------|---------|---------------------------------------|
| `Enabled`              | bool   | `true`  | **Enable learning (RECOMMENDED)**     |
| `LearningRate`         | double | `0.1`   | EMA learning rate (0.01-0.5)          |
| `MaxSupport`           | int    | `1000`  | Max effective sample count            |
| `ScoreDecayTauHours`   | int    | `168`   | Score decay time constant (7 days)    |
| `SupportDecayTauHours` | int    | `336`   | Support decay time constant (14 days) |
| `Prior`                | double | `0.5`   | Neutral prior for new patterns        |
| `PromoteToBadScore`    | double | `0.9`   | Score to promote to ConfirmedBad      |
| `PromoteToBadSupport`  | int    | `50`    | Support to promote to ConfirmedBad    |
| `DemoteFromBadScore`   | double | `0.7`   | Score to demote from ConfirmedBad     |
| `DemoteFromBadSupport` | int    | `100`   | Support to demote (hysteresis)        |
| `GcEligibleDays`       | int    | `90`    | Days before pattern GC eligible       |
| `EnableDriftDetection` | bool   | `true`  | Detect concept drift                  |
| `DriftThreshold`       | double | `0.05`  | Drift alert threshold                 |

See [learning-and-reputation.md](learning-and-reputation.md) for full details.

---

## Blocking Settings

| Option                       | Type   | Default           | Description                      |
|------------------------------|--------|-------------------|----------------------------------|
| `BlockDetectedBots`          | bool   | `false`           | Enable automatic blocking        |
| `BlockStatusCode`            | int    | `403`             | HTTP status when blocking        |
| `BlockMessage`               | string | `"Access denied"` | Response message                 |
| `MinConfidenceToBlock`       | double | `0.8`             | Confidence required to block     |
| `AllowVerifiedSearchEngines` | bool   | `true`            | Let Googlebot, Bingbot through   |
| `AllowSocialMediaBots`       | bool   | `true`            | Let Facebook, Twitter through    |
| `AllowMonitoringBots`        | bool   | `true`            | Let UptimeRobot, Pingdom through |
| `DefaultActionPolicyName`    | string | `"block"`         | Default action policy            |

---

## Version Age Settings

Detects outdated browser and OS versions that are suspicious.

```json
{
  "BotDetection": {
    "VersionAge": {
      "Enabled": true,
      "CheckBrowserVersion": true,
      "CheckOsVersion": true,
      "MaxBrowserVersionAge": 10
    }
  }
}
```

| Option                 | Type | Default | Description                     |
|------------------------|------|---------|---------------------------------|
| `Enabled`              | bool | `true`  | Enable version age detection    |
| `CheckBrowserVersion`  | bool | `true`  | Check browser version freshness |
| `CheckOsVersion`       | bool | `true`  | Check OS version freshness      |
| `MaxBrowserVersionAge` | int  | `10`    | Max browser major versions old  |

---

## Security Tools Settings

Detects security scanners, exploit frameworks, and hacking tools.

```json
{
  "BotDetection": {
    "SecurityTools": {
      "Enabled": true,
      "BlockSecurityTools": true,
      "LogDetections": true,
      "CustomPatterns": [],
      "ExcludedPatterns": [],
      "EnabledCategories": [],
      "HoneypotRedirectUrl": null
    }
  }
}
```

| Option                | Type     | Default | Description                              |
|-----------------------|----------|---------|------------------------------------------|
| `Enabled`             | bool     | `true`  | Enable security tool detection           |
| `BlockSecurityTools`  | bool     | `true`  | Block security tools immediately         |
| `LogDetections`       | bool     | `true`  | Log at Warning level                     |
| `CustomPatterns`      | string[] | `[]`    | Custom tool patterns to add              |
| `ExcludedPatterns`    | string[] | `[]`    | Patterns to allow                        |
| `EnabledCategories`   | string[] | `[]`    | Categories to detect (empty = all)       |
| `HoneypotRedirectUrl` | string   | `null`  | Redirect to honeypot instead of blocking |

See [security-tools-detection.md](security-tools-detection.md) for details.

---

## Project Honeypot Settings

Uses HTTP:BL DNS lookups for IP reputation. Requires a FREE API key
from [projecthoneypot.org](https://www.projecthoneypot.org/).

```json
{
  "BotDetection": {
    "ProjectHoneypot": {
      "Enabled": true,
      "AccessKey": "your-12-char-key",
      "HighThreatThreshold": 25,
      "MaxDaysAge": 90,
      "TimeoutMs": 1000,
      "CacheDurationSeconds": 1800,
      "SkipLocalIps": true,
      "TreatHarvestersAsMalicious": true,
      "TreatCommentSpammersAsMalicious": true,
      "TreatSuspiciousAsSuspicious": true
    }
  }
}
```

| Option                            | Type   | Default | Description                  |
|-----------------------------------|--------|---------|------------------------------|
| `Enabled`                         | bool   | `false` | Enable HTTP:BL lookups       |
| `AccessKey`                       | string | `null`  | Your 12-char API key         |
| `HighThreatThreshold`             | int    | `25`    | Threat score for high threat |
| `MaxDaysAge`                      | int    | `90`    | Max age for relevance        |
| `TimeoutMs`                       | int    | `1000`  | DNS lookup timeout           |
| `CacheDurationSeconds`            | int    | `1800`  | Cache duration (30 min)      |
| `SkipLocalIps`                    | bool   | `true`  | Skip private IPs             |
| `TreatHarvestersAsMalicious`      | bool   | `true`  | Flag email scrapers          |
| `TreatCommentSpammersAsMalicious` | bool   | `true`  | Flag comment spammers        |
| `TreatSuspiciousAsSuspicious`     | bool   | `true`  | Flag suspicious IPs          |

See [project-honeypot.md](project-honeypot.md) for details.

---

## Behavioral Settings

```json
{
  "BotDetection": {
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

See [behavioral-analysis.md](behavioral-analysis.md) for details.

---

## Client-Side Settings

```json
{
  "BotDetection": {
    "ClientSide": {
      "Enabled": true,
      "TokenSecret": "your-secret-key-min-32-chars-long",
      "TokenLifetimeSeconds": 300,
      "FingerprintCacheDurationSeconds": 1800,
      "CollectWebGL": true,
      "CollectCanvas": true,
      "CollectAudio": false,
      "MinIntegrityScore": 70,
      "HeadlessThreshold": 0.5
    }
  }
}
```

See [client-side-fingerprinting.md](client-side-fingerprinting.md) for details.

---

## Response Headers Settings

Add detection results to response headers for debugging and integration.

```json
{
  "BotDetection": {
    "ResponseHeaders": {
      "Enabled": true,
      "HeaderPrefix": "X-Bot-",
      "IncludePolicyName": true,
      "IncludeConfidence": true,
      "IncludeDetectors": true,
      "IncludeProcessingTime": true,
      "IncludeBotName": true,
      "IncludeFullJson": false
    }
  }
}
```

| Option                  | Type   | Default    | Description                 |
|-------------------------|--------|------------|-----------------------------|
| `Enabled`               | bool   | `false`    | Enable response headers     |
| `HeaderPrefix`          | string | `"X-Bot-"` | Prefix for all headers      |
| `IncludePolicyName`     | bool   | `true`     | Include policy name         |
| `IncludeConfidence`     | bool   | `true`     | Include confidence score    |
| `IncludeDetectors`      | bool   | `true`     | Include detector list       |
| `IncludeProcessingTime` | bool   | `true`     | Include timing              |
| `IncludeBotName`        | bool   | `true`     | Include bot name            |
| `IncludeFullJson`       | bool   | `false`    | Include full JSON (verbose) |

---

## Background Update Settings

| Option                       | Type | Default | Description                   |
|------------------------------|------|---------|-------------------------------|
| `EnableBackgroundUpdates`    | bool | `true`  | Enable automatic list updates |
| `UpdateIntervalHours`        | int  | `24`    | Hours between updates (1-168) |
| `UpdateCheckIntervalMinutes` | int  | `60`    | Minutes between update checks |
| `StartupDelaySeconds`        | int  | `5`     | Delay before first update     |
| `ListDownloadTimeoutSeconds` | int  | `30`    | Timeout per download          |
| `MaxDownloadRetries`         | int  | `3`     | Retries before giving up      |

---

## Throttling Settings

Adds artificial delay to slow down detected bots.

```json
{
  "BotDetection": {
    "Throttling": {
      "Enabled": true,
      "MinConfidenceToThrottle": 0.5,
      "BaseDelayMs": 1000,
      "MaxDelayMs": 10000,
      "EnableJitter": true,
      "JitterPercentage": 0.2
    }
  }
}
```

| Option                    | Type   | Default | Description                            |
|---------------------------|--------|---------|----------------------------------------|
| `Enabled`                 | bool   | `false` | Enable throttling for detected bots    |
| `MinConfidenceToThrottle` | double | `0.5`   | Minimum confidence to apply throttling |
| `BaseDelayMs`             | int    | `1000`  | Base delay in milliseconds             |
| `MaxDelayMs`              | int    | `10000` | Maximum delay in milliseconds          |
| `EnableJitter`            | bool   | `true`  | Add random jitter (less detectable)    |
| `JitterPercentage`        | double | `0.2`   | Jitter amount (0.0-1.0)                |

---

## SQLite Database

BotDetection uses SQLite to store bot patterns, datacenter IP ranges, learned weights, and reputation data. The database
is automatically created and initialized on first use.

### What's Stored in the Database

| Table                 | Purpose                                            |
|-----------------------|----------------------------------------------------|
| `bot_patterns`        | User-Agent regex patterns from configured sources  |
| `datacenter_ips`      | Cloud provider IP ranges (AWS, GCP, Azure, Oracle) |
| `list_updates`        | Last update timestamps for each data source        |
| `learned_weights`     | Heuristic model weights from continuous learning   |
| `reputation_patterns` | Pattern reputation scores and states               |

### Default Behavior

By default, the database is created at:

```
{AppContext.BaseDirectory}/botdetection.db
```

For ASP.NET applications, this is typically in the application's root directory.

### Configuration

```json
{
  "BotDetection": {
    "DatabasePath": "/app/data/botdetection.db",
    "EnableDatabaseWalMode": true
  }
}
```

| Option                  | Type   | Default | Description                            |
|-------------------------|--------|---------|----------------------------------------|
| `DatabasePath`          | string | `null`  | Custom path to SQLite database         |
| `EnableDatabaseWalMode` | bool   | `true`  | Enable WAL mode for better concurrency |

### Docker / Container Deployments

For persistent data across container restarts, mount a volume:

```bash
# Create a volume for bot detection data
docker run -d \
  -v botdetection-data:/app/data \
  -e BotDetection__DatabasePath=/app/data/botdetection.db \
  your-app
```

Or with docker-compose:

```yaml
services:
  app:
    volumes:
      - botdetection-data:/app/data
    environment:
      - BotDetection__DatabasePath=/app/data/botdetection.db

volumes:
  botdetection-data:
```

### Multi-Instance Deployments

SQLite with WAL mode supports multiple readers but only one writer. For high-availability setups:

1. **Single-instance**: Default SQLite works well
2. **Read replicas**: Each instance can have its own database; they sync via background updates
3. **Shared storage**: Mount the same volume across instances (WAL mode handles contention)
4. **Redis/External**: For true multi-writer scenarios, consider implementing a custom `IWeightStore`

### Database Maintenance

The database is self-maintaining:

- **Auto-updates**: Bot lists refresh every 24 hours (configurable via `UpdateIntervalHours`)
- **Stale data check**: On startup, if data is >24 hours old, it refreshes automatically
- **Learned weights**: Periodic consolidation removes low-impact patterns (configurable via
  `PatternConsolidationIntervalHours`)
- **WAL checkpointing**: SQLite handles this automatically

### Troubleshooting

```bash
# Check database size and tables
sqlite3 botdetection.db ".tables"
sqlite3 botdetection.db "SELECT list_type, last_update, record_count FROM list_updates"

# Count patterns and IPs
sqlite3 botdetection.db "SELECT COUNT(*) FROM bot_patterns"
sqlite3 botdetection.db "SELECT COUNT(*) FROM datacenter_ips"

# Force refresh (delete and restart app)
rm botdetection.db
```

---

## Production Storage (PostgreSQL)

For multi-server deployments or >100K requests/day, replace SQLite with PostgreSQL.
See the [Deployment Guide](deployment-guide.md) for full setup.

Package: `Mostlylucid.BotDetection.UI.PostgreSQL`

| Feature | Docs |
|---------|------|
| PostgreSQL setup | [PostgreSQL README](../../Mostlylucid.BotDetection.UI.PostgreSQL/README.md) |
| TimescaleDB | [TIMESCALEDB_GUIDE.md](../../Mostlylucid.BotDetection.UI.PostgreSQL/TIMESCALEDB_GUIDE.md) |
| pgvector | [PGVECTOR_GUIDE.md](../../Mostlylucid.BotDetection.UI.PostgreSQL/PGVECTOR_GUIDE.md) |
| Architecture | [ARCHITECTURE.md](../../Mostlylucid.BotDetection.UI.PostgreSQL/ARCHITECTURE.md) |

---

## Pattern Learning Settings (Experimental)

```json
{
  "BotDetection": {
    "EnablePatternLearning": true,
    "MinConfidenceToLearn": 0.9,
    "MaxLearnedPatterns": 1000,
    "PatternConsolidationIntervalHours": 24
  }
}
```

| Option                              | Type   | Default | Description                           |
|-------------------------------------|--------|---------|---------------------------------------|
| `EnablePatternLearning`             | bool   | `false` | Enable automatic pattern learning     |
| `MinConfidenceToLearn`              | double | `0.9`   | Minimum confidence to learn a pattern |
| `MaxLearnedPatterns`                | int    | `1000`  | Maximum patterns to store             |
| `PatternConsolidationIntervalHours` | int    | `24`    | Cleanup interval                      |

---

## Logging Settings

BotDetection uses standard ASP.NET Core `ILogger`. Logs go to wherever your application is configured to send them (
console, file, etc.).

### Recommended Configuration (Default)

Information-level to console for visibility, Warning-level to files for persistence:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Mostlylucid.BotDetection": "Information"
    },
    "Console": {
      "LogLevel": {
        "Default": "Information",
        "Mostlylucid.BotDetection": "Information"
      }
    },
    "File": {
      "LogLevel": {
        "Default": "Warning",
        "Mostlylucid.BotDetection": "Warning"
      }
    }
  }
}
```

### With Serilog (Recommended for Production)

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Mostlylucid.BotDetection": "Information"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "restrictedToMinimumLevel": "Information"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "logs/botdetection-.log",
          "rollingInterval": "Day",
          "restrictedToMinimumLevel": "Warning"
        }
      }
    ]
  }
}
```

### Suppress All BotDetection Logging

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Mostlylucid.BotDetection": "None"
    }
  }
}
```

### Enable Verbose Logging (Development/Debugging)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Mostlylucid.BotDetection": "Debug",
      "Mostlylucid.BotDetection.Services.BotListUpdateService": "Information"
    }
  }
}
```

### Fine-Grained Logging Control

Control specific components:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Mostlylucid.BotDetection": "Information",
      "Mostlylucid.BotDetection.Services.BotDetectionService": "Debug",
      "Mostlylucid.BotDetection.Services.BotListUpdateService": "Information",
      "Mostlylucid.BotDetection.Data.BotListDatabase": "Warning",
      "Mostlylucid.BotDetection.Middleware": "Information"
    }
  }
}
```

### BotDetection-Specific Logging Options

These options control what data is included in log messages:

| Option                  | Type | Default | Description                                                |
|-------------------------|------|---------|------------------------------------------------------------|
| `LogAllRequests`        | bool | `false` | Log all requests (verbose, not recommended for production) |
| `LogDetailedReasons`    | bool | `true`  | Include detection reasons in logs                          |
| `LogPerformanceMetrics` | bool | `false` | Include timing and cache statistics                        |
| `LogIpAddresses`        | bool | `true`  | Include IP addresses (disable for GDPR compliance)         |
| `LogUserAgents`         | bool | `true`  | Include User-Agent strings (disable for privacy)           |

---

## Whitelists and Blocklists

```json
{
  "BotDetection": {
    "WhitelistedBotPatterns": ["Googlebot", "Bingbot", "Slackbot"],
    "WhitelistedIps": ["192.168.1.0/24"],
    "BlacklistedIps": ["10.0.0.1"],
    "DatacenterIpPrefixes": ["3.0.0.0/8", "13.0.0.0/8"]
  }
}
```

### Default Whitelisted Bots

```
Googlebot, Bingbot, Slackbot, DuckDuckBot, Baiduspider,
YandexBot, Sogou, Exabot, facebot, ia_archiver
```

### Default Datacenter IP Prefixes

```
3.0.0.0/8, 13.0.0.0/8, 18.0.0.0/8, 52.0.0.0/8    (AWS)
20.0.0.0/8, 40.0.0.0/8, 104.0.0.0/8              (Azure)
34.0.0.0/8, 35.0.0.0/8                            (GCP)
138.0.0.0/8, 139.0.0.0/8, 140.0.0.0/8            (Oracle)
```

---

## Environment-Specific Examples

### Development

```json
{
  "BotDetection": {
    "BotThreshold": 0.5,
    "BlockDetectedBots": false,
    "EnableTestMode": true,
    "DefaultActionPolicyName": "debug",
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "Heuristic": { "Enabled": true, "EnableWeightLearning": true }
    },
    "Learning": { "Enabled": true },
    "LogAllRequests": true,
    "LogPerformanceMetrics": true
  }
}
```

### Staging

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "BlockDetectedBots": false,
    "DefaultActionPolicyName": "shadow",
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "Heuristic": { "Enabled": true, "EnableWeightLearning": true }
    },
    "Learning": { "Enabled": true, "EnableDriftDetection": true }
  }
}
```

### Production

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth",
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "Heuristic": { "Enabled": true, "EnableWeightLearning": true }
    },
    "Learning": {
      "Enabled": true,
      "EnableDriftDetection": true
    },
    "LogAllRequests": false,
    "LogPerformanceMetrics": false
  }
}
```

---

## Test Mode Simulations

When `EnableTestMode` is true, you can simulate different bot types with the `ml-bot-test-mode` header:

```json
{
  "BotDetection": {
    "EnableTestMode": true,
    "TestModeSimulations": {
      "human": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0",
      "googlebot": "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
      "scrapy": "Scrapy/2.11 (+https://scrapy.org)",
      "curl": "curl/8.4.0",
      "malicious": "sqlmap/1.7 (http://sqlmap.org)",
      "gptbot": "Mozilla/5.0 AppleWebKit/537.36 (compatible; GPTBot/1.0)",
      "nikto": "Mozilla/5.00 (Nikto/2.1.6)",
      "honeypot-harvester": "<test-honeypot:harvester>",
      "honeypot-spammer": "<test-honeypot:spammer>"
    }
  }
}
```

Usage:

```bash
curl -H "ml-bot-test-mode: googlebot" http://localhost/api/test
```
