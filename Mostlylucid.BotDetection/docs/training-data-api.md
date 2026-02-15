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

## Security

### API Key Authentication

Training endpoints expose detection intelligence. Enable API key protection for production:

```json
{
  "BotDetection": {
    "TrainingEndpoints": {
      "RequireApiKey": true,
      "ApiKeys": ["your-secure-key-here"]
    }
  }
}
```

Or via environment variables:

```bash
BOTDETECTION__TRAININGENDPOINTS__REQUIREAPIKEY=true
BOTDETECTION__TRAININGENDPOINTS__APIKEYS__0=your-secure-key-here
```

Clients must send the `X-Training-Api-Key` header:

```bash
curl -H "X-Training-Api-Key: your-secure-key-here" \
  http://localhost:5080/bot-detection/training/signatures
```

### Rate Limiting

Per-IP sliding window rate limiting is enabled by default (30 requests/minute). Configure via:

```json
{
  "BotDetection": {
    "TrainingEndpoints": {
      "RateLimitPerMinute": 60
    }
  }
}
```

Set to `0` to disable. Exceeding the limit returns `429 Too Many Requests` with a `Retry-After: 60` header.

### Disabling Endpoints

```json
{
  "BotDetection": {
    "TrainingEndpoints": {
      "Enabled": false
    }
  }
}
```

### Configuration Reference

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Enabled` | bool | `true` | Enable/disable all training endpoints |
| `RequireApiKey` | bool | `false` | Require `X-Training-Api-Key` header |
| `ApiKeys` | string[] | `[]` | Allowed API keys |
| `RateLimitPerMinute` | int | `30` | Per-IP rate limit (0 = unlimited) |
| `MaxExportRecords` | int | `10000` | Max records in `/export` JSONL stream |

## PII Filtering

All endpoints automatically strip PII signals before export:

- `request.user_agent` (raw UA string)
- `request.client_ip` (raw IP address)

Only privacy-safe signals (datacenter flags, ASN, header analysis results, behavioral metrics) are included. UA classification keys (`ua.is_bot`, `ua.bot_type`, etc.) are only included for bot-detected signatures.

Path data is generalized: GUIDs, long numeric IDs, and base64 tokens are replaced with `*`, and query strings are stripped.

## Endpoints

### GET /signatures

List all tracked signatures with behavioral summaries, cluster membership, spectral features, and family membership.

**Response**: JSON array of signature objects with multi-vector representation.

| Field | Type | Description |
|-------|------|-------------|
| `signature` | string | HMAC-SHA256 privacy-safe identifier |
| `label` | string | Derived label: `"bot"`, `"human"`, or `"uncertain"` |
| `vectors.behavioral` | object | Timing regularity, request rate, path diversity, entropy, bot probability, aberration |
| `vectors.temporal` | object | Request count, first/last seen, duration, interval stats |
| `vectors.spectral` | object? | FFT features (null if < 9 requests) |
| `vectors.geo` | object | Country code, ASN, datacenter flag |
| `isAberrant` | bool | Whether behavior is aberrant |
| `cluster` | object? | Cluster membership (null if not clustered) |
| `family` | object? | Signature family membership (null if not in a family) |

### GET /signatures/{signature}

Full detail for one signature including all individual requests with per-request signals.

**Response**: Same fields as `/signatures` plus full cluster detail (temporal density, dominant country/ASN), family member list, and per-request data.

**404** if signature not found.

### GET /export

Streaming JSONL export of all signatures for ML training pipelines.

**Content-Type**: `application/x-ndjson`

Each line is a self-contained JSON record with flat feature vectors (prefixed `v_`) for direct ML consumption:

| Label | Condition |
|-------|-----------|
| `"bot"` | `averageBotProbability >= 0.7` |
| `"human"` | `averageBotProbability <= 0.3` |
| `"uncertain"` | Everything in between |

Streamed line-by-line with periodic flushing. Capped at `MaxExportRecords` (default: 10,000) to prevent runaway memory usage. A truncation record is appended if the cap is reached.

### GET /clusters

Export all discovered bot clusters.

**Response**: JSON array of `BotCluster` objects.

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

**Response**: JSON array of country reputation entries.

| Field | Type | Description |
|-------|------|-------------|
| `countryCode` | string | Two-letter country code |
| `botRate` | double | Time-decayed bot detection rate (0-1) |
| `totalDecayed` | double | Decayed total observation count |
| `botDecayed` | double | Decayed bot observation count |

### GET /families

List all active signature families with members and aggregated stats.

**Response**: JSON array of family objects.

| Field | Type | Description |
|-------|------|-------------|
| `familyId` | string | SHA256-based family identifier |
| `canonicalSignature` | string | Most-requested member signature |
| `memberCount` | int | Number of member signatures |
| `memberSignatures` | string[] | All member signature IDs |
| `formationReason` | string | `TemporalProximity`, `BehavioralSimilarity`, or `HighBotProbability` |
| `mergeConfidence` | double | Merge score [0-1] |
| `createdUtc` | datetime | Family creation time |
| `aggregatedBotProbability` | double | Weighted mean bot probability |
| `totalRequestCount` | int | Total requests across all members |

### GET /families/{familyId}

Full detail for one signature family including per-member behavioral data.

**404** if family not found.

### GET /convergence/stats

Aggregated convergence statistics: family counts, formation reason breakdown, IP index stats.

**Response**:

| Field | Type | Description |
|-------|------|-------------|
| `totalFamilies` | int | Number of active families |
| `reasonBreakdown` | object | Count per formation reason |
| `averageMergeConfidence` | double | Mean merge confidence |
| `averageFamilySize` | double | Mean members per family |
| `maxFamilySize` | int | Largest family |
| `ipIndex.totalIpsTracked` | int | IPs with multiple signatures |
| `ipIndex.averageSignaturesPerIp` | double | Mean signatures per IP |
| `ipIndex.maxSignaturesPerIp` | int | Most signatures from one IP |

## Usage Examples

```bash
# List all signatures (requires API key if configured)
curl -H "X-Training-Api-Key: YOUR_KEY" \
  http://localhost:5080/bot-detection/training/signatures | jq .

# Export JSONL for ML training
curl -H "X-Training-Api-Key: YOUR_KEY" \
  http://localhost:5080/bot-detection/training/export > training-data.jsonl

# Get clusters
curl http://localhost:5080/bot-detection/training/clusters | jq .

# Get country reputation
curl http://localhost:5080/bot-detection/training/countries | jq .

# List signature families
curl http://localhost:5080/bot-detection/training/families | jq .

# Convergence statistics
curl http://localhost:5080/bot-detection/training/convergence/stats | jq .
```

### Python ML Pipeline Example

```python
import pandas as pd

# Load JSONL into DataFrame
df = pd.read_json('training-data.jsonl', lines=True)

# Filter to labeled data only
labeled = df[df['label'] != 'uncertain']

# Feature columns for training (v_ prefixed for flat ML format)
features = [
    'v_timingRegularity', 'v_requestRate', 'v_pathDiversity',
    'v_pathEntropy', 'v_avgBotProbability', 'v_aberrationScore',
    'v_requestCount', 'v_durationSeconds', 'v_averageInterval',
    'v_intervalStdDev', 'v_spectralEntropy', 'v_harmonicRatio',
    'v_peakToAvgRatio', 'v_dominantFrequency', 'v_spectralCentroid'
]

X = labeled[features].fillna(0)
y = (labeled['label'] == 'bot').astype(int)
```

## Configuration

The training endpoints use the same services as the detection pipeline. No additional configuration is needed beyond the standard `AddBotDetection()` registration.

The labeling thresholds (0.7 for bot, 0.3 for human) are hardcoded in the export endpoint to ensure consistent training data across deployments.
