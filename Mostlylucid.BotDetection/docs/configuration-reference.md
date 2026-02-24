# StyloBot Configuration Reference

This document is the comprehensive settings reference for `BotDetectionOptions` and related configuration classes. Every configurable property is listed with its type, default value, and description.

## Configuration Hierarchy

StyloBot settings are resolved in the following priority order (highest wins):

1. **Environment variables** -- e.g. `BotDetection__BotThreshold=0.8` (double-underscore separator)
2. **appsettings.json / appsettings.{Environment}.json** -- standard ASP.NET Core configuration
3. **YAML detector manifests** -- per-detector defaults in `Orchestration/Manifests/detectors/*.yaml`
4. **Code defaults** -- the initializer values shown in each table below

All settings live under the `BotDetection` configuration section:

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "EnableUserAgentDetection": true,
    ...
  }
}
```

---

## Table of Contents

- [Core Detection Settings](#core-detection-settings)
- [Detection Strategy Toggles](#detection-strategy-toggles)
- [AI Detection Settings](#ai-detection-settings)
- [Ollama Options](#ollama-options)
- [LLamaSharp Options](#llamasharp-options)
- [Heuristic Options](#heuristic-options)
- [LLM Coordinator Options](#llm-coordinator-options)
- [Blocking Policy Settings](#blocking-policy-settings)
- [Behavioral Analysis Settings](#behavioral-analysis-settings)
- [Behavioral Options (Advanced)](#behavioral-options-advanced)
- [Anomaly Saver Options](#anomaly-saver-options)
- [Version Age Options](#version-age-options)
- [Caching Settings](#caching-settings)
- [Background Update Service Settings](#background-update-service-settings)
- [List Update Schedule Options](#list-update-schedule-options)
- [External Data Sources Configuration](#external-data-sources-configuration)
- [Fast Path / Signal-Driven Detection Settings](#fast-path--signal-driven-detection-settings)
- [Signature Matching Options](#signature-matching-options)
- [Pattern Reputation Settings](#pattern-reputation-settings)
- [Blackboard Orchestrator Settings](#blackboard-orchestrator-settings)
- [Cluster Options](#cluster-options)
- [Country Reputation Options](#country-reputation-options)
- [Pattern Learning Settings (Legacy)](#pattern-learning-settings-legacy)
- [Storage Settings](#storage-settings)
- [Qdrant Options](#qdrant-options)
- [Whitelists and Customization](#whitelists-and-customization)
- [Logging Settings](#logging-settings)
- [Client-Side Detection Settings](#client-side-detection-settings)
- [Security Detection Settings](#security-detection-settings)
- [Project Honeypot Options](#project-honeypot-options)
- [Global Enable/Disable](#global-enabledisable)
- [Upstream Trust Settings](#upstream-trust-settings)
- [Response Headers Configuration](#response-headers-configuration)
- [Throttling Configuration](#throttling-configuration)
- [Policy Configuration](#policy-configuration)
- [Action Policy Configuration](#action-policy-configuration)
- [Path Exclusions and Overrides](#path-exclusions-and-overrides)
- [Pack Architecture Settings](#pack-architecture-settings)
- [Training Endpoints Options](#training-endpoints-options)
- [Block Action Policy Options](#block-action-policy-options)
- [Throttle Action Policy Options](#throttle-action-policy-options)
- [Environment Variable Mapping](#environment-variable-mapping)

---

## Core Detection Settings

Top-level detection thresholds and identity configuration.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BotThreshold` | `double` | `0.7` | Confidence threshold above which a request is classified as a bot (0.0-1.0) |
| `EnableTestMode` | `bool` | `false` | Enable test mode (allows ml-bot-test-mode header to override detection) |
| `TestModeSimulations` | `Dictionary<string, string>` | `{}` | Test mode name-to-simulated-User-Agent mapping |
| `SignatureHashKey` | `string?` | `null` | Base64-encoded HMAC key for PII signature hashing (min 128 bits) |
| `ExcludeLocalIpFromBroadcast` | `bool` | `true` | Exclude local/private IPs from SignalR broadcasts and live feed |
| `SignatureDescriptionThreshold` | `int` | `50` | Request count threshold before LLM generates a signature description (0 = disable) |

## Detection Strategy Toggles

Enable or disable individual detection strategies.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EnableUserAgentDetection` | `bool` | `true` | Enable user-agent based detection (static pattern matching) |
| `EnableHeaderAnalysis` | `bool` | `true` | Enable header analysis (Accept, Accept-Language, etc.) |
| `EnableIpDetection` | `bool` | `true` | Enable IP-based detection (datacenter IP range checks) |
| `EnableBehavioralAnalysis` | `bool` | `true` | Enable behavioral analysis (rate limiting, request patterns) |
| `EnableLlmDetection` | `bool` | `false` | Enable AI-based detection (Ollama or ONNX) |

## AI Detection Settings

Section path: `BotDetection:AiDetection`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Provider` | `AiProvider` | `Ollama` | AI provider: `Ollama`, `LlamaSharp`, or `Heuristic` |
| `TimeoutMs` | `int` | `15000` | Timeout for AI detection in ms (100-60000) |
| `MaxConcurrentRequests` | `int` | `5` | Maximum concurrent AI requests (1-100) |

### Ollama Options

Section path: `BotDetection:AiDetection:Ollama`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Whether Ollama LLM detection is enabled |
| `Endpoint` | `string` | `"http://localhost:11434"` | Ollama API endpoint URL |
| `Model` | `string` | `"qwen3:0.6b"` | Ollama model name for bot detection |
| `UseJsonMode` | `bool` | `true` | Use Ollama JSON mode for structured output |
| `CustomPrompt` | `string?` | `null` | Custom system prompt (use `{REQUEST_INFO}` placeholder) |
| `NumThreads` | `int` | `4` | Number of CPU threads for Ollama inference |

### LLamaSharp Options

Section path: `BotDetection:AiDetection:LlamaSharp`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Whether LLamaSharp inference is enabled |
| `ModelPath` | `string` | `"Qwen/Qwen2.5-0.5B-Instruct-GGUF/..."` | Path to GGUF model file or Hugging Face identifier |
| `ModelCacheDir` | `string?` | `null` | Cache directory for downloaded models |
| `ContextSize` | `int` | `512` | Maximum context size in tokens |
| `ThreadCount` | `int` | `Environment.ProcessorCount` | Number of CPU threads for inference |
| `Temperature` | `float` | `0.1` | Generation temperature (0.0 = deterministic, 1.0 = creative) |
| `MaxTokens` | `int` | `150` | Maximum tokens to generate per inference |
| `TimeoutMs` | `int` | `10000` | Inference timeout in ms |

### Heuristic Options

Section path: `BotDetection:AiDetection:Heuristic`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Whether heuristic detection is enabled |
| `LoadLearnedWeights` | `bool` | `true` | Load learned weights from WeightStore on startup |
| `EnableWeightLearning` | `bool` | `true` | Update weights based on detection feedback |
| `MinConfidenceForLearning` | `double` | `0.8` | Minimum confidence for learning updates (0.0-1.0) |
| `LearningRate` | `double` | `0.01` | Learning rate for weight updates (0.001-0.5) |
| `WeightReloadIntervalMinutes` | `int` | `60` | How often to reload weights from store (0 = startup only) |

### LLM Coordinator Options

Section path: `BotDetection:LlmCoordinator`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ChannelCapacity` | `int` | `20` | Maximum pending LLM classification requests |
| `MinProbabilityToEnqueue` | `double` | `0.3` | Minimum bot probability to enqueue for LLM analysis |
| `MaxProbabilityToEnqueue` | `double` | `0.85` | Maximum bot probability to enqueue (above = clearly bot) |
| `BaseSampleRate` | `double` | `0.05` | Base sampling rate for drift detection (5%) |
| `HighRiskConfirmationRate` | `double` | `0.1` | Sampling rate for high-risk confirmation (10%) |
| `ConclusiveSkipWindow` | `TimeSpan` | `1 hour` | Skip enqueue if conclusive AND last LLM run within this window |

## Blocking Policy Settings

Controls how detected bots are blocked.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BlockDetectedBots` | `bool` | `false` | Enable automatic blocking of detected bots |
| `BlockStatusCode` | `int` | `403` | HTTP status code when blocking (403, 429, 503) |
| `BlockMessage` | `string` | `"Access denied"` | Response body message when blocking bots |
| `MinConfidenceToBlock` | `double` | `0.8` | Minimum confidence required to block |
| `AllowVerifiedSearchEngines` | `bool` | `true` | Allow Googlebot, Bingbot, etc. through |
| `AllowSocialMediaBots` | `bool` | `true` | Allow Facebook, Twitter, LinkedIn preview bots |
| `AllowMonitoringBots` | `bool` | `true` | Allow UptimeRobot, Pingdom, etc. |
| `AllowTools` | `bool` | `false` | Allow developer HTTP tools (curl, wget, python-requests) |

## Behavioral Analysis Settings

Top-level behavioral analysis configuration.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxRequestsPerMinute` | `int` | `60` | Maximum requests per IP per minute (1-10000) |
| `BehavioralWindowSeconds` | `int` | `60` | Time window for behavioral analysis in seconds |

### Behavioral Options (Advanced)

Section path: `BotDetection:Behavioral`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ApiKeyHeader` | `string?` | `null` | HTTP header name for API key rate limiting |
| `ApiKeyRateLimit` | `int` | `0` | Rate limit per API key per minute (0 = 2x MaxRequestsPerMinute) |
| `UserIdClaim` | `string?` | `null` | Claim name for per-user rate limiting (e.g., "sub") |
| `UserIdHeader` | `string?` | `null` | HTTP header for user ID fallback (e.g., "X-User-Id") |
| `UserRateLimit` | `int` | `0` | Rate limit per user per minute (0 = 3x MaxRequestsPerMinute) |
| `EnableAnomalyDetection` | `bool` | `true` | Enable behavior anomaly detection (sudden spikes, unusual paths) |
| `SpikeThresholdMultiplier` | `double` | `5.0` | Multiplier for detecting request spikes |
| `NewPathAnomalyThreshold` | `double` | `0.8` | Threshold for new-path access rate anomaly (0.0-1.0) |
| `AnalysisWindow` | `TimeSpan` | `15 minutes` | Analysis window for behavioral pattern detection |
| `EnableAdvancedPatternDetection` | `bool` | `true` | Enable entropy analysis, Markov chains, time-series anomaly detection |
| `MinRequestsForPatternAnalysis` | `int` | `10` | Minimum requests before performing statistical analysis |
| `IdentityHashSalt` | `string` | `random GUID` | Salt for identity hashing in pattern analysis |

### Anomaly Saver Options

Section path: `BotDetection:AnomalySaver`

Writes bot detection events to rolling JSON files.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Enable anomaly saver background service |
| `OutputPath` | `string` | `"./logs/bot-detections.jsonl"` | Output file path for detection events |
| `MinBotProbabilityThreshold` | `double` | `0.5` | Minimum bot probability to save (0.0-1.0) |
| `BatchSize` | `int` | `50` | Events to batch before writing to file |
| `FlushInterval` | `TimeSpan` | `5 seconds` | Max time before flushing buffered events |
| `RollingInterval` | `TimeSpan` | `1 hour` | Time interval for rolling to new file |
| `MaxFileSizeBytes` | `long` | `10485760` (10 MB) | Max file size before rolling (0 = disable) |
| `RetentionDays` | `int` | `30` | Days to retain old files (0 = keep forever) |

### Version Age Options

Section path: `BotDetection:VersionAge`

Detects bots using outdated or impossible browser/OS combinations.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable version age detection |
| `UpdateIntervalHours` | `int` | `24` | Update interval for version data in hours |
| `CheckBrowserVersion` | `bool` | `true` | Enable browser version checking |
| `MaxBrowserVersionAge` | `int` | `10` | Max browser major version age to consider current |
| `BrowserSeverelyOutdatedConfidence` | `double` | `0.4` | Confidence boost for >20 versions behind |
| `BrowserModeratelyOutdatedConfidence` | `double` | `0.2` | Confidence boost for 10-20 versions behind |
| `BrowserSlightlyOutdatedConfidence` | `double` | `0.1` | Confidence boost for 5-10 versions behind |
| `CheckOsVersion` | `bool` | `true` | Enable OS version checking |
| `OsAncientConfidence` | `double` | `0.3` | Confidence boost for ancient OS (Windows XP, etc.) |
| `OsVeryOldConfidence` | `double` | `0.1` | Confidence boost for very old OS (Windows 7, etc.) |
| `OsOldConfidence` | `double` | `0.05` | Confidence boost for old OS (Windows 8, etc.) |
| `CombinedOutdatedBoost` | `double` | `0.15` | Extra boost when both browser AND OS are outdated |
| `ImpossibleCombinationConfidence` | `double` | `0.5` | Boost for impossible combos (Chrome 130 on XP) |
| `FallbackBrowserVersions` | `Dictionary<string, int>` | `{}` | Fallback versions when API unavailable |
| `OsAgeClassification` | `Dictionary<string, string>` | *(see source)* | OS version age classification mapping |
| `MinBrowserVersionByOs` | `Dictionary<string, int>` | *(see source)* | Minimum browser version per OS (impossible combo detection) |

## Caching Settings

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `CacheDurationSeconds` | `int` | `300` | Cache duration for detection results (0-86400, 0 = disabled) |
| `MaxCacheEntries` | `int` | `10000` | Maximum cached detection results |

## Background Update Service Settings

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EnableBackgroundUpdates` | `bool` | `true` | Enable background bot list update service |
| `UpdateIntervalHours` | `int` | `24` | **DEPRECATED** -- Use `UpdateSchedule` with cron instead |
| `UpdateCheckIntervalMinutes` | `int` | `60` | **DEPRECATED** -- Use `UpdateSchedule` with cron instead |
| `ListDownloadTimeoutSeconds` | `int` | `30` | Timeout for downloading bot lists |
| `MaxDownloadRetries` | `int` | `3` | Maximum retries for failed list downloads |
| `StartupDelaySeconds` | `int` | `5` | Delay before loading lists on startup |

### List Update Schedule Options

Section path: `BotDetection:UpdateSchedule`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Cron` | `string` | `"0 2 * * *"` | Cron expression for update schedule (daily at 2 AM UTC) |
| `Timezone` | `string` | `"UTC"` | Timezone for cron evaluation |
| `Signal` | `string` | `"botlist.update"` | Signal to emit on update completion |
| `Key` | `string?` | `null` | Optional tracking key |
| `RunOnStartup` | `bool` | `true` | Run update immediately on startup |
| `Description` | `string` | `"Daily bot list update"` | Human-readable description |
| `MaxExecutionSeconds` | `int` | `300` | Maximum execution time per update run |
| `UseDurableTask` | `bool` | `false` | Persist update progress for resume after restart |

## External Data Sources Configuration

Section path: `BotDetection:DataSources`

Each data source has the same sub-properties (`Enabled`, `Url`, `Description`, `UpdateSchedule`).

| Source | Config Path | Enabled by Default | Description |
|--------|------------|-------------------|-------------|
| `IsBot` | `DataSources:IsBot` | `true` | Comprehensive bot regex patterns from omrilotan/isbot |
| `Matomo` | `DataSources:Matomo` | `false` | Categorized bot patterns with metadata |
| `CrawlerUserAgents` | `DataSources:CrawlerUserAgents` | `false` | Community-maintained crawler patterns |
| `AwsIpRanges` | `DataSources:AwsIpRanges` | `true` | Official Amazon IP ranges |
| `GcpIpRanges` | `DataSources:GcpIpRanges` | `true` | Official Google Cloud IP ranges |
| `AzureIpRanges` | `DataSources:AzureIpRanges` | `false` | Azure IP ranges (URL changes weekly) |
| `CloudflareIpv4` | `DataSources:CloudflareIpv4` | `true` | Cloudflare IPv4 ranges |
| `CloudflareIpv6` | `DataSources:CloudflareIpv6` | `true` | Cloudflare IPv6 ranges |
| `BrowserVersions` | `DataSources:BrowserVersions` | `true` | Current browser versions from browsers.fyi |
| `ScannerUserAgents` | `DataSources:ScannerUserAgents` | `true` | Security scanner user agents |
| `CoreRuleSetScanners` | `DataSources:CoreRuleSetScanners` | `true` | OWASP CoreRuleSet scanner patterns |

Each `DataSourceConfig` has:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | varies | Whether this data source is enabled |
| `Url` | `string` | varies | URL to fetch data from |
| `Description` | `string` | varies | Human-readable description |
| `UpdateSchedule` | `ListUpdateScheduleOptions?` | `null` | Per-source update schedule (overrides global) |

## Fast Path / Signal-Driven Detection Settings

Section path: `BotDetection:FastPath`

Controls the dual-path architecture with fast synchronous and slow async detection.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable dual-path architecture |
| `MaxParallelDetectors` | `int` | `4` | Max detectors to run in parallel within a wave |
| `EnableWaveParallelism` | `bool` | `true` | Enable wave-based parallel execution |
| `ContinueOnWaveFailure` | `bool` | `true` | Continue to next wave on failure |
| `WaveTimeoutMs` | `int` | `50` | Per-wave timeout in ms |
| `AbortThreshold` | `double` | `0.95` | Confidence threshold for UA-only short-circuit |
| `SampleRate` | `double` | `0.01` | Sample rate for full-path analysis on aborted requests (1%) |
| `EarlyExitThreshold` | `double` | `0.85` | Confidence threshold for early exit from fast path |
| `SkipSlowPathThreshold` | `double` | `0.2` | Below this, skip slow path entirely |
| `SlowPathTriggerThreshold` | `double` | `0.5` | Threshold to trigger slow-path processing |
| `FastPathTimeoutMs` | `int` | `100` | Timeout for fast-path consensus in ms |
| `SlowPathQueueCapacity` | `int` | `10000` | Max events queued for slow-path |
| `AlwaysRunFullOnPaths` | `List<string>` | `[]` | Paths that always run full 8-layer detection |
| `EnableDriftDetection` | `bool` | `true` | Enable fast-path vs full-path drift detection |
| `DriftThreshold` | `double` | `0.005` | Disagreement rate threshold for drift alerts (0.5%) |
| `MinSamplesForDrift` | `int` | `100` | Minimum samples before evaluating drift |
| `DriftWindowHours` | `int` | `24` | Time window for drift detection |
| `EnableFeedbackLoop` | `bool` | `true` | Enable auto feedback from slow path to fast path |
| `FeedbackMinConfidence` | `double` | `0.9` | Minimum slow-path confidence to trigger feedback |
| `FeedbackMinOccurrences` | `int` | `5` | Minimum occurrences before feeding back |
| `FastPathDetectors` | `List<DetectorConfig>` | *(6 default detectors)* | Detectors that run on the fast path |
| `SlowPathDetectors` | `List<DetectorConfig>` | *(1 default detector)* | Detectors that run on the slow path |
| `AiPathDetectors` | `List<DetectorConfig>` | *(1 default detector)* | Detectors for AI escalation path |
| `SlowPathTriggers` | `List<string>` | `["HighConfidenceDetection", ...]` | Signals that trigger slow-path processing |

Each `DetectorConfig` has:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | `string` | `""` | Detector name (must match IDetector.Name) |
| `Signal` | `string` | `""` | Signal emitted when detector completes |
| `ExpectedLatencyMs` | `double` | `0` | Expected latency in ms |
| `Weight` | `double` | `1.0` | Weight for confidence in final score |
| `MinConfidenceToContribute` | `double` | `0.3` | Min confidence to contribute to early exit |
| `Category` | `string?` | `null` | Detector category |
| `Wave` | `int` | `1` | Wave number for parallel execution |
| `IsCacheable` | `bool` | `true` | Whether results can be cached |
| `TimeoutMs` | `int?` | `null` | Per-detector timeout (null = use policy timeout) |

### Signature Matching Options

Section path: `BotDetection:FastPath:SignatureMatching`

Multi-factor weighted scoring for returning client identification.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `WeightPrimary` | `double` | `100.0` | Weight for Primary match (HMAC of IP + UA) |
| `WeightIp` | `double` | `50.0` | Weight for IP-only match |
| `WeightUa` | `double` | `50.0` | Weight for UA-only match |
| `WeightIpSubnet` | `double` | `30.0` | Weight for IP /24 subnet match |
| `WeightClientSide` | `double` | `80.0` | Weight for client-side fingerprint (Canvas+WebGL) |
| `WeightPlugin` | `double` | `60.0` | Weight for plugin/font signature |
| `MinWeightForMatch` | `double` | `100.0` | Minimum combined weight for confident match |
| `MinWeightForWeakMatch` | `double` | `80.0` | Minimum weight for weak match (needs 3+ factors) |
| `MinFactorsForWeakMatch` | `int` | `3` | Minimum factors required for weak match |

## Pattern Reputation Settings

Section path: `BotDetection:Reputation`

Controls pattern reputation tracking with time decay and evidence-based updates. See the `ReputationOptions` class in `Data/PatternReputation.cs` for full details.

## Blackboard Orchestrator Settings

Section path: `BotDetection:Orchestrator`

Controls wave-based parallel execution, circuit breakers, and resilience. See `OrchestratorOptions` in `Orchestration/BlackboardOrchestrator.cs`.

Additional top-level orchestrator-related settings:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SignatureCoordinator` | `SignatureCoordinatorOptions` | `new()` | Cross-request signature tracking configuration |
| `ResponseCoordinator` | `ResponseCoordinatorOptions` | `new()` | Cross-request response analysis configuration |
| `SignatureConvergence` | `SignatureConvergenceOptions` | `new()` | Signature merge/split family detection |

### Cluster Options

Section path: `BotDetection:Cluster`

Bot cluster detection via Leiden/label propagation clustering.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ClusterIntervalSeconds` | `int` | `30` | How often to re-run clustering |
| `SimilarityThreshold` | `double` | `0.75` | Minimum pairwise similarity for an edge |
| `MinClusterSize` | `int` | `3` | Minimum cluster size to report |
| `MaxIterations` | `int` | `10` | Max label propagation iterations |
| `ProductSimilarityThreshold` | `double` | `0.85` | Threshold for "Bot Product" classification |
| `NetworkTemporalDensityThreshold` | `double` | `0.6` | Threshold for "Bot Network" classification |
| `MinBotProbabilityForClustering` | `double` | `0.5` | Minimum avg bot probability to enter clustering |
| `MinBotDetectionsToTrigger` | `int` | `20` | New detections that trigger early clustering run |
| `EnableSemanticEmbeddings` | `bool` | `true` | Enable semantic embeddings in clustering |
| `Algorithm` | `string` | `"leiden"` | Clustering algorithm: `leiden` or `label_propagation` |
| `LeidenResolution` | `double` | `1.0` | Leiden resolution (higher = more/smaller clusters) |
| `SemanticWeight` | `double` | `0.4` | Weight for semantic vs heuristic similarity (0-1) |
| `EnableLlmDescriptions` | `bool` | `false` | Enable LLM-generated cluster descriptions |
| `DescriptionModel` | `string` | `"qwen3:0.6b"` | LLM model for cluster descriptions |
| `DescriptionEndpoint` | `string?` | `null` | Ollama endpoint for descriptions (null = use main) |

### Country Reputation Options

Section path: `BotDetection:CountryReputation`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DecayTauHours` | `double` | `24` | Decay time constant in hours |
| `MinSampleSize` | `int` | `10` | Minimum detections before country rate is meaningful |

## Pattern Learning Settings (Legacy)

Use `Reputation` section instead for new deployments.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EnablePatternLearning` | `bool` | `false` | Enable learning and storing new bot patterns |
| `MinConfidenceToLearn` | `double` | `0.9` | Minimum confidence to learn a pattern |
| `MaxLearnedPatterns` | `int` | `1000` | Maximum number of stored learned patterns |
| `PatternConsolidationIntervalHours` | `int` | `24` | Interval for consolidating learned patterns |

## Storage Settings

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StorageProvider` | `StorageProvider` | `Sqlite` | Storage provider: `PostgreSQL`, `Sqlite`, or `Json` |
| `PostgreSQLConnectionString` | `string?` | `null` | PostgreSQL connection string (auto-enables PostgreSQL when set) |
| `DatabasePath` | `string?` | `null` | Path to SQLite/JSON file |
| `EnableDatabaseWalMode` | `bool` | `true` | Enable WAL mode for SQLite concurrent access |
| `WeightStoreCacheSize` | `int` | `1000` | Max weight entries cached in memory (LRU) |

### Qdrant Options

Section path: `BotDetection:Qdrant`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Enable Qdrant-backed similarity search |
| `Endpoint` | `string` | `"http://localhost:6334"` | Qdrant gRPC endpoint |
| `CollectionName` | `string` | `"stylobot-signatures"` | Collection name for signature vectors |
| `VectorDimension` | `int` | `64` | Vector dimension for heuristic features |
| `EnableEmbeddings` | `bool` | `false` | Enable ML embeddings via ONNX |
| `EmbeddingModel` | `string` | `"all-MiniLM-L6-v2.onnx"` | ONNX model file for embeddings |
| `EmbeddingDimension` | `int` | `384` | Embedding vector dimension |

## Whitelists and Customization

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `WhitelistedBotPatterns` | `List<string>` | `["Googlebot", "Bingbot", ...]` | Known good bot patterns (won't be flagged) |
| `DatacenterIpPrefixes` | `List<string>` | `["3.0.0.0/8", ...]` | Known datacenter IP ranges (CIDR notation) |
| `CustomBotPatterns` | `List<string>` | `[]` | Custom regex patterns to add to detection |
| `WhitelistedIps` | `List<string>` | `[]` | IPs/CIDRs to always allow (bypass detection) |
| `BlacklistedIps` | `List<string>` | `[]` | IPs/CIDRs to always block |

## Logging Settings

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `LogAllRequests` | `bool` | `false` | Log all detection results (not just bots) |
| `LogDetailedReasons` | `bool` | `true` | Log detailed detection reasons |
| `LogPerformanceMetrics` | `bool` | `false` | Log processing time, cache hits, etc. |
| `LogIpAddresses` | `bool` | `true` | Log IP addresses (disable for privacy) |
| `LogUserAgents` | `bool` | `true` | Log user agent strings (disable for privacy) |

## Client-Side Detection Settings

Section path: `BotDetection:ClientSide`

JavaScript-based headless browser and automation detection.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Enable client-side browser fingerprinting |
| `TokenSecret` | `string?` | `null` | Secret key for signing browser tokens |
| `TokenLifetimeSeconds` | `int` | `300` | Token lifetime in seconds |
| `FingerprintCacheDurationSeconds` | `int` | `1800` | Cache duration for fingerprint results (30 min) |
| `CollectionTimeoutMs` | `int` | `5000` | Client-side collection timeout in ms |
| `CollectWebGL` | `bool` | `true` | Collect WebGL fingerprint data |
| `CollectCanvas` | `bool` | `true` | Collect canvas fingerprint hash |
| `CollectAudio` | `bool` | `false` | Collect audio context fingerprint (reserved) |
| `MinIntegrityScore` | `int` | `70` | Minimum browser integrity score (0-100) |
| `HeadlessThreshold` | `double` | `0.5` | Headless likelihood threshold (0.0-1.0) |

## Security Detection Settings

Section path: `BotDetection:SecurityTools`

Detects vulnerability scanners, exploit frameworks, and hacking tools.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable security tool detection |
| `BlockSecurityTools` | `bool` | `true` | Block requests from security tools immediately |
| `LogDetections` | `bool` | `true` | Log security tool detections at Warning level |
| `CustomPatterns` | `List<string>` | `[]` | Custom tool patterns to add |
| `ExcludedPatterns` | `List<string>` | `[]` | Tool patterns to exclude (e.g., authorized pentests) |
| `EnabledCategories` | `List<string>` | `[]` | Categories to detect (empty = all) |
| `HoneypotRedirectUrl` | `string?` | `null` | Redirect security tools to honeypot URL |

### Project Honeypot Options

Section path: `BotDetection:ProjectHoneypot`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Enable Project Honeypot HTTP:BL lookups |
| `AccessKey` | `string?` | `null` | HTTP:BL access key (12 lowercase alphanumeric chars) |
| `HighThreatThreshold` | `int` | `25` | Threat score threshold for high threat (0-255) |
| `MaxDaysAge` | `int` | `90` | Max listing age in days to consider relevant |
| `TimeoutMs` | `int` | `1000` | DNS lookup timeout in ms |
| `CacheDurationSeconds` | `int` | `1800` | Cache duration for lookup results (30 min) |
| `SkipLocalIps` | `bool` | `true` | Skip lookup for local/private IPs |
| `TreatHarvestersAsMalicious` | `bool` | `true` | Treat email scrapers as malicious |
| `TreatCommentSpammersAsMalicious` | `bool` | `true` | Treat comment spammers as malicious |
| `TreatSuspiciousAsSuspicious` | `bool` | `true` | Treat suspicious entries as suspicious |

## Global Enable/Disable

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Master switch for all bot detection |

## Upstream Trust Settings

For gateway/reverse-proxy scenarios where detection runs on a YARP gateway.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `TrustUpstreamDetection` | `bool` | `false` | Trust upstream detection headers (X-Bot-Detected, etc.) |
| `UpstreamSignatureHeader` | `string?` | `null` | Header name for HMAC signature from upstream gateway |
| `UpstreamSignatureSecret` | `string?` | `null` | Shared secret (base64) for verifying upstream HMAC signatures |
| `UpstreamSignatureMaxAgeSeconds` | `int` | `300` | Max age for upstream HMAC signatures (replay prevention) |

## Response Headers Configuration

Section path: `BotDetection:ResponseHeaders`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Enable adding bot detection results to response headers |
| `HeaderPrefix` | `string` | `"X-Bot-"` | Prefix for all bot detection headers |
| `IncludePolicyName` | `bool` | `true` | Include policy name in headers |
| `IncludeConfidence` | `bool` | `true` | Include confidence score in headers |
| `IncludeDetectors` | `bool` | `true` | Include list of contributing detectors |
| `IncludeProcessingTime` | `bool` | `true` | Include processing time in ms |
| `IncludeBotName` | `bool` | `false` | Include detected bot name |
| `IncludeFullJson` | `bool` | `false` | Include full JSON result as Base64 header |
| `SkipPaths` | `List<string>` | `["/health", "/metrics", "/swagger"]` | Paths to skip adding headers |

## Throttling Configuration

Section path: `BotDetection:Throttling`

Legacy throttling settings. For fine-grained control, use Action Policies instead.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BaseDelaySeconds` | `int` | `60` | Base delay in seconds for Retry-After header |
| `MaxDelaySeconds` | `int` | `300` | Maximum delay in seconds |
| `EnableJitter` | `bool` | `true` | Enable jitter to randomize Retry-After |
| `JitterPercent` | `int` | `30` | Jitter range as percentage of BaseDelaySeconds |
| `ScaleByRisk` | `bool` | `false` | Scale delay based on risk score |
| `DelayResponse` | `bool` | `false` | Actually delay the TCP response |
| `ResponseDelayMs` | `int` | `1000` | Response delay in ms (capped at 5000) |
| `ThrottleMessage` | `string` | `"Please slow down and try again later."` | Custom throttle response message |
| `BlockRedirectUrl` | `string?` | `"/blocked"` | Redirect URL for blocked requests |
| `ChallengeType` | `string` | `"captcha"` | Challenge type: `captcha`, `pow`, `interstitial` |

## Policy Configuration

Section path: `BotDetection:Policies`, `BotDetection:PathPolicies`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Policies` | `Dictionary<string, DetectionPolicyConfig>` | `{}` | Named detection policies for path-based escalation |
| `PathPolicies` | `Dictionary<string, string>` | `{}` | Maps URL path patterns to policy names (glob support) |
| `DefaultPolicyName` | `string?` | `null` | Default policy when no path matches |
| `UseDefaultStaticPathPolicies` | `bool` | `true` | Auto-map static asset paths to "static" policy |
| `UseFileExtensionStaticDetection` | `bool` | `true` | Auto-detect static assets by file extension |
| `StaticAssetExtensions` | `List<string>` | `[]` | Custom static asset extensions (e.g., ".pdf") |
| `UseContentTypeStaticDetection` | `bool` | `false` | Detect static assets by Content-Type header |
| `StaticAssetMimeTypes` | `List<string>` | `[]` | Custom MIME type prefixes for static assets |
| `GlobalWeights` | `Dictionary<string, double>` | `{}` | Global detector weight overrides |

## Action Policy Configuration

Section path: `BotDetection:ActionPolicies`

Action policies define HOW to respond (block, throttle, challenge) and are separate from detection policies.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ActionPolicies` | `Dictionary<string, ActionPolicyConfig>` | `{}` | Named action policies |
| `DefaultActionPolicyName` | `string?` | `null` | Default action policy (null = "block") |
| `BotTypeActionPolicies` | `Dictionary<string, string>` | `{"Tool": "throttle-tools"}` | Per-bot-type action policy overrides |

Built-in action policies available without configuration:
- **Block**: `block`, `block-hard`, `block-soft`, `block-debug`
- **Throttle**: `throttle`, `throttle-gentle`, `throttle-moderate`, `throttle-aggressive`, `throttle-stealth`
- **Challenge**: `challenge`, `challenge-captcha`, `challenge-js`, `challenge-pow`
- **Redirect**: `redirect`, `redirect-honeypot`, `redirect-tarpit`, `redirect-error`
- **Other**: `logonly`, `shadow`, `debug`, `mask-pii`, `strip-pii` (`mask-pii`/`strip-pii` require `ResponsePiiMasking.Enabled = true`)

## Response PII Masking

Section path: `BotDetection:ResponsePiiMasking`

Controls response-time masking for action markers `mask-pii` and `strip-pii`.
Feature is disabled by default.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Global feature flag for response PII masking |
| `AutoApplyForHighConfidenceMalicious` | `bool` | `true` | Auto-apply `mask-pii` on high-confidence malicious traffic that is allowed through |
| `AutoApplyBotProbabilityThreshold` | `double` | `0.9` | Minimum bot probability for auto-apply (0.0-1.0) |
| `AutoApplyConfidenceThreshold` | `double` | `0.75` | Minimum confidence for auto-apply (0.0-1.0) |

```json
"ResponsePiiMasking": {
  "Enabled": false,
  "AutoApplyForHighConfidenceMalicious": true,
  "AutoApplyBotProbabilityThreshold": 0.9,
  "AutoApplyConfidenceThreshold": 0.75
}
```

## Path Exclusions and Overrides

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ExcludedPaths` | `List<string>` | `["/health", "/metrics"]` | Paths to completely exclude from detection |
| `SignatureOnlyPaths` | `List<string>` | `[]` | Paths where only signature generation runs |
| `PathOverrides` | `Dictionary<string, string>` | `{}` | Path overrides that always allow through (glob support) |

## Pack Architecture Settings

Ephemeral integration settings.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxSignalCapacity` | `int` | `10000` | Maximum signals in SignalSink |
| `SignalRetentionMinutes` | `int` | `5` | Signal retention time in minutes |
| `ParallelDetection` | `bool` | `true` | Enable parallel detection within waves |
| `EnableQuorumExit` | `bool` | `true` | Enable quorum-based early exit |
| `QuorumConfidenceThreshold` | `double` | `0.9` | Confidence threshold for quorum exit |
| `TimeoutMs` | `int` | `30000` | Overall detection timeout in ms |
| `LearningConfidenceThreshold` | `double` | `0.85` | Confidence threshold for learning system |
| `EscalationSalienceThreshold` | `double` | `0.8` | Salience threshold for escalation to persistent storage |

## Training Endpoints Options

Section path: `BotDetection:TrainingEndpoints`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable training data export endpoints |
| `RequireApiKey` | `bool` | `false` | Require X-Training-Api-Key header |
| `ApiKeys` | `List<string>` | `[]` | Allowed API keys for training endpoint access |
| `RateLimitPerMinute` | `int` | `30` | Max requests per minute per client IP |
| `MaxExportRecords` | `int` | `10000` | Max signatures returned by /export |

---

## Block Action Policy Options

Configuration for `BlockActionPolicy` instances via `ActionPolicies` in appsettings.json.

```json
"ActionPolicies": {
  "hardBlock": {
    "Type": "Block",
    "StatusCode": 403,
    "Message": "Access denied",
    "IncludeRiskScore": false,
    "Headers": { "X-Block-Reason": "bot-detection" }
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StatusCode` | `int` | `403` | HTTP status code (403, 429, 503) |
| `Message` | `string` | `"Access denied"` | Error message in response body |
| `ContentType` | `string` | `"application/json"` | Response content type |
| `IncludeRiskScore` | `bool` | `false` | Include risk score details in response |
| `Headers` | `Dictionary<string, string>` | `{}` | Additional response headers |

Built-in presets: `Soft` (429), `Hard` (403), `Debug` (403 with risk details).

## Throttle Action Policy Options

Configuration for `ThrottleActionPolicy` instances via `ActionPolicies` in appsettings.json.

```json
"ActionPolicies": {
  "softThrottle": {
    "Type": "Throttle",
    "BaseDelayMs": 500,
    "MaxDelayMs": 5000,
    "JitterPercent": 0.25,
    "ScaleByRisk": true
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BaseDelayMs` | `int` | `500` | Base delay in ms before jitter |
| `MinDelayMs` | `int` | `100` | Minimum delay floor after jitter |
| `MaxDelayMs` | `int` | `5000` | Maximum delay in ms |
| `JitterPercent` | `double` | `0.25` | Jitter percentage (0.0-1.0, e.g. 0.25 = +/-25%) |
| `ScaleByRisk` | `bool` | `true` | Scale delay based on risk score |
| `ExponentialBackoff` | `bool` | `false` | Use exponential backoff for repeated requests |
| `BackoffFactor` | `double` | `2.0` | Backoff multiplier for exponential backoff |
| `ReturnStatus` | `bool` | `false` | Return HTTP status after throttling (vs continue) |
| `StatusCode` | `int` | `429` | HTTP status code if ReturnStatus is true |
| `ContentType` | `string` | `"application/json"` | Response content type if ReturnStatus is true |
| `Message` | `string` | `"Request throttled - please slow down"` | Response body message |
| `IncludeHeaders` | `bool` | `false` | Include X-Throttle-Delay and X-Throttle-Policy headers |
| `IncludeRetryAfter` | `bool` | `true` | Include Retry-After header (requires IncludeHeaders) |

Built-in presets: `Gentle`, `Moderate`, `Aggressive`, `Stealth`, `Tools`.

---

## Environment Variable Mapping

ASP.NET Core maps nested configuration keys using double-underscore (`__`) separators. Common overrides:

| Environment Variable | Maps To | Example Value |
|---------------------|---------|---------------|
| `BotDetection__Enabled` | `Enabled` | `true` |
| `BotDetection__BotThreshold` | `BotThreshold` | `0.8` |
| `BotDetection__BlockDetectedBots` | `BlockDetectedBots` | `true` |
| `BotDetection__MinConfidenceToBlock` | `MinConfidenceToBlock` | `0.85` |
| `BotDetection__SignatureHashKey` | `SignatureHashKey` | `base64-encoded-key` |
| `BotDetection__EnableLlmDetection` | `EnableLlmDetection` | `true` |
| `BotDetection__AiDetection__Provider` | `AiDetection.Provider` | `Heuristic` |
| `BotDetection__AiDetection__TimeoutMs` | `AiDetection.TimeoutMs` | `10000` |
| `BotDetection__AiDetection__Ollama__Endpoint` | `AiDetection.Ollama.Endpoint` | `http://ollama:11434` |
| `BotDetection__AiDetection__Ollama__Model` | `AiDetection.Ollama.Model` | `qwen3:0.6b` |
| `BotDetection__StorageProvider` | `StorageProvider` | `PostgreSQL` |
| `BotDetection__PostgreSQLConnectionString` | `PostgreSQLConnectionString` | `Host=db;Database=bots;...` |
| `BotDetection__MaxRequestsPerMinute` | `MaxRequestsPerMinute` | `120` |
| `BotDetection__CacheDurationSeconds` | `CacheDurationSeconds` | `600` |
| `BotDetection__TrustUpstreamDetection` | `TrustUpstreamDetection` | `true` |
| `BotDetection__UpstreamSignatureSecret` | `UpstreamSignatureSecret` | `base64-encoded-secret` |
| `BotDetection__ProjectHoneypot__Enabled` | `ProjectHoneypot.Enabled` | `true` |
| `BotDetection__ProjectHoneypot__AccessKey` | `ProjectHoneypot.AccessKey` | `abcdefghijkl` |
| `BotDetection__ClientSide__Enabled` | `ClientSide.Enabled` | `true` |
| `BotDetection__ClientSide__TokenSecret` | `ClientSide.TokenSecret` | `your-production-secret` |
| `BotDetection__FastPath__AbortThreshold` | `FastPath.AbortThreshold` | `0.98` |
| `BotDetection__Qdrant__Enabled` | `Qdrant.Enabled` | `true` |
| `BotDetection__Qdrant__Endpoint` | `Qdrant.Endpoint` | `http://qdrant:6334` |
| `BotDetection__DefaultActionPolicyName` | `DefaultActionPolicyName` | `throttle-stealth` |
| `BotDetection__ResponsePiiMasking__Enabled` | `ResponsePiiMasking.Enabled` | `false` |
| `BotDetection__ResponsePiiMasking__AutoApplyForHighConfidenceMalicious` | `ResponsePiiMasking.AutoApplyForHighConfidenceMalicious` | `true` |
| `BotDetection__ResponsePiiMasking__AutoApplyBotProbabilityThreshold` | `ResponsePiiMasking.AutoApplyBotProbabilityThreshold` | `0.9` |
| `BotDetection__ResponsePiiMasking__AutoApplyConfidenceThreshold` | `ResponsePiiMasking.AutoApplyConfidenceThreshold` | `0.75` |
| `BOTDETECTION_TRAINING_API_KEYS` | `TrainingEndpoints.ApiKeys` | `key1,key2` |
| `STYLOBOT_MODEL_CACHE` | `AiDetection.LlamaSharp.ModelCacheDir` | `/app/models` |

Arrays can be set with indexed keys: `BotDetection__ExcludedPaths__0=/health`, `BotDetection__ExcludedPaths__1=/metrics`.
