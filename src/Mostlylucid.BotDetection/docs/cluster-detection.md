# Cluster Detection & Country Reputation

Cross-request bot network detection using Leiden community detection (with Label Propagation fallback), optional semantic embeddings, FFT-based spectral analysis, LLM-generated cluster descriptions, and time-decayed country reputation tracking.

## Overview

The cluster detection system operates across multiple requests to identify coordinated bot activity that single-request analysis cannot catch. It discovers two types of clusters:

- **Bot Product** clusters: Multiple signatures exhibiting the same behavioral fingerprint (same bot software running from different IPs)
- **Bot Network** clusters: Temporally correlated signatures with moderate similarity (coordinated campaigns, botnets)

Additionally, the system tracks per-country bot rates with time-decay so that a country's reputation naturally recovers when bot traffic stops.

## Architecture

```
Orchestrator
    |
    v
BlackboardOrchestrator --> NotifyBotDetected()
    |                           |
    v                           v
SignatureCoordinator      BotClusterService (background)
    |                      /          |          \
    |          Leiden Clustering   Spectral (FFT)  Semantic Embeddings
    |                 |                |               |
    v                 v                v               v
CountryReputationTracker   ClusterContributor (Wave 2)
                                       |
                           BotClusterDescriptionService (background)
                                       |
                               Ollama LLM (qwen3:0.6b)
                                       |
                               SignalR Dashboard Updates
```

### Components

| Component | Type | Purpose |
|-----------|------|---------|
| `BotClusterService` | `BackgroundService` | Periodically clusters bot signatures using Leiden (or Label Propagation) |
| `BotClusterDescriptionService` | Service | Background LLM-based cluster naming and description (GraphRAG-style) |
| `LeidenClustering` | Static helper | Native C# Leiden community detection with CPM quality function |
| `ClusterContributor` | `IContributingDetector` | Emits cluster membership and country reputation signals |
| `CountryReputationTracker` | Service | Tracks time-decayed bot rates per country |
| `SpectralFeatureExtractor` | Static helper | FFT-based timing pattern analysis |

## BotClusterService

### Trigger Mechanism

Clustering runs on a hybrid timer + event system:

1. **Timer**: Every `ClusterIntervalSeconds` (default: 60s)
2. **Event-driven**: When `MinBotDetectionsToTrigger` (default: 20) new bots are detected

The orchestrator calls `NotifyBotDetected()` after each detection with probability > 0.5. When the count reaches the threshold, the background loop wakes early.

### Pre-Filter (Zero False Positives)

Only signatures with `AverageBotProbability >= MinBotProbabilityForClustering` (default: 0.5) enter clustering. Human traffic is never clustered.

### Feature Extraction

Each bot signature is converted to a 12-dimensional feature vector:

| Feature | Source | Weight |
|---------|--------|--------|
| TimingRegularity | Coefficient of variation of inter-request intervals | 0.12 |
| RequestRate | Requests per minute | 0.10 |
| PathDiversity | Unique paths / total requests | 0.08 |
| PathEntropy | Shannon entropy of path distribution | 0.08 |
| AvgBotProbability | Mean probability across requests | 0.12 |
| CountryCode | Exact match (categorical) | 0.08 |
| IsDatacenter | Boolean match | 0.07 |
| ASN | Exact match (categorical) | 0.08 |
| SpectralEntropy | FFT-derived timing entropy | 0.09 |
| HarmonicRatio | Energy at harmonic frequencies | 0.06 |
| PeakToAvgRatio | Peak magnitude / average magnitude | 0.07 |
| DominantFrequency | Strongest timing frequency | 0.05 |

Spectral features are computed only when a signature has >= 9 requests (8 intervals). When unavailable, spectral dimensions use neutral similarity (0.5).

### Clustering Algorithms

The algorithm is configurable via `BotDetection:Cluster:Algorithm`. Default is `leiden`.

#### Leiden Algorithm (default)

The Leiden algorithm is a community detection method that guarantees well-connected communities (unlike Louvain which can produce disconnected communities). Our native C# implementation uses the CPM (Constant Potts Model) quality function.

1. Build a similarity graph: edges exist between pairs exceeding `SimilarityThreshold` (default: 0.7)
2. **Local Moving Phase**: Greedily move nodes to the community that maximizes CPM quality
3. **Refinement Phase**: Split communities and re-optimize to ensure connectivity
4. **Compaction**: Merge singleton labels and iterate until convergence
5. Groups with >= `MinClusterSize` (default: 3) members become clusters

Configuration:
- `LeidenResolution` (default: 1.0) - Higher values produce more, smaller clusters
- Deterministic seeding (seed=42) for reproducible results

#### Label Propagation (fallback)

Available via `Algorithm=label_propagation`:

1. Build a similarity graph: edges exist between pairs exceeding `SimilarityThreshold` (default: 0.7)
2. Initialize each node with its own label
3. Iterate: each node adopts the highest-weighted label among its neighbors
4. Converge when no labels change (or after `MaxIterations`)
5. Groups with >= `MinClusterSize` (default: 3) members become clusters

### Semantic Embeddings (Optional)

When `EnableSemanticEmbeddings=true` (default), the similarity graph edges blend heuristic and semantic similarity:

- **Heuristic similarity** (60%): 12-dimensional feature vector comparison (see table above)
- **Semantic similarity** (40%): Cosine similarity of 384-dim ONNX embeddings (all-MiniLM-L6-v2)

The semantic embedding is generated from a privacy-safe text representation of each signature's behavioral characteristics (no raw IP/UA). This helps detect behavioral patterns that are similar in meaning but differ in exact feature values.

Configure the blend via `SemanticWeight` (0.0 = heuristic only, 1.0 = semantic only).

### Cross-Correlation Boost

For signatures with interval data, temporal cross-correlation (FFT-based) is computed. The final similarity is blended: 85% standard similarity + 15% temporal correlation. High cross-correlation (> 0.8) indicates shared C2 timing or cron schedules.

### Cluster Classification

| Type | Criteria | Typical Label |
|------|----------|---------------|
| BotProduct | High similarity (>= 0.8) + bot probability >= 0.5 | Rapid-Scraper, Deep-Crawler, Targeted-Scanner, Bot-Software |
| BotNetwork | Temporal density >= 0.6 + similarity >= 0.5 + bot probability >= 0.5 | Burst-Campaign, Large-Botnet, Coordinated-Campaign |

Labels are auto-generated based on behavioral characteristics:
- **Rapid-Scraper**: Average request interval < 2s
- **Deep-Crawler**: Path entropy > 3.0 (many different pages)
- **Targeted-Scanner**: Path entropy < 1.0 (focused on few endpoints)
- **Burst-Campaign**: Temporal density > 0.8 (all active at once)
- **Large-Botnet**: More than 10 members

### LLM Cluster Descriptions (GraphRAG-style)

When `EnableLlmDescriptions=true`, the `BotClusterDescriptionService` generates creative names and descriptions for each cluster using an LLM (default: qwen3:0.6b via Ollama).

**How it works:**
1. `BotClusterService` fires `ClustersUpdated` event when clustering completes
2. `BotClusterDescriptionService` subscribes and processes clusters in background (fire-and-forget)
3. For each cluster without a description, it builds a prompt with aggregated behavioral data
4. The LLM generates a JSON response: `{ "name": "...", "description": "...", "confidence": 0.85 }`
5. The result is pushed via `IClusterDescriptionCallback` (SignalR) to the dashboard

**Privacy:** Prompts contain only aggregated statistics (average request rate, path entropy, datacenter %, country diversity). No raw IPs, User-Agents, or PII.

**Configuration:**
```json
{
  "BotDetection": {
    "Cluster": {
      "EnableLlmDescriptions": true,
      "DescriptionModel": "qwen3:0.6b",
      "DescriptionEndpoint": "http://localhost:11434"
    }
  }
}
```

When LLM is unavailable, heuristic labels are used as fallback.

### Thread Safety

Clustering results are stored in an immutable `FrozenDictionary` snapshot. The entire snapshot is swapped atomically via a single volatile reference write. Readers never see partial state.

## SpectralFeatureExtractor

Uses MathNet.Numerics FFT to analyze inter-request timing intervals in the frequency domain.

### Extracted Features

| Feature | Range | Bot Signal | Human Signal |
|---------|-------|-----------|-------------|
| DominantFrequency | 0-1 (fraction of Nyquist) | Sharp peak at timer frequency | No clear peak |
| SpectralEntropy | 0-1 | Low (< 0.3) = pure tone | High (> 0.7) = broadband noise |
| HarmonicRatio | 0-1 | High = timer with harmonics | Low = random |
| PeakToAvgRatio | 0-1 | High = sharp spectral line | Low = flat spectrum |
| SpectralCentroid | 0-1 | Depends on timer period | ~0.5 (broadband) |

### Algorithm

1. Pad intervals to next power of 2
2. Forward FFT (no scaling)
3. Compute magnitude spectrum (first N/2 bins, excluding DC)
4. Extract dominant bin, spectral entropy, harmonic ratio, peak-to-average, centroid

### Temporal Cross-Correlation

`ComputeTemporalCorrelation(a, b)` computes the normalized cross-correlation between two interval series:

1. Zero-pad both to same length (next power of 2 of max * 2)
2. FFT both
3. Cross-power spectrum: FFT(A) * conj(FFT(B))
4. IFFT -> cross-correlation
5. Return max |correlation| / (norm(A) * norm(B))

Input is capped at 128 elements to prevent excessive memory allocation.

## CountryReputationTracker

Tracks bot detection rates per country using exponential moving average (EMA) with time decay.

### Decay Model

```
decay_factor = exp(-dt / tau)
decayed_count = previous_count * decay_factor + new_observation
```

Where `tau` = `DecayTauHours` (default: 168 hours = 1 week). A country's bad reputation naturally fades over time if no new bot traffic arrives.

### Minimum Sample Size

`GetCountryBotRate()` returns 0 until a country has accumulated >= `MinSampleSize` (default: 10) decayed observations. This prevents noisy rates from low-traffic countries.

### Integration

The orchestrator calls `RecordDetection()` after every detection that has a `geo.country_code` signal. The `ClusterContributor` reads rates via `GetCountryBotRate()`.

## ClusterContributor

Wave 2 detector (priority 850) that runs after GeoContributor and BehavioralWaveform. Requires the `waveform.signature` signal.

### Three Detection Modes

#### 1. Cluster Membership

When a signature belongs to a discovered cluster, emits strong bot signals:

- **BotProduct**: Confidence delta = `product_confidence_delta` (0.4) * min(1.0, avgBotProb + 0.2)
- **BotNetwork**: Confidence delta = `network_confidence_delta` (0.25) * min(1.0, temporalDensity + 0.2)

#### 2. Community Affinity

For signatures NOT in a cluster, checks if they share infrastructure with known bot clusters:

- ASN match: +0.5 score
- Country match: +0.3 score
- Datacenter: +0.2 score
- Weighted by cluster's AverageBotProbability
- Minimum threshold: 0.2

The boost is small and proportional: `community_affinity_delta` (0.08) * affinity_score. This increases resolution on borderline calls without making arbitrary associations.

#### 3. Country Reputation

Applies a signal when a country has elevated bot traffic:

- Bot rate >= 0.9 (very high): delta = `country_very_high_delta` (0.2)
- Bot rate >= 0.7 (high): delta = `country_high_delta` (0.1)

### Output Signals

| Signal Key | Type | Description |
|-----------|------|-------------|
| `cluster.type` | string | "product" or "network" |
| `cluster.id` | string | SHA256-based cluster identifier |
| `cluster.member_count` | int | Number of signatures in cluster |
| `cluster.label` | string | Auto-generated label (e.g., "Rapid-Scraper") |
| `cluster.avg_bot_probability` | double | Average bot probability across members |
| `cluster.avg_similarity` | double | Average intra-cluster similarity |
| `cluster.temporal_density` | double | Temporal activity density |
| `cluster.spectral_entropy` | double | Timing spectrum entropy |
| `cluster.dominant_frequency` | double | Dominant timing frequency |
| `cluster.harmonic_ratio` | double | Harmonic energy ratio |
| `cluster.peak_to_avg` | double | Peak-to-average magnitude |
| `cluster.community_affinity` | double | Infrastructure affinity score |
| `cluster.community_cluster_id` | string | ID of affinity-matched cluster |
| `geo.country_bot_rate` | double | Time-decayed country bot rate |
| `geo.country_bot_rank` | int | Country rank by bot rate |

## Configuration

```json
{
  "BotDetection": {
    "Cluster": {
      "ClusterIntervalSeconds": 60,
      "MinClusterSize": 3,
      "SimilarityThreshold": 0.7,
      "ProductSimilarityThreshold": 0.8,
      "NetworkTemporalDensityThreshold": 0.6,
      "MaxIterations": 20,
      "MinBotProbabilityForClustering": 0.5,
      "MinBotDetectionsToTrigger": 20
    },
    "CountryReputation": {
      "DecayTauHours": 168,
      "MinSampleSize": 10
    }
  }
}
```

### Detector-Level Overrides (appsettings.json)

```json
{
  "BotDetection": {
    "Detectors": {
      "ClusterContributor": {
        "Parameters": {
          "product_confidence_delta": 0.4,
          "network_confidence_delta": 0.25,
          "community_affinity_delta": 0.08,
          "country_high_rate_threshold": 0.7,
          "country_very_high_rate_threshold": 0.9,
          "country_high_delta": 0.1,
          "country_very_high_delta": 0.2
        }
      }
    }
  }
}
```

## YAML Manifest

The `cluster.detector.yaml` defines the full signal contract:

```yaml
name: ClusterContributor
priority: 850
enabled: true

triggers:
  requires:
    - type: signal_exists
      signal: waveform.signature

output:
  signals:
    - key: cluster.type
    - key: cluster.id
    - key: cluster.member_count
    - key: cluster.label
    - key: cluster.avg_bot_probability
    - key: cluster.avg_similarity
    - key: cluster.temporal_density
    - key: cluster.spectral_entropy
    - key: cluster.dominant_frequency
    - key: cluster.harmonic_ratio
    - key: cluster.peak_to_avg
    - key: geo.country_bot_rate
    - key: geo.country_bot_rank
```
