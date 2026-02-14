# Training Data API

REST endpoints for exporting bot detection data in ML-ready formats. Used for training classifiers, building datasets, and analyzing cluster/country reputation patterns.

## Setup

Register the endpoints in your `Program.cs` or startup:

```csharp
app.MapBotTrainingEndpoints(); // default prefix: /bot-detection/training
```

Custom prefix:

```csharp
app.MapBotTrainingEndpoints("/api/training");
```

Requires `SignatureCoordinator`, `BotClusterService`, and `CountryReputationTracker` in DI (all registered automatically by `AddBotDetection()`).

## Endpoints

### GET /signatures

List all tracked signatures with behavioral summaries, cluster membership, and spectral features.

**Response**: JSON array of signature objects.

| Field | Type | Description |
|-------|------|-------------|
| `signature` | string | HMAC-SHA256 privacy-safe identifier |
| `requestCount` | int | Total requests from this signature |
| `firstSeen` | datetime | First request timestamp |
| `lastSeen` | datetime | Most recent request timestamp |
| `averageInterval` | double | Mean seconds between requests |
| `pathEntropy` | double | Shannon entropy of path distribution |
| `timingCoefficient` | double | Coefficient of variation of inter-request intervals |
| `averageBotProbability` | double | Mean bot probability across all requests |
| `aberrationScore` | double | Behavioral aberration score |
| `isAberrant` | bool | Whether behavior is aberrant |
| `countryCode` | string | Two-letter country code |
| `asn` | string | Autonomous System Number |
| `isDatacenter` | bool | Whether IP is from a datacenter range |
| `cluster` | object? | Cluster membership (null if not clustered) |
| `spectral` | object? | FFT spectral features (null if < 9 requests) |

**Cluster sub-object**:

| Field | Type | Description |
|-------|------|-------------|
| `clusterId` | string | SHA256-based cluster identifier |
| `type` | string | `"BotProduct"` or `"BotNetwork"` |
| `label` | string | Auto-generated label (e.g., "Rapid-Scraper") |
| `memberCount` | int | Number of signatures in cluster |
| `avgSimilarity` | double | Average intra-cluster similarity |

**Spectral sub-object**:

| Field | Type | Description |
|-------|------|-------------|
| `dominantFrequency` | double | Strongest timing frequency |
| `spectralEntropy` | double | 0 = periodic (bot), 1 = broadband (human) |
| `harmonicRatio` | double | Energy at harmonic frequencies |
| `peakToAvgRatio` | double | Peak magnitude / average magnitude |
| `spectralCentroid` | double | Center of mass of frequency spectrum |

### GET /signatures/{signature}

Full detail for one signature including all individual requests with per-request signals.

**Response**: Same fields as `/signatures` plus:

| Field | Type | Description |
|-------|------|-------------|
| `requests` | array | All requests from this signature |

Each request object:

| Field | Type | Description |
|-------|------|-------------|
| `requestId` | string | Unique request identifier |
| `timestamp` | datetime | Request timestamp |
| `path` | string | Requested URL path |
| `botProbability` | double | Per-request bot probability |
| `escalated` | bool | Whether LLM escalation was triggered |
| `detectorsRan` | string[] | Names of detectors that ran |
| `signals` | object? | PII-filtered signal dictionary |

**404** if signature not found.

### GET /export

Streaming JSONL export of all signatures for ML training pipelines.

**Content-Type**: `application/x-ndjson`

Each line is a self-contained JSON record with a derived training label:

| Label | Condition |
|-------|-----------|
| `"bot"` | `averageBotProbability >= 0.7` |
| `"human"` | `averageBotProbability <= 0.3` |
| `"uncertain"` | Everything in between |

**Fields per record**: Same behavioral, cluster, and spectral fields as `/signatures`, plus the `label` field. Streamed line-by-line for memory efficiency (no full materialization).

**Example record**:

```json
{"signature":"abc123","label":"bot","requestCount":47,"averageInterval":1.234,"pathEntropy":0.8912,"timingCoefficient":0.0523,"averageBotProbability":0.8921,"aberrationScore":0.45,"isAberrant":false,"countryCode":"US","asn":"AS15169","isDatacenter":true,"clusterType":"BotProduct","clusterLabel":"Rapid-Scraper","spectralEntropy":0.1234,"harmonicRatio":0.7891,"peakToAvgRatio":0.9012,"dominantFrequency":0.125,"spectralCentroid":0.2345}
```

### GET /clusters

Export all discovered bot clusters.

**Response**: JSON array of `BotCluster` objects returned by `BotClusterService.GetClusters()`.

| Field | Type | Description |
|-------|------|-------------|
| `clusterId` | string | SHA256-based cluster identifier |
| `type` | enum | `BotProduct` or `BotNetwork` |
| `label` | string | Auto-generated behavioral label |
| `memberCount` | int | Number of member signatures |
| `members` | string[] | Member signature identifiers |
| `averageBotProbability` | double | Mean bot probability across members |
| `averageSimilarity` | double | Mean intra-cluster similarity |
| `temporalDensity` | double | Temporal activity density |
| `dominantCountry` | string | Most common country code |
| `dominantAsn` | string | Most common ASN |

### GET /countries

Export country reputation data with time-decayed bot rates.

**Response**: JSON array of country reputation entries from `CountryReputationTracker.GetAllCountries()`.

| Field | Type | Description |
|-------|------|-------------|
| `countryCode` | string | Two-letter country code |
| `botRate` | double | Time-decayed bot detection rate (0-1) |
| `totalDecayed` | double | Decayed total observation count |
| `botDecayed` | double | Decayed bot observation count |

## PII Filtering

All endpoints automatically strip PII signals before export:

- `request.user_agent` (raw UA string)
- `request.client_ip` (raw IP address)

Only privacy-safe signals (datacenter flags, ASN, header analysis results, behavioral metrics) are included.

## Usage Examples

```bash
# List all signatures
curl http://localhost:5080/bot-detection/training/signatures | jq .

# Export JSONL for ML training
curl http://localhost:5080/bot-detection/training/export > training-data.jsonl

# Get clusters
curl http://localhost:5080/bot-detection/training/clusters | jq .

# Get country reputation
curl http://localhost:5080/bot-detection/training/countries | jq .

# Detail for one signature
curl http://localhost:5080/bot-detection/training/signatures/abc123 | jq .
```

### Python ML Pipeline Example

```python
import pandas as pd

# Load JSONL into DataFrame
df = pd.read_json('training-data.jsonl', lines=True)

# Filter to labeled data only
labeled = df[df['label'] != 'uncertain']

# Feature columns for training
features = [
    'requestCount', 'averageInterval', 'pathEntropy',
    'timingCoefficient', 'aberrationScore', 'isDatacenter',
    'spectralEntropy', 'harmonicRatio', 'peakToAvgRatio',
    'dominantFrequency', 'spectralCentroid'
]

X = labeled[features].fillna(0)
y = (labeled['label'] == 'bot').astype(int)
```

## Configuration

The training endpoints use the same services as the detection pipeline. No additional configuration is needed beyond the standard `AddBotDetection()` registration.

The labeling thresholds (0.7 for bot, 0.3 for human) are hardcoded in the export endpoint to ensure consistent training data across deployments.
