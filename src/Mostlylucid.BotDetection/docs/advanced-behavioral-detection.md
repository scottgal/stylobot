# Advanced Behavioral Detection

## Overview

The **AdvancedBehavioral** contributor uses statistical pattern analysis to detect bots based on entropy, timing
regularity, navigation patterns, and burst detection. It runs after basic behavioral detection (Priority 25 vs 20) to
provide deeper insights using mathematical analysis.

## Statistical Methods

### 1. Path Entropy Analysis

**Signal:** `PathEntropy`, `PathEntropyHigh`, `PathEntropyLow`

Uses **Shannon Entropy** to measure randomness in URL access patterns:

```
H = -Σ(p * log2(p))
```

Where `p` is the probability of accessing each unique path.

#### High Entropy (>3.5): Random Scanning

- **Confidence Delta:** 0.35
- **Weight:** 1.3
- **Pattern:** Client accessing many different paths with low repetition
- **Interpretation:** Scanner probing for vulnerabilities, directory enumeration
- **Example:** `/admin`, `/wp-login.php`, `/.env`, `/config.php`, `/backup.sql`

#### Low Entropy (<0.5): Too Repetitive

- **Confidence Delta:** 0.25
- **Weight:** 1.2
- **Pattern:** Client accessing the same 1-2 paths repeatedly
- **Interpretation:** Bot stuck in a loop, simplistic scraper
- **Example:** 50 requests to `/api/data` in 2 minutes

#### Moderate Entropy (0.5-3.0): Natural Browsing

- **Confidence Delta:** -0.2 (reduces bot probability)
- **Weight:** 1.0
- **Pattern:** Variety with repetition - normal browsing behavior
- **Interpretation:** Human exploring site naturally

### 2. Timing Entropy Analysis

**Signal:** `TimingEntropy`, `TimingTooRegular`

Measures randomness in inter-request timing intervals:

#### Low Timing Entropy (<0.3): Too Regular

- **Confidence Delta:** 0.3
- **Weight:** 1.3
- **Pattern:** Requests arrive at suspiciously consistent intervals
- **Interpretation:** Automated script with `sleep()` calls
- **Example:** Requests every 5.0 seconds ±0.1s

**Mathematical Basis:**

```
TimingEntropy = -Σ(freq * log2(freq))
```

Where freq is the frequency of each interval bucket (bucketed to 100ms precision).

### 3. Timing Anomaly Detection (Z-Score)

**Signal:** `TimingAnomalyZScore`, `TimingAnomalyDetected`

Uses **statistical Z-scores** to detect outlier timing patterns:

```
Z = (x - μ) / σ
```

Where:

- `x` = current interval
- `μ` = mean interval
- `σ` = standard deviation

#### Anomaly Detection

- **Threshold:** |Z| > 3.0 (3 standard deviations)
- **Confidence Delta:** 0.25
- **Weight:** 1.1
- **Pattern:** Sudden change in request timing (burst or pause)
- **Interpretation:** State change in bot, rate-limit detection evasion

### 4. Coefficient of Variation (CV) - Regular Pattern Detection

**Signal:** `CoefficientOfVariation`, `PatternTooRegular`

Detects **too-perfect timing consistency** using statistical CV:

```
CV = σ / μ
```

#### Too Regular (CV < 0.15)

- **Confidence Delta:** 0.35
- **Weight:** 1.4
- **Pattern:** Extremely consistent timing (like a metronome)
- **Interpretation:** Scripted bot with fixed delays
- **Real browsers:** CV typically 0.3-0.8 (high variance in human behavior)
- **Bots:** CV often < 0.15 (computer-generated timing)

**Example:**

- Human: 2.1s, 5.3s, 1.8s, 7.2s, 3.4s → CV = 0.51
- Bot: 5.0s, 5.1s, 4.9s, 5.0s, 5.1s → CV = 0.02 ⚠️

### 5. Navigation Pattern Analysis (Markov Chain)

**Signal:** `NavigationAnomalyScore`, `NavigationPatternUnusual`

Uses **first-order Markov chains** to model expected path transitions:

```
P(page_next | page_current) = transitions[current→next] / total_transitions[current]
```

#### Unusual Navigation

- **Confidence Delta:** Variable (based on improbability)
- **Weight:** 1.2
- **Pattern:** Highly unlikely path transitions
- **Example:** `/product` → `/.git/config` (nonsensical navigation)

**How It Works:**

1. Builds transition probability matrix from historical navigation
2. Scores current transition against expected probabilities
3. Flags low-probability transitions as suspicious

### 6. Burst Detection

**Signal:** `BurstDetected`, `BurstSize`, `BurstDurationSeconds`

Detects sudden spikes in request rate within a sliding window:

#### Burst Criteria

- **Window:** 30 seconds (configurable)
- **Threshold:** >5x normal rate
- **Confidence Delta:** 0.4
- **Weight:** 1.5
- **Pattern:** Sudden flood of requests
- **Interpretation:** Scraper in aggressive mode, DDoS attempt, rate-limit testing

**Example:**

- Normal rate: 2 requests/minute
- Burst: 15 requests in 30 seconds → **Burst detected**

## Configuration

```json
{
  "BotDetection": {
    "Behavioral": {
      "EnableAdvancedPatternDetection": true,      // Master switch
      "MinRequestsForPatternAnalysis": 10,         // Minimum data points
      "AnalysisWindow": "00:15:00",                 // 15-minute tracking window
      "IdentityHashSalt": "your-secret-salt-here"   // Privacy-preserving hash
    }
  }
}
```

### Configuration Options

| Option                           | Description                                | Default     |
|----------------------------------|--------------------------------------------|-------------|
| `EnableAdvancedPatternDetection` | Enable statistical analysis                | `true`      |
| `MinRequestsForPatternAnalysis`  | Minimum requests before analysis           | `10`        |
| `AnalysisWindow`                 | Time window for data collection            | 15 minutes  |
| `IdentityHashSalt`               | Salt for IP address hashing (KEEP SECRET!) | Random GUID |

## Privacy & Security

### Zero PII Storage

- **Hashed Identities:** Uses XxHash64 with configurable salt
- **Deterministic:** Same IP always produces same hash (for pattern tracking)
- **Non-Reversible:** Cannot recover IP address from hash
- **Salt-Protected:** Changing salt invalidates all existing hashes

```csharp
// NO PII - only hashed signatures
var clientHash = HashIdentity(clientIp);  // "A3F5B9C2D8E1F4A7"
_analyzer.RecordRequest(clientHash, path, timestamp);
```

### SHORT Tracking Windows

- Entries expire after `AnalysisWindow` (default 15 minutes)
- Automatic cleanup - no long-term storage
- Memory efficient - adapts to request pressure
- GDPR-friendly - ephemeral tracking only

## Priority & Wave Execution

- **Priority:** 25 (runs after basic behavioral at Priority 20)
- **Trigger Conditions:** None (runs in Wave 0)
- **Execution Time:** <5ms (fast statistical computations)
- **Dependencies:** None (independent analysis)

This allows advanced behavioral signals to be available for later-wave detectors.

## Example Detections

### Scanner Bot (High Path Entropy)

```
Client: 192.168.1.100 (hashed: "A3F5B9C2D8E1F4A7")
Paths accessed: /.env, /admin, /.git/config, /wp-admin, /backup.sql, ...
Path Entropy: 4.2 (very high)

Detection:
- PathEntropy: 4.2 → High (>3.5)
- ConfidenceDelta: +0.35
- Reason: "High path entropy: 4.20 (random scanning pattern)"
```

### Scripted Bot (Too Regular Timing)

```
Client: 10.0.0.5 (hashed: "F1A2B3C4D5E6F7A8")
Request intervals: 5.0s, 5.1s, 4.9s, 5.0s, 5.1s, ...
Coefficient of Variation: 0.01 (extremely consistent)

Detection:
- CV: 0.01 → Too Regular (<0.15)
- ConfidenceDelta: +0.35
- Reason: "Very low CoV: 0.01 (too consistent, likely scripted)"
```

### Burst Attack

```
Client: 172.16.0.10 (hashed: "D9E8F7A6B5C4D3E2")
Normal rate: 3 requests/minute
Burst: 20 requests in 25 seconds

Detection:
- BurstSize: 20 requests
- BurstDuration: 25 seconds
- ConfidenceDelta: +0.4
- Reason: "Burst detected: 20 requests in 25s"
```

### Natural Human Behavior

```
Client: 203.0.113.42 (hashed: "C5D4E3F2A1B0C9D8")
Path Entropy: 1.8 (moderate variety)
Timing CV: 0.45 (natural variance)
No bursts, normal navigation flow

Detection:
- Natural patterns detected
- ConfidenceDelta: -0.2 (reduces bot probability)
- Reason: "Natural browsing patterns detected (entropy, timing variation)"
```

## Mathematical Details

### Shannon Entropy Formula

For a set of paths accessed:

```
H = -Σ(p_i * log2(p_i))
```

Where:

- `p_i` = probability of accessing path i
- `log2` = logarithm base 2 (bits of information)

**Interpretation:**

- H = 0: Completely predictable (one path only)
- H = log2(N): Maximum entropy (all N paths equally likely)
- H > 3: High entropy (>8 equally likely paths, random behavior)

### Z-Score (Standard Score)

```
Z = (x - μ) / σ

Where:
- x = current value
- μ = sample mean
- σ = sample standard deviation
```

**Interpretation:**

- |Z| < 1: Within 1σ (68% of normal data)
- |Z| < 2: Within 2σ (95% of normal data)
- |Z| > 3: Outlier (only 0.3% of normal data) ⚠️

### Coefficient of Variation

```
CV = σ / μ

Where:
- σ = standard deviation of intervals
- μ = mean interval
```

**Interpretation:**

- CV < 0.15: Too consistent (bot-like)
- CV 0.3-0.8: Natural human variance
- CV > 1.0: Highly erratic (possibly legitimate, possibly misbehaving)

## Integration

### 1. Add to Policy Configuration

```json
{
  "Policies": {
    "default": {
      "FastPath": [
        "Behavioral",
        "AdvancedBehavioral",  // <-- Add after basic behavioral
        "..."
      ]
    }
  }
}
```

### 2. Verify Detection

Check detection output for advanced behavioral signals:

```json
{
  "detectorsRan": ["...", "AdvancedBehavioral", "..."],
  "contributions": [
    {
      "detectorName": "AdvancedBehavioral",
      "category": "AdvancedBehavioral",
      "confidenceDelta": 0.35,
      "weight": 1.3,
      "reason": "High path entropy: 4.20 (random scanning pattern)",
      "signals": {
        "PathEntropy": 4.2,
        "PathEntropyHigh": true
      }
    }
  ]
}
```

## Use Cases

### 1. Scanner Detection

High path entropy with unlikely navigation patterns reveals vulnerability scanners.

### 2. Scraper Detection

Too-regular timing (low CV) identifies scripted content scrapers.

### 3. DDoS Detection

Burst detection catches sudden traffic spikes from individual IPs.

### 4. Bot Framework Detection

Combination of moderate entropy + low CV + timing anomalies reveals headless browsers.

### 5. API Abuse Detection

Repetitive path access (low entropy) identifies API endpoint hammering.

## Best Practices

1. **Collect Enough Data** - Set `MinRequestsForPatternAnalysis` appropriately (default 10)
2. **Tune Thresholds** - Adjust entropy/CV thresholds based on your traffic patterns
3. **Monitor False Positives** - Some legitimate users may trigger statistical anomalies
4. **Combine with Other Signals** - Use as part of ensemble detection, not standalone
5. **Review Patterns** - Regularly audit detected patterns to refine thresholds

## Limitations

1. **Cold Start** - Requires historical data to build patterns (minimum 10 requests)
2. **Legitimate Variation** - Some humans exhibit bot-like patterns (e.g., power users)
3. **False Positives** - Overly aggressive thresholds can flag legitimate traffic
4. **Shared IPs** - Multiple users behind NAT may produce chaotic patterns

## Dependencies

- **MathNet.Numerics** v5.0.0 - Statistical computations
- **XxHash64** - Fast non-cryptographic hashing
- **ephemeral.complete** - Memory-efficient tracking windows

## See Also

- [Cache Behavior Detection](./cache-behavior-detection.md) - HTTP caching pattern analysis
- [Behavioral Detection](./behavioral-analysis.md) - Rate limiting and session tracking
- [Privacy & Security](./privacy-security.md) - PII protection strategies
- [Statistical Detection Methods](./statistical-methods.md) - Mathematical foundations
