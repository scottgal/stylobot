# BotDetection YAML Configuration Architecture

This document describes how to define the entire BotDetection system using YAML manifests, following the StyloFlow/Ephemeral modular synthesizer pattern.

## Hierarchy Overview

```
Pipeline (Wave Orchestrator)
  └── Waves (execution stages)
        └── Molecules (atom groupings)
              └── Atoms (individual detectors/contributors)
```

## Key Concepts

### Atoms
Individual processing units - detectors/contributors that emit and listen for signals.
Each atom owns its signals - signals die when the atom dies unless preserved via echo/escalate/propagate.

### Molecules
Assemblages of atoms with aggregate signal contracts. Groups related atoms that work together.
Example: `SignalExtractionMolecule` contains all stage-0 detectors.

### Waves
Execution stages with ordering and parallelism control.
Example: `fast-path` runs first, then `stage-0` in parallel, etc.

### Coordinator/Pipeline
Top-level orchestration that manages waves, early exit conditions, and escalation.

---

## Directory Structure

```
Mostlylucid.BotDetection/
  Orchestration/
    Manifests/
      atoms/                    # Individual detector manifests
        behavioral.atom.yaml
        useragent.atom.yaml
        header.atom.yaml
        ip.atom.yaml
        tls.atom.yaml
        http2.atom.yaml
        versionage.atom.yaml
        heuristic.atom.yaml
        llm.atom.yaml
        fastpath.atom.yaml
        securitytool.atom.yaml
        reputation.atom.yaml
      molecules/                # Atom groupings
        signal-extraction.molecule.yaml
        aggregation.molecule.yaml
        escalation.molecule.yaml
      pipelines/                # Top-level orchestration
        detection.pipeline.yaml
      schemas/                  # JSON schemas for validation
        atom.schema.json
        molecule.schema.json
        pipeline.schema.json
```

---

## Atom Manifest Schema

Each atom manifest defines a single detector/contributor.

```yaml
# File: atoms/behavioral.atom.yaml
name: behavioral
version: 1.0.0
description: Analyzes request patterns, timing, and frequency for bot indicators

# NuGet package containing the implementation (inverted model - manifests reference packages)
implementation:
  nuget:
    package: Mostlylucid.BotDetection
    version: "^3.0.0"
    type: Mostlylucid.BotDetection.Orchestration.ContributingDetectors.BehavioralContributor

# Taxonomy classification
taxonomy:
  kind: sensor           # sensor, extractor, embedder, ranker, coordinator, escalator, guard
  determinism: probabilistic
  persistence: stateful  # ephemeral, stateful, direct_write

# Signal scope hierarchy
scope:
  sink: botdetection
  coordinator: detection
  atom: behavioral

# Trigger conditions
triggers:
  requires: []           # No dependencies - tracks all requests
  skip_when:
    - signal: detection.early_exit
      condition: "== true"

# Signals this atom listens for
listens:
  required:
    - request.ip
    - request.path
  optional:
    - request.timestamp
    - behavioral.timing.*

# Signals this atom emits (owned by this atom)
emits:
  on_start:
    - behavioral.started
  on_complete:
    - key: detection.behavioral.confidence
      type: double
      description: Bot confidence from behavioral analysis
      confidence_range: [0.0, 1.0]
    - key: detection.behavioral.anomaly_detected
      type: bool
      description: Whether behavioral anomalies were detected
    - key: detection.behavioral.rate_exceeded
      type: bool
  on_failure:
    - behavioral.failed
  conditional:
    - key: detection.behavioral.is_bot
      type: bool
      when: "detection.behavioral.confidence > 0.7"

# Signal preservation rules
preserve:
  escalate:
    - signal: detection.behavioral.confidence
      to: learning_coordinator
      salience_threshold: 0.8
      when: "confidence > 0.6 OR confidence < 0.4"
      batch: true
      batch_size: 100
      batch_timeout: "00:00:30"
  propagate:
    - signal: detection.behavioral.confidence
      as: behavioral.score

# Concurrency lane
lane:
  name: fast
  max_concurrency: 8
  priority: 90

# Budget constraints
budget:
  max_duration: "00:00:00.050"  # 50ms

# Default configuration (no magic numbers)
defaults:
  weights:
    base: 1.0
    bot_signal: 1.5
    human_signal: 1.0
  confidence:
    neutral: 0.0
    bot_detected: 0.4
    human_indicated: -0.1
  parameters:
    rate_window_seconds: 60
    rate_limit_requests: 100
    min_request_interval_ms: 50

tags:
  - behavioral
  - rate-limiting
  - stage-0
```

---

## Molecule Manifest Schema

Molecules group related atoms with aggregate signal contracts.

```yaml
# File: molecules/signal-extraction.molecule.yaml
name: signal-extraction
version: 1.0.0
description: Stage-0 parallel signal extraction - no dependencies between atoms

taxonomy:
  primary_kind: sensor
  kinds: [sensor, extractor]
  determinism: probabilistic
  persistence: ephemeral

scope:
  sink: botdetection
  coordinator: detection
  molecule: signal-extraction

# Constituent atoms - reference by manifest file
atoms:
  - name: securitytool
    manifest: ../atoms/securitytool.atom.yaml
    required: true
    salience_threshold: 0.0

  - name: useragent
    manifest: ../atoms/useragent.atom.yaml
    required: true

  - name: header
    manifest: ../atoms/header.atom.yaml
    required: false

  - name: tls
    manifest: ../atoms/tls.atom.yaml
    required: false
    config_overrides:
      enabled: "${TLS_FINGERPRINTING_ENABLED:false}"

  - name: ip
    manifest: ../atoms/ip.atom.yaml
    required: true

  - name: http2
    manifest: ../atoms/http2.atom.yaml
    required: false

  - name: behavioral
    manifest: ../atoms/behavioral.atom.yaml
    required: true

# Aggregate emissions from constituent atoms
emits:
  on_complete:
    - key: extraction.complete
      type: bool
      source_atom: "*"
      description: All extractions finished
    - key: extraction.scores
      type: "Dictionary<string, double>"
      description: Collected confidence scores from all atoms

# Molecule-level escalation
escalation:
  to_llm:
    when:
      - signal: extraction.scores
        condition: "any > 0.4 AND any < 0.6"
    to: llm_molecule
    salience_threshold: 0.7
    description: Escalate ambiguous cases to LLM

# Execution semantics
execution:
  mode: parallel          # sequential, parallel, pipeline
  fail_fast: false        # Continue even if non-required atoms fail
  timeout: "00:00:00.100" # 100ms for entire molecule
  max_concurrency: 8

# Concurrency lane for molecule as unit
lane:
  name: extraction
  max_concurrency: 16
  priority: 200

tags:
  - stage-0
  - extraction
  - parallel
```

---

## Pipeline Manifest Schema

The top-level orchestration defining waves and coordination.

```yaml
# File: pipelines/detection.pipeline.yaml
name: detection-pipeline
version: 1.0.0
description: Main bot detection pipeline - fast-path through LLM escalation

scope:
  sink: botdetection
  coordinator: detection

# Wave definitions - sequential stages containing parallel molecules/atoms
waves:
  - name: fast-path
    wave_number: 0
    description: Ultra-fast reputation lookup for instant decisions
    molecules: []
    atoms:
      - manifest: ../atoms/fastpath.atom.yaml
    timeout: "00:00:00.010"  # 10ms
    early_exit: true
    parallelism: 1

  - name: stage-0
    wave_number: 1
    description: First wave - parallel signal extraction
    molecules:
      - manifest: ../molecules/signal-extraction.molecule.yaml
    atoms: []
    timeout: "00:00:00.100"  # 100ms
    early_exit: true
    parallelism: 8

  - name: stage-1
    wave_number: 2
    description: Second wave - version age analysis (depends on stage-0)
    molecules: []
    atoms:
      - manifest: ../atoms/versionage.atom.yaml
    timeout: "00:00:00.050"
    early_exit: false
    parallelism: 4

  - name: stage-2
    wave_number: 3
    description: Aggregation and scoring
    molecules:
      - manifest: ../molecules/aggregation.molecule.yaml
    atoms:
      - manifest: ../atoms/reputation.atom.yaml
      - manifest: ../atoms/heuristic.atom.yaml
    timeout: "00:00:00.500"
    early_exit: false
    parallelism: 1  # Sequential for aggregation

  - name: stage-3
    wave_number: 4
    description: LLM escalation for ambiguous cases
    molecules:
      - manifest: ../molecules/escalation.molecule.yaml
    atoms:
      - manifest: ../atoms/llm.atom.yaml
    timeout: "00:00:05.000"  # 5s
    early_exit: false
    parallelism: 1
    conditional:
      when:
        - signal: detection.heuristic.escalate_to_ai
          condition: "== true"
      skip_when:
        - signal: detection.budget.exhausted
          condition: "== true"

# Lane configurations - concurrency pools
lanes:
  fast:
    max_concurrency: 16
    priority: 200
    description: Ultra-fast detectors (cache lookups)
  normal:
    max_concurrency: 8
    priority: 100
    description: Standard detectors
  ml:
    max_concurrency: 4
    priority: 60
    description: ML/heuristic detectors
  llm:
    max_concurrency: 1
    priority: 50
    description: LLM detectors (expensive, rate-limited)

# Early exit signals
early_exit:
  allow:
    - detection.early_exit.allow
    - detection.useragent.verified_bot
  block:
    - detection.early_exit.block
    - detection.security_tool.detected

# Escalation rules
escalation:
  to_llm:
    signals:
      - detection.heuristic.escalate_to_ai
    skip_when:
      - detection.budget.exhausted

# Pipeline-level defaults
defaults:
  timing:
    total_timeout_ms: 6000
  scoring:
    bot_threshold: 0.7
    human_threshold: 0.3
    escalation_low: 0.4
    escalation_high: 0.6
  circuit_breaker:
    enabled: true
    failure_threshold: 5
    success_threshold: 3
    timeout_seconds: 30
  budget:
    max_total_cost_cents: 10
    llm_budget_percent: 80
    daily_llm_budget_dollars: 1.00
  features:
    enable_fast_path: true
    enable_llm_escalation: true
    enable_learning: true

tags:
  - main-pipeline
  - bot-detection
```

---

## Signal Flow Example

```
Request → fast-path atom (10ms)
            ↓ [no early exit]
          stage-0 molecule (parallel, 100ms)
            ├── securitytool atom → detection.security_tool.*
            ├── useragent atom → detection.useragent.*
            ├── header atom → detection.header.*
            ├── ip atom → detection.ip.*
            ├── tls atom → detection.tls.*
            ├── http2 atom → detection.http2.*
            └── behavioral atom → detection.behavioral.*
            ↓
          stage-1 atom (50ms)
            └── versionage atom → detection.versionage.*
            ↓
          stage-2 molecule (sequential, 500ms)
            ├── reputation atom → detection.reputation.*
            └── heuristic atom → detection.heuristic.*
                                  → detection.heuristic.escalate_to_ai?
            ↓ [if escalate_to_ai == true]
          stage-3 molecule (conditional, 5s)
            └── llm atom → detection.llm.*
            ↓
          Final Decision
```

---

## Key Benefits

1. **No Duplication**: Atoms are defined once and referenced by manifest path
2. **Clear Dependencies**: Signal contracts make data flow explicit
3. **Configurable**: All magic numbers are in `defaults` sections
4. **Extensible**: Add new atoms/molecules without code changes
5. **Testable**: Each atom/molecule can be tested in isolation
6. **Observable**: Signal emissions create clear audit trail
7. **Budget-Aware**: Built-in cost tracking for LLM escalation

---

## Implementation Notes

### Loading Manifests

```csharp
// Load and validate all manifests at startup
var registry = new ManifestRegistry();
await registry.LoadFromDirectoryAsync("Orchestration/Manifests");

// Build pipeline from manifests
var pipeline = PipelineBuilder
    .FromManifest("pipelines/detection.pipeline.yaml")
    .WithRegistry(registry)
    .Build();
```

### Runtime Signal Flow

```csharp
// Atoms emit signals through SignalSink
signals.Raise("detection.behavioral.confidence", 0.7);

// Molecules aggregate signals from constituent atoms
var moleculeOutput = await molecule.ExecuteAsync(input);

// Pipeline coordinates waves
var result = await pipeline.DetectAsync(httpContext);
```

### Configuration Override (appsettings.json)

```json
{
  "BotDetection": {
    "Atoms": {
      "behavioral": {
        "parameters": {
          "rate_limit_requests": 200
        }
      }
    },
    "Pipeline": {
      "defaults": {
        "scoring": {
          "bot_threshold": 0.8
        }
      }
    }
  }
}
```
