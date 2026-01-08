# Dashboard Persistence System Design

## Overview

This document describes the comprehensive dashboard persistence system for bot detection data.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                     Bot Detection Pipeline                           │
│  (BlackboardOrchestrator → AggregatedEvidence)                      │
└────────────────────────────┬────────────────────────────────────────┘
                             │
                             ▼
                  ┌──────────────────────┐
                  │ DetectionRecordFactory│  ← Creates DetectionRecord
                  │  (Zero-PII by default)│     with hashed signatures
                  └──────────┬────────────┘
                             │
                             ▼
              ┌──────────────────────────────┐
              │  DetectionRecordCache        │  ← Ephemeral LRU cache
              │  (Atom: SlidingCache)        │     (in-memory, 10k records)
              └──────────┬────────────────────┘
                         │ Periodic flush
                         │ (every 30s or 100 records)
                         ▼
              ┌──────────────────────────────┐
              │  Batch Export Queue           │  ← Channel-based queue
              │  (Atom: KeyedSequential)      │     (bounded, drop oldest)
              └──────────┬────────────────────┘
                         │
                         ▼
              ┌──────────────────────────────┐
              │  Pinned Background Task       │  ← Runs in singleton queue
              │  (DetectionPersistenceService)│     (ephemeral framework)
              └──────────┬────────────────────┘
                         │
                         ▼
              ┌──────────────────────────────┐
              │  IDetectionStore (Atom)       │  ← Swappable persistence
              │  - SQLiteDetectionStore       │
              │  - PostgresDetectionStore (future)
              │  - CosmosDetectionStore (future)
              └───────────────────────────────┘
```

## Data Flow

### 1. Capture Phase

**When**: After detection completes
**Where**: In `BlackboardOrchestrator.DetectAsync()`
**What**: Create `DetectionRecord` from `AggregatedEvidence`

```csharp
var record = DetectionRecordFactory.FromEvidence(
    evidence,
    state,
    _recordOptions
);
```

### 2. Cache Phase

**What**: Store in ephemeral LRU cache
**Why**: Buffer rapid requests, enable batching
**Atom**: `SlidingCache<string, DetectionRecord>`

```csharp
_recordCache.Set(record.DetectionId, record);
```

### 3. Batch Export Phase

**Trigger**: Every 30 seconds OR when cache reaches 100 records
**What**: Flush batch to export queue
**Atom**: `Channel<DetectionRecordBatch>`

```csharp
var batch = new DetectionRecordBatch
{
    Records = _recordCache.GetAll(),
    Timestamp = DateTime.UtcNow
};
await _exportQueue.Writer.WriteAsync(batch);
_recordCache.Clear();
```

### 4. Persistence Phase

**Where**: `DetectionPersistenceService` (pinned background task)
**What**: Write batches to database
**Atom**: `IDetectionStore`

```csharp
await foreach (var batch in _exportQueue.Reader.ReadAllAsync(ct))
{
    await _store.WriteBatchAsync(batch, ct);
}
```

## Data Model

### DetectionRecord (Zero-PII by Default)

```csharp
public sealed record DetectionRecord
{
    // Core detection data
    public string DetectionId { get; init; }
    public DateTime Timestamp { get; init; }
    public string Path { get; init; }
    public double BotProbability { get; init; }
    public string RiskBand { get; init; }

    // Hashed signatures (zero-PII)
    public string IpHash { get; init; }           // HMAC-SHA256(IP + salt)
    public string UserAgentHash { get; init; }    // HMAC-SHA256(UA + salt)
    public string RequestSignature { get; init; } // HMAC-SHA256(IP|UA|Path + salt)
    public string? GeoHash { get; init; }         // HMAC-SHA256(country + salt)
    public string? SubnetHash { get; init; }      // HMAC-SHA256(IP/24 + salt)

    // Detector contributions
    public ImmutableDictionary<string, DetectorContribution> DetectorContributions { get; init; }

    // Top reasons (for dashboard display)
    public ImmutableList<string> TopReasons { get; init; }

    // Optional plaintext (if explicitly enabled)
    public string? ClientIp { get; init; }        // null by default
    public string? UserAgent { get; init; }       // null by default
}
```

### DetectorContribution

```csharp
public sealed record DetectorContribution
{
    public string Name { get; init; }
    public string Category { get; init; }
    public double ConfidenceDelta { get; init; }
    public double Weight { get; init; }
    public double Contribution { get; init; }
    public string? Reason { get; init; }
    public double ExecutionTimeMs { get; init; }
}
```

## Privacy Design

### Zero-PII by Default

All potentially identifying information is **hashed by default** using HMAC-SHA256 with a configurable salt:

- **IP Address** → `IpHash` (16-byte truncated hash)
- **User Agent** → `UserAgentHash`
- **Country Code** → `GeoHash`
- **IP Subnet** → `SubnetHash` (for network-level pattern detection)

### Composite Signatures

Request signatures combine multiple hashed components:

```
RequestSignature = HMAC-SHA256(IP | UserAgent | Path, salt)
```

This allows:

- ✅ Detecting repeat visitors without storing IP
- ✅ Identifying attack patterns from same subnet
- ✅ Grouping by UA family without storing full string
- ✅ GDPR/CCPA compliance by default

### Optional Plaintext

For debugging/development, plaintext can be enabled:

```json
{
  "BotDetection": {
    "Dashboard": {
      "RecordOptions": {
        "IncludeClientIp": false,      // Default: hashed only
        "IncludeUserAgent": false,     // Default: hashed only
        "IncludePlaintextForDev": true // Enable for local dev
      }
    }
  }
}
```

## Persistence Atoms (Swappable Providers)

### IDetectionStore Interface

```csharp
public interface IDetectionStore
{
    Task WriteBatchAsync(DetectionRecordBatch batch, CancellationToken ct);
    Task<IReadOnlyList<DetectionRecord>> GetRecentAsync(int count, CancellationToken ct);
    Task<IReadOnlyList<DetectionRecord>> GetBySignatureAsync(string signature, CancellationToken ct);
    Task<DetectionStats> GetStatsAsync(DateTime from, DateTime to, CancellationToken ct);
}
```

### SQLiteDetectionStore (Default)

- **File**: `detections.db` in app data folder
- **Tables**:
    - `detections` - Main records
    - `detector_contributions` - Per-detector data (1:N)
- **Indexes**:
    - `idx_timestamp` - For time-range queries
    - `idx_signature` - For pattern queries
    - `idx_risk_band` - For filtering
- **Retention**: Auto-purge records older than 30 days

### PostgresDetectionStore (Future)

For high-volume production environments.

### CosmosDetectionStore (Future)

For global distribution.

## Ephemeral Integration

### 1. SlidingCache Atom

```csharp
var cache = new SlidingCache<string, DetectionRecord>(
    capacity: 10_000,
    ttl: TimeSpan.FromMinutes(5)
);
```

### 2. KeyedSequential Atom

Batch export queue with backpressure:

```csharp
var queue = Channel.CreateBounded<DetectionRecordBatch>(
    new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    }
);
```

### 3. Pinned Background Task

```csharp
services.AddHostedService<DetectionPersistenceService>();
```

Runs in singleton queue with pinned affinity - guaranteed single-writer to SQLite.

## Configuration

```json
{
  "BotDetection": {
    "Dashboard": {
      "Enabled": true,
      "Salt": "your-secret-salt-here",  // For PII hashing

      "Cache": {
        "MaxRecords": 10000,
        "TtlMinutes": 5,
        "FlushIntervalSeconds": 30,
        "FlushBatchSize": 100
      },

      "Persistence": {
        "Provider": "SQLite",  // SQLite | Postgres | Cosmos
        "ConnectionString": "Data Source=detections.db",
        "RetentionDays": 30,
        "BatchSize": 500
      },

      "RecordOptions": {
        "IncludeClientIp": false,
        "IncludeUserAgent": false,
        "IncludeGeo": true,
        "IncludeSignals": false
      }
    }
  }
}
```

## Dashboard Queries

The dashboard can query:

### 1. Recent Detections (Timeline View)

```sql
SELECT * FROM detections
ORDER BY timestamp DESC
LIMIT 100
```

### 2. By Signature (Attack Pattern View)

```sql
SELECT * FROM detections
WHERE request_signature = @signature
ORDER BY timestamp DESC
```

### 3. By Subnet (Network Attack View)

```sql
SELECT * FROM detections
WHERE subnet_hash = @subnetHash
ORDER BY timestamp DESC
```

### 4. Statistics (Overview Dashboard)

```sql
SELECT
    DATE(timestamp) as date,
    COUNT(*) as total_requests,
    SUM(CASE WHEN is_bot THEN 1 ELSE 0 END) as bot_count,
    AVG(bot_probability) as avg_probability
FROM detections
WHERE timestamp >= @fromDate
GROUP BY DATE(timestamp)
```

### 5. Detector Performance

```sql
SELECT
    d.name,
    AVG(d.execution_time_ms) as avg_time,
    AVG(d.contribution) as avg_contribution,
    COUNT(*) as usage_count
FROM detector_contributions d
GROUP BY d.name
ORDER BY avg_contribution DESC
```

## Benefits

1. **Zero-PII by default** - GDPR/CCPA compliant
2. **Pattern detection** - Hash consistency enables signature matching
3. **High performance** - Ephemeral cache + batch writes
4. **Swappable storage** - Atom pattern for different databases
5. **Detailed debugging** - Per-detector contributions
6. **Dashboard-ready** - Optimized for common queries
7. **Retention policies** - Auto-cleanup old data
8. **Development friendly** - Optional plaintext mode

## Implementation Checklist

- [x] DetectionRecord model (zero-PII)
- [x] PiiHasher utility
- [ ] DetectionRecordCache (SlidingCache atom)
- [ ] DetectionRecordBatch
- [ ] IDetectionStore interface
- [ ] SQLiteDetectionStore implementation
- [ ] DetectionPersistenceService (pinned background task)
- [ ] Configuration binding
- [ ] DI registration
- [ ] Dashboard query helpers
