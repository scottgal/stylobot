# Wave Orchestration & Learning Coordinator Design

## Overview

This document describes the wave-based orchestration pattern with keyed learning coordination, designed to:

1. **Never run LLM in hot path** - All expensive operations happen post-response
2. **Exit request thread ASAP** - Make fast decisions, validate later
3. **Enable safe blocking** - High-confidence blocks that verify in background
4. **Keyed sequential learning** - Process learning by signature key to bound memory

---

## Wave Definition

### Core Concept

A **Wave** is an execution stage containing parallel work items that complete before the next wave starts.
Inspired by audio synthesis: waves are sequential, items within waves are parallel.

```yaml
# File: Orchestration/Manifests/waves/wave.schema.yaml
wave:
  name: string                     # Unique identifier
  wave_number: int                 # Execution order (0 = first)
  description: string              # Human-readable purpose

  # What runs in this wave
  items:
    - type: atom | molecule
      manifest: string             # Path to manifest file
      required: bool               # Fail wave if this fails?
      salience_threshold: float    # Min salience to include output

  # Execution constraints
  execution:
    mode: parallel | sequential    # Within-wave parallelism
    max_concurrency: int           # Max parallel items
    timeout: TimeSpan              # Wave timeout
    fail_fast: bool                # Stop on first failure?

  # Flow control
  early_exit:
    enabled: bool                  # Can this wave exit pipeline?
    allow_signals: [string]        # Signals that allow early exit
    block_signals: [string]        # Signals that block early exit

  # Conditional execution
  conditional:
    when: [SignalCondition]        # Only run if these conditions met
    skip_when: [SignalCondition]   # Skip if these conditions met

  # Lane assignment
  lane: string                     # Concurrency pool
```

### Wave C# Definition

```csharp
namespace Mostlylucid.Ephemeral.Atoms.WaveOrchestrator;

/// <summary>
/// A wave is a sequential execution stage containing parallel work items.
/// </summary>
public sealed class Wave<TContext>
{
    public string Name { get; init; } = string.Empty;
    public int WaveNumber { get; init; }
    public string? Description { get; init; }

    // Items to execute in this wave
    public IReadOnlyList<IWaveItem<TContext>> Items { get; init; } = [];

    // Execution configuration
    public WaveExecutionOptions Execution { get; init; } = new();

    // Early exit configuration
    public WaveEarlyExitOptions? EarlyExit { get; init; }

    // Conditional execution
    public WaveCondition? Condition { get; init; }

    // Lane for concurrency control
    public string Lane { get; init; } = "default";
}

public interface IWaveItem<TContext>
{
    string Name { get; }
    bool Required { get; }
    double SalienceThreshold { get; }
    Task<WaveItemResult> ExecuteAsync(TContext context, CancellationToken ct);
}

public sealed record WaveExecutionOptions
{
    public WaveMode Mode { get; init; } = WaveMode.Parallel;
    public int MaxConcurrency { get; init; } = 4;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);
    public bool FailFast { get; init; } = false;
}

public enum WaveMode { Parallel, Sequential }
```

---

## Request Flow Architecture

### Phase 1: Hot Path (In Request Thread)

```
Request In
    │
    ▼
┌──────────────────────────────────────────────────┐
│ Wave 0: Fast-Path (10ms budget)                  │
│   └── FastPathReputationContributor              │
│       ├── Known bot signature → BLOCK immediately│
│       ├── Known good signature → ALLOW           │
│       └── Unknown → continue                     │
└──────────────────────────────────────────────────┘
    │ [no early exit]
    ▼
┌──────────────────────────────────────────────────┐
│ Wave 1: Signal Extraction (100ms budget)         │
│   ├── SecurityToolContributor (can early exit)   │
│   ├── UserAgentContributor                       │
│   ├── HeaderContributor                          │
│   ├── IpContributor                              │
│   ├── BehavioralContributor                      │
│   └── ... (all parallel)                         │
└──────────────────────────────────────────────────┘
    │
    ▼
┌──────────────────────────────────────────────────┐
│ Wave 2: Quick Aggregation (50ms budget)          │
│   └── HeuristicContributor                       │
│       ├── score > 0.85 → BLOCK (high confidence) │
│       ├── score < 0.15 → ALLOW (high confidence) │
│       └── 0.15-0.85 → UNCERTAIN                  │
└──────────────────────────────────────────────────┘
    │
    ▼
┌──────────────────────────────────────────────────┐
│ Decision Point (sync)                            │
│   ├── HIGH_CONFIDENCE_BLOCK → Block + Background │
│   ├── HIGH_CONFIDENCE_ALLOW → Allow + Background │
│   └── UNCERTAIN → Allow (safe default) + Learn   │
└──────────────────────────────────────────────────┘
    │
    ▼
Response Out (< 200ms total)
```

### Phase 2: Background Validation (Post-Response)

```
Background Queue (keyed by signature)
    │
    ▼
┌──────────────────────────────────────────────────┐
│ Learning Coordinator (sequential per key)        │
│   Keyed on: MultiFactorSignature hash            │
│   Bounds: max 10K concurrent keys, LRU eviction  │
│                                                  │
│   For each key:                                  │
│   1. Collect signals from request                │
│   2. If uncertain → queue for LLM validation     │
│   3. If high-confidence → sample 1% for verify   │
│   4. Update reputation cache with result         │
└──────────────────────────────────────────────────┘
    │ [if needs LLM]
    ▼
┌──────────────────────────────────────────────────┐
│ Wave 3: LLM Validation (background only)         │
│   └── LlmContributor                             │
│       ├── Validates uncertain decisions          │
│       ├── Spot-checks high-confidence decisions  │
│       └── Feeds learning with ground truth       │
└──────────────────────────────────────────────────┘
    │
    ▼
┌──────────────────────────────────────────────────┐
│ Reputation Update (async)                        │
│   └── Updates signature reputation cache         │
│       ├── Promotes good patterns                 │
│       ├── Demotes bad patterns                   │
│       └── Enables fast-path on future requests   │
└──────────────────────────────────────────────────┘
```

---

## Keyed Learning Coordinator

### Design Goals

1. **Sequential per key** - No concurrent processing for same signature
2. **Bounded memory** - Max keys in flight, LRU eviction
3. **Fair scheduling** - Round-robin across keys, no starvation
4. **Backpressure** - Drop/sample when overwhelmed

### YAML Definition

```yaml
# File: coordinators/learning.coordinator.yaml
name: learning-coordinator
version: 1.0.0
description: Keyed sequential learning with LLM validation

taxonomy:
  kind: coordinator
  determinism: deterministic
  persistence: ephemeral

# Keying configuration
keying:
  # Extract key from signals
  key_expression: "signals['detection.signature.hash']"

  # Bounds
  max_concurrent_keys: 10000
  eviction_policy: lru

  # Per-key queue
  per_key_queue_size: 100
  overflow_policy: drop_oldest

# Execution
execution:
  mode: sequential_per_key  # Sequential within key, parallel across keys
  max_parallel_keys: 100    # Max keys processing at once
  key_timeout: "00:00:30"   # Max time per key item

# Background validation triggers
triggers:
  # Always queue uncertain decisions for learning
  uncertain:
    when:
      - signal: detection.confidence
        condition: ">= 0.15 AND <= 0.85"
    priority: high

  # Sample high-confidence for verification (1%)
  verification_sample:
    when:
      - signal: detection.confidence
        condition: "> 0.85 OR < 0.15"
    sample_rate: 0.01
    priority: low

# Escalation to LLM
escalation:
  to_llm:
    when:
      - signal: learning.needs_validation
        condition: "== true"
    rate_limit:
      requests_per_minute: 60
      tokens_per_minute: 100000
    budget:
      daily_dollars: 10.00

# Output signals
emits:
  on_complete:
    - key: learning.validation_complete
      type: bool
    - key: learning.ground_truth
      type: string  # "bot" | "human" | "uncertain"
    - key: learning.confidence_delta
      type: double  # How wrong was original estimate?

# Metrics
metrics:
  - learning.queue_depth
  - learning.keys_active
  - learning.validation_latency_ms
  - learning.accuracy_rate
```

### C# Implementation

```csharp
namespace Mostlylucid.BotDetection.Learning;

/// <summary>
/// Keyed learning coordinator - processes learning sequentially per signature key.
/// Uses EphemeralKeyedWorkCoordinator for fair per-key sequential processing.
/// </summary>
public sealed class LearningCoordinator : IAsyncDisposable
{
    private readonly EphemeralKeyedWorkCoordinator<string, LearningWorkItem> _coordinator;
    private readonly ILlmValidator _llmValidator;
    private readonly IReputationCache _reputationCache;
    private readonly LearningCoordinatorOptions _options;
    private readonly SignalSink _signals;

    public LearningCoordinator(
        ILlmValidator llmValidator,
        IReputationCache reputationCache,
        LearningCoordinatorOptions options,
        SignalSink signals)
    {
        _llmValidator = llmValidator;
        _reputationCache = reputationCache;
        _options = options;
        _signals = signals;

        _coordinator = new EphemeralKeyedWorkCoordinator<string, LearningWorkItem>(
            new EphemeralKeyedWorkCoordinatorOptions
            {
                MaxConcurrentKeys = options.MaxConcurrentKeys,
                PerKeyQueueSize = options.PerKeyQueueSize,
                WorkerCount = options.MaxParallelKeys,
                IdleKeyTimeout = options.KeyTimeout
            },
            ProcessItemAsync);
    }

    /// <summary>
    /// Queue a detection result for background learning.
    /// </summary>
    public ValueTask QueueForLearningAsync(
        string signatureHash,
        DetectionResult result,
        LearningPriority priority)
    {
        var item = new LearningWorkItem
        {
            SignatureHash = signatureHash,
            Result = result,
            Priority = priority,
            QueuedAt = DateTimeOffset.UtcNow
        };

        _signals.Raise($"learning.queued:{signatureHash}:priority={priority}");

        return _coordinator.EnqueueAsync(signatureHash, item);
    }

    /// <summary>
    /// Process a single learning item (runs sequentially per key).
    /// </summary>
    private async Task ProcessItemAsync(
        string key,
        LearningWorkItem item,
        CancellationToken ct)
    {
        _signals.Raise($"learning.processing:{key}");

        try
        {
            // Determine if we need LLM validation
            var needsLlm = item.Priority == LearningPriority.High ||
                          (item.Priority == LearningPriority.Low && ShouldSample());

            string? groundTruth = null;

            if (needsLlm && await _options.LlmBudget.TryConsumeAsync(ct))
            {
                // Call LLM for validation
                groundTruth = await _llmValidator.ValidateAsync(item.Result, ct);
                _signals.Raise($"learning.llm_validated:{key}:result={groundTruth}");
            }
            else if (needsLlm)
            {
                // Budget exhausted - defer
                _signals.Raise($"learning.budget_exhausted:{key}");
                return;
            }
            else
            {
                // Use heuristic-derived truth for reputation update
                groundTruth = item.Result.Confidence > 0.5 ? "bot" : "human";
            }

            // Update reputation cache
            await _reputationCache.UpdateAsync(
                item.SignatureHash,
                groundTruth,
                item.Result.Confidence,
                ct);

            // Calculate accuracy delta
            var expectedConfidence = groundTruth == "bot" ? 1.0 : 0.0;
            var delta = Math.Abs(item.Result.Confidence - expectedConfidence);

            _signals.Raise($"learning.complete:{key}:truth={groundTruth}:delta={delta:F2}");
        }
        catch (Exception ex)
        {
            _signals.Raise($"learning.failed:{key}:error={ex.Message}");
        }
    }

    private bool ShouldSample() =>
        Random.Shared.NextDouble() < _options.VerificationSampleRate;

    public async ValueTask DisposeAsync() =>
        await _coordinator.DisposeAsync();
}

public record LearningWorkItem
{
    public required string SignatureHash { get; init; }
    public required DetectionResult Result { get; init; }
    public required LearningPriority Priority { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
}

public enum LearningPriority
{
    Low,   // Verification sample (1%)
    High   // Uncertain decision
}
```

---

## Safe Block with Background Verification

### Concept

When we block with high confidence, we still want to verify our decision was correct.
This creates a feedback loop that improves future decisions.

```yaml
# File: actions/safe-block.action.yaml
name: safe-block
version: 1.0.0
description: Block immediately but verify in background

# Trigger conditions
triggers:
  when:
    - signal: detection.confidence
      condition: "> 0.85"
    - signal: detection.action
      condition: "== 'block'"

# Immediate action (sync in request)
immediate:
  action: block
  response_code: 403
  headers:
    X-Block-Reason: "Bot detected"
    X-Block-Confidence: "${detection.confidence}"

# Background verification
background:
  # Queue for learning coordinator
  queue_to: learning-coordinator
  priority: low  # Sample-based verification

  # Signals to include for learning
  include_signals:
    - detection.*
    - request.path
    - request.method
    - request.ip

  # Callback when verification completes
  on_verification:
    # If we were wrong (blocked a human), log for analysis
    - when: "learning.ground_truth == 'human'"
      action: log_false_positive
      alert: true

    # Update reputation to prevent future false positives
    - when: "learning.ground_truth == 'human'"
      action: demote_signature
      amount: 0.2

# Metrics
metrics:
  - safe_block.total
  - safe_block.verified
  - safe_block.false_positive_rate
```

### Request/Response Flow

```
┌─────────────────────────────────────────────────────────────────┐
│ Request Thread (hot path)                                       │
├─────────────────────────────────────────────────────────────────┤
│ 1. Detection pipeline runs (< 200ms)                            │
│ 2. Confidence = 0.92 (high confidence bot)                      │
│ 3. Decision: BLOCK                                              │
│ 4. Queue signature for background verification                  │
│ 5. Return 403 immediately                                       │
│                                                                 │
│ Response: 403 Forbidden                                         │
│ X-Block-Confidence: 0.92                                        │
│ Latency: ~150ms                                                 │
└─────────────────────────────────────────────────────────────────┘
           │
           │ [async, post-response]
           ▼
┌─────────────────────────────────────────────────────────────────┐
│ Background Thread (learning coordinator)                        │
├─────────────────────────────────────────────────────────────────┤
│ 1. Dequeue work item for signature                              │
│ 2. 1% sampling → This one selected for LLM verification         │
│ 3. LLM validates: "human" (false positive!)                     │
│ 4. Log alert: False positive detected                           │
│ 5. Demote signature reputation by 0.2                           │
│ 6. Future requests from this signature get lower confidence     │
│                                                                 │
│ Processing time: ~2-5 seconds (doesn't affect response)         │
└─────────────────────────────────────────────────────────────────┘
```

---

## Wave Orchestrator Integration

### Full Pipeline YAML

```yaml
# File: pipelines/detection-with-learning.pipeline.yaml
name: detection-with-learning
version: 2.0.0
description: Bot detection with background learning and verification

# Hot path waves (in request thread)
hot_path:
  waves:
    - name: fast-path
      wave_number: 0
      items:
        - manifest: ../atoms/fastpath.atom.yaml
      execution:
        timeout: "00:00:00.010"
      early_exit:
        enabled: true
        allow_signals: [detection.fast_path.allow]
        block_signals: [detection.fast_path.block]

    - name: extraction
      wave_number: 1
      items:
        - manifest: ../molecules/signal-extraction.molecule.yaml
      execution:
        mode: parallel
        max_concurrency: 8
        timeout: "00:00:00.100"
      early_exit:
        enabled: true
        block_signals: [detection.security_tool.detected]

    - name: aggregation
      wave_number: 2
      items:
        - manifest: ../atoms/heuristic.atom.yaml
      execution:
        mode: sequential
        timeout: "00:00:00.050"
      early_exit:
        enabled: false

  # Total hot path budget
  total_timeout: "00:00:00.200"

# Background processing (post-response)
background:
  # Learning coordinator
  coordinator:
    manifest: ../coordinators/learning.coordinator.yaml

  # LLM validation wave (never in hot path)
  waves:
    - name: llm-validation
      wave_number: 0
      items:
        - manifest: ../atoms/llm.atom.yaml
      execution:
        mode: sequential
        timeout: "00:00:05.000"
      conditional:
        when:
          - signal: learning.needs_llm
            condition: "== true"

# Decision routing
routing:
  high_confidence_block:
    when: "detection.confidence > 0.85"
    action: block
    background: verification_sample  # 1% go to LLM

  high_confidence_allow:
    when: "detection.confidence < 0.15"
    action: allow
    background: verification_sample  # 1% go to LLM

  uncertain:
    when: "detection.confidence >= 0.15 AND detection.confidence <= 0.85"
    action: allow  # Safe default
    background: full_learning  # All go to LLM

# Defaults
defaults:
  hot_path:
    target_latency_ms: 150
    max_latency_ms: 200

  learning:
    verification_sample_rate: 0.01
    max_keys: 10000

  llm:
    daily_budget_dollars: 10.00
    requests_per_minute: 60
```

---

## Key Design Principles

1. **Request Thread Is Sacred**
   - Never block on LLM in hot path
   - Make decision within 200ms
   - If uncertain, default to allow (safer for UX)

2. **Learn in Background**
   - All interesting cases get queued for learning
   - LLM runs post-response, no latency impact
   - Keyed processing prevents memory blowup

3. **Verify High Confidence**
   - Even "obvious" decisions can be wrong
   - 1% sampling catches systematic errors
   - Feedback improves future fast-path accuracy

4. **Bound Everything**
   - Max keys in learning coordinator
   - Daily LLM budget
   - Per-key queue size with overflow policy

5. **Signal-Driven**
   - Everything emits signals
   - Decisions are signal conditions
   - Enables observability and debugging
