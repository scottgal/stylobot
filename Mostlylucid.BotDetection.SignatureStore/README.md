# Mostlylucid.BotDetection.SignatureStore

Optional feature for storing bot detection signatures in Postgres with real-time SignalR updates.

## Features

- **Postgres JSONB Storage** - Store full signatures with efficient indexing
- **Query by Any Signal** - GIN indexes enable fast queries on any nested signal
- **SignalR Real-Time Updates** - Stream new signatures to connected clients
- **REST API** - Query signatures by various criteria
- **Automatic Cleanup** - TTL-based expiration and retention policies
- **Works Everywhere** - ASP.NET middleware and YARP gateway support

## Quick Start

### 1. Install Postgres

```bash
# Using Docker
docker run --name postgres-signatures \
  -e POSTGRES_PASSWORD=yourpassword \
  -e POSTGRES_DB=bot_signatures \
  -p 5432:5432 \
  -d postgres:16

# Or use your existing Postgres instance
```

### 2. Configure appsettings.json

```json
{
  "BotDetection": {
    "SignatureStore": {
      "Enabled": true,
      "ConnectionString": "Host=localhost;Database=bot_signatures;Username=postgres;Password=${POSTGRES_PASSWORD}",
      "RetentionDays": 30,
      "EnableSignalR": true,
      "SignalRHubPath": "/hubs/signatures",
      "EnableApiEndpoints": true,
      "ApiBasePath": "/api/signatures"
    }
  }
}
```

### 3. Register Services (Program.cs)

```csharp
using Mostlylucid.BotDetection.SignatureStore.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add SignatureStore services
builder.Services.AddSignatureStore(builder.Configuration);

var app = builder.Build();

// Use SignatureStore middleware (AFTER bot detection middleware)
app.UseSignatureStore();

// Map API endpoints
app.MapSignatureStoreApi();

app.Run();
```

### 4. Use in Client (JavaScript/React)

```javascript
import * as signalR from "@microsoft/signalr";

// Connect to SignalR hub
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/signatures")
  .withAutomaticReconnect()
  .build();

// Load initial top signatures
connection.on("connected", async () => {
  const topSignatures = await connection.invoke("GetTopSignatures", 100);
  displaySignatures(topSignatures);
});

// Receive real-time updates
connection.on("NewSignature", (signature) => {
  console.log("New signature:", signature);
  updateDisplay(signature);
});

await connection.start();
```

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `false` | Enable/disable signature storage |
| `ConnectionString` | `""` | Postgres connection string (supports `${ENV_VAR}` substitution) |
| `RetentionDays` | `30` | Keep signatures for N days (0 = forever) |
| `BatchSize` | `100` | Batch size for bulk inserts |
| `FlushIntervalMs` | `5000` | Flush interval for batched writes |
| `EnableAutoCleanup` | `true` | Auto-delete expired signatures |
| `CleanupIntervalHours` | `24` | How often to run cleanup |
| `MaxSignatures` | `0` | Max signatures to keep (0 = unlimited) |
| `EnableSignalR` | `true` | Enable SignalR real-time updates |
| `SignalRHubPath` | `/hubs/signatures` | SignalR hub endpoint path |
| `EnableApiEndpoints` | `true` | Enable REST API endpoints |
| `ApiBasePath` | `/api/signatures` | REST API base path |

## API Endpoints

### GET /api/signatures/recent
Get recent signatures ordered by timestamp.

**Query Parameters:**
- `count` (int, default: 100) - Number of signatures to return

**Example:**
```bash
curl http://localhost:5000/api/signatures/recent?count=50
```

### GET /api/signatures/top
Get top signatures ordered by bot probability.

**Query Parameters:**
- `count` (int, default: 100) - Number of signatures to return

**Example:**
```bash
curl http://localhost:5000/api/signatures/top?count=100
```

### GET /api/signatures/{id}
Get a single signature by ID (includes full JSON).

**Example:**
```bash
curl http://localhost:5000/api/signatures/sig_abc123
```

### GET /api/signatures/stats
Get signature statistics (count by risk band, averages, etc.)

**Example:**
```bash
curl http://localhost:5000/api/signatures/stats
```

### GET /api/signatures/filter
Filter signatures by signal path and optional value.

**Query Parameters:**
- `signalPath` (string, required) - JSONB path to signal (e.g., `signals.ua.headless_detected`)
- `signalValue` (string, optional) - Value to filter by (auto-parsed to bool/number/string)
- `count` (int, default: 100) - Number of results
- `offset` (int, default: 0) - Pagination offset

**Examples:**
```bash
# Find all signatures with headless browser detection
curl "http://localhost:5000/api/signatures/filter?signalPath=signals.ua.headless_detected&signalValue=true"

# Find all datacenter IPs
curl "http://localhost:5000/api/signatures/filter?signalPath=signals.ip.datacenter"

# Find signatures with high UA bot probability
curl "http://localhost:5000/api/signatures/filter?signalPath=signals.ua.bot_probability&signalValue=0.9"
```

### GET /api/signatures/by-risk-band/{riskBand}
Get signatures by risk band (VeryLow, Low, Elevated, Medium, High, VeryHigh).

**Query Parameters:**
- `count` (int, default: 100)
- `offset` (int, default: 0)

**Example:**
```bash
curl http://localhost:5000/api/signatures/by-risk-band/VeryHigh?count=50
```

## SignalR Hub Methods

### GetTopSignatures(count: int)
Client calls this to get initial top signatures.

```javascript
const signatures = await connection.invoke("GetTopSignatures", 100);
```

### GetRecentSignatures(count: int)
Client calls this to get recent signatures.

```javascript
const signatures = await connection.invoke("GetRecentSignatures", 100);
```

### SubscribeToFilter(signalPath: string, signalValue: object)
Subscribe to filtered updates (future feature).

```javascript
await connection.invoke("SubscribeToFilter", "signals.ua.headless_detected", true);
```

### Hub Events (Server -> Client)

#### NewSignature
Fired when a new signature is stored.

```javascript
connection.on("NewSignature", (signature) => {
  console.log("New signature:", signature);
});
```

#### NewSignatures
Fired when multiple signatures are stored in batch.

```javascript
connection.on("NewSignatures", (signatures) => {
  console.log("Batch of signatures:", signatures.length);
});
```

## Database Schema

The SignatureStore creates a single table: `bot_signatures`

**Columns:**
- `signature_id` (PK) - Unique signature identifier
- `timestamp` - When signature was created
- `bot_probability` - Bot probability (0.0-1.0)
- `confidence` - Detection confidence (0.0-1.0)
- `risk_band` - Risk category (VeryLow, Low, Elevated, Medium, High, VeryHigh)
- `request_path` - Request path
- `request_method` - HTTP method
- `remote_ip` - Client IP address
- `user_agent` - User-Agent string
- `bot_name` - Detected bot name
- `policy_name` - Detection policy used
- `detector_count` - Number of detectors that ran
- `processing_time_ms` - Detection processing time
- `signature_json` (JSONB) - Full signature data with GIN index
- `signals_json` (JSONB) - Extracted signals with GIN index
- `created_at` - Database insertion timestamp
- `expires_at` - Expiration timestamp (for TTL)

**Indexes:**
- Primary key on `signature_id`
- B-tree indexes on: `timestamp`, `bot_probability`, `confidence`, `risk_band`, `request_path`, `remote_ip`, `user_agent`, `bot_name`, `policy_name`
- GIN indexes on: `signature_json`, `signals_json` (for fast JSONB queries)
- Composite indexes: `(timestamp, bot_probability)`, `(risk_band, timestamp)`

## JSONB Query Examples

The GIN indexes enable efficient queries on any nested signal:

```sql
-- Find signatures with HeadlessChrome detected
SELECT * FROM bot_signatures
WHERE signature_json @> '{"signals": {"ua.headless_detected": true}}'::jsonb
ORDER BY bot_probability DESC
LIMIT 100;

-- Find datacenter IPs
SELECT * FROM bot_signatures
WHERE signature_json -> 'signals' -> 'ip' ->> 'datacenter' IS NOT NULL
ORDER BY timestamp DESC;

-- Find high TLS fingerprint matches
SELECT * FROM bot_signatures
WHERE (signature_json -> 'signals' -> 'tls' ->> 'ja3_match_confidence')::float > 0.9
ORDER BY bot_probability DESC;

-- Count by detected bot name
SELECT
  signature_json -> 'signals' ->> 'ua.bot_name' as bot_name,
  COUNT(*) as count
FROM bot_signatures
WHERE signature_json -> 'signals' -> 'ua' ->> 'bot_name' IS NOT NULL
GROUP BY bot_name
ORDER BY count DESC;
```

## Performance Tips

1. **Use Pagination** - Always use `LIMIT` and `OFFSET` for large result sets
2. **Index Hints** - The GIN indexes are automatically used for `@>` (containment) queries
3. **Batch Writes** - Configure `BatchSize` and `FlushIntervalMs` for high-throughput scenarios
4. **Cleanup** - Enable `EnableAutoCleanup` to prevent unbounded growth
5. **Connection Pooling** - Npgsql automatically pools connections
6. **Read Replicas** - For very high query load, consider Postgres read replicas

## Environment Variables

Connection strings support environment variable substitution:

```json
{
  "ConnectionString": "Host=${POSTGRES_HOST};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
}
```

Set environment variables:
```bash
export POSTGRES_HOST=postgres.example.com
export POSTGRES_DB=bot_signatures
export POSTGRES_USER=bot_user
export POSTGRES_PASSWORD=supersecret
```

## Removal

To completely remove SignatureStore:

1. Remove package reference from your project
2. Remove configuration from appsettings.json
3. Remove service registration calls from Program.cs
4. Drop Postgres database (if dedicated): `DROP DATABASE bot_signatures;`

The feature is designed to be fully optional and removable without affecting core bot detection.

## License

Same as parent project (Mostlylucid.BotDetection).
